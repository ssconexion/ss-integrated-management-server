using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using ss.Internal.Management.Server.AutoRef;
using ss.Internal.Management.Server.Resources;

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
    [SlashCommand("importscores", "Sube un archivo .txt/.csv con los resultados de un match")]
    public async Task ImportScoresAsync(
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
        };

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
                    // D. Bloqueamos la hora LOCAL (ej: 18)
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
    }
}