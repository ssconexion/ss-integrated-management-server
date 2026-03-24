namespace ss.Internal.Management.Server.Discord.Helpers;

/// <summary>
/// Models used by the discord integration component
/// </summary>
public class DiscordModels
{
    /// <summary>
    /// Info assigned to each timeslot available. Used for scheduling purposes.
    /// </summary>
    public class TimeSlot
    {
        /// <summary>
        /// Primary Key.
        /// </summary>
        public int Id { get; set; }
        
        /// <summary>
        /// Represents the day of the weekend. 0 -> Friday, 1 -> Saturday, 2 -> Sunday, 3 -> Monday.
        /// </summary>
        public int DayIndex { get; set; }
        
        /// <summary>
        /// The hour in a 24h format.
        /// </summary>
        public int Hour { get; set; }
        
        /// <summary>
        /// Abstract number used for the CP solver. Should be assigned relative to other scores applied .
        /// </summary>
        public int PenaltyScore { get; set; }
        
        /// <summary>
        /// Weekend starting from 1. Used when matches are spread over multiple weekends.
        /// </summary>
        public int Week { get; set; }
    }
    
    /// <summary>
    /// Match snapshot used for scheduling.
    /// </summary>
    public class PendingMatch
    {
        public string MatchId { get; set; }
        public int RedId { get; set; }
        public int BlueId { get; set; }
        public DateTime ReferenceDate { get; set; }
        public string AvailRed { get; set; }
        public string AvailBlue { get; set; }
        public string RedName { get; set; }
        public string BlueName { get; set; }
        public int RoundId { get; set; }
    }
    
    /// <summary>
    /// Information extracted from an !mp settings command in a digestible format
    /// </summary>
    /// <param name="RoomName">The name of the room.</param>
    /// <param name="HistoryUrl">MP Link associated to the room</param>
    /// <param name="BeatmapUrl">Active beatmap URL</param>
    /// <param name="BeatmapName">Active beatmap name</param>
    /// <param name="TeamMode">Active team mode (H2H, TeamVS, etc...)</param>
    /// <param name="WinCondition">ScoreV1, ScoreV2, Acc, etc..</param>
    /// <param name="ActiveMods">Mods that are globally active</param>
    /// <param name="Slots">List containing each player in the room. See <see cref="SlotInfo"/> to see what is being parsed.</param>
    public record MpSettingsResult(
        string RoomName,
        string HistoryUrl,
        string BeatmapUrl,
        string BeatmapName,
        string TeamMode,
        string WinCondition,
        string ActiveMods,
        IReadOnlyList<SlotInfo> Slots
    );

    /// <summary>
    /// A slot occupied by a player with a given information
    /// </summary>
    /// <param name="SlotNumber">The slot number being used</param>
    /// <param name="IsReady">User is ready (or not)</param>
    /// <param name="ProfileUrl">User's link to their profile</param>
    /// <param name="Username">The user's display name</param>
    /// <param name="Team">Team color of the user</param>
    /// <param name="Mods">Mods being used by the player</param>
    public record SlotInfo(
        int SlotNumber,
        bool IsReady,
        string ProfileUrl,
        string Username,
        string Team,
        string Mods
    );
}