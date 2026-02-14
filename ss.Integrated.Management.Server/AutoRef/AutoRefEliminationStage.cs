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
    
    private bool joined = false;

    private bool redTimeoutRequest;
    private bool blueTimeoutRequest;

    private int repeat = 2;

    private TeamColor firstPick = TeamColor.None;
    private TeamColor firstBan = TeamColor.None;

    private List<Models.RoundChoice> bannedMaps = [];
    private List<Models.RoundChoice> pickedMaps = [];

    private Dictionary<string, int> currentMapScores = new(); // Nickname -> Score

    private TeamColor lastPick = TeamColor.None;

    private MatchState currentState;
    private MatchState previousState;

    private bool stoppedPreviously;

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
        OnTimeout,
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

            currentMatch.TeamRed = await db.Users.FirstAsync(u => u.Id == currentMatch.TeamRedId) ?? throw new Exception("Team red not found in the DB");
            currentMatch.TeamBlue = await db.Users.FirstAsync(u => u.Id == currentMatch.TeamBlueId) ?? throw new Exception("Team blue not found in the DB");

            currentMatch.Round = await db.Rounds.FirstAsync(r => r.Id == currentMatch.RoundId);
        }

        await ConnectToBancho();
    }

    public async Task StopAsync()
    {
        // Este método solo debería ser llamado desde DiscordManager, de lo contrario nos
        // metemos en camisa de once varas, cosa con la que tampoco quiero lidiar
        // TODO recheck si de verdad se guarda currentmatch

        await using var db = new ModelsContext();
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

        client.OnPrivateMessageSent += message =>
        {
            _ = PeruTrim(message);
        };

        client.OnAuthenticated += () =>
        {
            _ = client.MakeTournamentLobbyAsync($"{Program.TournamentName}: ({currentMatch.TeamRed.DisplayName}) vs ({currentMatch.TeamBlue.DisplayName})", true); // TODO poner en false
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

        // REGIÓN DEDICADA AL !PANIC. ESTÁ DESACOPLADA DEL RESTO POR SER UN CASO DE EMERGENCIA
        // QUE NO DEBERÍA CAER EN NINGUNA OTRA SUBRUTINA

        if (content.Contains("!panic_over"))
        {
            await SendMessageBothWays(Strings.BackToAuto);
            currentState = MatchState.WaitingForStart;
            await SendMessageBothWays("!mp timer 10");
        }
        else if (content.Contains("!panic"))
        {
            currentState = MatchState.MatchOnHold;
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

        await SendMessageBothWays(
            $"{currentMatch!.TeamRed.DisplayName} {matchScore[0]} - {matchScore[1]} {currentMatch!.TeamBlue.DisplayName} | Best of {currentMatch!.Round.BestOf}");
    }

    private async Task SendMatchStatus()
    {
        string bannedmaps = bannedMaps.Any() ? string.Join(", ", bannedMaps.Select(m => m.Slot)) : Strings.None;
        string pickedmaps = pickedMaps.Any() ? string.Join(", ", pickedMaps.Select(m => m.Slot)) : Strings.None;

        string availablemaps = string.Join(", ", currentMatch!.Round.MapPool
            .Where(m =>
                !pickedMaps.Any(p => p.Slot == m.Slot) &&
                !bannedMaps.Any(p => p.Slot == m.Slot))
            .Select(m => m.Slot));

        await SendMessageBothWays($"Bans: {bannedmaps} | Picks: {pickedmaps}");
        await Task.Delay(250);
        await SendMessageBothWays(string.Format(Strings.AvailableMaps, availablemaps));
        await Task.Delay(250);
        await SendMessageBothWays(string.Format(Strings.TimeoutAvailable, !redTimeoutRequest, !blueTimeoutRequest));
    }

    private async Task ExecuteAdminCommand(string sender, string[] args)
    {
        if (sender != currentMatch!.Referee.DisplayName.Replace(' ', '_')) return;

        switch (args[0].ToLower())
        {
            case "invite":
                await SendMessageBothWays($"!mp invite {currentMatch!.TeamRed.DisplayName.Replace(' ', '_')}");
                await Task.Delay(250);
                await SendMessageBothWays($"!mp invite {currentMatch!.TeamBlue.DisplayName.Replace(' ', '_')}");
                break;
            
            case "finish":
                await SendMessageBothWays("!mp close");
                await client!.DisconnectAsync();
                break;

            case "maps":
                await SendMatchStatus();
                break;
            
            case "setmap":
                if (currentState != MatchState.Idle)
                {
                    await SendMessageBothWays(Strings.SetMapFail);
                    break;
                }
                await PreparePick(args[1]);
                currentState = MatchState.Idle;
                break;
            
            case "timeout":
                await SendMessageBothWays(Strings.RefTimeout);
                await Task.Delay(250);
                previousState = currentState;
                currentState = MatchState.OnTimeout;
                break;

            case "start":
                if (firstPick == TeamColor.None || firstBan == TeamColor.None)
                {
                    await SendMessageFromDiscord(Strings.PropertiesNotInit);
                    return;
                }

                if (currentState != MatchState.Idle)
                {
                    await SendMessageBothWays(Strings.AutoAlreadyEngaged);
                    break;
                }
                
                if (!stoppedPreviously)
                {
                    await SendMessageBothWays(string.Format(Strings.EngagingAuto, currentMatch!.Id));
                    currentState = MatchState.BanPhaseStart;
                }
                else
                {
                    await SendMessageBothWays(string.Format(Strings.EngagingAuto, currentMatch!.Id));
                    currentState = previousState;
                    stoppedPreviously = false;
                }

                await TryStateChange("a", "a"); // esto es lo más peruano que he hecho pero adivina que, funciona
                break;

            case "stop":
                if (currentState == MatchState.Idle)
                {
                    await SendMessageBothWays(Strings.AutoAlreadyStopped);
                    break;
                }
                await SendMessageBothWays(Strings.StoppingAuto);
                previousState = currentState;
                currentState = MatchState.Idle;
                stoppedPreviously = true;
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
        await SendMessageBothWays("!mp timer 90");

        currentState = MatchState.WaitingForStart;
    }

    private async Task SendStateInfo(string info)
    {
        await SendMessageBothWays(info);
        await Task.Delay(250);
        await SendMatchStatus();
        await Task.Delay(250);
        await SendMessageBothWays("!mp timer 90");
    }

    private async Task TryStateChange(string sender, string content) // transiciones de estado
    {
        if (currentState == MatchState.Idle) return;

        #region TimeoutRegion

        if ((currentState == MatchState.WaitingForStart ||
             currentState == MatchState.WaitingForBanBlue ||
             currentState == MatchState.WaitingForBanRed ||
             currentState == MatchState.WaitingForPickBlue ||
             currentState == MatchState.WaitingForPickRed) && content == "!timeout")
        {
            if (sender == currentMatch!.TeamRed.DisplayName.Replace(' ', '_') && !redTimeoutRequest)
            {
                await SendMessageBothWays(Strings.RedTimeout);
                previousState = currentState;
                currentState = MatchState.OnTimeout;
                redTimeoutRequest = true;
            }
            else if (sender == currentMatch!.TeamBlue.DisplayName.Replace(' ', '_') && !blueTimeoutRequest)
            {
                await SendMessageBothWays(Strings.BlueTimeout);
                previousState = currentState;
                currentState = MatchState.OnTimeout;
                blueTimeoutRequest = true;
            }
            
            return;
        }

        if (currentState == MatchState.OnTimeout && sender == "BanchoBot" && content == "Countdown finished")
        {
            await SendMessageBothWays("!mp timer 120");
            await Task.Delay(250);
            await SendMessageBothWays(Strings.TimeoutStart);
            currentState = previousState;
            return;
        } 

        #endregion

        #region BanningPhaseRegion

        if (currentState == MatchState.BanPhaseStart)
        {
            if (firstBan == TeamColor.TeamRed)
            {
                await SendStateInfo(string.Format(Strings.BanCall, currentMatch!.TeamRed.DisplayName));
                currentState = MatchState.WaitingForBanRed;
            }
            else
            {
                await SendStateInfo(string.Format(Strings.BanCall, currentMatch!.TeamBlue.DisplayName));
                currentState = MatchState.WaitingForBanBlue;
            }
        }

        if (currentState == MatchState.WaitingForBanRed && sender == currentMatch!.TeamRed.DisplayName.Replace(' ', '_'))
        {
            if (IsMapAvailable(content))
            {
                bannedMaps.Add(new Models.RoundChoice { Slot = content.ToUpper(), TeamColor = Models.TeamColor.TeamRed });
                await SendMessageBothWays(string.Format(Strings.RedBanned, content.ToUpper()));
                await Task.Delay(250);
                repeat--;

                if (repeat == 0)
                {
                    currentState = MatchState.PickPhaseStart;
                    repeat = 2;
                }
                else
                {
                    currentState = MatchState.WaitingForBanBlue;
                    await SendMessageBothWays(string.Format(Strings.BanCall, currentMatch!.TeamBlue.DisplayName));
                }
            }

            return;
        }

        if (currentState == MatchState.WaitingForBanBlue && sender == currentMatch!.TeamBlue.DisplayName.Replace(' ', '_'))
        {
            if (IsMapAvailable(content))
            {
                bannedMaps.Add(new Models.RoundChoice { Slot = content.ToUpper(), TeamColor = Models.TeamColor.TeamBlue });
                await SendMessageBothWays(string.Format(Strings.BlueBanned, content.ToUpper()));
                await Task.Delay(250);
                repeat--;

                if (repeat == 0)
                {
                    currentState = MatchState.PickPhaseStart;
                    repeat = 2;
                }
                else
                {
                    currentState = MatchState.WaitingForBanRed;
                    await SendMessageBothWays(string.Format(Strings.BanCall, currentMatch!.TeamRed.DisplayName));
                }
            }

            return;
        }

        #endregion

        #region PickPhaseRegion

        if (currentState == MatchState.PickPhaseStart)
        {
            if (firstPick == TeamColor.TeamRed)
            {
                await SendStateInfo(string.Format(Strings.PickCall, currentMatch!.TeamRed.DisplayName));
                currentState = MatchState.WaitingForPickRed;
            }
            else
            {
                await SendStateInfo(string.Format(Strings.PickCall, currentMatch!.TeamBlue.DisplayName));
                currentState = MatchState.WaitingForPickBlue;
            }

            return;
        }

        if (currentState == MatchState.WaitingForPickRed && sender == currentMatch!.TeamRed.DisplayName.Replace(' ', '_'))
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

        if (currentState == MatchState.WaitingForPickBlue && sender == currentMatch!.TeamBlue.DisplayName.Replace(' ', '_'))
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

        if (currentState == MatchState.WaitingForStart)
        {
            if ((content.Contains("All players are ready") || content.Contains("Countdown finished")) && sender == "BanchoBot")
            {
                await SendMessageBothWays("!mp start 10");
                currentState = MatchState.Playing;
            }
        }
        else if (currentState == MatchState.Playing)
        {
            if (content.Contains("The match has finished"))
            {
                if (currentMatch!.Round.BanRounds == 2 && pickedMaps.Count == 4)
                {
                    currentState = MatchState.BanPhaseStart;
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
                        currentState = MatchState.MatchFinished;
                        return;
                    }

                    if (bluewin)
                    {
                        await SendMessageBothWays(string.Format(Strings.MatchWin, currentMatch!.TeamBlue.DisplayName));
                        currentState = MatchState.MatchFinished;
                        return;
                    }

                    if (lastPick == TeamColor.TeamRed)
                    {
                        await SendMessageBothWays(string.Format(Strings.PickCall, currentMatch!.TeamBlue.DisplayName));
                        await Task.Delay(250);
                        await SendMatchStatus();
                        currentState = MatchState.WaitingForPickBlue;
                    }
                    else
                    {
                        await SendMessageBothWays(string.Format(Strings.PickCall, currentMatch!.TeamRed.DisplayName));
                        await Task.Delay(250);
                        await SendMatchStatus();
                        currentState = MatchState.WaitingForPickRed;
                    }
                }
            }
        }
    }
}