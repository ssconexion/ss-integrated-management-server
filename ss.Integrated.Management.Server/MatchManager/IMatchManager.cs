namespace ss.Internal.Management.Server.MatchManager;

/// <summary>
/// Defines the contract for an automated referee bot that manages a specific match lifecycle.
/// </summary>
public interface IMatchManager
{
    /// <summary>
    /// Triggers whenever there's a state change. Used for discord live match display
    /// </summary>
    public event Func<string, Task>? OnStateUpdated;
    
    /// <summary>
    /// Represents the finite states of the match flow.
    /// </summary>
    public enum MatchState
    {
        Idle,
        BanPhaseStart,
        WaitingForBanRed,
        WaitingForBanBlue,
        PickPhaseStart,
        SecondBanPhaseStart,
        WaitingForPickRed,
        WaitingForPickBlue,
        WaitingForStart,
        Playing,
        MatchFinished,
        OnTimeout,
        MatchOnHold,
    };

    
    /// <summary>
    /// Tracks what kind of message is being thrown around the systems.
    /// </summary>
    public enum MessageKind
    {
        PlayerMessage,
        PinOrderMessage,
        SystemMessage,
    }
    
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