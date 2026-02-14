using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using System.Reflection;
using ss.Integrated.Management.Server;
using ss.Internal.Management.Server.AutoRef;
using ss.Internal.Management.Server.Resources;

namespace ss.Internal.Management.Server.Discord;

public class DiscordManager
{
    private readonly DiscordSocketClient client;
    private readonly InteractionService interactionService;
    private readonly IServiceProvider services;

    private readonly ulong guildId = Convert.ToUInt64(Environment.GetEnvironmentVariable("DISCORD_GUILD_ID"));
    private readonly ulong parentChannelId = Convert.ToUInt64(Environment.GetEnvironmentVariable("DISCORD_MATCHES_CHANNEL_ID"));
    private readonly string token;

    // Diccionarios de estado para saber lo que acontece. Por si me olvido (ocurrirá):
    // activeChannels => <match_id, thread_id>
    // activeMatches  => <match_id, autoref_instance>
    private ConcurrentDictionary<string, ulong> activeChannels = new();
    private ConcurrentDictionary<string, IAutoRef> activeMatches = new();

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
            name: $"{Program.TournamentName}: Match {matchId}",
            autoArchiveDuration: ThreadArchiveDuration.OneDay,
            type: ThreadType.PublicThread
        );

        activeChannels.TryAdd(matchId, newThread.Id);

        IAutoRef worker = type == Models.MatchType.QualifiersStage
            ? new AutoRefQualifiersStage(matchId, referee, HandleMatchIRCMessage)
            : new AutoRefEliminationStage(matchId, referee, HandleMatchIRCMessage);

        activeMatches.TryAdd(matchId, worker);

        await newThread.SendMessageAsync(string.Format(Strings.WorkerInit, referee));

        try
        {
            _ = worker.StartAsync();
        }
        catch (Exception ex)
        {
            await newThread.SendMessageAsync(string.Format(Strings.ErrInitAutoref, ex.Message));
        }

        return true;
    }

    public async Task EndMatchEnvironmentAsync(string matchId, IMessageChannel requestChannel)
    {
        if (activeMatches.TryRemove(matchId, out var worker))
        {
            await worker.StopAsync();
            Console.WriteLine($"Worker {matchId} stopped.");
        }
        else
        {
            await requestChannel.SendMessageAsync(Strings.WorkerNotFound);
            return;
        }

        if (activeChannels.TryRemove(matchId, out ulong threadId))
        {
            if (client.GetChannel(threadId) is IThreadChannel thread)
            {
                await requestChannel.SendMessageAsync(string.Format(Strings.MatchFinishedGlobal, threadId));

                await thread.SendMessageAsync(Strings.MatchFinishedThread);

                await thread.ModifyAsync(props =>
                {
                    props.Archived = true;
                    props.Locked = true;
                });
            }
        }
    }

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

                // Busca la instancia de autoref asociada al canal al que se envia el mensaje
                var key = activeChannels.FirstOrDefault(m => m.Value == channelid).Key;
                await activeMatches.GetValueOrDefault(key)!.SendMessageFromDiscord(msgToIRC);
            }
        }

        if (message.Content == "!testfill")
        {
            await Tests.TestFill();
            await message.Channel.SendMessageAsync("Sample database filled.");
        }

        if (message.Content == "!roundfill")
        {
            var roundTemplate = new Models.Round
            {
                Id = 1,
                DisplayName = "Grand Finals",
                BestOf = 13,
                BanRounds = 2,
                Mode = Models.BansType.SpanishShowdown,
                MapPool = new List<Models.RoundBeatmap>
                {
                    new() { BeatmapID = 4579522, Slot = "NM1" },
                    new() { BeatmapID = 4277943, Slot = "NM2" },
                    new() { BeatmapID = 3269529, Slot = "NM3" },
                    new() { BeatmapID = 4723710, Slot = "NM4" },
                    new() { BeatmapID = 4096455, Slot = "NM5" },
                    new() { BeatmapID = 4497114, Slot = "NM6" },
                    new() { BeatmapID = 5177739, Slot = "HD1" },
                    new() { BeatmapID = 4274519, Slot = "HD2" },
                    new() { BeatmapID = 4798495, Slot = "HD3" },
                    new() { BeatmapID = 4761303, Slot = "HD4" },
                    new() { BeatmapID = 4931381, Slot = "HR1" },
                    new() { BeatmapID = 3214479, Slot = "HR2" },
                    new() { BeatmapID = 3609114, Slot = "HR3" },
                    new() { BeatmapID = 4881729, Slot = "HR4" },
                    new() { BeatmapID = 1872396, Slot = "DT1" },
                    new() { BeatmapID = 5178950, Slot = "DT2" },
                    new() { BeatmapID = 2458659, Slot = "DT3" },
                    new() { BeatmapID = 4951914, Slot = "DT4" },
                    new() { BeatmapID = 4691422, Slot = "TB1" },
                },
            };
            
            await using var db = new ModelsContext();
            
            await db.Rounds.AddAsync(roundTemplate);
            await db.SaveChangesAsync();
            
            await message.Channel.SendMessageAsync("d");
        }

        if (message.Content == "ITS ME")
        {
            string s1 = "https://methalox.s-ul.eu/ReZNgSND";
            string s2 = "https://methalox.s-ul.eu/rI0fEYx9";
            string s3 = "https://methalox.s-ul.eu/xKGEVh9c";
            string s4 = "https://methalox.s-ul.eu/NSs8jJAA";
            string[] list = { s1, s2, s3, s4 };

            var rnd = new Random();
            await message.Channel.SendMessageAsync(list[rnd.Next(0, list.Length)]);
        }
    }

    private void HandleMatchIRCMessage(string matchId, string messageContent)
    {
        Task.Run(async () =>
        {
            if (activeChannels.TryGetValue(matchId, out ulong channelId))
            {
                if (client.GetChannel(channelId) is IMessageChannel channel)
                {
                    await channel.SendMessageAsync($"{messageContent}");
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