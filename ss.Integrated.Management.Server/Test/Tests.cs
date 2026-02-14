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
            var osuUser1 = new Models.OsuUser { Id = 18217876, Username = "ESCRUPULILLO" };
            var osuUser2 = new Models.OsuUser { Id = 16512684, Username = "towny1" };

            if (!db.Set<Models.OsuUser>().Any())
            {
                db.AddRange(osuUser1, osuUser2);
                await db.SaveChangesAsync();
            }

            // 2. Crear Usuarios de App (TeamInfo)
            var team1 = new Models.User { OsuID = 18217876, DiscordID = "234547235647" };
            var team2 = new Models.User { OsuID = 16512684, DiscordID = "348756234" };

            if (!db.Set<Models.User>().Any())
            {
                db.AddRange(team1, team2);
                await db.SaveChangesAsync();
                // Recargamos para asegurar que el AutoInclude traiga los nombres
                team1 = await db.Set<Models.User>().FirstAsync(t => t.OsuID == 18217876);
                team2 = await db.Set<Models.User>().FirstAsync(t => t.OsuID == 16512684);
            }
            else
            {
                // Si ya existen, los cargamos
                team1 = await db.Set<Models.User>().FirstAsync();
                team2 = await db.Set<Models.User>().Skip(1).FirstAsync();
            }

            // 3. Crear Ronda Template (Tabla round)
            var roundTemplate = new Models.Round
            {
                Id = 1,
                DisplayName = "Grand Finals",
                BestOf = 13,
                BanRounds = 2,
                Mode = Models.BansType.SpanishShowdown,
                MapPool = new List<Models.RoundBeatmap>
                {
                    new() { BeatmapID = 4579522, Slot = "NM1" },
                    new() { BeatmapID = 4277943, Slot = "NM2" },
                    new() { BeatmapID = 3269529, Slot = "NM3" },
                    new() { BeatmapID = 4723710, Slot = "NM4" },
                    new() { BeatmapID = 4096455, Slot = "NM5" },
                    new() { BeatmapID = 4497114, Slot = "NM6" },
                    new() { BeatmapID = 5177739, Slot = "HD1" },
                    new() { BeatmapID = 4274519, Slot = "HD2" },
                    new() { BeatmapID = 4798495, Slot = "HD3" },
                    new() { BeatmapID = 4761303, Slot = "HD4" },
                    new() { BeatmapID = 4931381, Slot = "HR1" },
                    new() { BeatmapID = 3214479, Slot = "HR2" },
                    new() { BeatmapID = 3609114, Slot = "HR3" },
                    new() { BeatmapID = 4881729, Slot = "HR4" },
                    new() { BeatmapID = 1872396, Slot = "DT1" },
                    new() { BeatmapID = 5178950, Slot = "DT2" },
                    new() { BeatmapID = 2458659, Slot = "DT3" },
                    new() { BeatmapID = 4951914, Slot = "DT4" },
                    new() { BeatmapID = 4691422, Slot = "TB1" },
                },
            };
            
            var roundTemplate2 = new Models.Round
            {
                Id = 2,
                DisplayName = "Group Stage",
                BestOf = 9,
                BanRounds = 1,
                Mode = Models.BansType.SpanishShowdown,
                MapPool = new List<Models.RoundBeatmap>
                {
                    new() { BeatmapID = 4305272, Slot = "NM1" },
                    new() { BeatmapID = 4032765, Slot = "NM2" },
                    new() { BeatmapID = 4187402, Slot = "NM3" },
                    new() { BeatmapID = 4745015, Slot = "NM4" },
                    new() { BeatmapID = 3832921, Slot = "NM5" },
                    new() { BeatmapID = 4128475, Slot = "HD1" },
                    new() { BeatmapID = 3597015, Slot = "HD2" },
                    new() { BeatmapID = 3689413, Slot = "HR1" },
                    new() { BeatmapID = 4478358, Slot = "HR2" },
                    new() { BeatmapID = 4253516, Slot = "DT1" },
                    new() { BeatmapID = 4741558, Slot = "DT2" },
                    new() { BeatmapID = 3155518, Slot = "DT3" },
                    new() { BeatmapID = 4365544, Slot = "TB1" },
                    
                },
            };
            
            var roundTemplate3 = new Models.Round
            {
                Id = 3,
                DisplayName = "Qualifiers",
                BestOf = 9,
                BanRounds = 1,
                Mode = Models.BansType.SpanishShowdown,
                MapPool = new List<Models.RoundBeatmap>
                {
                    new() { BeatmapID = 2918331, Slot = "NM1" },
                    new() { BeatmapID = 5245557, Slot = "NM2" },
                    new() { BeatmapID = 5231751, Slot = "NM3" },
                    new() { BeatmapID = 5007159, Slot = "NM4" },
                    new() { BeatmapID = 5181796, Slot = "HD1" },
                    new() { BeatmapID = 4349038, Slot = "HR1" },
                    new() { BeatmapID = 2219980, Slot = "HR2" },
                    new() { BeatmapID = 790428, Slot = "DT1" },
                    new() { BeatmapID = 3842022, Slot = "DT2" },
                    
                },
            };

            if (!db.Set<Models.Round>().Any())
            {
                db.Add(roundTemplate);
                db.Add(roundTemplate2);
                db.Add(roundTemplate3);
                await db.SaveChangesAsync();
            }

            // 4. CREAR EL MATCH
            var match = new Models.MatchRoom
            {
                Id = "69",
                StartTime = DateTime.UtcNow,
                TeamRedId = team1.Id,
                TeamBlueId = team2.Id,
                RoundId = roundTemplate.Id,
                RefereeId = null,
            };
            
            var match2 = new Models.MatchRoom
            {
                Id = "67",
                StartTime = DateTime.UtcNow,
                TeamRedId = team1.Id,
                TeamBlueId = team2.Id,
                RoundId = roundTemplate2.Id,
                RefereeId = null,
            };
            
            var qualis = new Models.QualifierRoom
            {
                Id = "1444",
                StartTime = DateTime.UtcNow,
                RoundId = roundTemplate3.Id,
                RefereeId = null,
            };

            if (!await db.MatchRooms.AnyAsync(m => m.Id == match.Id))
            {
                db.MatchRooms.Add(match);
                db.MatchRooms.Add(match2);
                db.QualifierRooms.Add(qualis);
                await db.SaveChangesAsync();
                Console.WriteLine("Match creado exitosamente.");
            }
            else
            {
                Console.WriteLine("El match ya existía.");
            }

            // LEER PARA COMPROBAR
            var savedMatch = await db.MatchRooms.FirstOrDefaultAsync(x => x.Id == "A5");
            if (savedMatch != null)
            {
                Console.WriteLine($"Leído de DB -> Red: {savedMatch.TeamRed.DisplayName} vs Blue: {savedMatch.TeamBlue.DisplayName}");
            }
        }
    }
};