using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.RegularExpressions;
using ss.Internal.Management.Server.AutoRef;
using ss.Internal.Management.Server.Discord.Helpers;
using ss.Internal.Management.Server.MatchManager;
using ss.Internal.Management.Server.Resources;

namespace ss.Internal.Management.Server.Discord;

/// <summary>
/// Service responsible for managing the lifecycle of Discord interactions and bridging them with the AutoRef system.
/// </summary>
public class DiscordManager
{
    private readonly DiscordSocketClient client;
    private readonly InteractionService interactionService;
    private readonly IServiceProvider services;

    private readonly ulong guildId = Convert.ToUInt64(Environment.GetEnvironmentVariable("DISCORD_GUILD_ID"));
    private readonly ulong parentChannelId = Convert.ToUInt64(Environment.GetEnvironmentVariable("DISCORD_MATCHES_CHANNEL_ID"));
    private readonly string token;
    
    private record QueuedMessage(string Content, IMatchManager.MessageKind Kind);
    
    // activeChannels => <match_id, thread_id>
    // activeMatches  => <match_id, autoref_instance>
    private readonly ConcurrentDictionary<string, ulong> activeChannels = new();
    private readonly ConcurrentDictionary<string, IMatchManager?> activeMatches = new();
    private readonly ConcurrentDictionary<string, ulong> liveEmbedMessages = new();
    private readonly ConcurrentDictionary<string, System.Threading.Channels.Channel<QueuedMessage>> messageQueues = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> queueCancellationTokens = new();
    
    private static readonly Regex BanchoNoise = 
        new(@"\[BanchoBot\].*Match starts in \d+ seconds?", RegexOptions.Compiled);
    
    public DiscordManager(string token)
    {
        this.token = token;

        client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.AllUnprivileged | GatewayIntents.MessageContent | GatewayIntents.Guilds
        });

        interactionService = new InteractionService(client.Rest);

        services = new ServiceCollection()
            .AddSingleton(this)
            .AddSingleton(client)
            .AddSingleton(interactionService)
            .BuildServiceProvider();

        client.Log += LogAsync;
        client.Ready += ReadyAsync;
        client.InteractionCreated += HandleInteractionAsync;

        client.MessageReceived += HandleMessageAsync;
    }

    /// <summary>
    /// Connects to Discord, registers modules, and starts the event loop.
    /// </summary>
    public async Task StartAsync()
    {
        await client.LoginAsync(TokenType.Bot, token);
        await client.StartAsync();

        await interactionService.AddModulesAsync(Assembly.GetEntryAssembly(), services);

        Console.WriteLine("DoloresRelay initialized and services loaded.");
    }

    private async Task ReadyAsync()
    {
        try
        {
            await interactionService.RegisterCommandsToGuildAsync(guildId);
            Console.WriteLine($"Slash commands registered in Guild: {guildId}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error while registering a command: {ex.Message}");
        }
    }

    // Manejador central de interacciones
    private async Task HandleInteractionAsync(SocketInteraction interaction)
    {
        try
        {
            var ctx = new SocketInteractionContext(client, interaction);
            await interactionService.ExecuteCommandAsync(ctx, services);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error while executing a command: {ex}");

            if (interaction.Type == InteractionType.ApplicationCommand)
                await interaction.GetOriginalResponseAsync().ContinueWith(async (msg) => await msg.Result.DeleteAsync());
        }
    }

    /// <summary>
    /// Sets up the Discord thread and spawns the AutoRef worker for a specific match.
    /// </summary>
    /// <param name="matchId">The unique match identifier (e.g. "A1").</param>
    /// <param name="referee">The display name of the referee in charge.</param>
    /// <param name="guild">The Discord guild object where the thread will be created.</param>
    /// <param name="type">The type of match (Qualifiers vs Elimination) which determines the logic used.</param>
    /// <returns>True if the environment was created; false if the match is already active.</returns>
    public async Task<bool> CreateMatchEnvironmentAsync(string matchId, string referee, IGuild guild, Models.MatchType type)
    {
        if (activeMatches.ContainsKey(matchId)) return false;

        var parentChannel = await guild.GetTextChannelAsync(parentChannelId);

        if (parentChannel == null)
        {
            Console.WriteLine($"Error: No se encontró el canal con ID {parentChannelId}");
            return false;
        }

        var newThread = await parentChannel.CreateThreadAsync(
            name: $"{Program.TournamentName}: Playoffs, match {matchId}",
            autoArchiveDuration: ThreadArchiveDuration.OneDay,
            type: ThreadType.PublicThread
        );

        activeChannels.TryAdd(matchId, newThread.Id);
        
        var queue = System.Threading.Channels.Channel.CreateUnbounded<QueuedMessage>(new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true });
        var cts = new CancellationTokenSource();
        
        messageQueues.TryAdd(matchId, queue);
        queueCancellationTokens.TryAdd(matchId, cts);
        
        _ = Task.Run(() => RunMessageQueueAsync(matchId, cts.Token), cts.Token);

        IMatchManager worker = type == Models.MatchType.QualifiersStage
            ? new MatchManagerQualifiersStage(matchId, referee, HandleMatchIRCMessage)
            : new MatchManagerEliminationStage(matchId, referee, HandleMatchIRCMessage);

        activeMatches.TryAdd(matchId, worker);

        await newThread.SendMessageAsync(string.Format(Strings.WorkerInit, referee));

        try
        {
            _ = worker.StartAsync();
            worker.OnStateUpdated += UpdateLiveEmbedAsync;
            worker.OnSettingsReceived += SendSettingsEmbedAsync;
        }
        catch (Exception ex)
        {
            await newThread.SendMessageAsync(string.Format(Strings.ErrInitAutoref, ex.Message));
        }

        return true;
    }

    /// <summary>
    /// Stops the match worker, archives the Discord thread, and removes the match from active memory.
    /// </summary>
    /// <param name="matchId">The unique identifier of the match to close.</param>
    public async Task<bool> EndMatchEnvironmentAsync(string matchId)
    {
        if (activeMatches.TryRemove(matchId, out var worker))
        {
            await worker.StopAsync();
            
            if (queueCancellationTokens.TryRemove(matchId, out var cts))
            {
                await Task.Delay(5000);
                await cts.CancelAsync();
                cts.Dispose();
            }

            messageQueues.TryRemove(matchId, out _);
        }
        else
        {
            return false;
        }

        if (activeChannels.TryRemove(matchId, out ulong threadId))
        {
            if (client.GetChannel(threadId) is IThreadChannel thread)
            {
                await thread.SendMessageAsync(Strings.MatchFinishedThread);

                await thread.ModifyAsync(props =>
                {
                    props.Archived = true;
                    props.Locked = true;
                });
            }
        }

        return true;
    }
    
    private Embed BuildLiveMatchEmbed(MatchManagerEliminationStage matchManagerEliminationStage)
    {
        var match = matchManagerEliminationStage.currentMatch;
        if (match == null) return new EmbedBuilder().WithTitle("Cargando partido...").Build();
        
        var embed = new EmbedBuilder()
            .WithTitle($"{match.Id}: {match.TeamRed.DisplayName} vs {match.TeamBlue.DisplayName}")
            .WithUrl($"https://osu.ppy.sh/mp/{match.MpLinkId}")
            .AddField("Marcador", $"🔴 **{matchManagerEliminationStage.MatchScore[0]}** - **{matchManagerEliminationStage.MatchScore[1]}** 🔵", false)
            .AddField("Estado Actual", $"`{matchManagerEliminationStage.currentState}`", false);
        
        string bans = matchManagerEliminationStage.bannedMaps.Any() 
            ? string.Join("\n", matchManagerEliminationStage.bannedMaps.Select(m => $"{(m.TeamColor == Models.TeamColor.TeamRed ? "🔴" : "🔵")} {m.Slot}")) 
            : "*Ninguno todavía*";
        
        string picks = matchManagerEliminationStage.pickedMaps.Any() 
            ? string.Join("\n", matchManagerEliminationStage.pickedMaps.Select(m => 
            {
                string picker = m.TeamColor == Models.TeamColor.TeamRed ? "🔴" : "🔵";
                
                string winnerIndicator = "";
                if (m.Winner == Models.TeamColor.TeamRed)
                    winnerIndicator = " ➔ 🔴 Wins!";
                else if (m.Winner == Models.TeamColor.TeamBlue)
                    winnerIndicator = " ➔ 🔵 Wins!";

                else if (m.TeamColor == Models.TeamColor.None)
                    picker = "🟣";

                return $"{picker} **{m.Slot}**{winnerIndicator}";
            })) 
            : "*Ninguno todavía*";

        embed.AddField("Bans", bans, true);
        embed.AddField("Picks", picks, true);
        
        embed.WithFooter($"Árbitro: {match.Referee.DisplayName}");
        embed.WithCurrentTimestamp();

        return embed.Build();
    }
    
    private async Task UpdateLiveEmbedAsync(string matchId)
    {
        if (!activeMatches.TryGetValue(matchId, out var autoRefInterface)) return;
        
        if (autoRefInterface is not MatchManagerEliminationStage autoRef) return;
        
        if (client.GetChannel(parentChannelId) is not IMessageChannel channel) return;
        
        var embed = BuildLiveMatchEmbed(autoRef);
        
        if (liveEmbedMessages.TryGetValue(matchId, out ulong messageId))
        {
            if (await channel.GetMessageAsync(messageId) is IUserMessage message)
            {
                await message.ModifyAsync(x => x.Embed = embed);
            }
        }
        else
        {
            var sentMessage = await channel.SendMessageAsync(embed: embed);
            liveEmbedMessages.TryAdd(matchId, sentMessage.Id);
        }
    }

    private async Task SendSettingsEmbedAsync(string matchId, DiscordModels.MpSettingsResult settings)
    {
        if (!activeChannels.TryGetValue(matchId, out ulong channelId)) return;
        if (client.GetChannel(channelId) is not IMessageChannel channel) return;

        var embed = BuildMpSettingsEmbed(settings);
        await channel.SendMessageAsync(embed: embed);
    }
    
    private static Embed BuildMpSettingsEmbed(DiscordModels.MpSettingsResult settings)
    {
        var redSlots  = settings.Slots.Where(s => s.Team == "Red");
        var blueSlots = settings.Slots.Where(s => s.Team == "Blue");

        static string OrNone(string value) =>
            string.IsNullOrWhiteSpace(value) ? "*None*" : value;

        string FormatSlots(IEnumerable<DiscordModels.SlotInfo> slots) =>
            slots.Any()
                ? string.Join("\n", slots.Select(s =>
                    $"{(s.IsReady ? "✅" : "❌")} [{s.Username}]({s.ProfileUrl}) *{s.Mods}*"))
                : "*Empty*";

        var embed = new EmbedBuilder()
            .WithTitle(OrNone(settings.RoomName))
            .AddField("Mode", $"{OrNone(settings.TeamMode)} — {OrNone(settings.WinCondition)}", true)
            .AddField("Mods", OrNone(settings.ActiveMods), true)
            .AddField("🔴 Red",  FormatSlots(redSlots),  false)
            .AddField("🔵 Blue", FormatSlots(blueSlots), true)
            .WithCurrentTimestamp();
        
        if (!string.IsNullOrWhiteSpace(settings.HistoryUrl))
            embed.WithUrl(settings.HistoryUrl);

        if (!string.IsNullOrWhiteSpace(settings.BeatmapName))
            embed.AddField("Beatmap", string.IsNullOrWhiteSpace(settings.BeatmapUrl)
                ? settings.BeatmapName
                : $"[{settings.BeatmapName}]({settings.BeatmapUrl})", false);

        return embed.Build();
    }
    
    /// <summary>
    /// Registers a new referee in the database with their IRC credentials.
    /// </summary>
    /// <param name="model">The referee data model.</param>
    public async Task AddRefereeToDbAsync(Models.RefereeInfo model)
    {
        await using var db = new ModelsContext();
        db.Referees.Add(model);
        await db.SaveChangesAsync();
    }

    private async Task HandleMessageAsync(SocketMessage message)
    {
        if (message.Author.IsBot || message.Attachments.Count > 0 || message.Stickers.Count > 0) return;

        foreach (var channelid in activeChannels.Values)
        {
            if (message.Channel.Id == channelid)
            {
                var msgToIRC = message.Content;

                bool isCommand = msgToIRC.StartsWith("!");
                bool interaction = msgToIRC.StartsWith(">");

                if (!isCommand && !interaction) msgToIRC = $"[DISCORD | {message.Author.Username}] {message.Content}";

                // Busca la instancia de autoref asociada al canal al que se envia el mensaje. Vaya puto círculo, loco
                var key = activeChannels.FirstOrDefault(m => m.Value == channelid).Key;
                await activeMatches.GetValueOrDefault(key)!.SendMessageFromDiscord(msgToIRC);
            }
        }

        if (message.Content == "ITS ME") // ITS ME
        {
            const string s1 = "https://methalox.s-ul.eu/ReZNgSND";
            const string s2 = "https://methalox.s-ul.eu/rI0fEYx9";
            const string s3 = "https://methalox.s-ul.eu/xKGEVh9c";
            const string s4 = "https://methalox.s-ul.eu/NSs8jJAA";
            string[] list = [s1, s2, s3, s4];

            var rnd = new Random();
            await message.Channel.SendMessageAsync(list[rnd.Next(0, list.Length)]);
        }
    }

    private void HandleMatchIRCMessage(string matchId, string messageContent, IMatchManager.MessageKind messageType)
    {
        if (BanchoNoise.IsMatch(messageContent)) return;

        if (messageQueues.TryGetValue(matchId, out var queue))
        {
            queue.Writer.TryWrite(new QueuedMessage(messageContent, messageType));
        }
    }
    
    private async Task RunMessageQueueAsync(string matchId, CancellationToken ct)
    {
        if (!messageQueues.TryGetValue(matchId, out var queue)) return;

        await foreach (var message in queue.Reader.ReadAllAsync(ct))
        {
            try
            {
                if (!activeChannels.TryGetValue(matchId, out ulong channelId)) continue;
                if (client.GetChannel(channelId) is not IMessageChannel channel) continue;

                string escaped = message.Content.Replace("_", @"\_");
                var sent = await channel.SendMessageAsync(escaped);

                if (message.Kind == IMatchManager.MessageKind.PinOrderMessage)
                    await sent.PinAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Queue:{matchId}] Send failed: {ex.Message}");
            }

            // Delay applied between messages sent to discord.
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
    }

    private Task LogAsync(LogMessage msg)
    {
        Console.WriteLine(msg.ToString());
        return Task.CompletedTask;
    }
}