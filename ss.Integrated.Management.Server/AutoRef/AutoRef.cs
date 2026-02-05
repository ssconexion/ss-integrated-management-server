using BanchoSharp;
using BanchoSharp.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ss.Internal.Management.Server.AutoRef;

public partial class AutoRef
{
    private Models.Match? currentMatch;
    private readonly string matchId;
    private readonly string refDisplayName;
    private readonly Models.MatchType type;
    
    private IBanchoClient? client;
    private string? lobbyChannelName;
    
    private int[] matchScore = [0, 0];
    private bool auto = false;
    private bool joined = false;
    
    private readonly Action<string, string> msgCallback;

    public AutoRef(string matchId, string refDisplayName, Models.MatchType type, Action<string, string> msgCallback)
    {
        this.matchId = matchId;
        this.refDisplayName = refDisplayName;
        this.type = type;
        this.msgCallback = msgCallback;
    }

    public async Task StartAsync()
    {
        using (var db = new ModelsContext())
        {
            currentMatch = await db.Matches
                .Include(m => m.Round)
                .Include(m => m.TeamRed)
                .Include(m => m.TeamBlue)
                .Include(m => m.Referee)
                .FirstOrDefaultAsync(m => m.Id == matchId) ?? throw new Exception("Match no encontrado en DB");
        }

        await ConnectToBancho();
    }

    private async Task ConnectToBancho()
    {
        var config = new BanchoClientConfig(
            new IrcCredentials(currentMatch.Referee.Name, currentMatch.Referee.IRC)
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
        
        if (senderNick == "BanchoBot" && content.Contains("Created the tournament match"))
        {
            var parts = content.Split('/');
            var idPart = parts.Last().Split(' ')[0];
            lobbyChannelName = $"#mp_{idPart}";

            await client.JoinChannelAsync(lobbyChannelName);
            await InitializeLobbySettings();
            joined = true;
            return;
        }
        
        if (senderNick == "BanchoBot")
        {
            if (content.Contains("Team Red wins"))
            {
                matchScore[0]++;
                await PrintScore();
            }
            else if (content.Contains("Team Blue wins"))
            {
                matchScore[1]++;
                await PrintScore();
            }
        }
        
        if (content == "PING")
        {
            await client.SendPrivateMessageAsync(lobbyChannelName,"pong");
        }
    }

    private async Task InitializeLobbySettings()
    {
        await client.SendPrivateMessageAsync(lobbyChannelName,"!mp set 2 3 2");
        await client.SendPrivateMessageAsync(lobbyChannelName, "!mp invite " + currentMatch.Referee.Name);
        //TODO addrefs streamers
    }

    private async Task PrintScore()
    {
        string scoreMsg = $"{currentMatch.TeamRed.DisplayName} {matchScore[0]} -- {matchScore[1]} {currentMatch.TeamBlue.DisplayName}";
        await client.SendPrivateMessageAsync(lobbyChannelName, scoreMsg);
    }

    private async Task ExecuteAdminCommand(string[] args)
    {
        switch (args[0].ToLower())
        {
            case "auto":
                auto = args.Length > 1 && args[1] == "on";
                await client.SendPrivateMessageAsync(lobbyChannelName, $"Auto-Ref status: {(auto ? "ENABLED" : "DISABLED")}");
                break;
            case "close":
                await client.SendPrivateMessageAsync(lobbyChannelName, "!mp close");
                await client.DisconnectAsync();
                break;
            case "invite":
                await client.SendPrivateMessageAsync(lobbyChannelName, $"!mp invite {currentMatch.TeamRed.DisplayName}");
                await client.SendPrivateMessageAsync(lobbyChannelName, $"!mp invite {currentMatch.TeamBlue.DisplayName}");
                break;
        }
    }
}