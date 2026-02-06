using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using System.Reflection;
using ss.Integrated.Management.Server;
using ss.Internal.Management.Server.AutoRef;

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
    private ConcurrentDictionary<string, AutoRef.AutoRef> activeMatches = new();

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
        
        Console.WriteLine("DoloresRelay iniciado y servicios cargados.");
    }
    
    private async Task ReadyAsync()
    {
        try 
        {
            await interactionService.RegisterCommandsToGuildAsync(guildId);
            Console.WriteLine($"Comandos Slash registrados en Guild: {guildId}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error registrando comandos: {ex.Message}");
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
            Console.WriteLine($"Error ejecutando comando: {ex}");
            if (interaction.Type == InteractionType.ApplicationCommand)
                await interaction.GetOriginalResponseAsync().ContinueWith(async (msg) => await msg.Result.DeleteAsync());
        }
    }

    public async Task<bool> CreateMatchEnvironmentAsync(string matchId, string referee, IGuild guild)
    {
        if (activeMatches.ContainsKey(matchId)) return false; // Ya existe

        var newChannel = await guild.CreateTextChannelAsync($"match_{matchId}", props =>
        {
            props.CategoryId = targetCategoryId;
            props.Topic = $"Referee: {referee} | Match ID: {matchId}";
        });

        activeChannels.TryAdd(matchId, newChannel.Id);

        var worker = new AutoRef.AutoRef(matchId, referee, HandleMatchIRCMessage);
        activeMatches.TryAdd(matchId, worker);

        await newChannel.SendMessageAsync($"Canal creado para Referee: **{referee}**. Iniciando worker...");
        
        _ = worker.StartAsync();

        return true;
    }

    public async Task EndMatchEnvironmentAsync(string matchId, IMessageChannel requestChannel)
    {
        if (activeMatches.TryRemove(matchId, out var worker))
        {
            // TODO parar AutoRef thread 
            Console.WriteLine($"Worker {matchId} eliminado de memoria.");
        }
        else
        {
            await requestChannel.SendMessageAsync("No se encontró un worker activo con ese Match ID.");
            return;
        }
        
        if (activeChannels.TryRemove(matchId, out ulong channelId))
        {
            var channel = client.GetChannel(channelId) as ITextChannel;
            if (channel != null)
            {
                await requestChannel.SendMessageAsync("Eliminando canal y cerrando proceso...");
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
        if(message.Author.IsBot || message.Attachments.Count > 0 || message.Stickers.Count > 0) return;
        
        foreach (var channelid in activeChannels.Values)
        {
            if (message.Channel.Id == channelid)
            {
                var msgToIRC = message.Content;
                if (!message.Content.StartsWith("!")) msgToIRC = $"[DISCORD | {message.Author.Username}] {message.Content}";
                
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