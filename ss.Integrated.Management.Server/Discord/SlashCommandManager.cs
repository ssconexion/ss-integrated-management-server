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
    [SlashCommand("startref", "Inicia un nuevo match y crea su canal")]
    public async Task StartRefAsync(string matchId, string referee)
    {
        await DeferAsync(ephemeral: false);

        await using var db = new ModelsContext();

        if (await db.Referees.FirstOrDefaultAsync(r => r.DisplayName == referee) != null)
        {
            bool created = await Manager.CreateMatchEnvironmentAsync(matchId, referee, Context.Guild);

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
    [SlashCommand("endref", "Finaliza el match y borra el canal")]
    public async Task EndRefAsync(string matchId)
    {
        await RespondAsync($"Procesando cierre para **{matchId}**...");
        
        await Manager.EndMatchEnvironmentAsync(matchId, Context.Channel);
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
}