using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using System.Reflection;
using ss.Internal.Management.Server.AutoRef;
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

                // Busca la instancia de autoref asociada al canal al que se envia el mensaje
                var key = activeChannels.FirstOrDefault(m => m.Value == channelid).Key;
                await activeMatches.GetValueOrDefault(key)!.SendMessageFromDiscord(msgToIRC);
            }
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