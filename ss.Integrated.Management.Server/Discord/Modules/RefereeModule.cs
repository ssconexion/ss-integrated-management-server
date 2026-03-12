using System.Diagnostics;
using System.Text.Json;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using ss.Internal.Management.Server.AutoRef;
using ss.Internal.Management.Server.Discord.Helpers;

namespace ss.Internal.Management.Server.Discord.Modules;

public class RefereeModule : InteractionModuleBase<SocketInteractionContext>
{
    public DiscordManager Manager { get; set; }

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
            await FollowupAsync($"Partido finalizado. Archivando hilo {matchId}...");
        }
        else
        {
            await FollowupAsync("No se encontró un worker con esa ID");
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
    [SlashCommand("forfeit-results-embed", "Embed auxiliar por si falla el lector")]
    private async Task AuxEmbedForfeitCreation(string matchId, bool trueIfRedFF)
    {
        await DeferAsync(ephemeral: false);
        
        await using var db = new ModelsContext();

        EmbedBuilder embed;

        var match = await db.MatchRooms.Include(matchRoom => matchRoom.TeamRed).Include(matchRoom => matchRoom.TeamBlue).Include(matchRoom => matchRoom.Referee)
            .FirstOrDefaultAsync(m => m.Id == matchId);

        string ffstring = trueIfRedFF ? "🔵 El equipo azul gana por defecto" : "🔴 El equipo rojo gana por defecto";
        
        if (match != null)
        {
            embed = new EmbedBuilder()
                .WithTitle($"{match.Id}: 🔴 {match.TeamRed.DisplayName} vs {match.TeamBlue.DisplayName} 🔵")
                .AddField("Marcador", ffstring, false);
                
            embed.WithCurrentTimestamp();
        }
        else
        {
            embed = new EmbedBuilder().WithTitle("Cargando partido...");
        }

        await FollowupAsync(embed: embed.Build());
    }

    [RequireFromEnvId("DISCORD_REFEREE_ROLE_ID")]
    [SlashCommand("match-results-embed", "Embed auxiliar por si falla el lector")]
    private async Task AuxEmbedCreation(string matchId)
    {
        await DeferAsync(ephemeral: false);
        
        await using var db = new ModelsContext();

        EmbedBuilder embed;

        var match = await db.MatchRooms.Include(matchRoom => matchRoom.TeamRed).Include(matchRoom => matchRoom.TeamBlue).Include(matchRoom => matchRoom.Referee)
            .FirstOrDefaultAsync(m => m.Id == matchId);

        if (match != null)
        {
            embed = new EmbedBuilder()
                .WithTitle($"{match.Id}: {match.TeamRed.DisplayName} vs {match.TeamBlue.DisplayName}")
                .WithUrl($"https://osu.ppy.sh/mp/{match.MpLinkId}")
                .AddField("Marcador", $"🔴 **{match.TeamRedScore}** - **{match.TeamBlueScore}** 🔵", false)
                .AddField("Estado Actual", $"`MatchFinished`", false);

            Debug.Assert(match.BannedMaps != null, "match.BannedMaps != null");
            string bans = match.BannedMaps.Any()
                ? string.Join("\n", match.BannedMaps.Select(m => $"{(m.TeamColor == Models.TeamColor.TeamRed ? "🔴" : "🔵")} {m.Slot}"))
                : "*Ninguno todavía*";

            Debug.Assert(match.PickedMaps != null, "match.PickedMaps != null");
            string picks = match.PickedMaps.Any()
                ? string.Join("\n", match.PickedMaps.Select(m =>
                {
                    string picker = m.TeamColor == Models.TeamColor.TeamRed ? "🔴" : "🔵";

                    string winnerIndicator = "";

                    if (m.Winner == Models.TeamColor.TeamRed)
                        winnerIndicator = " ➔ 🔴 Wins!";
                    else if (m.Winner == Models.TeamColor.TeamBlue)
                        winnerIndicator = " ➔ 🔵 Wins!";

                    if (m.TeamColor == Models.TeamColor.None)
                        picker = "🟣";

                    return $"{picker} **{m.Slot}**{winnerIndicator}";
                }))
                : "*Ninguno todavía*";

            embed.AddField("Bans", bans, true);
            embed.AddField("Picks", picks, true);

            embed.WithFooter($"Árbitro: {match.Referee.DisplayName}");
            embed.WithCurrentTimestamp();
        }
        else
        {
            embed = new EmbedBuilder().WithTitle("Cargando partido...");
        }

        await FollowupAsync(embed: embed.Build());
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

        SchedulingModule.PendingMatches[tempId] = new DiscordModels.PendingMatch
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

        if (!SchedulingModule.PendingMatches.TryGetValue(tempId, out var pm))
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
                desc = $"Solo {SchedulingModule.PendingMatches[tempId].RedName} disponible";
            }
            else if (b == '1')
            {
                emoji = "🔵";
                desc = $"Solo {SchedulingModule.PendingMatches[tempId].BlueName} disponible";
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

        if (!SchedulingModule.PendingMatches.TryGetValue(tempId, out var pm))
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

            SchedulingModule.PendingMatches.Remove(tempId);

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

    public Embed CreateMatchEmbed(List<Models.MatchRoom> allMatches, int page, out MessageComponent components)
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
}