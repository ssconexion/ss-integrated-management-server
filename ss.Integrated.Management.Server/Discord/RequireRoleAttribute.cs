using Discord;
using Discord.Interactions;
using Discord.WebSocket;

namespace ss.Internal.Management.Server.Discord;

/// <summary>
/// A custom precondition attribute that restricts command execution to users with a specific Discord Role.
/// The Role ID is fetched dynamically from an Environment Variable.
/// </summary>
public class RequireFromEnvIdAttribute : PreconditionAttribute
{
    private readonly string envVarName;
    
    /// <summary>
    /// Initializes the attribute with the name of the environment variable containing the Role ID.
    /// </summary>
    /// <param name="envVarName">Key of the env var (e.g. "DISCORD_REFEREE_ROLE_ID").</param>
    public RequireFromEnvIdAttribute(string envVarName)
    {
        this.envVarName = envVarName;
    }

    public override Task<PreconditionResult> CheckRequirementsAsync(IInteractionContext context, ICommandInfo commandInfo, IServiceProvider services)
    {
        string? roleIdString = Environment.GetEnvironmentVariable(envVarName);
        
        if (string.IsNullOrEmpty(roleIdString))
        {
            return Task.FromResult(PreconditionResult.FromError($"Error interno: La variable de entorno '{envVarName}' no está configurada en el servidor."));
        }

        if (!ulong.TryParse(roleIdString, out ulong roleId))
        {
            return Task.FromResult(PreconditionResult.FromError($"Error interno: La variable '{envVarName}' no contiene una ID numérica válida."));
        }
        
        if (context.User is SocketGuildUser user)
        {
            return Task.FromResult(user.Roles.Any(r => r.Id == roleId) ? PreconditionResult.FromSuccess() : PreconditionResult.FromError("No tienes el rol necesario para ejecutar este comando."));
        }

        return Task.FromResult(PreconditionResult.FromError("Este comando solo funciona dentro de un servidor."));
    }
}