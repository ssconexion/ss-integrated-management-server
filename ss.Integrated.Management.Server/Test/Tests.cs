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
            var osuUser1 = new Models.OsuUser { Id = 727, DisplayName = "Team1" };
            var osuUser2 = new Models.OsuUser { Id = 12431, DisplayName = "Team2" };

            if (!db.Set<Models.OsuUser>().Any())
            {
                db.AddRange(osuUser1, osuUser2);
                await db.SaveChangesAsync();
            }

            // 2. Crear Usuarios de App (TeamInfo)
            var team1 = new Models.TeamInfo { Id = Guid.NewGuid().ToString(), OsuID = 727, DiscordID = "D_1" };
            var team2 = new Models.TeamInfo { Id = Guid.NewGuid().ToString(), OsuID = 12431, DiscordID = "D_2" };

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
                MapPool = new List<Models.RoundBeatmap> { new() { BeatmapID = 100, slot = "NM1" } }
            };

            if (!db.Set<Models.Round>().Any())
            {
                db.Add(roundTemplate);
                await db.SaveChangesAsync();
            }

            // 4. CREAR EL MATCH (Usando Snapshots)
            // Aquí es donde "congelamos" los datos de las tablas en el JSON del partido
            var match = new Models.Match
            {
                Id = "MATCH_TEST_05",
                Type = (int)Models.MatchType.EliminationStage,
                StartTime = DateTime.UtcNow,

                // CONVERTIR ENTIDAD -> SNAPSHOT
                TeamRed = new Models.TeamSnapshot
                {
                    Id = team1.Id,
                    OsuID = team1.OsuID,
                    DiscordID = team1.DiscordID,
                    DisplayName = team1.DisplayName // Guardamos "Cookiezi"
                },

                TeamBlue = new Models.TeamSnapshot
                {
                    Id = team2.Id,
                    OsuID = team2.OsuID,
                    DiscordID = team2.DiscordID,
                    DisplayName = team2.DisplayName
                },

                Round = new Models.RoundSnapshot
                {
                    Id = roundTemplate.Id,
                    Name = roundTemplate.DisplayName,
                    BestOf = roundTemplate.BestOf,
                    BanRounds = roundTemplate.BanRounds,
                    Mode = roundTemplate.Mode,
                    MapPool = roundTemplate.MapPool // Copiamos la lista
                },

                Referee = new Models.RefereeSnapshot
                {
                    Id = 1,
                    Name = "Furina",
                    IRC = "77bc905b"
                }
            };

            if (!await db.Matches.AnyAsync(m => m.Id == match.Id))
            {
                db.Matches.Add(match);
                await db.SaveChangesAsync();
                Console.WriteLine("Match creado exitosamente con Snapshots.");
            }
            else
            {
                Console.WriteLine("El match ya existía.");
            }

            // LEER PARA COMPROBAR
            var savedMatch = await db.Matches.FirstOrDefaultAsync(x => x.Id == "MATCH_TEST_05" );
            Console.WriteLine($"Leído de DB -> Red: {savedMatch.TeamRed.DisplayName} vs Blue: {savedMatch.TeamBlue.DisplayName}");
        }
    }
};