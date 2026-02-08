using System.Text.RegularExpressions;
using BanchoSharp;
using BanchoSharp.Interfaces;
using Microsoft.EntityFrameworkCore;

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
            currentMatch = await db.MatchRooms.FirstAsync(m => m.Id == matchId) ?? throw new Exception("Match no encontrado en DB");
            currentMatch.Referee = await db.Referees.FirstAsync(r => r.DisplayName == refDisplayName) ?? throw new Exception("Referee no encontrado en DB");

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
        };

        // REGIÓN DEDICADA AL !PANIC. ESTÁ DESACOPLADA DEL RESTO POR SER UN CASO DE EMERGENCIA
        // QUE NO DEBERÍA CAER EN NINGUNA OTRA SUBRUTINA

        if (content.Contains("!panic_over"))
        {
            await SendMessageBothWays("Going back to auto mode. Starting soon...");
            state = MatchState.WaitingForStart;
            await SendMessageBothWays("!mp timer 10");
        }
        else if (content.Contains("!panic"))
        {
            state = MatchState.MatchOnHold;
            await SendMessageBothWays("!mp aborttimer");

            await SendMessageBothWays(
                $"<@&{Environment.GetEnvironmentVariable("DISCORD_REFEREE_ROLE_ID")}>, {senderNick} has requested human intervention. Auto mode has been disabled, resume it with !panic_over");
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
            await SendMessageBothWays($"Team Red wins the map! ({redTotal} vs {blueTotal})");
        }
        else
        {
            matchScore[1]++;
            await SendMessageBothWays($"Team Blue wins the map! ({blueTotal} vs {redTotal})");
        }
        
        currentMapScores.Clear();
    }

    private async Task ExecuteAdminCommand(string sender, string[] args)
    {
        Console.WriteLine("admin command ejecutando");

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
                await SendMessageBothWays($"!mp invite {currentMatch!.TeamBlue.DisplayName.Replace(' ', '_')}");
                break;

            case "start":
                if (firstPick == TeamColor.None || firstBan == TeamColor.None)
                {
                    await SendMessageFromDiscord("Properties not initialized. Set both first_ban and first_pick");
                    return;
                }

                await SendMessageBothWays($"Engaging autoreferee mode for Elimination Stage, Lobby {currentMatch!.Id}");
                state = MatchState.BanPhaseStart;
                auto = true;
                break;

            case "firstpick":
                if (args.Length > 1)
                {
                    firstPick = args[1] == "red" ? TeamColor.TeamRed : TeamColor.TeamBlue;
                    await SendMessageBothWays("Set first_pick successfully");
                }
                else
                {
                    await SendMessageBothWays("Not enough arguments.");
                }

                break;

            case "firstban":
                if (args.Length > 1)
                {
                    firstBan = args[1] == "red" ? TeamColor.TeamRed : TeamColor.TeamBlue;
                    await SendMessageBothWays("Set first_ban successfully");
                }
                else
                {
                    await SendMessageBothWays("Not enough arguments.");
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
                await SendMessageBothWays($"Please {currentMatch!.TeamRed.DisplayName}, state in chat your BAN (ej.: NM1, HD2)");
                state = MatchState.WaitingForBanRed;
            }
            else
            {
                await SendMessageBothWays($"Please {currentMatch!.TeamBlue.DisplayName}, state in chat your BAN (ej.: NM1, HD2)");
                state = MatchState.WaitingForBanBlue;
            }
        }

        if (state == MatchState.WaitingForBanRed && sender == currentMatch!.TeamRed.DisplayName.Replace(' ','_'))
        {
            if (currentMatch.Round.MapPool.Find(beatmap => beatmap.Slot == content.ToUpper()) != null)
            {
                bannedMaps.Add(new Models.RoundChoice { Slot = content.ToUpper(), TeamColor = Models.TeamColor.TeamRed });
                await SendMessageBothWays($"Red has picked {content.ToUpper()} for their ban.");
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
                    await SendMessageBothWays($"Please {currentMatch!.TeamBlue.DisplayName}, state in chat your BAN (ej.: NM1, HD2)");
                }
                
            }
            
            return;
        }

        if (state == MatchState.WaitingForBanBlue && sender == currentMatch!.TeamBlue.DisplayName.Replace(' ','_'))
        {
            if (currentMatch.Round.MapPool.Find(beatmap => beatmap.Slot == content.ToUpper()) != null)
            {
                bannedMaps.Add(new Models.RoundChoice { Slot = content.ToUpper(), TeamColor = Models.TeamColor.TeamBlue });
                await SendMessageBothWays($"Blue has picked {content.ToUpper()} for their ban.");
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
                    await SendMessageBothWays($"Please {currentMatch!.TeamRed.DisplayName}, state in chat your BAN (ej.: NM1, HD2)");
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
                await SendMessageBothWays($"Please {currentMatch!.TeamRed.DisplayName}, state in chat you PICK (ej.: NM1, HD2)");
                state = MatchState.WaitingForPickRed;
            }
            else
            {
                await SendMessageBothWays($"Please {currentMatch!.TeamBlue.DisplayName}, state in chat you PICK (ej.: NM1, HD2)");
                state = MatchState.WaitingForPickBlue;
            }
        }

        if (state == MatchState.WaitingForPickRed && sender == currentMatch!.TeamRed.DisplayName.Replace(' ','_'))
        {
            if (currentMatch.Round.MapPool.Find(beatmap => beatmap.Slot == content.ToUpper()) != null)
            {
                pickedMaps.Add(new Models.RoundChoice { Slot = content.ToUpper(), TeamColor = Models.TeamColor.TeamRed });
                await SendMessageBothWays($"Red has picked {content.ToUpper()} for their PICK.");
                await PreparePick(content.ToUpper());
                lastPick = TeamColor.TeamRed;
            }
        }
        else if (state == MatchState.WaitingForPickBlue && sender == currentMatch!.TeamBlue.DisplayName.Replace(' ','_'))
        {
            if (currentMatch.Round.MapPool.Find(beatmap => beatmap.Slot == content.ToUpper()) != null)
            {
                pickedMaps.Add(new Models.RoundChoice { Slot = content.ToUpper(), TeamColor = Models.TeamColor.TeamBlue });
                await SendMessageBothWays($"Blue has picked {content.ToUpper()} for their PICK");
                await PreparePick(content.ToUpper());
                lastPick = TeamColor.TeamBlue;
            }
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
                    await SendMessageBothWays("Second ban round is about to start");
                }
                else
                {
                    if (pickedMaps.Count == currentMatch.Round.BestOf - 1)
                    {
                        await PreparePick("TB1");
                    }

                    bool redwin = matchScore[0] == ((currentMatch.Round.BestOf - 1) / 2) + 1;
                    bool bluewin = matchScore[1] == ((currentMatch.Round.BestOf - 1) / 2) + 1;

                    if (redwin)
                    {
                        await SendMessageBothWays($"GGWP! {currentMatch!.TeamRed.DisplayName} wins the match!");
                        state = MatchState.MatchFinished;
                        return;
                    }

                    if (bluewin)
                    {
                        await SendMessageBothWays($"GGWP! {currentMatch!.TeamBlue.DisplayName} wins the match!");
                        state = MatchState.MatchFinished;
                        return;
                    }

                    if (lastPick == TeamColor.TeamRed)
                    {
                        state = MatchState.WaitingForPickBlue;
                        await SendMessageBothWays($"Please {currentMatch!.TeamBlue.DisplayName}, state in chat you PICK (ej.: NM1, HD2)");
                    }
                    else
                    {
                        state = MatchState.WaitingForPickRed;
                        await SendMessageBothWays($"Please {currentMatch!.TeamRed.DisplayName}, state in chat you PICK (ej.: NM1, HD2)");
                    }
                }
            }
        }
    }
}