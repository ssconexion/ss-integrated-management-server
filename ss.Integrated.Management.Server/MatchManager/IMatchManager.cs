using ss.Internal.Management.Server.Discord.Helpers;

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
    /// Triggers after a user has typed !mp settings and all the required messages to build
    /// the discord embed have been sent. 
    /// </summary>
    event Func<string, DiscordModels.MpSettingsResult, Task>? OnSettingsReceived;

    /// <summary>
    /// Represents the finite states of the match flow.
    /// </summary>
    public enum MatchState
    {
        /// <summary>
        /// The fallback state.
        /// </summary>
        Idle,

        /// <summary>
        /// Auxiliary state. Called whenever you want the first ban round to happen.
        /// </summary>
        BanPhaseStart,

        /// <summary>
        /// State where we are waiting for the red team to select a map to ban 
        /// </summary>
        WaitingForBanRed,

        /// <summary>
        /// State where we are waiting for the blue team to select a map to ban 
        /// </summary>
        WaitingForBanBlue,

        /// <summary>
        /// Auxiliary state. Called whenever you want the picking phase to start.
        /// </summary>
        PickPhaseStart,

        /// <summary>
        /// Auxiliary state. Called whenever you want the second ban round to happen.
        /// </summary>
        SecondBanPhaseStart,

        /// <summary>
        /// State where we are waiting for the red team to select a map to play 
        /// </summary>
        WaitingForPickRed,

        /// <summary>
        /// State where we are waiting for the blue team to select a map to play 
        /// </summary>
        WaitingForPickBlue,

        /// <summary>
        /// State where we are waiting for all the players to ready up.
        /// </summary>
        WaitingForStart,

        /// <summary>
        /// State where the players are playing the map
        /// </summary>
        Playing,

        /// <summary>
        /// Final state of a match.
        /// </summary>
        MatchFinished,

        /// <summary>
        /// If there's a timeout active, we will be on this state
        /// </summary>
        OnTimeout,

        /// <summary>
        /// If something has gone wrong for whatever reason, this is the state that will indicate that
        /// </summary>
        MatchOnHold,
    };


    /// <summary>
    /// Tracks what kind of message is being thrown around the systems.
    /// </summary>
    public enum MessageKind
    {
        /// <summary>
        /// Normal messages being sent across discord and bancho
        /// </summary>
        PlayerMessage,

        /// <summary>
        /// A message that the discord side will try pinning
        /// </summary>
        PinOrderMessage,

        /// <summary>
        /// Internal communication messages that need some kind of special treatment
        /// </summary>
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