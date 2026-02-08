using System.Text.RegularExpressions;
using BanchoSharp;
using BanchoSharp.Interfaces;
using Microsoft.EntityFrameworkCore;
using ss.Internal.Management.Server.Resources;

namespace ss.Internal.Management.Server.AutoRef;

public partial class AutoRefEliminationStage : IAutoRef
{
    private Models.MatchRoom? currentMatch;
    private readonly string matchId;
    private readonly string refDisplayName;

    private IBanchoClient? client;
    private string? lobbyChannelName;

    private int[] matchScore = [0, 0];
    private bool auto = false;
    private bool joined = false;

    private int repeat = 2;

    private TeamColor firstPick = TeamColor.None;
    private TeamColor firstBan = TeamColor.None;

    private List<Models.RoundChoice> bannedMaps = [];
    private List<Models.RoundChoice> pickedMaps = [];

    private Dictionary<string, int> currentMapScores = new(); // Nickname -> Score

    private TeamColor lastPick = TeamColor.None;

    private MatchState state;

    private TaskCompletionSource<string>? chatResponseTcs;

    private readonly Action<string, string> msgCallback;

    public enum TeamColor
    {
        TeamBlue,
        TeamRed,
        None,
    }

    public enum MatchState
    {
        Idle,
        BanPhaseStart,
        WaitingForBanRed,
        WaitingForBanBlue,
        PickPhaseStart,
        WaitingForPickRed,
        WaitingForPickBlue,
        WaitingForStart,
        Playing,
        MatchFinished,
        MatchOnHold,
    }

    public AutoRefEliminationStage(string matchId, string refDisplayName, Action<string, string> msgCallback)
    {
        this.matchId = matchId;
        this.refDisplayName = refDisplayName;
        this.msgCallback = msgCallback;
    }

    public async Task StartAsync()
    {
        await using (var db = new ModelsContext())
        {
            currentMatch = await db.MatchRooms.FirstAsync(m => m.Id == matchId) ?? throw new Exception("Match not found in the DB");
            currentMatch.Referee = await db.Referees.FirstAsync(r => r.DisplayName == refDisplayName) ?? throw new Exception("Referee not found in the DB");

            currentMatch.TeamRed = await db.Users.FirstAsync(u => u.Id == currentMatch.TeamRedId);
            currentMatch.TeamBlue = await db.Users.FirstAsync(u => u.Id == currentMatch.TeamBlueId);

            currentMatch.Round = await db.Rounds.FirstAsync(r => r.Id == currentMatch.RoundId);
        }

        await ConnectToBancho();
    }

    public async Task StopAsync()
    {
        await using var db = new ModelsContext();
        // TODO recheck si de verdad se guarda currentmatch
        currentMatch!.BannedMaps = bannedMaps;
        currentMatch!.PickedMaps = pickedMaps;

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

        client.OnAuthenticated += () =>
        {
            _ = client.MakeTournamentLobbyAsync($"{Program.TournamentName}: jowjowosu vs methalox", false);
        };

        await client.ConnectAsync();
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

        if (senderNick == "BanchoBot")
        {
            if (content.Contains("finished playing"))
            {
                // Regex para extraer Nick y Score
                var match = Regex.Match(content, @"^(.*) finished playing \(Score: (\d+),");

                if (match.Success)
                {
                    string nick = match.Groups[1].Value;
                    int score = int.Parse(match.Groups[2].Value);
                    currentMapScores[nick] = score;
                }
            }

            if (content.Contains("The match has finished!"))
            {
                await ProcessFinalScores();
            }

            await Task.Delay(250);
        }

        ;

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
                string.Format(Strings.Panic, Environment.GetEnvironmentVariable("DISCORD_REFEREE_ROLE_ID"), senderNick));
        }

        _ = TryStateChange(senderNick, content);

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
        await client!.SendPrivateMessageAsync(lobbyChannelName!, "!mp set 2 3 3");
        await client!.SendPrivateMessageAsync(lobbyChannelName!, "!mp invite " + currentMatch!.Referee.DisplayName.Replace(' ', '_'));
    }

    private async Task ProcessFinalScores()
    {
        long redTotal = 0;
        long blueTotal = 0;

        foreach (var player in currentMapScores)
        {
            if (player.Key.Equals(currentMatch!.TeamRed.DisplayName, StringComparison.OrdinalIgnoreCase))
                redTotal += player.Value;
            else if (player.Key.Equals(currentMatch.TeamBlue.DisplayName, StringComparison.OrdinalIgnoreCase))
                blueTotal += player.Value;
        }

        if (redTotal > blueTotal)
        {
            matchScore[0]++;
            await SendMessageBothWays(string.Format(Strings.RedWins, redTotal, blueTotal));
        }
        else
        {
            matchScore[1]++;
            await SendMessageBothWays(string.Format(Strings.BlueWins, blueTotal, redTotal));
        }

        currentMapScores.Clear();

        await SendMessageBothWays($"{currentMatch!.TeamRed.DisplayName} {matchScore[0]} - {matchScore[1]} {currentMatch!.TeamBlue.DisplayName} | Best of {currentMatch!.Round.BestOf}");
    }

    private async Task SendMatchStatus()
    {
        string bannedmaps = bannedMaps.Any() ? string.Join(", ", bannedMaps.Select(m => m.Slot)) : "None";
        string pickedmaps = pickedMaps.Any() ? string.Join(", ", pickedMaps.Select(m => m.Slot)) : "None";
        string availablemaps = string.Join(", ", currentMatch!.Round.MapPool
            .Where(m =>
                !pickedMaps.Any(p => p.Slot == m.Slot) &&
                !bannedMaps.Any(p => p.Slot == m.Slot))
            .Select(m => m.Slot));

        await SendMessageBothWays($"Bans: {bannedmaps} | Picks: {pickedmaps}");
        await Task.Delay(250);
        await SendMessageBothWays($"Available: {availablemaps}");
    }

    private async Task ExecuteAdminCommand(string sender, string[] args)
    {
        Console.WriteLine("admin command is being executed");

        if (sender != currentMatch!.Referee.DisplayName.Replace(' ', '_')) return;

        switch (args[0].ToLower())
        {
            case "close":
                await SendMessageBothWays("!mp close");
                break;

            case "panic_abort":
                await SendMessageBothWays("!mp close");
                await client!.DisconnectAsync();
                break;

            case "invite":
                await SendMessageBothWays($"!mp invite {currentMatch!.TeamRed.DisplayName.Replace(' ', '_')}");
                await Task.Delay(250);
                await SendMessageBothWays($"!mp invite {currentMatch!.TeamBlue.DisplayName.Replace(' ', '_')}");
                break;
            
            case "maps":
                await SendMatchStatus();
                break;

            case "start":
                if (firstPick == TeamColor.None || firstBan == TeamColor.None)
                {
                    await SendMessageFromDiscord(Strings.PropertiesNotInit);
                    return;
                }

                await SendMessageBothWays(string.Format(Strings.EngagingAuto, currentMatch!.Id));
                state = MatchState.BanPhaseStart;
                auto = true;
                break;

            case "firstpick":
                if (args.Length > 1)
                {
                    firstPick = args[1] == "red" ? TeamColor.TeamRed : TeamColor.TeamBlue;
                    await SendMessageBothWays(Strings.SuccessfulFirstPick);
                }
                else
                {
                    await SendMessageBothWays(Strings.NotEnoughArgs);
                }

                break;

            case "firstban":
                if (args.Length > 1)
                {
                    firstBan = args[1] == "red" ? TeamColor.TeamRed : TeamColor.TeamBlue;
                    await SendMessageBothWays(Strings.SuccessfulFirstBan);
                }
                else
                {
                    await SendMessageBothWays(Strings.NotEnoughArgs);
                }

                break;
        }
    }

    private async Task SendMessageBothWays(string content)
    {
        await client!.SendPrivateMessageAsync(lobbyChannelName!, content);
        msgCallback(matchId, $"**[AUTO | {currentMatch!.Referee.DisplayName}]** {content}");
    }

    private async Task WaitForResponseAsync(string keyword)
    {
        chatResponseTcs = new TaskCompletionSource<string>();

        var ct = new CancellationTokenSource(TimeSpan.FromMinutes(2)).Token;

        await using (ct.Register(() => chatResponseTcs.TrySetCanceled()))
        {
            await chatResponseTcs.Task;
        }
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

    private bool IsMapAvailable(string content)
    {
        // Un mapa a la hora de pickearse debería cumplir siempre las siguientes condiciones:
        // - Debe existir en la pool actual
        // - No debe estar baneado previamente
        // - No debe estar pickeado previamente
        // - No puede ser el Tiebreaker (esto lo manejamos por otro lado)
        bool canAdd = currentMatch!.Round.MapPool.Find(beatmap => beatmap.Slot == content.ToUpper()) != null &&
                      bannedMaps.Find(beatmap => beatmap.Slot == content.ToUpper()) == null &&
                      pickedMaps.Find(beatmap => beatmap.Slot == content.ToUpper()) == null &&
                      content.ToUpper() != "TB1";

        return canAdd;
    }

    private async Task PreparePick(string slot)
    {
        var beatmap = currentMatch!.Round.MapPool.Find(b => b.Slot == slot);

        await SendMessageBothWays($"!mp map {beatmap!.BeatmapID}");
        await Task.Delay(250);
        await SendMessageBothWays($"!mp mods {slot[..2]} NF");
        await Task.Delay(250);
        await SendMessageBothWays("!mp timer 120");

        state = MatchState.WaitingForStart;
    }

    private async Task TryStateChange(string sender, string content) // transiciones de estado
    {
        if (state == MatchState.Idle) return;

        #region BanningPhaseRegion

        if (state == MatchState.BanPhaseStart)
        {
            if (firstBan == TeamColor.TeamRed)
            {
                await SendMessageBothWays(string.Format(Strings.BanCall, currentMatch!.TeamRed.DisplayName));
                await Task.Delay(250);
                await SendMatchStatus();
                state = MatchState.WaitingForBanRed;
            }
            else
            {
                await SendMessageBothWays(string.Format(Strings.BanCall, currentMatch!.TeamBlue.DisplayName));
                await Task.Delay(250);
                await SendMatchStatus();
                state = MatchState.WaitingForBanBlue;
            }
        }

        if (state == MatchState.WaitingForBanRed && sender == currentMatch!.TeamRed.DisplayName.Replace(' ', '_'))
        {
            if (IsMapAvailable(content))
            {
                bannedMaps.Add(new Models.RoundChoice { Slot = content.ToUpper(), TeamColor = Models.TeamColor.TeamRed });
                await SendMessageBothWays(string.Format(Strings.RedBanned, content.ToUpper()));
                await Task.Delay(250);
                repeat--;

                if (repeat == 0)
                {
                    state = MatchState.PickPhaseStart;
                    repeat = 2;
                }
                else
                {
                    state = MatchState.WaitingForBanBlue;
                    await SendMessageBothWays(string.Format(Strings.BanCall, currentMatch!.TeamBlue.DisplayName));
                    await Task.Delay(250);
                    await SendMatchStatus();
                }
            }

            return;
        }

        if (state == MatchState.WaitingForBanBlue && sender == currentMatch!.TeamBlue.DisplayName.Replace(' ', '_'))
        {
            if (IsMapAvailable(content))
            {
                bannedMaps.Add(new Models.RoundChoice { Slot = content.ToUpper(), TeamColor = Models.TeamColor.TeamBlue });
                await SendMessageBothWays(string.Format(Strings.BlueBanned, content.ToUpper()));
                await Task.Delay(250);
                repeat--;

                if (repeat == 0)
                {
                    state = MatchState.PickPhaseStart;
                    repeat = 2;
                }
                else
                {
                    state = MatchState.WaitingForBanRed;
                    await SendMessageBothWays(string.Format(Strings.BanCall, currentMatch!.TeamRed.DisplayName));
                    await Task.Delay(250);
                    await SendMatchStatus();
                }
            }

            return;
        }

        #endregion

        #region PickPhaseRegion

        if (state == MatchState.PickPhaseStart)
        {
            if (firstPick == TeamColor.TeamRed)
            {
                await SendMessageBothWays(string.Format(Strings.PickCall, currentMatch!.TeamRed.DisplayName));
                await Task.Delay(250);
                await SendMatchStatus();
                state = MatchState.WaitingForPickRed;
            }
            else
            {
                await SendMessageBothWays(string.Format(Strings.PickCall, currentMatch!.TeamBlue.DisplayName));
                await Task.Delay(250);
                await SendMatchStatus();
                state = MatchState.WaitingForPickBlue;
            }

            return;
        }

        if (state == MatchState.WaitingForPickRed && sender == currentMatch!.TeamRed.DisplayName.Replace(' ', '_'))
        {
            if (IsMapAvailable(content))
            {
                pickedMaps.Add(new Models.RoundChoice { Slot = content.ToUpper(), TeamColor = Models.TeamColor.TeamRed });
                await SendMessageBothWays(string.Format(Strings.RedPicked, content.ToUpper()));
                await PreparePick(content.ToUpper());
                lastPick = TeamColor.TeamRed;
            }

            return;
        }

        if (state == MatchState.WaitingForPickBlue && sender == currentMatch!.TeamBlue.DisplayName.Replace(' ', '_'))
        {
            if (IsMapAvailable(content))
            {
                pickedMaps.Add(new Models.RoundChoice { Slot = content.ToUpper(), TeamColor = Models.TeamColor.TeamBlue });
                await SendMessageBothWays(string.Format(Strings.BluePicked, content.ToUpper()));
                await PreparePick(content.ToUpper());
                lastPick = TeamColor.TeamBlue;
            }

            return;
        }

        #endregion

        if (state == MatchState.WaitingForStart)
        {
            if ((content.Contains("All players are ready") || content.Contains("Countdown finished")) && sender == "BanchoBot")
            {
                await SendMessageBothWays("!mp start 10");
                state = MatchState.Playing;
            }
        }
        else if (state == MatchState.Playing)
        {
            if (content.Contains("The match has finished"))
            {
                if (currentMatch!.Round.BanRounds == 2 && pickedMaps.Count == 4)
                {
                    state = MatchState.BanPhaseStart;
                    await SendMessageBothWays(Strings.SecondBanRound);
                }
                else
                {
                    if (pickedMaps.Count == currentMatch.Round.BestOf - 1)
                    {
                        await PreparePick("TB1");
                        return;
                    }

                    bool redwin = matchScore[0] == (currentMatch.Round.BestOf - 1) / 2 + 1;
                    bool bluewin = matchScore[1] == (currentMatch.Round.BestOf - 1) / 2 + 1;

                    if (redwin)
                    {
                        await SendMessageBothWays(string.Format(Strings.MatchWin, currentMatch!.TeamRed.DisplayName));
                        state = MatchState.MatchFinished;
                        return;
                    }

                    if (bluewin)
                    {
                        await SendMessageBothWays(string.Format(Strings.MatchWin, currentMatch!.TeamBlue.DisplayName));
                        state = MatchState.MatchFinished;
                        return;
                    }

                    if (lastPick == TeamColor.TeamRed)
                    {
                        await SendMessageBothWays(string.Format(Strings.PickCall, currentMatch!.TeamBlue.DisplayName));
                        await Task.Delay(250);
                        await SendMatchStatus();
                        state = MatchState.WaitingForPickBlue;
                    }
                    else
                    {
                        await SendMessageBothWays(string.Format(Strings.PickCall, currentMatch!.TeamRed.DisplayName));
                        await Task.Delay(250);
                        await SendMatchStatus();
                        state = MatchState.WaitingForPickRed;
                    }
                }
            }
        }
    }
}