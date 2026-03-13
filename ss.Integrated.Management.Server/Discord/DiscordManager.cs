using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using System.Reflection;
using ss.Internal.Management.Server.AutoRef;
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
    
    // activeChannels => <match_id, thread_id>
    // activeMatches  => <match_id, autoref_instance>
    private readonly ConcurrentDictionary<string, ulong> activeChannels = new();
    private readonly ConcurrentDictionary<string, IMatchManager?> activeMatches = new();
    private readonly ConcurrentDictionary<string, ulong> liveEmbedMessages = new();

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

        IMatchManager worker = type == Models.MatchType.QualifiersStage
            ? new MatchManagerQualifiersStage(matchId, referee, HandleMatchIRCMessage)
            : new MatchManagerEliminationStage(matchId, referee, HandleMatchIRCMessage);

        activeMatches.TryAdd(matchId, worker);

        await newThread.SendMessageAsync(string.Format(Strings.WorkerInit, referee));

        try
        {
            _ = worker.StartAsync();
            worker.OnStateUpdated += UpdateLiveEmbedAsync;
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
        Task.Run(async () =>
        {
            if (activeChannels.TryGetValue(matchId, out ulong channelId))
            {
                if (client.GetChannel(channelId) is IMessageChannel channel)
                {
                    bool shouldPin = false;
                    
                    if (messageContent.StartsWith("🗣️", StringComparison.OrdinalIgnoreCase))
                    {
                        shouldPin = true;
                        messageContent = messageContent.Substring(6).Trim();
                    }

                    string replaced = messageContent.Replace("_", @"\_");

                    var sentMessage = await channel.SendMessageAsync(replaced);
                    
                    if (shouldPin)
                    {
                        await sentMessage.PinAsync();
                    }
                }
            }
        });
    }

    private Task LogAsync(LogMessage msg)
    {
        Console.WriteLine(msg.ToString());
        return Task.CompletedTask;
    }
}