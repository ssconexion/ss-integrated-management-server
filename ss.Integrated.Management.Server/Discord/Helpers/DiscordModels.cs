namespace ss.Internal.Management.Server.Discord.Helpers;

public class DiscordModels
{
    public class TimeSlot
    {
        public int Id { get; set; }
        public int DayIndex { get; set; }
        public int Hour { get; set; }
        public int PenaltyScore { get; set; }
        public int Week { get; set; }
    }
    
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

    public record SlotInfo(
        int SlotNumber,
        bool IsReady,
        string ProfileUrl,
        string Username,
        string Team,
        string Mods
    );
}