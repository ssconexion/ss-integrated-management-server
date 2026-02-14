using BanchoSharp;
using BanchoSharp.Interfaces;
using Microsoft.EntityFrameworkCore;
using ss.Internal.Management.Server.Resources;

namespace ss.Internal.Management.Server.AutoRef;

public partial class AutoRefQualifiersStage : IAutoRef
{
    private Models.QualifierRoom? currentMatch;
    private readonly string matchId;
    private readonly string refDisplayName;

    private IBanchoClient? client;
    private string? lobbyChannelName;
    
    private bool joined;

    private int currentMapIndex;
    private MatchState state;

    private List<int> usersInRoom = new();

    private TaskCompletionSource<string>? chatResponseTcs;

    private readonly Action<string, string> msgCallback;

    public enum MatchState
    {
        Idle,
        WaitingForStart,
        Playing,
        MatchFinished,
        MatchOnHold,
    }

    public AutoRefQualifiersStage(string matchId, string refDisplayName, Action<string, string> msgCallback)
    {
        this.matchId = matchId;
        this.refDisplayName = refDisplayName;
        this.msgCallback = msgCallback;
    }

    public async Task StartAsync()
    {
        await using (var db = new ModelsContext())
        {
            currentMatch = await db.QualifierRooms.FirstAsync(m => m.Id == matchId) ?? throw new Exception("Match not found in the DB");
            currentMatch.Referee = await db.Referees.FirstAsync(r => r.DisplayName == refDisplayName) ?? throw new Exception("Referee not found in the DB");
            
            currentMatch.Round = await db.Rounds.FirstAsync(r => r.Id == currentMatch.RoundId);

            //usersInRoom = await db.Players.Where(p => p.QualifiersRoom == matchId).Select(p => p.User.Id).ToListAsync();
        }

        await ConnectToBancho();
    }

    public async Task StopAsync()
    {
        // Este método solo debería ser llamado desde DiscordManager, de lo contrario nos
        // metemos en camisa de once varas, cosa con la que tampoco quiero lidiar

        await using var db = new ModelsContext();
        await db.SaveChangesAsync();
    }

    private async Task ConnectToBancho()
    {
        var config = new BanchoClientConfig(
            new IrcCredentials(currentMatch!.Referee.DisplayName, currentMatch.Referee.IRC)
        );

        client = new BanchoClient(config);

        client.OnMessageReceived += message =>
        {
            _ = HandleIrcMessage(message);
        };
        
        client.OnPrivateMessageSent += message =>
        {
            _ = PeruTrim(message);
        };

        client.OnAuthenticated += () =>
        {
            _ = client.MakeTournamentLobbyAsync($"{Program.TournamentName}: jowjowosu vs methalox", true);
        };

        await client.ConnectAsync();
    }
    
    private async Task PeruTrim(IIrcMessage msg)
    {
        string prefix = msg.Prefix.StartsWith(":") ? msg.Prefix[1..] : msg.Prefix;
        string senderNick = prefix.Contains('!') ? prefix.Split('!')[0] : prefix;

        string content = msg.Parameters[1];

        if (content.StartsWith('>'))
        {
            await ExecuteAdminCommand(senderNick, content[1..].Split(' '));
        }
    }

    private async Task HandleIrcMessage(IIrcMessage msg)
    {
        string prefix = msg.Prefix.StartsWith(":") ? msg.Prefix[1..] : msg.Prefix;
        string senderNick = prefix.Contains('!') ? prefix.Split('!')[0] : prefix;

        //string target = msg.Parameters[0];
        string content = msg.Parameters[1];

        Console.WriteLine($"{senderNick}: {content}");

        if (joined) msgCallback(matchId, $"**[{senderNick}]** {content}");

        switch (senderNick)
        {
            case "BanchoBot" when content.Contains("Created the tournament match"):
                var parts = content.Split('/');
                var idPart = parts.Last().Split(' ')[0];
                lobbyChannelName = $"#mp_{idPart}";

                await client!.JoinChannelAsync(lobbyChannelName);
                await InitializeLobbySettings();
                joined = true;
                return;
            case "BanchoBot" when content.Contains("Closed the match"):
                await client!.DisconnectAsync();
                break;
            case "BanchoBot" when chatResponseTcs != null && SearchKeywords(content):
                chatResponseTcs.TrySetResult(content);
                chatResponseTcs = null;
                break;
        }

        if (senderNick == "BanchoBot") _ = TryStateChange(content);

        // REGIÓN DEDICADA AL !PANIC. ESTÁ DESACOPLADA DEL RESTO POR SER UN CASO DE EMERGENCIA
        // QUE NO DEBERÍA CAER EN NINGUNA OTRA SUBRUTINA

        if (content.Contains("!panic_over"))
        {
            await SendMessageBothWays(Strings.BackToAuto);
            state = MatchState.WaitingForStart;
            await SendMessageBothWays("!mp timer 10");
        }
        else if (content.Contains("!panic"))
        {
            state = MatchState.MatchOnHold;
            await SendMessageBothWays("!mp aborttimer");

            await SendMessageBothWays(
                string.Format(Strings.Panic, Environment.GetEnvironmentVariable("DISCORD_REFEREE_ROLE_ID"), senderNick)
                );
        }

        if (content.StartsWith('>'))
        {
            await ExecuteAdminCommand(senderNick, content[1..].Split(' '));
        }
    }

    public async Task SendMessageFromDiscord(string content)
    {
        await client!.SendPrivateMessageAsync(lobbyChannelName!, content);
    }

    private async Task InitializeLobbySettings()
    {
        await client!.SendPrivateMessageAsync(lobbyChannelName!, "!mp set 0 3 16");
        await client!.SendPrivateMessageAsync(lobbyChannelName!, "!mp invite " + currentMatch!.Referee.DisplayName.Replace(' ', '_'));
    }

    private async Task ExecuteAdminCommand(string sender, string[] args)
    {
        if (sender != currentMatch!.Referee.DisplayName.Replace(' ', '_')) return;

        switch (args[0].ToLower())
        {
            case "close":
                await SendMessageBothWays("!mp close");
                await client!.DisconnectAsync();
                break;
            case "invite":
                foreach (var osuId in usersInRoom)
                {
                    await SendMessageBothWays($"!mp invite #{osuId}");
                    await Task.Delay(500);
                }
                break;
            case "start":
                await SendMessageBothWays(
                    string.Format(Strings.QualifiersAutoEngage, currentMatch!.Id));
                _ = StartQualifiersFlow();
                break;
        }
    }

    private async Task SendMessageBothWays(string content)
    {
        await client!.SendPrivateMessageAsync(lobbyChannelName!, content);
        msgCallback(matchId, $"**[AUTO | {currentMatch!.Referee.DisplayName}]** {content}");
    }

    private bool SearchKeywords(string content)
    {
        bool found = content switch
        {
            var s when s.Contains("All players are ready") => true,
            var s when s.Contains("Changed beatmap") => true,
            var s when s.Contains("Enabled") => true,
            var s when s.Contains("Countdown finished") => true,
            _ => false
        };

        return found;
    }

    private async Task TryStateChange(string banchoMsg) // transiciones de estado
    {
        switch (state)
        {
            case MatchState.Idle:
                return;
            case MatchState.WaitingForStart:
            {
                if (banchoMsg.Contains("All players are ready") || banchoMsg.Contains("Countdown finished"))
                {
                    await SendMessageBothWays("!mp start 10");
                    state = MatchState.Playing;
                }

                break;
            }
            case MatchState.Playing:
            {
                if (banchoMsg.Contains("The match has finished"))
                {
                    currentMapIndex++;
                    state = MatchState.Idle;

                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(10000);
                        await PrepareNextQualifierMap();
                    });
                }

                break;
            }
        }
    }

    private async Task StartQualifiersFlow()
    {
        currentMapIndex = 0;
        state = MatchState.Idle;
        await PrepareNextQualifierMap();
    }

    private async Task PrepareNextQualifierMap()
    {
        if (currentMapIndex >= currentMatch!.Round.MapPool.Count)
        {
            await SendMessageBothWays(Strings.QualifiersOver);
            state = MatchState.MatchFinished;
            return;
        }

        var beatmap = currentMatch.Round.MapPool[currentMapIndex];

        await SendMessageBothWays($"!mp map {beatmap.BeatmapID}");
        await SendMessageBothWays($"!mp mods {beatmap.Slot[..2]} NF");
        await SendMessageBothWays("!mp timer 120");

        state = MatchState.WaitingForStart;
    }
}