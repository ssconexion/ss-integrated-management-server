using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace ss.Internal.Management.Server.Discord;

public static class OsuMatchImporter
{
    private static readonly HttpClient HttpClient;

    static OsuMatchImporter()
    {
        HttpClient = new HttpClient();
        HttpClient.DefaultRequestHeaders.Accept.Clear();
        HttpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }
    
    public static async Task<List<OsuApiGame>?> FetchAllGamesAsync(long matchId)
    {
        long currentEventId = 0;
        long latestEventId = -1;
        int triesLeft = 10;
        var allGames = new List<OsuApiGame>();

        do
        {
            triesLeft--;
            string url = $"https://osu.ppy.sh/community/matches/{matchId}?limit=100&after={currentEventId}";
            
            var response = await HttpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode) return null;

            var jsonString = await response.Content.ReadAsStringAsync();
            var matchQuery = JsonSerializer.Deserialize<OsuMatchResponse>(jsonString);

            if (matchQuery == null || matchQuery.Events.Count == 0) break;

            currentEventId = matchQuery.Events.Last().Id;
            latestEventId = matchQuery.LatestEventId;

            var gamesInPage = matchQuery.Events
                .Where(e => e.Detail.Type == "other" && e.Game != null)
                .Select(e => e.Game);
                
            allGames.AddRange(gamesInPage!);

        } while (triesLeft > 0 && currentEventId != latestEventId);

        return allGames;
    }
    
    public static string CalculateGrade(OsuApiScore score)
    {
        int hitTotal = score.Statistics.Great + score.Statistics.Ok + score.Statistics.Meh + score.Statistics.Miss;
        if (hitTotal == 0) return "D";

        double acc = Math.Round(score.Accuracy, 4);
        var mods = score.Mods?.Select(m => m.Acronym) ?? Enumerable.Empty<string>();
        bool visionMod = mods.Contains("HD") || mods.Contains("FL");

        double ratioGreat = (double)score.Statistics.Great / hitTotal;
        double ratioMeh = (double)score.Statistics.Meh / hitTotal;

        if (acc == 1.0) return visionMod ? "SSH" : "SS";
        if (ratioGreat > 0.90 && ratioMeh < 0.01 && score.Statistics.Miss == 0) return visionMod ? "SH" : "S";
        if ((ratioGreat > 0.80 && score.Statistics.Miss == 0) || ratioGreat > 0.90) return "A";
        if ((ratioGreat > 0.70 && score.Statistics.Miss == 0) || ratioGreat > 0.80) return "B";
        if (ratioGreat > 0.60) return "C";
        
        return "D";
    }
    
    public class OsuMatchResponse
    {
        [JsonPropertyName("events")] public List<OsuApiEvent> Events { get; set; } = new();
        [JsonPropertyName("latest_event_id")] public long LatestEventId { get; set; }
    }
    public class OsuApiEvent
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("detail")] public OsuApiDetail Detail { get; set; }
        [JsonPropertyName("game")] public OsuApiGame? Game { get; set; } 
    }
    public class OsuApiDetail
    {
        [JsonPropertyName("type")] public string Type { get; set; }
    }
    public class OsuApiGame
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        
        [JsonPropertyName("beatmap_id")] public int BeatmapId { get; set; }
        [JsonPropertyName("scores")] public List<OsuApiScore> Scores { get; set; } = new();
    }
    public class OsuApiScore
    {
        [JsonPropertyName("user_id")] public int UserId { get; set; }
        [JsonPropertyName("accuracy")] public double Accuracy { get; set; }
        [JsonPropertyName("max_combo")] public int MaxCombo { get; set; }
        [JsonPropertyName("score")] public int TotalScore { get; set; }
        [JsonPropertyName("statistics")] public OsuApiStatistics Statistics { get; set; }
        [JsonPropertyName("mods")] public List<OsuApiMod> Mods { get; set; } = new();
    }
    public class OsuApiStatistics
    {
        [JsonPropertyName("great")] public int Great { get; set; }
        [JsonPropertyName("ok")] public int Ok { get; set; }
        [JsonPropertyName("meh")] public int Meh { get; set; }
        [JsonPropertyName("miss")] public int Miss { get; set; }
    }
    public class OsuApiMod { [JsonPropertyName("acronym")] public string Acronym { get; set; } }
}