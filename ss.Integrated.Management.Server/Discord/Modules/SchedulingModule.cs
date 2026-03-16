using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Google.OrTools.Sat;
using Humanizer;
using Microsoft.EntityFrameworkCore;
using ss.Internal.Management.Server.AutoRef;
using ss.Internal.Management.Server.Discord.Helpers;

namespace ss.Internal.Management.Server.Discord.Modules;

public class SchedulingModule : InteractionModuleBase<SocketInteractionContext>
{

    public static Dictionary<string, DiscordModels.PendingMatch> PendingMatches = new();

    [RequireFromEnvId("DISCORD_REFEREE_ROLE_ID")]
    [SlashCommand("reschedulematchup", "Reschedulea una match. Introduce el tiempo en UTC.")]
    public async Task UpdateMatchTimeAsync(string matchId, string date, string hour)
    {
        await DeferAsync(ephemeral: false);
        await using var db = new ModelsContext();

        var match = await db.MatchRooms
            .Include(matchRoom => matchRoom.TeamRed)
            .Include(matchRoom => matchRoom.TeamBlue)
            .FirstOrDefaultAsync(room => room.Id == matchId);

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

        long unixTime = 1;

        if (match.StartTime != null)
        {
            unixTime = ((DateTimeOffset)match.StartTime).ToUnixTimeSeconds();
        }

        var embed = new EmbedBuilder()
            .WithTitle($"✅ Reschedule exitoso para match {match.Id}")
            .WithColor(Color.Green)
            .WithDescription($"`{match.TeamRed.DisplayName}` vs `{match.TeamBlue.DisplayName}`\nHora nueva de comienzo: <t:{unixTime}:f>");

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

        var slots = new List<DiscordModels.TimeSlot>();
        int slotIdCounter = 0;

        for (int week = 1; week <= 2; week++)
        {
            for (int day = 0; day < 4; day++)
            {
                int penalty = (day == 0) ? 3 : (day == 3) ? 50 : 0;

                for (int hour = 16; hour <= 23; hour++)
                {
                    slots.Add(new DiscordModels.TimeSlot { Id = ++slotIdCounter, DayIndex = day, Hour = hour, PenaltyScore = penalty, Week = week });
                }
            }
        }

        slots.Add(new DiscordModels.TimeSlot { Id = 999, DayIndex = -1, Hour = 0, PenaltyScore = 10000, Week = 0 });

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
    [SlashCommand("generate-schedules-bracket", "Genera los horarios de la ronda especificada automáticamente según la disponibilidad.")]
    public async Task GenerateBracketScheduleAsync(int roundId, string fechaInicioViernes)
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

        var slots = new List<DiscordModels.TimeSlot>();
        int slotIdCounter = 0;

        for (int day = 0; day < 4; day++)
        {
            int penalty = (day == 0) ? 3 : (day == 3) ? 50 : 0;

            for (int hour = 16; hour <= 21; hour++)
            {
                slots.Add(new DiscordModels.TimeSlot { Id = ++slotIdCounter, DayIndex = day, Hour = hour, PenaltyScore = penalty, Week = 1 });
            }
        }


        slots.Add(new DiscordModels.TimeSlot { Id = 999, DayIndex = -1, Hour = 0, PenaltyScore = 10000, Week = 0 });

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
    [SlashCommand("fill-room", "rellena una sala yo estoy cansado ya joder")]
    public async Task FillRoom(string matchId, string teamRedName, string teamBlueName)
    {
        await DeferAsync(ephemeral: false);
        
        try
        {
            await using var db = new ModelsContext();
            
            var match = await db.MatchRooms.FirstOrDefaultAsync(m => m.Id == matchId);
            if (match == null)
            {
                await FollowupAsync($"**Error:** No se encontró ningún partido con la ID `{matchId}`.");
                return;
            }
            
            var teamRed = await db.Users.FirstOrDefaultAsync(u => u.OsuData.Username.ToLower() == teamRedName.ToLower());
            if (teamRed == null)
            {
                await FollowupAsync($"**Error:** No se encontró al jugador/equipo Rojo llamado `{teamRedName}`.");
                return;
            }
            
            var teamBlue = await db.Users.FirstOrDefaultAsync(u => u.OsuData.Username.ToLower() == teamBlueName.ToLower());
            if (teamBlue == null)
            {
                await FollowupAsync($"**Error:** No se encontró al jugador/equipo Azul llamado `{teamBlueName}`.");
                return;
            }
            
            match.TeamRedId = teamRed.Id;
            match.TeamBlueId = teamBlue.Id;

            await db.SaveChangesAsync();

            await FollowupAsync($"✅ **Partido {matchId} actualizado con éxito:**\n🔴 `{teamRed.OsuData.Username}` vs 🔵 `{teamBlue.OsuData.Username}`");
        }
        catch (Exception ex)
        {
            await FollowupAsync($"**Ocurrió un error al rellenar el partido:** {ex.Message}");
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

    [RequireFromEnvId("DISCORD_REFEREE_ROLE_ID")]
    [SlashCommand("lookup-match", "Devuelve la información de una match a partir de una ID")]
    public async Task LookupMatch(string matchId)
    {
        await DeferAsync(ephemeral: false);
        await using var db = new ModelsContext();

        var match = await db.MatchRooms
            .Include(room => room.TeamBlue)
            .Include(room => room.TeamRed)
            .FirstOrDefaultAsync(room => room.Id == matchId);

        if (match == null)
        {
            await FollowupAsync("No se ha encontrado una match con la ID especificada");
            return;
        }

        long unixTime = 1;

        if (match.StartTime != null)
        {
            unixTime = ((DateTimeOffset)match.StartTime).ToUnixTimeSeconds();
        }

        var embed = new EmbedBuilder()
            .WithTitle($"Match {match.Id}")
            .WithColor(Color.Blue)
            .WithDescription($"`{match.TeamRed.DisplayName}` vs `{match.TeamBlue.DisplayName}`\nHora de comienzo: <t:{unixTime}:f>");

        await FollowupAsync(embed: embed.Build());
    }

    [RequireFromEnvId("DISCORD_REFEREE_ROLE_ID")]
    [SlashCommand("check-availability", "Muestra la disponibilidad visual de ambos jugadores de un match")]
    public async Task CheckMatchAvailabilityAsync(string matchId)
    {
        await DeferAsync(ephemeral: false);
        await using var db = new ModelsContext();

        var match = await db.MatchRooms
            .Include(m => m.TeamRed)
            .Include(m => m.TeamBlue)
            .FirstOrDefaultAsync(m => m.Id == matchId);

        if (match == null)
        {
            await FollowupAsync("No se ha encontrado un partido con la ID especificada");
            return;
        }

        var p1 = await db.Players.FirstOrDefaultAsync(p => p.UserId == match.TeamRedId);
        var p2 = await db.Players.FirstOrDefaultAsync(p => p.UserId == match.TeamBlueId);

        string redName = match.TeamRed?.DisplayName ?? "Desconocido";
        string blueName = match.TeamBlue?.DisplayName ?? "Desconocido";

        var embed = new EmbedBuilder()
            .WithTitle($"Disponibilidad - Match {match.Id}")
            .WithDescription($"**Rojo:** `{redName}` vs **Azul:** `{blueName}`\n*Mostrando franja de 16:00 a 23:00 UTC*")
            .WithFooter("Te metes al adminer pa mirar el resto")
            .WithColor(Color.Blue);

        int startHour = 16;
        int endHour = 23;

        var horasArray = Enumerable.Range(startHour, endHour - startHour + 1).Select(h => h.ToString("00"));
        string headerHoras = "`" + string.Join(" ", horasArray) + "`";

        for (int day = 0; day < 4; day++)
        {
            string dayName = AvailabilityHelper.DayToName(day);

            string p1Emojis = "";
            string p2Emojis = "";

            for (int hour = startHour; hour <= endHour; hour++)
            {
                bool p1Avail = p1 != null && AvailabilityHelper.IsAvailable(p1.Availability, day, hour);
                bool p2Avail = p2 != null && AvailabilityHelper.IsAvailable(p2.Availability, day, hour);

                p1Emojis += p1Avail ? "🟢 " : "🔴 ";
                p2Emojis += p2Avail ? "🟢 " : "🔴 ";
            }

            string fieldContent =
                $"{headerHoras}\n" +
                $"{p1Emojis} **{redName}**\n" +
                $"{p2Emojis} **{blueName}**";

            embed.AddField(dayName, fieldContent, inline: false);
        }

        if (p1 == null || string.IsNullOrWhiteSpace(p1.Availability))
            embed.Description += $"\n⚠️ *{redName} no tiene registrada su disponibilidad en la BD.*";

        if (p2 == null || string.IsNullOrWhiteSpace(p2.Availability))
            embed.Description += $"\n⚠️ *{blueName} no tiene registrada su disponibilidad en la BD.*";

        await FollowupAsync(embed: embed.Build());
    }
}