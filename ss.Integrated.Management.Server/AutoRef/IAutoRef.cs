namespace ss.Internal.Management.Server.AutoRef;

/// <summary>
/// Defines the contract for an automated referee bot that manages a specific match lifecycle.
/// </summary>
public interface IAutoRef
{
    /// <summary>
    /// Initializes the connection to Bancho and joins the match lobby.
    /// </summary>
    Task StartAsync();

    /// <summary>
    /// Gracefully stops the automation, saves the state to the DB, and parts the channel.
    /// </summary>
    Task StopAsync();

    /// <summary>
    /// Proxies a message from a Discord channel to the Bancho IRC lobby.
    /// </summary>
    /// <param name="content">The raw message content to send.</param>
    Task SendMessageFromDiscord(string content);
}