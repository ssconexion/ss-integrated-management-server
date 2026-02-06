using Microsoft.EntityFrameworkCore;
using ss.Internal.Management.Server.AutoRef;

namespace ss.Integrated.Management.Server
{
    public partial class Tests
    {
        // EL SUPER TEST ESTE LO HA MONTAO UNA IA NO YO QUE ME DABA MUCHO PALO
        public static async Task TestFill()
        {
            await using var db = new ModelsContext();
            await db.Database.MigrateAsync(); // Asegura DB creada

            Console.WriteLine("--- Iniciando Seed ---");

            // 1. Crear Osu Users (Cache)
            var osuUser1 = new Models.OsuUser { Id = 727, DisplayName = "Qualifiers" };
            var osuUser2 = new Models.OsuUser { Id = 12431, DisplayName = "A4" };

            if (!db.Set<Models.OsuUser>().Any())
            {
                db.AddRange(osuUser1, osuUser2);
                await db.SaveChangesAsync();
            }

            // 2. Crear Usuarios de App (TeamInfo)
            var team1 = new Models.TeamInfo { OsuID = 727, DiscordID = "234547235647" };
            var team2 = new Models.TeamInfo { OsuID = 12431, DiscordID = "348756234" };

            if (!db.Set<Models.TeamInfo>().Any())
            {
                db.AddRange(team1, team2);
                await db.SaveChangesAsync();
                // Recargamos para asegurar que el AutoInclude traiga los nombres
                team1 = await db.Set<Models.TeamInfo>().FirstAsync(t => t.OsuID == 727);
                team2 = await db.Set<Models.TeamInfo>().FirstAsync(t => t.OsuID == 12431);
            }
            else
            {
                // Si ya existen, los cargamos
                team1 = await db.Set<Models.TeamInfo>().FirstAsync();
                team2 = await db.Set<Models.TeamInfo>().Skip(1).FirstAsync();
            }

            // 3. Crear Ronda Template (Tabla round)
            var roundTemplate = new Models.Round
            {
                Id = 1,
                DisplayName = "Finals",
                BestOf = 7,
                Mode = Models.BansType.SpanishShowdown,
                MapPool = new List<Models.RoundBeatmap>
                {
                    new() { BeatmapID = 1453229, Slot = "NM1" },
                    new() { BeatmapID = 1453229, Slot = "HD1" },
                    new() { BeatmapID = 1453229, Slot = "HR1" },
                    new() { BeatmapID = 1453229, Slot = "DT1" },
                },
            };

            if (!db.Set<Models.Round>().Any())
            {
                db.Add(roundTemplate);
                await db.SaveChangesAsync();
            }

            // 4. CREAR EL MATCH
            var match = new Models.Match
            {
                Id = "A5",
                Type = Models.MatchType.QualifiersStage,
                StartTime = DateTime.UtcNow,
                TeamRedId = team1.Id,
                TeamBlueId = team2.Id,
                RoundId = roundTemplate.Id,
                Referee = new Models.RefereeInfo
                {
                    Id = 1,
                    DisplayName = "Furina",
                    OsuID = 16393244,
                    DiscordID = 404328124961128453,
                    IRC = "77bc905b"
                }
            };

            if (!await db.Matches.AnyAsync(m => m.Id == match.Id))
            {
                db.Matches.Add(match);
                await db.SaveChangesAsync();
                Console.WriteLine("Match creado exitosamente.");
            }
            else
            {
                Console.WriteLine("El match ya existía.");
            }

            // LEER PARA COMPROBAR
            var savedMatch = await db.Matches.FirstOrDefaultAsync(x => x.Id == "A4");
            if (savedMatch != null)
            {
                Console.WriteLine($"Leído de DB -> Red: {savedMatch.TeamRed.DisplayName} vs Blue: {savedMatch.TeamBlue.DisplayName}");
            }
        }
    }
};