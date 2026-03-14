using System.Text.RegularExpressions;
using BanchoSharp;
using BanchoSharp.Interfaces;
using Microsoft.EntityFrameworkCore;
using ss.Internal.Management.Server.AutoRef;
using ss.Internal.Management.Server.Resources;

namespace ss.Internal.Management.Server.MatchManager;

/// <summary>
/// Handles the automated refereeing logic for Elimination/Versus matches.
/// Implements a complex State Machine to manage Picks, Bans, Timeouts, and Scoring.
/// </summary>
/// <remarks>
/// ## State Transition Diagram
/// \dot
/// digraph MatchStateMachine {
///     // Graph Style
///     graph [fontname = "helvetica", fontsize = 10, nodesep = 0.5, ranksep = 0.7, compound=true];
///     node [fontname = "helvetica", fontsize = 10, shape = box, style = rounded];
///     edge [fontname = "helvetica", fontsize = 8];
///
///     // Nodes
///     Idle [style="filled,rounded", fillcolor="#EEEEEE"];
///     BanPhase [label="BanPhaseStart\n(Decide who bans first)"];
///     WaitBanRed [label="WaitingForBanRed"];
///     WaitBanBlue [label="WaitingForBanBlue"];
///     PickPhase [label="PickPhaseStart\n(Decide who picks first)"];
///     WaitPickRed [label="WaitingForPickRed"];
///     WaitPickBlue [label="WaitingForPickBlue"];
///     WaitStart [label="WaitingForStart\n(Timer Active)"];
///     Playing [label="Playing\n(Waiting for results)", style="filled,rounded", fillcolor="#D4E6F1"];
///     Finish [label="MatchFinished", style="filled,rounded", fillcolor="#D5F5E3"];
///     Timeout [label="OnTimeout\n(Paused)", shape=octagon, style=filled, fillcolor="#FADBD8"];
///     Panic [label="MatchOnHold\n(PANIC)", shape=doubleoctagon, style=filled, fillcolor="#E74C3C", fontcolor="white"];
///
///     // Initialization
///     Idle -> BanPhase [label="!start"];
///     
///     // Banning Logic
///     BanPhase -> WaitBanRed [label="FirstBan = Red"];
///     BanPhase -> WaitBanBlue [label="FirstBan = Blue"];
///     
///     WaitBanRed -> WaitBanBlue [label="Red Bans"];
///     WaitBanBlue -> WaitBanRed [label="Blue Bans"];
///     
///     WaitBanRed -> PickPhase [label="Bans Done"];
///     WaitBanBlue -> PickPhase [label="Bans Done"];
///
///     // Picking Logic
///     PickPhase -> WaitPickRed [label="FirstPick = Red"];
///     PickPhase -> WaitPickBlue [label="FirstPick = Blue"];
///     
///     WaitPickRed -> WaitStart [label="Red Pick\n(Prepare Map)"];
///     WaitPickBlue -> WaitStart [label="Blue Pick\n(Prepare Map)"];
///
///     // Gameplay Loop
///     WaitStart -> Playing [label="Players Ready\n(Match Start)"];
///     
///     Playing -> WaitPickBlue [label="Map finished playing\n(Next Pick Blue)"];
///     Playing -> WaitPickRed [label="Map finished playing\n(Next Pick Red)"];
///     
///     // Special Cases
///     Playing -> BanPhase [label="Double Ban Round\n(Mid-Match)"];
///     Playing -> WaitStart [label="Teams are both at match point; Tiebreaker"];
///     Playing -> Finish [label="Win Condition Met for any of the teams"];
///
///     // Timeout System
///     {WaitPickRed WaitPickBlue WaitStart} -> Timeout [label="!timeout"];
///     Timeout -> WaitPickRed [label="Resume"];
///     Timeout -> WaitPickBlue [label="Resume"];
///     Timeout -> WaitStart [label="Resume"];
///
///     // PANIC SYSTEM (Global Interrupt)
///     // Can be triggered from any active waiting/playing state
///     {WaitBanRed WaitBanBlue WaitPickRed WaitPickBlue WaitStart Playing} -> Panic [label="!panic\n(ANYONE)", color="#E74C3C", fontcolor="#E74C3C"];
///     
///     // Panic Recovery (Ref Only -> Resets to WaitingForStart)
///     Panic -> WaitStart [label="!panic_over\n(REF ONLY)", color="#27AE60", fontcolor="#27AE60", penwidth=2];
/// }
/// \enddot
/// </remarks>
public partial class MatchManagerEliminationStage : IMatchManager
{
    internal Models.MatchRoom? currentMatch;
    private readonly string matchId;
    private readonly string refDisplayName;

    internal IBanchoClient? client;
    internal string? lobbyChannelName;

    /// <inheritdoc />
    public event Func<string, Task>? OnStateUpdated;
    
    private record MatchSnapshot(Models.MatchRoom Room, IMatchManager.MatchState State, Models.TeamColor LastPick);

    private readonly Stack<MatchSnapshot> matchHistory = new();

    private int[] matchScore = [0, 0];
    public int[] MatchScore => matchScore;
    
    internal bool joined = false;
    private bool isStolenPick = false;

    private bool redTimeoutRequest;
    private bool blueTimeoutRequest;

    private int mpLinkId;

    private int mapsLeftToBan = 2;

    // Using Models.TeamColor to avoid ambiguity in Doxygen
    private Models.TeamColor firstPick = Models.TeamColor.None;
    private Models.TeamColor firstBan = Models.TeamColor.None;

    internal List<Models.RoundChoice> bannedMaps = [];
    internal List<Models.RoundChoice> pickedMaps = [];

    private Dictionary<string, int> currentMapScores = new(); // Nickname -> Score

    private Models.TeamColor lastPick = Models.TeamColor.None;

    internal IMatchManager.MatchState currentState;
    internal OperationMode currentMode;
    private string? currentBeatmapSlot;
    
    private IMatchManager.MatchState previousState;

    private int refId;

    private bool stoppedPreviously;

    /// <summary>
    /// The delay used between messages when sending them to bancho
    /// </summary>
    public static readonly int IrcMessageDelay = 250;
    
    private string RedIrcName => currentMatch!.TeamRed.DisplayName.Replace(' ', '_');
    private string BlueIrcName => currentMatch!.TeamBlue.DisplayName.Replace(' ', '_');

    private TaskCompletionSource<string>? chatResponseTcs;

    private readonly Action<string, string, IMatchManager.MessageKind> msgCallback;

    public enum OperationMode
    {
        Automatic,
        Assisted
    }

    public MatchManagerEliminationStage(string matchId, string refDisplayName, Action<string, string, IMatchManager.MessageKind> msgCallback)
    {
        this.matchId = matchId;
        this.refDisplayName = refDisplayName;
        this.msgCallback = msgCallback;
    }
    
    /// <inheritdoc />
    public async Task StartAsync()
    {
        await using (var db = new ModelsContext())
        {
            currentMatch = await db.MatchRooms.FirstAsync(m => m.Id == matchId) ?? throw new Exception("Match not found in the DB");
            currentMatch.Referee = await db.Referees.FirstAsync(r => r.DisplayName == refDisplayName) ?? throw new Exception("Referee not found in the DB");

            currentMatch.TeamRed = await db.Users.Include(u => u.OsuData).FirstAsync(u => u.Id == currentMatch.TeamRedId) ?? throw new Exception("Team red not found in the DB");
            currentMatch.TeamBlue = await db.Users.Include(u => u.OsuData).FirstAsync(u => u.Id == currentMatch.TeamBlueId) ?? throw new Exception("Team blue not found in the DB");

            currentMatch.Round = await db.Rounds.FirstAsync(r => r.Id == currentMatch.RoundId);
        }

        await ConnectToBancho();
    }

    /// <inheritdoc />
    public async Task StopAsync()
    {
        await using var db = new ModelsContext();

        await db.MatchRooms
            .Where(m => m.Id == matchId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.MpLinkId, mpLinkId)
                .SetProperty(m => m.EndTime, DateTime.UtcNow)
                .SetProperty(m => m.TeamRedScore, matchScore[0])
                .SetProperty(m => m.TeamBlueScore, matchScore[1])
                .SetProperty(m => m.RefereeId, currentMatch!.Referee.Id)
            );
        
        var match = await db.MatchRooms.FirstOrDefaultAsync(m => m.Id == matchId);
        match!.PickedMaps = pickedMaps;
        match!.BannedMaps = bannedMaps;

        await db.SaveChangesAsync();
        
        await SendMessageBothWays("!mp close");
        await client!.DisconnectAsync();
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
            _ = client.MakeTournamentLobbyAsync($"{Program.TournamentName}: ({currentMatch.TeamRed.DisplayName}) vs ({currentMatch.TeamBlue.DisplayName})", false);
        };

        await client.ConnectAsync();
    }

    /// <summary>
    /// Sanitizes and parses outgoing private messages to intercept command usage.
    /// </summary>
    /// <remarks>
    /// "PeruTrim" -> Legacy name. Handles raw IRC string manipulation to extract clean content.
    /// </remarks>
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

    /// <summary>
    /// The core event loop. Intercepts every message in the IRC channel to drive the State Machine.
    /// </summary>
    internal async Task HandleIrcMessage(IIrcMessage msg)
    {
        string prefix = msg.Prefix.StartsWith(":") ? msg.Prefix[1..] : msg.Prefix;
        string senderNick = prefix.Contains('!') ? prefix.Split('!')[0] : prefix;

        string target = msg.Parameters[0];
        string content = msg.Parameters[1];

        if (joined)
        {
            if(target == lobbyChannelName) msgCallback(matchId, $"**[{senderNick}]** {content}", IMatchManager.MessageKind.PlayerMessage);
        }

        // 1. System Events (Lobby creation/closure)
        switch (senderNick)
        {
            case "BanchoBot" when content.Contains("Created the tournament match"):
                var parts = content.Split('/');
                var idPart = parts.Last().Split(' ')[0];
                lobbyChannelName = $"#mp_{idPart}";

                mpLinkId = int.Parse(idPart);
                currentMatch!.MpLinkId = mpLinkId;

                await client!.JoinChannelAsync(lobbyChannelName);
                await InitializeLobbySettings();
                joined = true;
                msgCallback(matchId, $"mp link: https://osu.ppy.sh/mp/{mpLinkId}", IMatchManager.MessageKind.PinOrderMessage);
                return;
            case "BanchoBot" when chatResponseTcs != null && SearchKeywords(content):
                chatResponseTcs.TrySetResult(content);
                chatResponseTcs = null;
                break;
        }

        // 2. Gameplay Events (Score processing & Match finish)
        if (senderNick == "BanchoBot")
        {
            if (content.Contains("finished playing") && currentState != IMatchManager.MatchState.Idle)
            {
                // Regex for Nick and Score
                var match = Regex.Match(content, @"^(.*) finished playing \(Score: (\d+),");

                if (match.Success)
                {
                    string nick = match.Groups[1].Value;
                    int score = int.Parse(match.Groups[2].Value);
                    currentMapScores[nick] = score;
                }
            }

            if (content.Contains("The match has finished!") && currentState != IMatchManager.MatchState.Idle)
            {
                await ProcessFinalScores();
            }
            
            await Task.Delay(IrcMessageDelay);
        }

        // 3. Emergency Protocols (!panic)
        // Decoupled from the main State Machine to ensure it works regardless of the current state.
        if (content.Contains(">panic_over") && senderNick == currentMatch!.Referee.DisplayName.Replace(' ', '_'))
        {
            await SendMessageBothWays(Strings.BackToAuto);
            await ChangeState(IMatchManager.MatchState.WaitingForStart);
            await SendMessageBothWays("!mp timer 10");
        }
        else if (content.Contains("!panic"))
        {
            await ChangeState(IMatchManager.MatchState.MatchOnHold);
            await SendMessageBothWays("!mp aborttimer");

            await SendMessageBothWays(
                string.Format(Strings.Panic, Environment.GetEnvironmentVariable("DISCORD_REFEREE_ROLE_ID"), senderNick));
        }

        // 4. Drive the State Machine
        await TryStateChange(senderNick, content);
        
        // 5. Admin Commands
        if (content.StartsWith('>'))
        {
            await ExecuteAdminCommand(senderNick, content[1..].Split(' '));
        }
    }

    /// <inheritdoc />
    public async Task SendMessageFromDiscord(string content)
    {
        await client!.SendPrivateMessageAsync(lobbyChannelName!, content);
    }

    private async Task InitializeLobbySettings()
    {
        // Hardcoded Settings: TeamMode=2 (TeamVs), WinCondition=3 (ScoreV2), Slots=3
        await client!.SendPrivateMessageAsync(lobbyChannelName!, "!mp set 2 3 3");
        await client!.SendPrivateMessageAsync(lobbyChannelName!, "!mp invite " + currentMatch!.Referee.DisplayName.Replace(' ', '_'));
        await SendMessageBothWays($"Join this match via an IRC app with this command: \n- `/join {lobbyChannelName}`");
    }

    /// <summary>
    /// Aggregates individual scores from the dictionary and determines the point winner.
    /// </summary>
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
            switch (currentMode)
            {
                case OperationMode.Automatic:
                    matchScore[0]++;
                    await SendMessageBothWays(string.Format(Strings.RedWins, redTotal, blueTotal));
                    var winnedMap = pickedMaps.Find(c => c.Slot == currentBeatmapSlot);
                    if (winnedMap != null) winnedMap.Winner = Models.TeamColor.TeamRed;
                    await Task.Delay(IrcMessageDelay);
                    await SendMessageBothWays($"{currentMatch!.TeamRed.DisplayName} {matchScore[0]} - {matchScore[1]} {currentMatch!.TeamBlue.DisplayName} | Best of {currentMatch!.Round.BestOf}");
                    break;
                
                case OperationMode.Assisted:
                    msgCallback(matchId, $"**[RESULT] {string.Format(Strings.RedWins, redTotal, blueTotal)}**", IMatchManager.MessageKind.PlayerMessage);
                    break;
            }
        }
        else
        {
            switch (currentMode)
            {
                case OperationMode.Automatic:
                    matchScore[1]++;
                    await SendMessageBothWays(string.Format(Strings.BlueWins, blueTotal, redTotal));
                    var winnedMap = pickedMaps.Find(c => c.Slot == currentBeatmapSlot);
                    if (winnedMap != null) winnedMap.Winner = Models.TeamColor.TeamBlue;
                    await Task.Delay(IrcMessageDelay);
                    await SendMessageBothWays($"{currentMatch!.TeamRed.DisplayName} {matchScore[0]} - {matchScore[1]} {currentMatch!.TeamBlue.DisplayName} | Best of {currentMatch!.Round.BestOf}");
                    break;
                
                case OperationMode.Assisted:
                    msgCallback(matchId, $"**[RESULT] {string.Format(Strings.BlueWins, redTotal, blueTotal)}**", IMatchManager.MessageKind.PlayerMessage);
                    break;
            }
        }

        currentMapScores.Clear();
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
        await Task.Delay(IrcMessageDelay);
        await SendMessageBothWays(string.Format(Strings.AvailableMaps, availablemaps));
        await Task.Delay(IrcMessageDelay);
        await SendMessageBothWays(string.Format(Strings.TimeoutAvailable, !redTimeoutRequest, !blueTimeoutRequest));
    }

    /// <summary>
    /// Processes commands issued by the Referee (via IRC or Discord).
    /// Prefix: '>'
    /// </summary>
    private async Task ExecuteAdminCommand(string sender, string[] args)
    {
        if (sender != currentMatch!.Referee.DisplayName.Replace(' ', '_')) return;

        switch (args[0].ToLower())
        {
            case "invite":
                await SendMessageBothWays($"!mp invite #{currentMatch!.TeamRed.OsuData.Id}");
                await Task.Delay(IrcMessageDelay);
                await SendMessageBothWays($"!mp invite #{currentMatch!.TeamBlue.OsuData.Id}");
                break;
            
            case "finish":
                await SendMessageBothWays("!mp close");
                break;

            case "maps":
                await SendMatchStatus();
                break;
            
            case "setmap":
                if (currentState != IMatchManager.MatchState.Idle)
                {
                    await SendMessageBothWays(Strings.SetMapFail);
                    break;
                }
                previousState =  currentState;
                await PreparePick(args[1]);
                currentState = previousState;
                break;
            
            case "setscore":
                if (args.Length < 3)
                {
                    await SendMessageBothWays(Strings.NotEnoughArgs);
                    break;
                }

                if (int.TryParse(args[1], out int scoreRed) && int.TryParse(args[2], out int scoreBlue))
                {
                    matchScore[0] = scoreRed;
                    matchScore[1] = scoreBlue;
                    await SendMessageBothWays($"Set match score to {scoreRed} - {scoreBlue}");
                }
                else
                {
                    await SendMessageBothWays(Strings.NotEnoughArgs);
                }
                
                break;
            
            case "undo":
                if (matchHistory.Count == 0)
                {
                    await SendMessageBothWays("Nothing to revert.");
                    break;
                }
                
                var lastMatchState = matchHistory.Pop();
                
                currentState = lastMatchState.State;
                lastPick = lastMatchState.LastPick;
                matchScore[0] = lastMatchState.Room.TeamRedScore!.Value;
                matchScore[1] = lastMatchState.Room.TeamBlueScore!.Value;
                pickedMaps = lastMatchState.Room.PickedMaps!;
                bannedMaps = lastMatchState.Room.BannedMaps!;
                
                await SendMessageBothWays("!mp aborttimer");
                await SendMessageBothWays("Reverted to last known state.");
                if(OnStateUpdated != null) await OnStateUpdated.Invoke(matchId);
                break;
            
            case "timeout":
                if (args.Length < 3)
                {
                    await SendMessageBothWays(Strings.RefTimeout);
                    await Task.Delay(IrcMessageDelay);
                    await ChangeState(IMatchManager.MatchState.OnTimeout);
                }
                else
                {
                    if (args[1] == "red")
                    {
                        redTimeoutRequest = true;
                        await SendMessageBothWays("!mp timer 120");
                    } 
                    else if (args[1] == "blue")
                    {
                        blueTimeoutRequest = true;
                        await SendMessageBothWays("!mp timer 120");
                    }
                }
                break;

            case "start":
                if (firstPick == Models.TeamColor.None || firstBan == Models.TeamColor.None)
                {
                    await SendMessageFromDiscord(Strings.PropertiesNotInit);
                    return;
                }

                if (currentState != IMatchManager.MatchState.Idle)
                {
                    await SendMessageBothWays(Strings.AutoAlreadyEngaged);
                    break;
                }

                if (args.Length < 2)
                {
                    await SendMessageBothWays(Strings.NotEnoughArgs);
                }
                else
                {
                    if (args[1] == "auto")
                    {
                        currentMode = OperationMode.Automatic;
                    } else if (args[1] == "assisted")
                    {
                        currentMode = OperationMode.Assisted;
                    }
                    else
                    {
                        await SendMessageBothWays(Strings.NotEnoughArgs);
                        return;
                    }
                }
                
                if (!stoppedPreviously)
                {
                    await SendMessageBothWays(string.Format(Strings.EngagingAuto, currentMatch!.Id));
                    await ChangeState(IMatchManager.MatchState.BanPhaseStart);
                }
                else
                {
                    await SendMessageBothWays(string.Format(Strings.EngagingAuto, currentMatch!.Id));
                    await ChangeState(previousState);
                    stoppedPreviously = false;
                }

                await SendMessageBothWays("!mp clearhost");
                await Task.Delay(IrcMessageDelay);
                await EvaluateCurrentState();
                break;

            case "stop":
                if (currentState == IMatchManager.MatchState.Idle)
                {
                    await SendMessageBothWays(Strings.AutoAlreadyStopped);
                    break;
                }
                await SendMessageBothWays(Strings.StoppingAuto);
                await ChangeState(IMatchManager.MatchState.Idle);
                stoppedPreviously = true;
                break;
            
            case "next":
                if (currentMode == OperationMode.Automatic)
                {
                    await SendMessageBothWays("Auto mode is engaged, engage assisted mode for this.");
                    break;
                }
                
                if (args.Length < 2)
                {
                    await SendMessageBothWays(Strings.NotEnoughArgs);
                }
                
                await TryStateChange(string.Empty, args[1], IMatchManager.MessageKind.SystemMessage);
                break;
            
            case "operation":
                if (args.Length < 2)
                {
                    await SendMessageBothWays(Strings.NotEnoughArgs);
                }
                
                if (args[1] == "auto")
                {
                    currentMode = OperationMode.Automatic;
                    await SendMessageBothWays("Switched to auto mode");
                } else if (args[1] == "assisted")
                {
                    currentMode = OperationMode.Assisted;
                    await SendMessageBothWays("Switched to assisted mode");
                }
                else
                {
                    await SendMessageBothWays(Strings.NotEnoughArgs);
                }
                
                break;
            
            case "win":
                if (args.Length > 1 && currentMode == OperationMode.Assisted)
                {
                    if (args[1] == "red")
                    {
                        matchScore[0]++;
                        var wonMap = pickedMaps.Find(c => c.Slot == currentBeatmapSlot);
                        if (wonMap != null) wonMap.Winner = Models.TeamColor.TeamRed;
                        await SendMessageBothWays(
                            $"{currentMatch!.TeamRed.DisplayName} {matchScore[0]} - {matchScore[1]} {currentMatch!.TeamBlue.DisplayName} | Best of {currentMatch!.Round.BestOf}");
                    }
                    else if (args[1] == "blue")
                    {
                        matchScore[1]++;
                        var winnedMap = pickedMaps.Find(c => c.Slot == currentBeatmapSlot);
                        if (winnedMap != null) winnedMap.Winner = Models.TeamColor.TeamBlue;
                        await SendMessageBothWays(
                            $"{currentMatch!.TeamRed.DisplayName} {matchScore[0]} - {matchScore[1]} {currentMatch!.TeamBlue.DisplayName} | Best of {currentMatch!.Round.BestOf}");
                    }

                    await TryStateChange(string.Empty, "EXIT PLAYING STATE", IMatchManager.MessageKind.SystemMessage); // playing -> whatever
                }
                else
                {
                    await SendMessageBothWays(Strings.NotEnoughArgs);
                }

                break;

            case "firstpick":
                if (args.Length > 1)
                {
                    if (args[1] == "red")
                        firstPick = Models.TeamColor.TeamRed;
                    else if (args[1] == "blue")
                        firstPick = Models.TeamColor.TeamBlue;
                    else
                    {
                        await SendMessageBothWays(Strings.NotEnoughArgs);
                        break;
                    }
                    
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
                    if (args[1] == "red")
                        firstBan = Models.TeamColor.TeamRed;
                    else if (args[1] == "blue")
                        firstBan = Models.TeamColor.TeamBlue;
                    else
                    {
                        await SendMessageBothWays(Strings.NotEnoughArgs);
                        break;
                    }
                    
                    await SendMessageBothWays(Strings.SuccessfulFirstBan);
                }
                else
                {
                    await SendMessageBothWays(Strings.NotEnoughArgs);
                }

                break;
        }
    }

    private async Task SendMessageBothWays(string content, IMatchManager.MessageKind messageKind = IMatchManager.MessageKind.PlayerMessage)
    {
        await client!.SendPrivateMessageAsync(lobbyChannelName!, content);
        msgCallback(matchId, $"**[AUTO | {currentMatch!.Referee.DisplayName}]** {content}", messageKind);
    }
    
    private static bool SearchKeywords(string content) =>
        content.Contains("All players are ready") ||
        content.Contains("Changed beatmap") ||
        content.Contains("Enabled") ||
        content.Contains("Countdown finished");

    /// <summary>
    /// Validates if a map slot (e.g., "NM1") is eligible to be picked or banned.
    /// </summary>
    private bool IsMapAvailable(string content)
    {
        // Validation Rules:
        // 1. Must exist in the MapPool.
        // 2. Must not be in BannedMaps.
        // 3. Must not be in PickedMaps.
        // 4. Cannot be the Tiebreaker (TB1) - handled separately.
        bool canAdd = currentMatch!.Round.MapPool.Find(beatmap => beatmap.Slot == content.ToUpper()) != null &&
                      bannedMaps.Find(beatmap => beatmap.Slot == content.ToUpper()) == null &&
                      pickedMaps.Find(beatmap => beatmap.Slot == content.ToUpper()) == null &&
                      content.ToUpper() != "TB1";

        return canAdd;
    }

    private async Task PreparePick(string slot)
    {
        var beatmap = currentMatch!.Round.MapPool.Find(b => b.Slot == slot.ToUpper());

        if (beatmap == null)
        {
            await SendMessageBothWays($"{slot.ToUpper()} does not exist in the current mappool");
            return;
        }

        currentBeatmapSlot = slot.ToUpper();
        
        await SendMessageBothWays($"!mp map {beatmap!.BeatmapID}");
        await Task.Delay(IrcMessageDelay);
        await SendMessageBothWays($"!mp mods {slot[..2]} NF");
        
        await ChangeState(IMatchManager.MatchState.WaitingForStart);
    }

    private Task SaveMatchHistoryToStack()
    {
        var currentMatchState = new Models.MatchRoom
        {
            Id = matchId,
            TeamRedScore = matchScore[0],
            TeamBlueScore = matchScore[1],
            BannedMaps = bannedMaps.Select(m => new Models.RoundChoice { Slot = m.Slot, TeamColor = m.TeamColor, Winner = m.Winner }).ToList(),
            PickedMaps = pickedMaps.Select(m => new Models.RoundChoice { Slot = m.Slot, TeamColor = m.TeamColor, Winner = m.Winner }).ToList(),
            Round = currentMatch!.Round,
            TeamRed = currentMatch.TeamRed,
            TeamBlue = currentMatch.TeamBlue,
            Referee = currentMatch.Referee,
        };

        matchHistory.Push(new MatchSnapshot(currentMatchState, currentState, lastPick));
        return Task.CompletedTask;
    }
    
    private async Task ChangeState(IMatchManager.MatchState newState)
    {
        previousState = currentState;
        currentState = newState;
        if(OnStateUpdated != null) await OnStateUpdated.Invoke(matchId);
    }

    private async Task SendStateInfo(string info)
    {
        await SendMessageBothWays(info);
        await Task.Delay(IrcMessageDelay);
        await SendMatchStatus();
    }

    private Task EvaluateCurrentState() => TryStateChange(string.Empty, string.Empty);

    /// <summary>
    /// The Brain of the operation. Evaluates the current state and incoming content to transition to the next state.
    /// </summary>
    private async Task TryStateChange(string sender, string content, IMatchManager.MessageKind messageKind = IMatchManager.MessageKind.PlayerMessage) // transiciones de estado
    {
        if (currentState == IMatchManager.MatchState.Idle) return;

        #region TimeoutRegion

        if ((currentState == IMatchManager.MatchState.WaitingForStart ||
             currentState == IMatchManager.MatchState.WaitingForBanBlue ||
             currentState == IMatchManager.MatchState.WaitingForBanRed ||
             currentState == IMatchManager.MatchState.WaitingForPickBlue ||
             currentState == IMatchManager.MatchState.WaitingForPickRed) && content == "!timeout" && currentMode == OperationMode.Automatic)
        {
            if (sender == RedIrcName && !redTimeoutRequest)
            {
                await SendMessageBothWays(Strings.RedTimeout);
                await ChangeState(IMatchManager.MatchState.OnTimeout);
                redTimeoutRequest = true;
            }
            else if (sender == BlueIrcName && !blueTimeoutRequest)
            {
                await SendMessageBothWays(Strings.BlueTimeout);
                await ChangeState(IMatchManager.MatchState.OnTimeout);
                blueTimeoutRequest = true;
            }
            
            return;
        }

        if (currentState == IMatchManager.MatchState.OnTimeout && sender == "BanchoBot" && content == "Countdown finished")
        {
            await SendMessageBothWays("!mp timer 120");
            await Task.Delay(IrcMessageDelay);
            await SendMessageBothWays(Strings.TimeoutStart);
            await ChangeState(previousState);
            return;
        } 

        #endregion

        #region CountdownForPickFinished

        if (currentState == IMatchManager.MatchState.WaitingForPickBlue && currentMode == OperationMode.Automatic)
        {
            if (content.Contains("Countdown finished") && sender == "BanchoBot")
            {
                await SendMessageBothWays($"The timer has ran out, the opponent will be picking now. {currentMatch!.TeamRed.DisplayName}, please state your pick in chat.");
                await Task.Delay(IrcMessageDelay);
                await SendMessageBothWays("!mp timer 60");
                await Task.Delay(IrcMessageDelay);
                await ChangeState(IMatchManager.MatchState.WaitingForPickRed);
                isStolenPick = true;
                await SendMatchStatus();
                return;
            }
        }
        
        if (currentState == IMatchManager.MatchState.WaitingForPickRed && currentMode == OperationMode.Automatic)
        {
            if (content.Contains("Countdown finished") && sender == "BanchoBot")
            {
                await SendMessageBothWays($"The timer has ran out, the opponent will be picking now. {currentMatch!.TeamBlue.DisplayName}, please state your pick in chat.");
                await Task.Delay(IrcMessageDelay);
                await SendMessageBothWays("!mp timer 60");
                await Task.Delay(IrcMessageDelay);
                await ChangeState(IMatchManager.MatchState.WaitingForPickBlue);
                isStolenPick = true;
                await SendMatchStatus();
                return;
            }
        }  

        #endregion

        #region BanningPhaseRegion

        if (currentState == IMatchManager.MatchState.BanPhaseStart)
        {
            if (firstBan == Models.TeamColor.TeamRed)
            {
                await SendStateInfo(string.Format(Strings.BanCall, currentMatch!.TeamRed.DisplayName));
                await ChangeState(IMatchManager.MatchState.WaitingForBanRed);
            }
            else
            {
                await SendStateInfo(string.Format(Strings.BanCall, currentMatch!.TeamBlue.DisplayName));
                await ChangeState(IMatchManager.MatchState.WaitingForBanBlue);
            }
        }
        
        if (currentState == IMatchManager.MatchState.SecondBanPhaseStart)
        {
            if (firstBan == Models.TeamColor.TeamRed)
            {
                await SendStateInfo(string.Format(Strings.BanCall, currentMatch!.TeamBlue.DisplayName));
                await ChangeState(IMatchManager.MatchState.WaitingForBanBlue);
            }
            else
            {
                await SendStateInfo(string.Format(Strings.BanCall, currentMatch!.TeamRed.DisplayName));
                await ChangeState(IMatchManager.MatchState.WaitingForBanRed);
            }
        }

        if (currentState == IMatchManager.MatchState.WaitingForBanRed && 
            ((sender == RedIrcName && currentMode == OperationMode.Automatic) || 
             (messageKind == IMatchManager.MessageKind.SystemMessage && currentMode == OperationMode.Assisted) ))
        {
            if (IsMapAvailable(content))
            {
                await SaveMatchHistoryToStack();
                bannedMaps.Add(new Models.RoundChoice { Slot = content.ToUpper(), TeamColor = Models.TeamColor.TeamRed });
                await SendMessageBothWays(string.Format(Strings.RedBanned, content.ToUpper()));
                await Task.Delay(IrcMessageDelay);
                mapsLeftToBan--;

                if (mapsLeftToBan == 0)
                {
                    mapsLeftToBan = 2;
                    await ChangeState(IMatchManager.MatchState.PickPhaseStart);
                    await EvaluateCurrentState();
                }
                else
                {
                    await ChangeState(IMatchManager.MatchState.WaitingForBanBlue);
                    await SendMessageBothWays(string.Format(Strings.BanCall, currentMatch!.TeamBlue.DisplayName));
                }
            }
            
            return;
        }

        if (currentState == IMatchManager.MatchState.WaitingForBanBlue && 
            ((sender == BlueIrcName && currentMode == OperationMode.Automatic) || 
             (messageKind == IMatchManager.MessageKind.SystemMessage && currentMode == OperationMode.Assisted) ))
        {
            if (IsMapAvailable(content))
            {
                await SaveMatchHistoryToStack();
                bannedMaps.Add(new Models.RoundChoice { Slot = content.ToUpper(), TeamColor = Models.TeamColor.TeamBlue });
                await SendMessageBothWays(string.Format(Strings.BlueBanned, content.ToUpper()));
                await Task.Delay(IrcMessageDelay);
                mapsLeftToBan--;

                if (mapsLeftToBan == 0)
                {
                    mapsLeftToBan = 2;
                    await ChangeState(IMatchManager.MatchState.PickPhaseStart);
                    await EvaluateCurrentState();
                }
                else
                {
                    await ChangeState(IMatchManager.MatchState.WaitingForBanRed);
                    await SendMessageBothWays(string.Format(Strings.BanCall, currentMatch!.TeamRed.DisplayName));
                }
            }
            
            return;
        }

        #endregion

        #region PickPhaseRegion

        if (currentState == IMatchManager.MatchState.PickPhaseStart)
        {
            if (firstPick == Models.TeamColor.TeamRed)
            {
                await SendStateInfo(string.Format(Strings.PickCall, currentMatch!.TeamRed.DisplayName));
                await ChangeState(IMatchManager.MatchState.WaitingForPickRed);
            }
            else
            {
                await SendStateInfo(string.Format(Strings.PickCall, currentMatch!.TeamBlue.DisplayName));
                await ChangeState(IMatchManager.MatchState.WaitingForPickBlue);
            }
            
            return;
        }

        if (currentState == IMatchManager.MatchState.WaitingForPickRed && 
            ((sender == RedIrcName && currentMode == OperationMode.Automatic) || 
             (messageKind == IMatchManager.MessageKind.SystemMessage && currentMode == OperationMode.Assisted) ))
        {
            if (IsMapAvailable(content))
            {
                await SaveMatchHistoryToStack();
                pickedMaps.Add(new Models.RoundChoice { Slot = content.ToUpper(), TeamColor = Models.TeamColor.TeamRed });
                await SendMessageBothWays(string.Format(Strings.RedPicked, content.ToUpper()));
                await PreparePick(content.ToUpper());
                
                if (isStolenPick)
                {
                    lastPick = Models.TeamColor.TeamBlue; 
                    isStolenPick = false;
                }
                else
                {
                    lastPick = Models.TeamColor.TeamRed;
                }
            }
            
            return;
        }

        if (currentState == IMatchManager.MatchState.WaitingForPickBlue && 
            ((sender == BlueIrcName && currentMode == OperationMode.Automatic) || 
             (messageKind == IMatchManager.MessageKind.SystemMessage && currentMode == OperationMode.Assisted) ))
        {
            if (IsMapAvailable(content))
            {
                await SaveMatchHistoryToStack();
                pickedMaps.Add(new Models.RoundChoice { Slot = content.ToUpper(), TeamColor = Models.TeamColor.TeamBlue });
                await SendMessageBothWays(string.Format(Strings.BluePicked, content.ToUpper()));
                await PreparePick(content.ToUpper());
                
                if (isStolenPick)
                {
                    lastPick = Models.TeamColor.TeamRed; 
                    isStolenPick = false;
                }
                else
                {
                    lastPick = Models.TeamColor.TeamBlue;
                }
            }
            
            return;
        }

        #endregion

        #region CommonPhaseRegion
        
        if (currentState == IMatchManager.MatchState.WaitingForStart)
        {
            if ((content.Contains("All players are ready") || content.Contains("Countdown finished")) && sender == "BanchoBot")
            {
                if(currentMode == OperationMode.Automatic) await SendMessageBothWays("!mp start 10");
                await ChangeState(IMatchManager.MatchState.Playing);
            }
        }
        else if (currentState == IMatchManager.MatchState.Playing)
        {
            if ((content.Contains("The match has finished") && currentMode == OperationMode.Automatic) || 
                (messageKind == IMatchManager.MessageKind.SystemMessage && currentMode == OperationMode.Assisted && content == "go") )
            {
                if (currentMatch!.Round.BanRounds == 2 && pickedMaps.Count == 4)
                {
                    // Logic for "Double Ban" rounds (Ban -> Pick 4 -> Ban -> Pick rest)
                    await ChangeState(IMatchManager.MatchState.SecondBanPhaseStart);
                    await SendMessageBothWays(Strings.SecondBanRound);
                    await EvaluateCurrentState();
                }
                else
                {
                    
                    bool redWin = matchScore[0] == (currentMatch.Round.BestOf - 1) / 2 + 1;
                    bool blueWin = matchScore[1] == (currentMatch.Round.BestOf - 1) / 2 + 1;

                    if (redWin)
                    {
                        await SendMessageBothWays(string.Format(Strings.MatchWin, currentMatch!.TeamRed.DisplayName));
                        await ChangeState(IMatchManager.MatchState.MatchFinished);
                        return;
                    }

                    if (blueWin)
                    {
                        await SendMessageBothWays(string.Format(Strings.MatchWin, currentMatch!.TeamBlue.DisplayName));
                        await ChangeState(IMatchManager.MatchState.MatchFinished);
                        return;
                    }

                    if (pickedMaps.Count == currentMatch.Round.BestOf - 1)
                    {
                        await PreparePick("TB1");
                        pickedMaps.Add(new Models.RoundChoice { Slot = "TB1", TeamColor = Models.TeamColor.None });
                        return;
                    }

                    if (lastPick == Models.TeamColor.TeamRed)
                    {
                        await SendMessageBothWays(string.Format(Strings.PickCall, currentMatch!.TeamBlue.DisplayName));
                        await Task.Delay(IrcMessageDelay);
                        await SendMatchStatus();
                        await ChangeState(IMatchManager.MatchState.WaitingForPickBlue);
                    }
                    else
                    {
                        await SendMessageBothWays(string.Format(Strings.PickCall, currentMatch!.TeamRed.DisplayName));
                        await Task.Delay(IrcMessageDelay);
                        await SendMatchStatus();
                        await ChangeState(IMatchManager.MatchState.WaitingForPickRed);
                    }
                }
            }
        }
        
        #endregion
    }
}
