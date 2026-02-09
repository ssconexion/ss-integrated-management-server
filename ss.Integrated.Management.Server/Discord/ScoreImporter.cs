using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ss.Internal.Management.Server.AutoRef;

namespace ss.Internal.Management.Server.Discord;

public class ScoreImporter
{
    private readonly ModelsContext db;

    public ScoreImporter(ModelsContext db)
    {
        this.db = db;
    }

    public async Task<bool> ProcessScoresAsync(string csvContent, string matchIdStr)
    {
        var matchRoom = await db.MatchRooms.FirstOrDefaultAsync(m => m.Id == matchIdStr);

        if (matchRoom == null)
        {
            Console.WriteLine($"Error: No se encontró la MatchRoom con ID {matchIdStr}");
            return false;
        }

        int roundId = matchRoom.RoundId;

        var rawScores = ParseCsv(csvContent);
        if (!rawScores.Any()) return false;

        var beatmapIds = rawScores.Select(s => s.BeatmapId).Distinct().ToList();
        var osuUserIdsFromCsv = rawScores.Select(s => s.OsuUserId).Distinct().ToList();

        var userMap = await db.Users
            .AsNoTracking()
            .Where(u => osuUserIdsFromCsv.Contains(u.OsuID))
            .Select(u => new { u.OsuID, u.Id })
            .ToDictionaryAsync(x => x.OsuID, x => x.Id);

        var roundObj = await db.Rounds
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == roundId);

        Dictionary<int, string> mapSlots = new();

        if (roundObj != null)
        {
            mapSlots = roundObj.MapPool
                .Where(rb => beatmapIds.Contains(rb.BeatmapID))
                .ToDictionary(rb => rb.BeatmapID, rb => rb.Slot);
        }
        else
        {
            Console.WriteLine($"Warning: No se encontró pool para la ronda {roundId}");
        }

        var entitiesToAdd = new List<Models.ScoreResults>();

        foreach (var raw in rawScores)
        {
            if (!userMap.TryGetValue(raw.OsuUserId, out int internalUserId)) continue;

            if (!mapSlots.TryGetValue(raw.BeatmapId, out string? slot)) continue;

            var scoreEntity = new Models.ScoreResults
            {
                RoundId = roundId,
                UserId = internalUserId,
                Slot = slot,
                Score = raw.Score,
                Accuracy = raw.Accuracy,
                MaxCombo = raw.MaxCombo,
                Grade = raw.Grade
            };

            entitiesToAdd.Add(scoreEntity);
        }

        if (entitiesToAdd.Count > 0)
        {
            await db.Scores.AddRangeAsync(entitiesToAdd);
            await db.SaveChangesAsync();
            Console.WriteLine($"Guardados {entitiesToAdd.Count} scores correctamente.");
            return true;
        }

        Console.WriteLine("No se guardó nada");
        return false;
    }

    private List<RawScoreData> ParseCsv(string content)
    {
        var list = new List<RawScoreData>();
        var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        var csvSplitter = new Regex(",(?=(?:[^\"]*\"[^\"]*\")*[^\"]*$)");

        foreach (var line in lines)
        {
            var cleanLine = line.Trim();
            if (!cleanLine.StartsWith(Program.TournamentName)) continue;

            var cols = csvSplitter.Split(cleanLine);
            if (cols.Length < 18) continue;

            try
            {
                list.Add(new RawScoreData
                {
                    BeatmapId = int.Parse(cols[5]),
                    OsuUserId = int.Parse(cols[7]),
                    Score = int.Parse(cols[9]),
                    Accuracy = float.Parse(cols[10], System.Globalization.CultureInfo.InvariantCulture),
                    MaxCombo = int.Parse(cols[13]),
                    Grade = cols[18].Trim()
                });
            }
            catch (Exception)
            {
                /* perro sanxe */
            }
        }

        return list;
    }

    private class RawScoreData
    {
        public int BeatmapId { get; set; }
        public int OsuUserId { get; set; }
        public int Score { get; set; }
        public float Accuracy { get; set; }
        public int MaxCombo { get; set; }
        public string Grade { get; set; }
    }
}