using System.Data;
using BanchoSharp;
using BanchoSharp.Interfaces;
using Npgsql;

namespace ss.Internal.Management.Server.AutoRef;

public partial class AutoRef
{
    private IBanchoClient client;
    
    public AutoRef(string id, string type, string refId)
    {
        MatchInfo currentMatch = GetMatchFromId(Program.ConnectionString, id).Result;
        RefereeInfo referee = GetRefereeIRCLogin(Program.ConnectionString, refId).Result;
        client = new BanchoClient(new BanchoClientConfig(new IrcCredentials(referee.Username, referee.Password)));

        client.OnAuthenticated += matchStart;
    }

    private void matchStart()
    {
        // logica
    }

    public struct MatchInfo
    {
        public string Id { get; set; }
        public string Team1 { get; set; }
        public string Team2 { get; set; }
        public string BestOf { get; set; }
        public string Round { get; set; }
        public string Type { get; set; }
    }
     
    public struct RefereeInfo
    {
        public string Username { get; set; }
        public string Password { get; set; }
    }
    
    public static async Task<MatchInfo> GetMatchFromId(string connectionString, string matchId)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT match_id, match_team1, match_team2, match_bestOf, match_type, match_round, match_type FROM match WHERE match_id == ${matchId}", connection);
        await using var reader = await command.ExecuteReaderAsync();

        if (!await reader.ReadAsync()) return new MatchInfo { Id = "trolacion" }; // super manejo de errores i know

        return new MatchInfo
        {
            Id = reader.GetString("match_id"),
            Team1 = reader.GetString("match_team1"),
            Team2 = reader.GetString("match_team2"),
            BestOf = reader.GetString("match_bestOf"),
            Round = reader.GetString("match_round"),
            Type = reader.GetString("match_type")
        };
    }
    
    public static async Task<RefereeInfo> GetRefereeIRCLogin(string connectionString, string refName)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT ref_username, ref_password FROM referees WHERE ref_username == ${refName}", connection);
        await using var reader = await command.ExecuteReaderAsync();

        if (!await reader.ReadAsync()) return new RefereeInfo() { Username = "trolacion" }; // we are so back

        return new RefereeInfo()
        {
            Username = reader.GetString("ref_username"),
            Password = reader.GetString("ref_password"),
        };
    }
}
