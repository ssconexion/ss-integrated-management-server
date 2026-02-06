using BanchoSharp;
using BanchoSharp.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ss.Internal.Management.Server.AutoRef;

public partial class AutoRef
{
    private Models.Match? currentMatch;
    private readonly string matchId;
    private readonly string refDisplayName;

    private IBanchoClient? client;
    public string? LobbyChannelName;

    private int[] matchScore = [0, 0];
    private bool auto = false;
    private bool joined = false;

    private TeamColor firstPick;
    private TeamColor firstBan;

    private int currentMapIndex = 0; // Esto solo se usa en Qualifiers
    private MatchState state;

    private TaskCompletionSource<string>? chatResponseTcs;

    private readonly Action<string, string> msgCallback;

    public enum TeamColor
    {
        TeamBlue,
        TeamRed
    }

    public enum MatchState
    {
        Idle,
        WaitingForStart,
        Playing,
        MatchFinished,
        MatchOnHold,
    }

    public AutoRef(string matchId, string refDisplayName, Action<string, string> msgCallback)
    {
        this.matchId = matchId;
        this.refDisplayName = refDisplayName;
        this.msgCallback = msgCallback;
    }

    public async Task StartAsync()
    {
        using (var db = new ModelsContext())
        {
            currentMatch = await db.Matches.FirstAsync(m => m.Id == matchId) ?? throw new Exception("Match no encontrado en DB");
            currentMatch.Referee = await db.Referees.FirstAsync(r => r.DisplayName == refDisplayName) ?? throw new Exception("Referee no encontrado en DB");
        }

        await ConnectToBancho();
    }

    private async Task ConnectToBancho()
    {
        var config = new BanchoClientConfig(
            new IrcCredentials(currentMatch.Referee.DisplayName, currentMatch.Referee.IRC)
        );

        client = new BanchoClient(config);

        client.OnMessageReceived += message =>
        {
            _ = HandleIrcMessage(message);
        };

        client.OnAuthenticated += () =>
        {
            _ = client.MakeTournamentLobbyAsync($"{Program.TournamentName}: jowjowosu vs methalox", true);
        };

        await client.ConnectAsync();
    }

    private async Task HandleIrcMessage(IIrcMessage msg)
    {
        string prefix = msg.Prefix.StartsWith(":") ? msg.Prefix[1..] : msg.Prefix;
        string senderNick = prefix.Contains('!') ? prefix.Split('!')[0] : prefix;

        string target = msg.Parameters[0];
        string content = msg.Parameters[1];

        Console.WriteLine($"{senderNick}: {content}");

        if (joined) msgCallback(matchId, $"**[{senderNick}]** {content}");

        switch (senderNick)
        {
            case "BanchoBot" when content.Contains("Created the tournament match"):
                var parts = content.Split('/');
                var idPart = parts.Last().Split(' ')[0];
                LobbyChannelName = $"#mp_{idPart}";

                await client.JoinChannelAsync(LobbyChannelName);
                await InitializeLobbySettings();
                joined = true;
                return;
            case "BanchoBot" when content.Contains("Closed the match"):
                await client.DisconnectAsync();
                break;
            case "BanchoBot" when chatResponseTcs != null && SearchKeywords(content):
                chatResponseTcs.TrySetResult(content);
                chatResponseTcs = null;
                break;
        }

        if (senderNick == "BanchoBot") TryStateChange(content);

        // REGIÓN DEDICADA AL !PANIC. ESTÁ DESACOPLADA DEL RESTO POR SER UN CASO DE EMERGENCIA
        // QUE NO DEBERÍA CAER EN NINGUNA OTRA SUBRUTINA

        if (content.Contains("!panic_over"))
        {
            await SendMessageBothWays($"Going back to auto mode. Starting soon...");
            state = MatchState.WaitingForStart;
            await SendMessageBothWays("!mp timer 10");
        }
        else if (content.Contains("!panic"))
        {
            state = MatchState.MatchOnHold;
            await SendMessageBothWays("!mp aborttimer");
            await SendMessageBothWays($"<@&{Environment.GetEnvironmentVariable("DISCORD_REFEREE_ROLE_ID")}>, {senderNick} has requested human intervention. Auto mode has been disabled, resume it with !panic_over");
        }

        if (content.StartsWith('>'))
        {
            await ExecuteAdminCommand(content[1..].Split(' '));
        }
    }

    public async Task SendMessageFromDiscord(string content)
    {
        await client.SendPrivateMessageAsync(LobbyChannelName, content);
    }

    private async Task InitializeLobbySettings()
    {
        if (currentMatch.Type == Models.MatchType.QualifiersStage)
        {
            await client.SendPrivateMessageAsync(LobbyChannelName, "!mp set 0 3 16");
        }
        else
        {
            await client.SendPrivateMessageAsync(LobbyChannelName, "!mp set 2 3 3");
        }

        await client.SendPrivateMessageAsync(LobbyChannelName, "!mp invite " + currentMatch.Referee.DisplayName);
        //TODO addrefs streamers
    }

    private async Task PrintScore()
    {
        string scoreMsg = $"{currentMatch.TeamRed.DisplayName} {matchScore[0]} -- {matchScore[1]} {currentMatch.TeamBlue.DisplayName}";
        await client.SendPrivateMessageAsync(LobbyChannelName, scoreMsg);
    }

    private async Task ExecuteAdminCommand(string[] args)
    {
        Console.WriteLine("admin command ejecutando");

        switch (args[0].ToLower())
        {
            case "close":
                await SendMessageBothWays("!mp close");
                break;
            case "invite":
                await SendMessageBothWays($"!mp invite {currentMatch.TeamRed.DisplayName}");
                await SendMessageBothWays($"!mp invite {currentMatch.TeamBlue.DisplayName}");
                break;
            case "start":
                if (currentMatch.Type == Models.MatchType.QualifiersStage)
                {
                    await SendMessageBothWays($"Engaging autoreferee mode for Qualifiers, Lobby {currentMatch.Id}. Use '!panic' if you need human intervention and a referee will get back to you ASAP");
                    StartQualifiersFlow();
                }
                else
                {
                    await SendMessageBothWays($"Engaging autoreferee mode for Elimination Stage, Lobby {currentMatch.Id}");
                }
                break;
        }
    }

    private async Task SendMessageBothWays(string content)
    {
        await client.SendPrivateMessageAsync(LobbyChannelName, content);
        msgCallback(matchId, $"**[AUTO | {currentMatch.Referee.DisplayName}]** {content}");
    }

    private async Task WaitForResponseAsync(string keyword)
    {
        chatResponseTcs = new TaskCompletionSource<string>();

        var ct = new CancellationTokenSource(TimeSpan.FromMinutes(2)).Token;
        using (ct.Register(() => chatResponseTcs.TrySetCanceled()))
        {
            await chatResponseTcs.Task;
        }
    }

    private bool SearchKeywords(string content)
    {
        bool found = false;

        switch (content)
        {
            case var s when s.Contains("All players are ready"):
                found = true;
                break;
            case var s when s.Contains("Changed beatmap"):
                found = true;
                break;
            case var s when s.Contains("Enabled"):
                found = true;
                break;
            case var s when s.Contains("Countdown finished"):
                found = true;
                break;
        }

        return found;
    }

    private async Task TryStateChange(string banchoMsg) // transiciones de estado
    {
        if (state == MatchState.Idle) return;

        if (state == MatchState.WaitingForStart)
        {
            if (banchoMsg.Contains("All players are ready") || banchoMsg.Contains("Countdown finished"))
            {
                await SendMessageBothWays("!mp start 10");
                state = MatchState.Playing;
            }
        }
        else if (state == MatchState.Playing)
        {
            if (banchoMsg.Contains("The match has finished"))
            {
                if (currentMatch.Type == Models.MatchType.QualifiersStage)
                {
                    currentMapIndex++;
                    state = MatchState.Idle;

                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(10000);
                        await PrepareNextQualifierMap();
                    });

                }
                else // Elimination Stage
                {
                    //TODO enseñar scores
                    state = MatchState.Idle;
                }

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
        if (currentMapIndex >= currentMatch.Round.MapPool.Count)
        {
            await SendMessageBothWays("Qualifiers lobby finished. Thank you for playing!");
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