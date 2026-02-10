using Discord;
using Discord.Interactions;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualBasic.CompilerServices;
using ss.Internal.Management.Server.AutoRef;

namespace ss.Internal.Management.Server.Discord;

public class SlashCommandManager : InteractionModuleBase<SocketInteractionContext>
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

        if (db.MatchRooms.First(m => m.Id == matchId).IsOver)
        {
            await RespondAsync($"Procesando cierre para **{matchId}**...");
            await Manager.EndMatchEnvironmentAsync(matchId, Context.Channel);            
        }
        else
        {
            await RespondAsync($"La match {matchId} aún no ha procesado sus scores. Procesalas e intenta de nuevo.");
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
        [Summary("match_id", "El ID del match (ej: 15 o A1)")] string matchId, 
        [Summary("archivo", "El archivo txt/csv con los datos raw")] IAttachment file)
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
                db.MatchRooms.First(m => m.Id == matchId).IsOver = true;
                await db.SaveChangesAsync();
            }
            else
            {
                await FollowupAsync($"**Procesado sin cambios:** El archivo se leyó pero no se guardaron filas. \nPossible causas:\n- MatchID incorrecto.\n- IDs de mapas no coinciden con el pool de la ronda.\n- Usuarios no existen en la DB.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
            await FollowupAsync($"**Error Crítico:** {ex.Message}");
        }
    }
}