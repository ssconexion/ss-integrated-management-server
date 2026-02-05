using Discord;
using Discord.WebSocket;
using Discord.WebSocket;
using System.Collections.Concurrent;
using ss.Internal.Management.Server.AutoRef;

namespace ss.Internal.Management.Server.Discord;

public class DiscordManager
{
    private readonly DiscordSocketClient client;
    private readonly ulong guildId = 806245928927100938;
    private readonly string token;
    private readonly ulong targetCategoryId; // ID de la Categoría donde crear canales

    // Diccionario para mapear MatchID -> ChannelID
    private ConcurrentDictionary<string, ulong> activeChannels = new();
    
    private ConcurrentDictionary<string, AutoRef.AutoRef> activeMatches = new();

    public DiscordManager(string token, ulong categoryId)
    {
        this.token = token;
        targetCategoryId = categoryId;

        client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.AllUnprivileged | GatewayIntents.MessageContent | GatewayIntents.Guilds
        });

        client.Log += LogAsync;
        client.MessageReceived += HandleCommandAsync;
    }

    public async Task StartAsync()
    {
        await client.LoginAsync(TokenType.Bot, token);
        await client.StartAsync();
        Console.WriteLine("DoloresRelay iniciado");
    }

    private async Task HandleCommandAsync(SocketMessage message)
    {
        if (message.Author.IsBot) return;

        // Comando: !startref <match_id> <referee> <type>
        if (message.Content.StartsWith("!startref"))
        {
            var parts = message.Content.Split(' ');

            if (parts.Length < 4)
            {
                await message.Channel.SendMessageAsync("Uso: `!startref <matchId> <referee> <type>`");
                return;
            }

            string matchId = parts[1];
            string referee = parts[2];
            int type = int.Parse(parts[3]);

            await CreateMatchEnvironmentAsync(client.Guilds.FirstOrDefault(g => g.Id == guildId)!, matchId, referee);
        }
    }

    private async Task CreateMatchEnvironmentAsync(IGuild guild, string matchId, string referee)
    {
        var newChannel = await guild.CreateTextChannelAsync($"match_{matchId}", props =>
        {
            props.CategoryId = targetCategoryId; // Esto hereda los permisos de la categoría automáticamente
            props.Topic = $"Referee: {referee} | Match ID: {matchId}";
        });

        activeChannels.TryAdd(matchId, newChannel.Id);
        
        var worker = new AutoRef.AutoRef(matchId, referee, Models.MatchType.EliminationStage, HandleMatchIRCMessage);
        
        activeMatches.TryAdd(matchId, worker);

        await newChannel.SendMessageAsync($"Canal creado para Referee: **{referee}**. Iniciando worker...");
        
        _ = worker.StartAsync();
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
            else
            {
                Console.WriteLine($"Error: No se encontró canal para MatchID {matchId}");
            }
        });
    }

    private Task LogAsync(LogMessage msg)
    {
        Console.WriteLine(msg.ToString());
        return Task.CompletedTask;
    }

}