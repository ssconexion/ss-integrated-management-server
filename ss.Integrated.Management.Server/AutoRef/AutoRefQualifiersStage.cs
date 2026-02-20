using BanchoSharp;
using BanchoSharp.Interfaces;
using Microsoft.EntityFrameworkCore;
using ss.Internal.Management.Server.Resources;

namespace ss.Internal.Management.Server.AutoRef;

/// <summary>
/// Handles the automated refereeing logic for Qualifier Lobbies.
/// manages a linear flow of maps with no picks/bans.
/// </summary>
/// <remarks>
/// ## State Transition Diagram
/// \dot
/// digraph QualifiersStateMachine {
///     // Graph Settings
///     graph [fontname = "helvetica", fontsize = 10, nodesep = 0.5, ranksep = 0.7];
///     node [fontname = "helvetica", fontsize = 10, shape = box, style = rounded];
///     edge [fontname = "helvetica", fontsize = 8];
///
///     // Nodes
///     Init [label="Start\n(Lobby Idle)", shape=circle, width=0.8, fixedsize=true, style=filled, fillcolor="#E5E7E9"];
///     Idle [label="Idle\n(Cooldown / Loading)", style="filled,rounded", fillcolor="#EEEEEE"];
///     WaitStart [label="WaitingForStart\n(Timer Active)"];
///     Playing [label="Playing\n(Map in Progress)", style="filled,rounded", fillcolor="#D4E6F1"];
///     Finished [label="MatchFinished\n(Pool Complete)", style="filled,rounded", fillcolor="#D5F5E3"];
///     Panic [label="MatchOnHold\n(PANIC)", shape=doubleoctagon, style=filled, fillcolor="#E74C3C", fontcolor="white"];
///
///     // Main Flow
///     Init -> Idle [label="Admin types\n!start\n(auto-ref engaged)"];
///     
///     // The Loop
///     Idle -> WaitStart [label="Next Map Available\n(Loaded Map & Mods)"];
///     WaitStart -> Playing [label="Players Ready\nOR Countdown ends"];
///     Playing -> Idle [label="Map Finished\n(Increment Map Index)"];
///
///     // Exit Condition
///     Idle -> Finished [label="No Maps Left"];
///
///     // Emergency System
///     {WaitStart Playing} -> Panic [label="!panic\n(ANYONE)", color="#E74C3C", fontcolor="#E74C3C"];
///     Panic -> WaitStart [label="!panic_over\n(REF ONLY)", color="#27AE60", fontcolor="#27AE60", penwidth=2];
/// }
/// \enddot
/// </remarks>
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

    private int mpLinkId;

    private List<int> usersInRoom = new();

    private TaskCompletionSource<string>? chatResponseTcs;

    private readonly Action<string, string> msgCallback;

    /// <summary>
    /// Represents the finite states of the match flow.
    /// </summary>
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

    /// <inheritdoc />
    public async Task StartAsync()
    {
        await using (var db = new ModelsContext())
        {
            currentMatch = await db.QualifierRooms.FirstAsync(m => m.Id == matchId) ?? throw new Exception("Match not found in the DB");
            currentMatch.Referee = await db.Referees.FirstAsync(r => r.DisplayName == refDisplayName) ?? throw new Exception("Referee not found in the DB");
            
            currentMatch.Round = await db.Rounds.FirstAsync(r => r.Id == currentMatch.RoundId);

            usersInRoom = await db.Players.Where(p => p.QualifierRoomId == matchId).Select(p => p.User.OsuID).ToListAsync();
        }

        await ConnectToBancho();
    }
    
    /// <inheritdoc />
    public async Task StopAsync()
    {
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
            _ = client.MakeTournamentLobbyAsync($"{Program.TournamentName}: (Qualifiers) vs (Lobby {matchId})", false);
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
    private async Task HandleIrcMessage(IIrcMessage msg)
    {
        string prefix = msg.Prefix.StartsWith(":") ? msg.Prefix[1..] : msg.Prefix;
        string senderNick = prefix.Contains('!') ? prefix.Split('!')[0] : prefix;

        //string target = msg.Parameters[0];
        string content = msg.Parameters[1];

        Console.WriteLine($"{senderNick}: {content}");

        if (joined) msgCallback(matchId, $"**[{senderNick}]** {content}");

        // 1. System Events (Lobby creation/closure)
        switch (senderNick)
        {
            case "BanchoBot" when content.Contains("Created the tournament match"):
                var parts = content.Split('/');
                var idPart = parts.Last().Split(' ')[0];

                mpLinkId = int.Parse(idPart);
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

        // 2. Emergency Protocols (!panic)
        // Decoupled from the main State Machine to ensure it works regardless of the current state.
        if (content.Contains("!panic_over") && senderNick == currentMatch!.Referee.DisplayName.Replace(' ', '_'))
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

        // 3. Drive the State Machine
        if(senderNick == "BanchoBot") _ = TryStateChange(content);
            
        // 4. Admin Commands
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
        // Hardcoded Settings: TeamMode=0 (Head2Head), WinCondition=3 (ScoreV2), Slots=16
        await client!.SendPrivateMessageAsync(lobbyChannelName!, "!mp set 0 3 16");
        await client!.SendPrivateMessageAsync(lobbyChannelName!, "!mp invite " + currentMatch!.Referee.DisplayName.Replace(' ', '_'));
        await SendMessageBothWays($"Join this match via an IRC app with this command: \n- `/join {lobbyChannelName}`");
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
            case "finish":
                currentMatch!.MpLinkId = mpLinkId;
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

    /// <summary>
    /// The Brain of the operation. Evaluates the current state and incoming content to transition to the next state.
    /// </summary>
    private async Task TryStateChange(string banchoMsg)
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

                    // ASYNC VOID PATTERN (Intentional):
                    // We fire-and-forget this task to create a non-blocking 10s cooldown
                    // between maps, giving players time to breathe/check scores.
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

    /// <summary>
    /// Initializes the Qualifier lifecycle. Resets the map index and triggers the first map load.
    /// </summary>
    private async Task StartQualifiersFlow()
    {
        currentMapIndex = 0;
        state = MatchState.Idle;
        await PrepareNextQualifierMap();
    }

    /// <summary>
    /// Iterates to the next map in the pool, applies mods, and sets the ready timer.
    /// Handles the exit condition when all maps have been played.
    /// </summary>
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