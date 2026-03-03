using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using ss.Internal.Management.Server.AutoRef;
using ss.Internal.Management.Server.Resources;
using Google.OrTools.Sat;

namespace ss.Internal.Management.Server.Discord;

public class SlashCommandManager : InteractionModuleBase<SocketInteractionContext>
{
    public DiscordManager Manager { get; set; }

    public class PendingMatch
    {
        public string MatchId { get; set; }
        public int RedId { get; set; }
        public int BlueId { get; set; }
        public DateTime ReferenceDate { get; set; }
        public string AvailRed { get; set; }
        public string AvailBlue { get; set; }
        public string RedName { get; set; }
        public string BlueName { get; set; }
        public int RoundId { get; set; }
    }

    public static Dictionary<string, PendingMatch> PendingMatches = new();

    [RequireFromEnvId("DISCORD_REFEREE_ROLE_ID")]
    [SlashCommand("startref", "Inicia un nuevo match y crea su thread")]
    public async Task StartRefAsync(string matchId, string referee, Models.MatchType matchType)
    {
        await DeferAsync(ephemeral: false);

        await using var db = new ModelsContext();

        if (await db.Referees.FirstOrDefaultAsync(r => r.DisplayName == referee) != null)
        {
            bool created = await Manager.CreateMatchEnvironmentAsync(matchId, referee, Context.Guild, matchType);

            if (created)
                await FollowupAsync($"Match **{matchId}** iniciado correctamente.");
            else
                await FollowupAsync($"El Match ID **{matchId}** ya está en curso.", ephemeral: true);
        }
        else
        {
            await FollowupAsync($"El Referee **{referee}** no existe.", ephemeral: true);
        }
    }

    [RequireFromEnvId("DISCORD_REFEREE_ROLE_ID")]
    [SlashCommand("endref", "Finaliza el match y archiva el thread")]
    public async Task EndRefAsync(string matchId)
    {
        await DeferAsync(ephemeral: false);
        await using var db = new ModelsContext();
        await FollowupAsync($"Procesando cierre para **{matchId}**...");
        bool deleted = await Manager.EndMatchEnvironmentAsync(matchId);

        if (deleted)
        {
            await FollowupAsync(string.Format(Strings.MatchFinishedGlobal, matchId));
        }
        else
        {
            await FollowupAsync(Strings.WorkerNotFound);
        }
    }

    [RequireFromEnvId("DISCORD_REFEREE_ROLE_ID")]
    [SlashCommand("linkirc", "Configura tus credenciales de IRC para hacer uso del bot")]
    public async Task AddRefCredentialsAsync(string nombre, int osuId, string ircPass)
    {
        ulong discordId = Context.User.Id;

        var model = new Models.RefereeInfo
        {
            DisplayName = nombre,
            OsuID = osuId,
            IRC = ircPass,
            DiscordID = discordId
        };

        await RespondAsync($"Referee **{nombre}** añadido/actualizado en la base de datos.\n" +
                           $"- OsuID: {osuId}\n" +
                           $"- DiscordID: {discordId}", ephemeral: true);

        await Manager.AddRefereeToDbAsync(model);
    }

    [RequireFromEnvId("DISCORD_REFEREE_ROLE_ID")]
    [SlashCommand("assignref", "Asigna un referee a una match concreta")]
    public async Task AssignRefToMatch(string matchId, string refName)
    {
        await DeferAsync(ephemeral: false);

        await using var db = new ModelsContext();

        var referee = await db.Referees.FirstOrDefaultAsync(r => r.DisplayName == refName);

        if (referee == null)
        {
            await FollowupAsync($"No se encontró ningún referee con el nombre `{refName}` en la base de datos.");
            return;
        }

        var matchRoom = await db.MatchRooms.FirstOrDefaultAsync(m => m.Id == matchId);
        var qualRoom = await db.QualifierRooms.FirstOrDefaultAsync(q => q.Id == matchId);

        if (matchRoom == null && qualRoom == null)
        {
            await FollowupAsync($"No se encontró ninguna match con el ID `{matchId}`.");
            return;
        }

        if (matchRoom != null)
        {
            matchRoom.Referee = referee;
        }
        else if (qualRoom != null)
        {
            qualRoom.Referee = referee;
        }

        await db.SaveChangesAsync();
        await FollowupAsync($"El referee **{referee.DisplayName}** ha sido asignado correctamente a la partida `{matchId}`.");
    }

    [RequireFromEnvId("DISCORD_REFEREE_ROLE_ID")]
    [SlashCommand("importscores-privaterooms", "Sube un archivo .txt/.csv con los resultados de un match")]
    public async Task ImportPrivateScoresAsync(
        [Summary("match_id", "El ID del match (ej: 15 o A1)")]
        string matchId,
        [Summary("archivo", "El archivo txt/csv con los datos raw")]
        IAttachment file)
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
            var importer = new ScoreImporter(db);

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
    public async Task ImportScoresAsync(
        [Summary(description: "El ID del lobby en la web de osu! (ej. 118575195)")]
        string osuLobbyIdStr,
        [Summary(description: "El ID de la sala en la Base de Datos (ej. A1, 56, EX2)")]
        string dbRoomId)
    {
        await DeferAsync(ephemeral: false);

        if (!long.TryParse(osuLobbyIdStr, out long osuLobbyId))
        {
            await FollowupAsync("El ID del lobby de osu! debe ser un número válido.");
            return;
        }

        await using var db = new ModelsContext();

        var matchRoom = await db.MatchRooms.FirstOrDefaultAsync(m => m.Id == dbRoomId);
        var qualsRoom = await db.QualifierRooms.FirstOrDefaultAsync(q => q.Id == dbRoomId);

        int roundId;

        if (matchRoom != null) roundId = matchRoom.RoundId;
        else if (qualsRoom != null) roundId = qualsRoom.RoundId;
        else
        {
            await FollowupAsync($"No se encontró ninguna sala en la base de datos con el ID `{dbRoomId}`.");
            return;
        }

        var games = await OsuMatchImporter.FetchAllGamesAsync(osuLobbyId);

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
        await FollowupAsync($"Guardadas {importedScores} scores en la DB para la sala `{dbRoomId}`");
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
    [SlashCommand("matchups", "Muestra la lista de matches activos con paginación")]
    public async Task ListMatchesAsync()
    {
        await DeferAsync(ephemeral: false);

        await using var db = new ModelsContext();

        var allMatches = await db.MatchRooms
            .Include(m => m.TeamRed)
            .Include(m => m.TeamBlue)
            .OrderByDescending(m => m.StartTime)
            .ToListAsync();

        if (!allMatches.Any())
        {
            await FollowupAsync("No hay matches registrados.");
            return;
        }

        var embed = CreateMatchEmbed(allMatches, 0, out var components);

        await FollowupAsync(embed: embed, components: components);
    }

    [RequireFromEnvId("DISCORD_REFEREE_ROLE_ID")]
    [SlashCommand("addmplinkid", "Añade un mp link a una match. Consulta previamente si ya tenía uno asignado")]
    private async Task AddMpLinkIdAsync(string matchId, string mpLinkId)
    {
        await DeferAsync(ephemeral: false);
        await using var db = new ModelsContext();

        var match = await db.MatchRooms.FirstOrDefaultAsync(m => m.Id == matchId);

        if (!int.TryParse(mpLinkId, out int id))
        {
            await FollowupAsync("Mp link ID no válido. Debe ser un int");
            return;
        }

        ;

        if (match == null)
        {
            await FollowupAsync("Match ID no válido.");
            return;
        }

        match.MpLinkId = id;
        await db.SaveChangesAsync();
        await FollowupAsync($"MP link {mpLinkId} añadido a match {match.Id}");
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

    [RequireFromEnvId("DISCORD_REFEREE_ROLE_ID")]
    [SlashCommand("reschedulematchup", "Crea una match verificando disponibilidad")]
    public async Task UpdateMatchTimeAsync(string matchId, string date, string hour)
    {
        await DeferAsync(ephemeral: false);
        await using var db = new ModelsContext();

        var match = await db.MatchRooms.FirstOrDefaultAsync(room => room.Id == matchId);

        if (match == null)
        {
            await FollowupAsync("No se ha encontrado una match con la ID especificada");
            return;
        }

        string fulldate = $"{date}/{DateTime.UtcNow.Year} {hour}";
        string[] formatos = { "d/M/yyyy H:m", "dd/MM/yyyy HH:mm" };

        if (!DateTime.TryParseExact(fulldate, formatos, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal,
                out DateTime result))
        {
            await FollowupAsync("Formato inválido, usa DD/MM HH:mm (ej: 09/11, 08:46)");
            return;
        }

        match.StartTime = DateTime.SpecifyKind(result, DateTimeKind.Utc);

        await db.SaveChangesAsync();
        await FollowupAsync($"Se ha rescheduleado la match con ID `{match.Id}` a las `{match.StartTime}`");
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
    [SlashCommand("creatematchup", "Crea una match verificando disponibilidad")]
    public async Task CreateMatchCheck(string matchId, string teamRed, string teamBlue, string fridayDate, int roundId)
    {
        await DeferAsync(ephemeral: false);

        if (!DateTime.TryParse(fridayDate, out DateTime refDate))
        {
            await FollowupAsync("Fecha inválida. Usa formato YYYY-MM-DD.");
            return;
        }


        await using var db = new ModelsContext();

        if (db.Rounds.FirstOrDefault(round => round.Id == roundId) == null)
        {
            await FollowupAsync("No existe la ronda especificada");
            return;
        }

        var userRed = await db.Users
            .Include(u => u.OsuData)
            .FirstOrDefaultAsync(u => u.OsuData.Username == teamRed);

        var userBlue = await db.Users
            .Include(u => u.OsuData)
            .FirstOrDefaultAsync(u => u.OsuData.Username == teamBlue);

        if (userRed == null || userBlue == null)
        {
            await FollowupAsync($"**Error:** Uno de los usuarios ({teamRed} o {teamBlue}) no existe en la tabla `user`.");
            return;
        }

        var playerRed = await db.Players.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userRed.Id) ?? throw new Exception("Red team user not found");
        var playerBlue = await db.Players.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userBlue.Id) ?? throw new Exception("Blue team user not found");
        ;

        string emptySchedule = "000000000000000000000000|000000000000000000000000|000000000000000000000000|000000000000000000000000";

        string availRed = playerRed?.Availability ?? emptySchedule;
        string availBlue = playerBlue?.Availability ?? emptySchedule;

        string tempId = Guid.NewGuid().ToString();

        PendingMatches[tempId] = new PendingMatch
        {
            MatchId = matchId,
            RedId = userRed.Id,
            BlueId = userBlue.Id,
            ReferenceDate = refDate,
            AvailRed = availRed,
            AvailBlue = availBlue,
            RedName = teamRed,
            BlueName = teamBlue,
            RoundId = roundId,
        };

        var menu = new SelectMenuBuilder()
            .WithPlaceholder("Selecciona el DÍA del partido")
            .WithCustomId($"match_day_select:{tempId}")
            .AddOption("Viernes", "0", "Ver horas del Viernes")
            .AddOption("Sábado", "1", "Ver horas del Sábado")
            .AddOption("Domingo", "2", "Ver horas del Domingo")
            .AddOption("Lunes", "3", "Ver horas del Lunes");

        await FollowupAsync(
            $"Configurando Match **{matchId}**\n {teamRed} vs {teamBlue}\nSelecciona un día para comparar disponibilidades:",
            components: new ComponentBuilder().WithSelectMenu(menu).Build());
    }

    [ComponentInteraction("match_day_select:*")]
    public async Task HandleDaySelection(string tempId, string[] selection)
    {
        await DeferAsync();

        if (!PendingMatches.TryGetValue(tempId, out var pm))
        {
            await FollowupAsync("Sesión expirada.", ephemeral: true);
            return;
        }


        int dayIndex = int.Parse(selection[0]);
        string dayName = AvailabilityHelper.DayToName(dayIndex);

        DateTime rawDate = pm.ReferenceDate.AddDays(dayIndex).Date;
        DateTime targetDate = DateTime.SpecifyKind(rawDate, DateTimeKind.Utc);

        var rawStart = pm.ReferenceDate.AddDays(dayIndex - 1);
        var rawEnd = pm.ReferenceDate.AddDays(dayIndex + 2);

        DateTime searchStart = DateTime.SpecifyKind(rawStart, DateTimeKind.Utc);
        DateTime searchEnd = DateTime.SpecifyKind(rawEnd, DateTimeKind.Utc);

        var spainZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid");
        HashSet<int> busyHoursInSpain = new HashSet<int>();

        await using (var db = new ModelsContext())
        {
            var rawMatches = await db.MatchRooms
                .Where(m => m.StartTime >= searchStart && m.StartTime <= searchEnd)
                .Select(m => m.StartTime)
                .ToListAsync();

            foreach (var utcDate in rawMatches)
            {
                var cleanUtc = DateTime.SpecifyKind(utcDate ?? DateTime.UnixEpoch, DateTimeKind.Utc);

                DateTime spainTime = TimeZoneInfo.ConvertTimeFromUtc(cleanUtc, spainZone);

                if (spainTime.Date == targetDate.Date)
                {
                    busyHoursInSpain.Add(spainTime.Hour);
                }
            }
        }


        string bitsRed = AvailabilityHelper.GetDayBits(pm.AvailRed, dayIndex);
        string bitsBlue = AvailabilityHelper.GetDayBits(pm.AvailBlue, dayIndex);

        var menu = new SelectMenuBuilder()
            .WithPlaceholder($"Elige HORA ({dayName})")
            .WithCustomId($"match_hour_select:{tempId}:{dayIndex}");

        for (int i = 0; i < 24; i++)
        {
            int hour = 23 - i;
            char r = bitsRed[i];
            char b = bitsBlue[i];

            string emoji = "❌";
            string desc = "Ninguno disponible";

            if (r == '1' && b == '1')
            {
                emoji = "🟢";
                desc = "Coincidencia";
            }
            else if (r == '1')
            {
                emoji = "🔴";
                desc = $"Solo {PendingMatches[tempId].RedName} disponible";
            }
            else if (b == '1')
            {
                emoji = "🔵";
                desc = $"Solo {PendingMatches[tempId].BlueName} disponible";
            }

            int hora = 23 - i;

            if (busyHoursInSpain.Contains(hora))
            {
                emoji = "⚠️";
                desc = "Ya hay una match a esta hora";
            }

            menu.AddOption($"{hour:00}:00 Hora España", hour.ToString(), desc, Emoji.Parse(emoji));
        }

        await ModifyOriginalResponseAsync(msg =>
        {
            msg.Content = $"📅 Has elegido **{dayName}**.\nAhora selecciona la hora definitiva (🟢 = Recomendado):";
            msg.Components = new ComponentBuilder().WithSelectMenu(menu).Build();
        });
    }

    [ComponentInteraction("match_hour_select:*:*")]
    public async Task HandleHourSelection(string tempId, string dayIndexStr, string[] selection)
    {
        await DeferAsync();

        if (!PendingMatches.TryGetValue(tempId, out var pm))
        {
            await FollowupAsync("Sesión expirada.", ephemeral: true);
            return;
        }

        int dayIndex = int.Parse(dayIndexStr);
        int hour = int.Parse(selection[0]);

        DateTime finalDate = pm.ReferenceDate.AddDays(dayIndex).AddHours(hour).ToUniversalTime();

        await using var db = new ModelsContext();

        var match = new Models.MatchRoom
        {
            Id = pm.MatchId,
            TeamRedId = pm.RedId,
            TeamBlueId = pm.BlueId,
            StartTime = finalDate,
            RoundId = pm.RoundId,
            RefereeId = null,
        };

        try
        {
            db.MatchRooms.Add(match);
            await db.SaveChangesAsync();

            PendingMatches.Remove(tempId);

            await ModifyOriginalResponseAsync(msg =>
            {
                msg.Content = $"**Match Creada Exitosamente**\n" +
                              $"- Match: `{match.Id}`\n" +
                              $"- Fecha: <t:{new DateTimeOffset(finalDate).ToUnixTimeSeconds()}:F>\n" +
                              $"- Hora UTC: `{finalDate:HH:mm}`";

                msg.Components = null;
            });
        }
        catch (Exception ex)
        {
            await FollowupAsync($"**Error DB:** {ex.Message}");
        }
    }

    [ComponentInteraction("matches_page:*")]
    public async Task HandleMatchPaginationAsync(string pageStr)
    {
        if (!int.TryParse(pageStr, out int pageIndex)) return;

        await using var db = new ModelsContext();

        var allMatches = await db.MatchRooms
            .Include(m => m.TeamRed)
            .Include(m => m.TeamBlue)
            .OrderByDescending(m => m.StartTime)
            .ToListAsync();

        var embed = CreateMatchEmbed(allMatches, pageIndex, out var components);

        if (Context.Interaction is SocketMessageComponent component)
        {
            await component.UpdateAsync(msg =>
            {
                msg.Embed = embed;
                msg.Components = components;
            });
        }
    }

    private Embed CreateMatchEmbed(List<Models.MatchRoom> allMatches, int page, out MessageComponent components)
    {
        int pageSize = 5;
        int totalItems = allMatches.Count;
        int totalPages = (int)Math.Ceiling((double)totalItems / pageSize);

        page = Math.Clamp(page, 0, totalPages - 1);

        var pageItems = allMatches
            .Skip(page * pageSize)
            .Take(pageSize)
            .ToList();

        var eb = new EmbedBuilder()
            .WithTitle($"Lista de Matches (Página {page + 1}/{totalPages})")
            .WithColor(Color.Blue)
            .WithFooter($"Total de matches: {totalItems}");

        foreach (var match in pageItems)
        {
            string redName = match.TeamRed?.DisplayName ?? "TBD";
            string blueName = match.TeamBlue?.DisplayName ?? "TBD";

            eb.AddField(
                name: $"{match.Id}",
                value: $"{match.StartTime:dd/MM HH:mm}\n **{redName}** vs **{blueName}**",
                inline: false);
        }

        var cb = new ComponentBuilder();

        cb.WithButton("◀️ Anterior", $"matches_page:{page - 1}", ButtonStyle.Secondary, disabled: page == 0);

        cb.WithButton("Siguiente ▶️", $"matches_page:{page + 1}", ButtonStyle.Secondary, disabled: page >= totalPages - 1);

        components = cb.Build();
        return eb.Build();
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

    [RequireFromEnvId("DISCORD_ADMIN_ROLE_ID")]
    [SlashCommand("generate-schedules", "Genera los horarios de la ronda especificada automáticamente según la disponibilidad.")]
    public async Task GenerateGroupScheduleAsync(int roundId, string fechaInicioViernes)
    {
        await DeferAsync(ephemeral: false);

        if (!DateTime.TryParse(fechaInicioViernes, out DateTime baseDate))
        {
            await FollowupAsync("Formato de fecha inválido. Usa un formato como DD/MM/YYYY (ej: 06/03/2026).");
            return;
        }

        await using var db = new ModelsContext();

        var matches = await db.MatchRooms
            .Include(m => m.TeamRed)
            .Include(m => m.TeamBlue)
            .Where(m => m.RoundId == roundId)
            .ToListAsync();

        if (!matches.Any())
        {
            await FollowupAsync("No hay partidos programados para esta ronda en la DB.");
            return;
        }

        var userIds = matches.Select(m => m.TeamRedId).Concat(matches.Select(m => m.TeamBlueId)).Distinct().ToList();
        var players = await db.Players.Where(p => userIds.Contains(p.UserId)).ToListAsync();

        var slots = new List<TimeSlot>();
        int slotIdCounter = 0;

        for (int week = 1; week <= 2; week++)
        {
            for (int day = 0; day < 4; day++)
            {
                int penalty = (day == 0) ? 3 : (day == 3) ? 50 : 0;

                for (int hour = 16; hour <= 23; hour++)
                {
                    slots.Add(new TimeSlot { Id = ++slotIdCounter, DayIndex = day, Hour = hour, PenaltyScore = penalty, Week = week });
                }
            }
        }

        slots.Add(new TimeSlot { Id = 999, DayIndex = -1, Hour = 0, PenaltyScore = 10000, Week = 0 });

        var model = new CpModel();
        var matchVars = new Dictionary<(string, int), BoolVar>();

        foreach (var match in matches)
        {
            foreach (var slot in slots)
            {
                matchVars[(match.Id, slot.Id)] = model.NewBoolVar($"match_{match.Id}_slot_{slot.Id}");
            }
        }

        foreach (var match in matches)
        {
            var matchSlots = slots.Select(s => matchVars[(match.Id, s.Id)]).ToArray();
            model.AddExactlyOne(matchSlots);
        }

        foreach (var match in matches)
        {
            var p1 = players.FirstOrDefault(p => p.UserId == match.TeamRedId);
            var p2 = players.FirstOrDefault(p => p.UserId == match.TeamBlueId);

            foreach (var slot in slots)
            {
                if (slot.Id == 999) continue;

                bool p1Avail = p1 != null && AvailabilityHelper.IsAvailable(p1.Availability, slot.DayIndex, slot.Hour);
                bool p2Avail = p2 != null && AvailabilityHelper.IsAvailable(p2.Availability, slot.DayIndex, slot.Hour);

                if (!p1Avail || !p2Avail)
                {
                    model.Add(matchVars[(match.Id, slot.Id)] == 0);
                }
            }
        }

        foreach (var uid in userIds)
        {
            var playerMatches = matches.Where(m => m.TeamRedId == uid || m.TeamBlueId == uid).ToList();

            foreach (var slot in slots)
            {
                if (slot.Id == 999) continue;

                var varsInSlot = playerMatches.Select(m => matchVars[(m.Id, slot.Id)]).ToArray();
                model.AddAtMostOne(varsInSlot);
            }
        }

        foreach (var uid in userIds)
        {
            var playerMatches = matches.Where(m => m.TeamRedId == uid || m.TeamBlueId == uid).ToList();

            foreach (var week in new[] { 1, 2 })
            {
                var slotsInWeek = slots.Where(s => s.Week == week).Select(s => s.Id).ToList();
                var varsInWeek = playerMatches.SelectMany(m => slotsInWeek.Select(sid => matchVars[(m.Id, sid)])).ToList();

                model.Add(LinearExpr.Sum(varsInWeek) <= 2);
            }
        }

        var penaltyTerms = new List<LinearExpr>();

        foreach (var match in matches)
        {
            foreach (var slot in slots)
            {
                if (slot.PenaltyScore > 0)
                {
                    penaltyTerms.Add(LinearExpr.Term(matchVars[(match.Id, slot.Id)], slot.PenaltyScore));
                }
            }
        }

        foreach (var slot in slots)
        {
            if (slot.Id == 999) continue;

            var matchesInSlot = matches.Select(m => matchVars[(m.Id, slot.Id)]).ToArray();
            var matchesCount = model.NewIntVar(0, matches.Count, $"count_slot_{slot.Id}");

            model.Add(matchesCount == LinearExpr.Sum(matchesInSlot));

            var countSquared = model.NewIntVar(0, matches.Count * matches.Count, $"sq_count_{slot.Id}");
            model.AddMultiplicationEquality(countSquared, new[] { matchesCount, matchesCount });

            penaltyTerms.Add(LinearExpr.Term(countSquared, 20));
        }

        model.Minimize(LinearExpr.Sum(penaltyTerms));
        var solver = new CpSolver { StringParameters = "max_time_in_seconds: 30.0" };
        var status = solver.Solve(model);

        if (status == CpSolverStatus.Optimal || status == CpSolverStatus.Feasible)
        {
            var resultados = new List<(string MatchId, int Week, int DayIndex, int Hour, string RedName, string BlueName, bool IsLimbo)>();
            int matchesInLimbo = 0;

            foreach (var match in matches)
            {
                foreach (var slot in slots)
                {
                    if (solver.BooleanValue(matchVars[(match.Id, slot.Id)]))
                    {
                        bool isLimbo = slot.Id == 999;
                        if (isLimbo) matchesInLimbo++;

                        resultados.Add((
                            MatchId: match.Id,
                            Week: slot.Week,
                            DayIndex: slot.DayIndex,
                            Hour: slot.Hour,
                            RedName: match.TeamRed?.DisplayName ?? "Desconocido",
                            BlueName: match.TeamBlue?.DisplayName ?? "Desconocido",
                            IsLimbo: isLimbo
                        ));

                        break;
                    }
                }
            }

            var resultadosOrdenados = resultados
                .OrderBy(r => r.IsLimbo ? 1 : 0)
                .ThenBy(r => r.Week)
                .ThenBy(r => r.DayIndex)
                .ThenBy(r => r.Hour)
                .ToList();

            var csvBuilder = new System.Text.StringBuilder();
            csvBuilder.AppendLine("Match ID,Semana,Día,Hora (UTC),Red Team,Blue Team");

            foreach (var r in resultadosOrdenados)
            {
                var matchToUpdate = matches.First(m => m.Id == r.MatchId);

                if (r.IsLimbo)
                {
                    csvBuilder.AppendLine($"{r.MatchId},N/A,Sin Asignar (Incompatibles),N/A,{r.RedName},{r.BlueName}");

                    matchToUpdate.StartTime = null;
                }
                else
                {
                    string dayName = AvailabilityHelper.DayToName(r.DayIndex);
                    csvBuilder.AppendLine($"{r.MatchId},{r.Week},{dayName},{r.Hour}:00,{r.RedName},{r.BlueName}");

                    // Sumamos (r.Week - 1) * 7 para saltar a la segunda semana si hace falta
                    // Sumamos r.DayIndex para movernos del Viernes al Sábado/Domingo/Lunes
                    // Sumamos r.Hour para fijar la hora UTC
                    int daysToAdd = ((r.Week - 1) * 7) + r.DayIndex;
                    DateTime finalDate = baseDate.Date.AddDays(daysToAdd).AddHours(r.Hour - 1); // La disponibilidad esta en utc+1, ajustamos acorde. 

                    matchToUpdate.StartTime = DateTime.SpecifyKind(finalDate, DateTimeKind.Utc);
                }
            }

            await db.SaveChangesAsync();

            foreach (var r in resultadosOrdenados)
            {
                if (r.IsLimbo)
                {
                    csvBuilder.AppendLine($"{r.MatchId},N/A,Sin Asignar (Incompatibles),N/A,{r.RedName},{r.BlueName}");
                }
                else
                {
                    string dayName = AvailabilityHelper.DayToName(r.DayIndex);
                    csvBuilder.AppendLine($"{r.MatchId},{r.Week},{dayName},{r.Hour}:00,{r.RedName},{r.BlueName}");
                }
            }

            string extraInfo = matchesInLimbo > 0
                ? $"\n**Atención:** Hay **{matchesInLimbo} partido(s)** sin asignar en el limbo debido a incompatibilidades horarias."
                : "\nTodos los partidos se han programado con éxito!";

            var embed = new EmbedBuilder()
                .WithTitle("✅ Horarios Generados")
                .WithColor(matchesInLimbo > 0 ? Color.Orange : Color.Green)
                .WithDescription(
                    $"Horarios calculados y ordenados cronológicamente para la ronda `{roundId}`.\nCoste algorítmico: **{solver.ObjectiveValue}**{extraInfo}");

            using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(csvBuilder.ToString()));

            await FollowupWithFileAsync(
                stream,
                $"horarios_ronda_{roundId}.csv",
                embed: embed.Build()
            );
        }
        else
        {
            await FollowupAsync("**Error crítico:** Ni siquiera usando el Limbo se ha podido generar el horario. Revisa las reglas base del torneo.");
        }
    }

    [RequireFromEnvId("DISCORD_ADMIN_ROLE_ID")]
    [SlashCommand("import-schedules", "Importa un archivo CSV modificado y actualiza los horarios en la BD")]
    public async Task ImportSchedulesAsync(int roundId, string fechaInicioViernes, IAttachment csvFile)
    {
        await DeferAsync(ephemeral: false);

        if (!DateTime.TryParse(fechaInicioViernes, out DateTime baseDate))
        {
            await FollowupAsync("Formato de fecha inválido. Usa un formato como DD/MM/YYYY (ej: 06/03/2026).");
            return;
        }

        if (!csvFile.Filename.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
        {
            await FollowupAsync("El archivo debe ser un `.csv` válido.");
            return;
        }

        using var httpClient = new HttpClient();
        var csvContent = await httpClient.GetStringAsync(csvFile.Url);

        var lines = csvContent.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        if (lines.Length <= 1)
        {
            await FollowupAsync("El archivo CSV está vacío o no tiene las columnas correctas.");
            return;
        }

        await using var db = new ModelsContext();

        int actualizados = 0;
        int enviadosAlLimbo = 0;
        int errores = 0;

        foreach (var line in lines.Skip(1))
        {
            var cols = line.Split(',');
            if (cols.Length < 4) continue;

            string matchId = cols[0].Trim();
            string weekStr = cols[1].Trim();
            string dayStr = cols[2].Trim();
            string hourStr = cols[3].Trim();

            var match = await db.MatchRooms.FirstOrDefaultAsync(m => m.Id == matchId && m.RoundId == roundId);

            if (match == null)
            {
                errores++;
                continue;
            }

            if (weekStr == "N/A" || dayStr.Contains("Sin Asignar", StringComparison.OrdinalIgnoreCase))
            {
                match.StartTime = null;
                enviadosAlLimbo++;
                continue;
            }

            if (int.TryParse(weekStr, out int week))
            {
                int dayIndex = dayStr.ToLower() switch
                {
                    "viernes" => 0,
                    "sábado" => 1,
                    "sabado" => 1, // yep
                    "domingo" => 2,
                    "lunes" => 3,
                    _ => -1
                };

                if (dayIndex != -1 && hourStr.Contains(':'))
                {
                    string hourPart = hourStr.Split(':')[0];

                    if (int.TryParse(hourPart, out int hour))
                    {
                        int daysToAdd = ((week - 1) * 7) + dayIndex;

                        DateTime finalDate = baseDate.Date.AddDays(daysToAdd).AddHours(hour - 1);
                        match.StartTime = DateTime.SpecifyKind(finalDate, DateTimeKind.Utc);

                        actualizados++;
                    }
                    else
                    {
                        errores++;
                    }
                }
                else
                {
                    errores++;
                }
            }
            else
            {
                errores++;
            }
        }

        await db.SaveChangesAsync();

        var embed = new EmbedBuilder()
            .WithTitle("Horarios Importados con Éxito")
            .WithColor(errores > 0 ? Color.Orange : Color.Green)
            .WithDescription($"Se ha procesado y sincronizado el archivo **{csvFile.Filename}** con la base de datos para la ronda `{roundId}`.")
            .AddField("Actualizados", $"`{actualizados}` partidos", inline: true)
            .AddField("En el Limbo", $"`{enviadosAlLimbo}` partidos", inline: true)
            .AddField("Errores / No encontrados", $"`{errores}` partidos", inline: true);

        await FollowupAsync(embed: embed.Build());
    }

    /// <summary>
    /// This is how we translate availability into days of the week. Availability is stored like this:
    /// 00000000000000000000000000000000|00000000000000000000000000000000|00000000000000000000000000000000|00000000000000000000000000000000
    /// Each string of 0's represents a day of the week, starting with Friday. Each bit is an hour in 24-hour format
    /// LSB is 0:00, MSB is 23:00
    /// </summary>
    public static class AvailabilityHelper
    {
        private static readonly string[] DayLabels = { "Viernes", "Sábado", "Domingo", "Lunes" };

        public static string GetDayBits(string fullAvailability, int dayIndex)
        {
            if (string.IsNullOrEmpty(fullAvailability)) return new string('0', 24);

            var parts = fullAvailability.Split('|');
            return parts.Length > dayIndex ? parts[dayIndex] : new string('0', 24);
        }

        public static string DayToName(int i) => i >= 0 && i < DayLabels.Length ? DayLabels[i] : "Día no preferente.";

        public static bool IsAvailable(string availability, int dayIndex, int hour)
        {
            if (string.IsNullOrWhiteSpace(availability)) return false;

            var days = availability.Split('|');
            if (dayIndex >= days.Length) return false;

            string dayStr = days[dayIndex];

            if (dayStr.Length < 24) return false;

            int index = dayStr.Length - 1 - hour;

            if (index < 0 || index >= dayStr.Length) return false;

            return dayStr[index] == '1';
        }

        public static string GetDayName(int dayIndex) => dayIndex switch
        {
            0 => "Viernes",
            1 => "Sábado",
            2 => "Domingo",
            3 => "Lunes",
            _ => "Otro día"
        };
    }

    public class TimeSlot
    {
        public int Id { get; set; }
        public int DayIndex { get; set; }
        public int Hour { get; set; }
        public int PenaltyScore { get; set; }
        public int Week { get; set; }
    }
}