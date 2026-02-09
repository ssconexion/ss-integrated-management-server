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
    private readonly ulong targetCategoryId = Convert.ToUInt64(Environment.GetEnvironmentVariable("DISCORD_REFEREE_CATEGORY_ID"));
    private readonly string token;

    // Diccionarios de estado para saber lo que acontece. Por si me olvido (ocurrirá):
    // activeChannels => <match_id, channel_id>
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
        if (activeMatches.ContainsKey(matchId)) return false; // Ya existe

        var newChannel = await guild.CreateTextChannelAsync($"match_{matchId}", props =>
        {
            props.CategoryId = targetCategoryId;
            props.Topic = $"Referee: {referee} | Match ID: {matchId}";
        });

        activeChannels.TryAdd(matchId, newChannel.Id);

        IAutoRef worker = type == Models.MatchType.QualifiersStage
            ? new AutoRefQualifiersStage(matchId, referee, HandleMatchIRCMessage)
            : new AutoRefEliminationStage(matchId, referee, HandleMatchIRCMessage);
        
        activeMatches.TryAdd(matchId, worker);

        await newChannel.SendMessageAsync(string.Format(Strings.WorkerInit, referee));

        try
        {
            _ = worker.StartAsync();
        }
        catch (Exception ex)
        {
            await newChannel.SendMessageAsync(string.Format(Strings.AutorefError, ex.Message));
        }

        return true;
    }

    public async Task EndMatchEnvironmentAsync(string matchId, IMessageChannel requestChannel)
    {
        if (activeMatches.TryRemove(matchId, out var worker))
        {
            // TODO parar AutoRef thread 
            Console.WriteLine($"Worker {matchId} deleted from memory.");
        }
        else
        {
            await requestChannel.SendMessageAsync(Strings.WorkerNotFound);
            return;
        }

        if (activeChannels.TryRemove(matchId, out ulong channelId))
        {
            var channel = client.GetChannel(channelId) as ITextChannel;

            if (channel != null)
            {
                await requestChannel.SendMessageAsync(Strings.DeletingChannel);
                await Task.Delay(3000);
                await channel.DeleteAsync();
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