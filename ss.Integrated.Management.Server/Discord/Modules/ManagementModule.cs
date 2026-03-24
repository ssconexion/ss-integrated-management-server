using Discord;
using Discord.Interactions;
using Microsoft.EntityFrameworkCore;
using ss.Internal.Management.Server.AutoRef;

namespace ss.Internal.Management.Server.Discord.Modules;

public class ManagementModule : InteractionModuleBase<SocketInteractionContext>
{
    [RequireFromEnvId("DISCORD_REFEREE_ROLE_ID")]
    [SlashCommand("importscores-privaterooms", "Sube un archivo .txt/.csv con los resultados de un match")]
    public async Task ImportPrivateScoresAsync(string matchId, IAttachment file)
    {
        await DeferAsync(ephemeral: false);

        if (!file.Filename.EndsWith(".txt") && !file.Filename.EndsWith(".csv"))
        {
            await FollowupAsync("**Error:** El archivo debe ser .txt o .csv");
            return;
        }

        string csvContent;

        try
        {
            using (var httpClient = new HttpClient())
            {
                csvContent = await httpClient.GetStringAsync(file.Url);
            }
        }
        catch (Exception ex)
        {
            await FollowupAsync($"**Error descargando archivo:** {ex.Message}");
            return;
        }

        if (string.IsNullOrWhiteSpace(csvContent))
        {
            await FollowupAsync("**Error:** El archivo está vacío.");
            return;
        }

        try
        {
            await using var db = new ModelsContext();
            var importer = new ScoreImporterModule(db);

            bool success = await importer.ProcessScoresAsync(csvContent, matchId);

            if (success)
            {
                await FollowupAsync($"**Importación Exitosa:** Se han guardado los resultados para el Match **{matchId}** desde el archivo `{file.Filename}`.");
            }
            else
            {
                await FollowupAsync(
                    $"**Procesado sin cambios:** El archivo se leyó pero no se guardaron filas. \nPossible causas:\n- MatchID incorrecto.\n- IDs de mapas no coinciden con el pool de la ronda.\n- Usuarios no existen en la DB.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
            await FollowupAsync($"**Error Crítico:** {ex.Message}");
        }
    }

    [RequireFromEnvId("DISCORD_REFEREE_ROLE_ID")]
    [SlashCommand("importscores", "Importa las scores de un lobby de osu! directamente desde la API")]
    public async Task ImportScoresAsync(string matchId)
    {
        await DeferAsync(ephemeral: false);

        await using var db = new ModelsContext();

        var matchRoom = await db.MatchRooms.FirstOrDefaultAsync(m => m.Id == matchId);
        var qualsRoom = await db.QualifierRooms.FirstOrDefaultAsync(q => q.Id == matchId);

        int roundId;
        bool isPlayoffs = false;

        if (matchRoom != null)
        {
            roundId = matchRoom.RoundId!.Value;
            isPlayoffs = true;
        }
        else if (qualsRoom != null) roundId = qualsRoom.RoundId;
        else
        {
            await FollowupAsync($"No se encontró ninguna sala en la base de datos con el ID `{matchId}`.");
            return;
        }

        int? osuMpId = isPlayoffs ? matchRoom!.MpLinkId : qualsRoom!.MpLinkId;

        if (osuMpId == null)
        {
            await FollowupAsync($"La match con ID `{matchId}` no tiene un MP link asignado.");
            return;
        }

        var games = await OsuMatchImporter.FetchAllGamesAsync(osuMpId.Value);

        if (games == null || games.Count == 0)
        {
            await FollowupAsync("No se pudieron obtener datos del lobby (Es privado o está vacío). Considera usar la versión manual.");
            return;
        }

        int importedScores = 0;
        var validUsersCache = new Dictionary<int, int>();

        foreach (var game in games)
        {
            var map = db.Rounds.First(r => r.Id == roundId).MapPool.FirstOrDefault(m => m.BeatmapID == game.BeatmapId) ??
                      new Models.RoundBeatmap { BeatmapID = 0, Slot = "?" };

            foreach (var score in game.Scores)
            {
                if (!validUsersCache.TryGetValue(score.UserId, out int internalUserId))
                {
                    var dbUser = await db.Users.FirstOrDefaultAsync(u => u.OsuData.Id == score.UserId);
                    if (dbUser == null) continue;

                    internalUserId = dbUser.Id;
                    validUsersCache[score.UserId] = internalUserId;
                }

                var newScore = new Models.ScoreResults
                {
                    RoundId = roundId,
                    UserId = internalUserId,
                    Score = score.TotalScore,
                    Accuracy = (float)score.Accuracy,
                    MaxCombo = score.MaxCombo,
                    Grade = OsuMatchImporter.CalculateGrade(score),
                    Slot = map.Slot,
                };

                db.Scores.Add(newScore);
                importedScores++;
            }
        }

        await db.SaveChangesAsync();
        await FollowupAsync($"Guardadas {importedScores} scores en la DB para la sala `{matchId}`");
    }

    [RequireFromEnvId("DISCORD_ADMIN_ROLE_ID")]
    [SlashCommand("preparescores", "Genera un CSV para ser usado por el generador de bracket.json de DIO")]
    public async Task PrepareScoresAsync(int roundId)
    {
        await DeferAsync(ephemeral: false);

        await using var db = new ModelsContext();

        var scores = await db.Scores
            .Include(s => s.Team)
            .ThenInclude(u => u.OsuData)
            .Where(s => s.RoundId == roundId)
            .ToListAsync();

        if (!scores.Any())
        {
            await FollowupAsync("No hay resultados registrados para esta ronda.");
            return;
        }

        var mapStats = scores.GroupBy(s => s.Slot)
            .ToDictionary(g => g.Key, g =>
            {
                double average = g.Average(s => s.Score);
                double sumOfSquares = g.Sum(s => Math.Pow(s.Score - average, 2));
                double stdDev = g.Count() > 1 ? Math.Sqrt(sumOfSquares / (g.Count() - 1)) : 0;

                return new { Average = average, StdDev = stdDev };
            });

        var mapRanks = scores.GroupBy(s => s.Slot)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(s => s.Score)
                    .Select((s, index) => new { s.UserId, Rank = index + 1 })
                    .ToDictionary(x => x.UserId, x => x.Rank)
            );

        var playerStats = scores.GroupBy(s => s.UserId)
            .Select(g =>
            {
                double zSum = 0;
                var userScores = new Dictionary<string, int>();
                var userRanks = new Dictionary<string, int>();

                foreach (var score in g)
                {
                    var stats = mapStats[score.Slot];

                    if (stats.StdDev > 0)
                    {
                        zSum += (score.Score - stats.Average) / stats.StdDev;
                    }

                    userScores[score.Slot] = score.Score;
                    userRanks[score.Slot] = mapRanks[score.Slot][score.UserId];
                }

                var team = g.First().Team;
                var osuData = team.OsuData;

                return new
                {
                    Username = team.DisplayName,
                    OsuId = osuData?.Id ?? 0,
                    CountryCode = "ES", // NO voy a actualizar models para esto.
                    ZSum = zSum,
                    Scores = userScores,
                    Ranks = userRanks
                };
            })
            .OrderByDescending(p => p.ZSum)
            .ToList();

        var modOrder = new Dictionary<string, int>
        {
            { "NM", 1 }, { "HD", 2 }, { "HR", 3 }, { "DT", 4 }, { "TB", 5 }
        };

        var mapSlots = mapStats.Keys.OrderBy(k =>
        {
            string mod = new string(k.TakeWhile(char.IsLetter).ToArray()).ToUpper();
            int num = int.TryParse(new string(k.Where(char.IsDigit).ToArray()), out int n) ? n : 0;
            int orderWeight = modOrder.ContainsKey(mod) ? modOrder[mod] : 99;
            return (orderWeight, num);
        }).ToList();

        var csvBuilder = new System.Text.StringBuilder();

        string mapsHeader = string.Join(",", mapSlots);
        csvBuilder.AppendLine($"team name,flag ISO,{mapsHeader},Seed,NM Seed,HD Seed,HR Seed,DT Seed,{mapsHeader},team size,p1 ID,p1 flag code");

        int currentSeed = 1;
        int totalPlayers = playerStats.Count;

        foreach (var player in playerStats)
        {
            var playerMapScores = mapSlots.Select(slot =>
                player.Scores.ContainsKey(slot) ? player.Scores[slot].ToString() : "0");

            var playerMapRanks = mapSlots.Select(slot =>
                player.Ranks.ContainsKey(slot) ? player.Ranks[slot].ToString() : totalPlayers.ToString());

            string scoresStr = string.Join(",", playerMapScores);
            string ranksStr = string.Join(",", playerMapRanks);

            csvBuilder.AppendLine($"{player.Username},{player.Username},{scoresStr},{currentSeed},1,1,1,1,{ranksStr},1,{player.OsuId},{player.CountryCode}");

            currentSeed++;
        }

        var embed = new EmbedBuilder()
            .WithTitle("Archivo de Scores Generado")
            .WithColor(Color.Green)
            .WithDescription(
                $"Se ha generado el archivo de puntuaciones y seeding para la ronda `{roundId}` con un total de **{playerStats.Count} jugadores**.");

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(csvBuilder.ToString()));

        await FollowupWithFileAsync(
            stream,
            $"seeding_ronda_{roundId}.csv",
            embed: embed.Build()
        );
    }

    [RequireFromEnvId("DISCORD_ADMIN_ROLE_ID")]
    [SlashCommand("stats", "Calcula stats para una ronda dada")]
    public async Task GenerateZSumResultsAsync(int roundId)
    {
        await DeferAsync(ephemeral: false);

        await using var db = new ModelsContext();

        var scores = await db.Scores
            .Include(s => s.Team)
            .ThenInclude(u => u.OsuData)
            .Where(s => s.RoundId == roundId)
            .ToListAsync();

        if (!scores.Any())
        {
            await FollowupAsync("No hay resultados registrados para esta ronda.");
            return;
        }

        var mapStats = scores.GroupBy(s => s.Slot)
            .ToDictionary(g => g.Key, g =>
            {
                double average = g.Average(s => s.Score);
                double sumOfSquares = g.Sum(s => Math.Pow(s.Score - average, 2));
                double stdDev = g.Count() > 1 ? Math.Sqrt(sumOfSquares / (g.Count() - 1)) : 0;

                return new { Average = average, StdDev = stdDev };
            });

        var mapRanks = scores.GroupBy(s => s.Slot)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(s => s.Score)
                    .Select((s, index) => new { s.UserId, Rank = index + 1 })
                    .ToDictionary(x => x.UserId, x => x.Rank)
            );

        var playerStats = scores.GroupBy(s => s.UserId)
            .Select(g =>
            {
                double zSum = 0;
                double totalRawScore = 0;
                var playerScores = new Dictionary<string, string>();

                foreach (var score in g)
                {
                    var stats = mapStats[score.Slot];

                    if (stats.StdDev > 0)
                    {
                        zSum += (score.Score - stats.Average) / stats.StdDev;
                    }

                    totalRawScore += score.Score;

                    int rank = mapRanks[score.Slot][score.UserId];
                    playerScores[score.Slot] = $"#{rank} {score.Score}";
                }

                return new
                {
                    Username = g.First().Team.DisplayName,
                    ZSum = Math.Round(zSum, 2),
                    AvgScore = Math.Round(totalRawScore / g.Count(), 0),
                    Scores = playerScores
                };
            })
            .OrderByDescending(p => p.ZSum)
            .ToList();

        var modOrder = new Dictionary<string, int>
        {
            { "NM", 1 },
            { "HD", 2 },
            { "HR", 3 },
            { "DT", 4 },
            { "TB", 5 }
        };

        var mapSlots = mapStats.Keys.OrderBy(k =>
        {
            string mod = new string(k.TakeWhile(char.IsLetter).ToArray()).ToUpper();
            int num = int.TryParse(new string(k.Where(char.IsDigit).ToArray()), out int n) ? n : 0;
            int orderWeight = modOrder.ContainsKey(mod) ? modOrder[mod] : 99;
            return (orderWeight, num);
        }).ToList();

        var csvBuilder = new System.Text.StringBuilder();

        csvBuilder.AppendLine($"Rank,Jugador,Z-Sum,Avg. Score,{string.Join(",", mapSlots)}");

        int rank = 1;

        foreach (var player in playerStats)
        {
            var playerMapScores = mapSlots.Select(slot =>
                player.Scores.ContainsKey(slot) ? player.Scores[slot].ToString() : "0");

            csvBuilder.AppendLine($"{rank},{player.Username},{player.ZSum},{player.AvgScore},{string.Join(",", playerMapScores)}");
            rank++;
        }

        var embed = new EmbedBuilder()
            .WithTitle("Qualifiers Standing")
            .WithColor(Color.Purple)
            .WithDescription("Solo incluido el top 10. Consulta todo al completo en el .csv");

        int topCount = Math.Min(10, playerStats.Count);

        for (int i = 0; i < topCount; i++)
        {
            var p = playerStats[i];
            embed.AddField($"#{i + 1} `{p.Username}`", $"**Z-Sum:** {p.ZSum} | **Avg:** {p.AvgScore:N0}", inline: false);
        }

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(csvBuilder.ToString()));

        await FollowupWithFileAsync(
            stream,
            $"stats_ronda_{roundId}.csv",
            embed: embed.Build()
        );
    }

    [RequireFromEnvId("DISCORD_REFEREE_ROLE_ID")]
    [SlashCommand("createqualifierslobby", "Crea una match verificando disponibilidad")]
    public async Task CreateQualifiersRoom(string roomId, string date, string hour, int roundId, int requestedBy = 1)
    {
        await DeferAsync(ephemeral: false);

        await using var db = new ModelsContext();

        string fulldate = $"{date}/{DateTime.UtcNow.Year} {hour}";
        string[] formatos = { "d/M/yyyy H:m", "dd/MM/yyyy HH:mm" };

        if (!DateTime.TryParseExact(fulldate, formatos, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal,
                out DateTime result))
        {
            await FollowupAsync("Formato inválido, usa DD/MM HH:mm (ej: 09/11, 08:46)");
            return;
        }

        if (db.QualifierRooms.FirstOrDefault(room => room.Id == roomId) != null)
        {
            await FollowupAsync("Ya existía previamente una match con esa ID.");
            return;
        }

        if (db.Rounds.FirstOrDefault(round => round.Id == roundId) == null)
        {
            await FollowupAsync("No existe la ronda especificada");
            return;
        }

        var room = new Models.QualifierRoom
        {
            Id = roomId,
            RoundId = roundId,
            StartTime = DateTime.SpecifyKind(result, DateTimeKind.Utc),
            RequestedBy = null,
            RefereeId = null,
            Approved = true,
        };

        db.QualifierRooms.Add(room);
        await db.SaveChangesAsync();

        await FollowupAsync($"Sala de Qualifiers `{room.Id}` creada con éxito con fecha `{room.StartTime}`");
    }

    [RequireFromEnvId("DISCORD_REFEREE_ROLE_ID")]
    [SlashCommand("removequalifiersroom", "Elimina una sala de qualifiers del listado a través de su ID")]
    public async Task RemoveQualifiersRoomAsync(string matchid)
    {
        await DeferAsync(ephemeral: false);
        await using var db = new ModelsContext();

        var remover = await db.QualifierRooms.FirstOrDefaultAsync(room => room.Id == matchid);

        if (remover == null)
        {
            await FollowupAsync("No se ha encontrado una sala con la ID especificada");
            return;
        }

        db.QualifierRooms.Remove(remover);
        await db.SaveChangesAsync();
        await FollowupAsync($"Se ha borrado la match con ID `{remover.Id}`");
    }

    [RequireFromEnvId("DISCORD_REFEREE_ROLE_ID")]
    [SlashCommand("removematchup", "Elimina una match del listado a través de su ID")]
    public async Task RemoveMatchAsync(string matchid)
    {
        await DeferAsync(ephemeral: false);
        await using var db = new ModelsContext();

        var remover = await db.MatchRooms.FirstOrDefaultAsync(room => room.Id == matchid);

        if (remover == null)
        {
            await FollowupAsync("No se ha encontrado una match con la ID especificada");
            return;
        }

        db.MatchRooms.Remove(remover);
        await db.SaveChangesAsync();
        await FollowupAsync($"Se ha borrado la match con ID `{remover.Id}`");
    }

    [RequireFromEnvId("DISCORD_ADMIN_ROLE_ID")]
    [SlashCommand("generate-groups", "NO voy a generar todos los grupos uno a uno")]
    public async Task GenerateGroupMatchesAsync(int roundId)
    {
        await DeferAsync(ephemeral: false);

        var sorteoGrupos = new Dictionary<string, List<int>>
        {
            { "A", new List<int> { 8433636, 15254495, 17594074, 35745799 } },
            { "B", new List<int> { 3938945, 14791081, 4638940, 19744046 } },
            { "C", new List<int> { 21622646, 8335721, 36396047, 15049805 } },
            { "D", new List<int> { 15907157, 15207748, 13174212, 18202514 } },
            { "E", new List<int> { 14153496, 22037489, 10771616, 36687057 } },
            { "F", new List<int> { 15101114, 15289150, 15842178, 12781215 } },
            { "G", new List<int> { 11830831, 12048359, 18296881, 21412869 } },
            { "H", new List<int> { 10596572, 15961685, 15627043, 23762052 } }
        };

        await using var db = new ModelsContext();

        var roundExists = await db.Rounds.AnyAsync(r => r.Id == roundId);

        if (!roundExists)
        {
            await FollowupAsync($"La ronda con ID `{roundId}` no existe en la base de datos.");
            return;
        }

        var allOsuIds = sorteoGrupos.Values.SelectMany(x => x).ToList();

        var users = await db.Users
            .Where(u => allOsuIds.Contains(u.OsuID))
            .Select(u => new { u.OsuID, u.Id })
            .ToListAsync();

        var osuIdToInternalId = users.ToDictionary(u => u.OsuID, u => u.Id);

        var missingPlayers = allOsuIds.Where(id => !osuIdToInternalId.ContainsKey(id)).ToList();

        if (missingPlayers.Any())
        {
            await FollowupAsync($"**Error:** Faltan estos osu! IDs en la BD: {string.Join(", ", missingPlayers)}");
            return;
        }

        var nuevosPartidos = new List<Models.MatchRoom>();

        foreach (var grupo in sorteoGrupos)
        {
            var internalPlayers = grupo.Value.Select(osuId => osuIdToInternalId[osuId]).ToList();

            var matchups = new List<(int, int)>
            {
                (internalPlayers[0], internalPlayers[1]),
                (internalPlayers[2], internalPlayers[3]),
                (internalPlayers[0], internalPlayers[2]),
                (internalPlayers[1], internalPlayers[3]),
                (internalPlayers[0], internalPlayers[3]),
                (internalPlayers[1], internalPlayers[2])
            };

            int i = 1;

            foreach (var (p1, p2) in matchups)
            {
                nuevosPartidos.Add(new Models.MatchRoom
                {
                    RoundId = roundId,
                    TeamRedId = p1,
                    TeamBlueId = p2,
                    Id = $"{grupo.Key}{i}"
                });

                i++;
            }
        }

        await db.MatchRooms.AddRangeAsync(nuevosPartidos);
        await db.SaveChangesAsync();

        var embed = new EmbedBuilder()
            .WithTitle("✅ Fase de Grupos Generada")
            .WithColor(Color.Blue)
            .WithDescription($"Se han insertado exitosamente **{nuevosPartidos.Count}** partidos en la ronda `{roundId}`.");

        await FollowupAsync(embed: embed.Build());
    }
}