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
            return Task.FromResult(PreconditionResult.FromError($"Error: Environment variable '{envVarName}' is not configured."));
        }

        if (!ulong.TryParse(roleIdString, out ulong roleId))
        {
            return Task.FromResult(PreconditionResult.FromError($"Error: Environment variable '{envVarName}' does not have a valid ID"));
        }
        
        if (context.User is SocketGuildUser user)
        {
            return Task.FromResult(user.Roles.Any(r => r.Id == roleId) ? PreconditionResult.FromSuccess() : PreconditionResult.FromError("You don't have a valid role for this."));
        }

        return Task.FromResult(PreconditionResult.FromError("This command only works inside a given server."));
    }
}