using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ss.Internal.Management.Server.AutoRef;

/// <summary>
/// Container for all Entity Framework Core data models.
/// </summary>
public class Models
{

    /// <summary>
    /// Represents a scheduled match between two teams in the Elimination Stage.
    /// </summary>
    [Table("match_rooms")]
    public class MatchRoom
    {
        /// <summary>
        /// The unique identifier for the match (e.g., "A1", "C2").
        /// </summary>
        [Key]
        [Column("id")]
        public string Id { get; set; }

        [Column("round_id")]
        public int RoundId { get; set; }

        [Column("team_red_id")]
        public int TeamRedId { get; set; }

        [Column("team_blue_id")]
        public int TeamBlueId { get; set; }

        /// <summary>
        /// The ID of the referee assigned to this match, if any.
        /// </summary>
        [Column("referee_id")]
        public int? RefereeId { get; set; }

        /// <summary>
        /// The scheduled start time in UTC.
        /// </summary>
        [Column("start_time")]
        public DateTime? StartTime { get; set; }

        [Column("end_time")]
        public DateTime? EndTime { get; set; }

        /// <summary>
        /// JSON-stored list of maps banned during the match.
        /// </summary>
        [Column("banned_maps")]
        public List<RoundChoice>? BannedMaps { get; set; }
        
        /// <summary>
        /// JSON-stored list of maps picked during the match.
        /// </summary>
        [Column("picked_maps")]
        public List<RoundChoice>? PickedMaps { get; set; }
        
        /// <summary>
        /// The numerical ID of the Bancho Match History (e.g. https://osu.ppy.sh/community/matches/{MpLinkId}).
        /// </summary>
        [Column("mp_link_id")]
        public int MpLinkId { get; set; }

        [ForeignKey("RoundId")]
        public virtual Round Round { get; set; }

        [ForeignKey("TeamRedId")]
        public virtual User TeamRed { get; set; }

        [ForeignKey("TeamBlueId")]
        public virtual User TeamBlue { get; set; }

        [ForeignKey("RefereeId")]
        public virtual RefereeInfo Referee { get; set; }
    }

    /// <summary>
    /// Represents a lobby for the Qualifier Stage where multiple players play a pool at once.
    /// </summary>
    [Table("qualifier_rooms")]
    public class QualifierRoom
    {
        [Key]
        [Column("id")]
        public string Id { get; set; }

        [Column("round_id")]
        public int RoundId { get; set; }

        [Column("start_time")]
        public DateTime StartTime { get; set; }

        [Column("referee_id")]
        public int? RefereeId { get; set; }

        /// <summary>
        /// The user who requested this specific qualifier slot.
        /// </summary>
        [Column("requested_by")]
        public int? RequestedBy { get; set; }

        [Column("is_approved")]
        public bool? Approved { get; set; }

        [Column("mp_link_id")]
        public int MpLinkId { get; set; }
        
        [ForeignKey("RefereeId")]
        public virtual RefereeInfo Referee { get; set; }

        [ForeignKey("RoundId")]
        public virtual Round Round { get; set; }

        [ForeignKey("RequestedBy")]
        public virtual User RequestUser { get; set; }
    }

    /// <summary>
    /// Represents a stage of the tournament (e.g., "Round of 16", "Qualifiers").
    /// </summary>
    [Table("rounds")]
    public class Round
    {
        [Key]
        [Column("id")]
        public int Id { get; set; }

        [Column("name")]
        public string DisplayName { get; set; }

        /// <summary>
        /// Number of bans allowed per team in this round.
        /// </summary>
        [Column("ban_rounds")]
        public int BanRounds { get; set; }

        [Column("ban_mode")]
        public BansType Mode { get; set; }

        /// <summary>
        /// The maximum number of maps (e.g., 7 for Best of 7).
        /// </summary>
        [Column("best_of")]
        public int BestOf { get; set; }

        /// <summary>
        /// The list of beatmaps available to be picked in this round.
        /// </summary>
        [Column("map_pool")]
        public List<RoundBeatmap> MapPool { get; set; }
    }

    /// <summary>
    /// Represents a registered user in the system, linking their osu! identity to Discord.
    /// </summary>
    [Table("users")]
    public class User
    {
        [Key]
        [Column("id")]
        public int Id { get; set; }

        [Column("osu_id")]
        public int OsuID { get; set; }

        [Column("discord_id")]
        public string? DiscordID { get; set; }

        [ForeignKey("OsuID")]
        public virtual OsuUser OsuData { get; set; }

        [NotMapped]
        public string DisplayName => OsuData.Username ?? "Desconocido";
    }

    [Table("osu_user")]
    public class OsuUser
    {
        [Key]
        [Column("id")]
        public int Id { get; set; }

        [Column("username")]
        public string Username { get; set; }

        [Column("global_rank")]
        public int GlobalRank { get; set; }

        [Column("country_rank")]
        public int CountryRank { get; set; }
    }

    [Table("players")]
    public class Player
    {
        [Key]
        [Column("id")]
        public int Id { get; set; }
        
        [Column("user_id")]
        public int UserId { get; set; }
        
        [Column("registered_at")]
        public DateTime RegisteredAt { get; set; }
        
        [Column("availability")]
        public string Availability { get; set; }

        [Column("qualifier_room_id")]
        public string? QualifierRoomId { get; set; }
        
        [ForeignKey("UserId")]
        public virtual User User { get; set; }

        [ForeignKey("QualifierRoomId")]
        public virtual QualifierRoom? QualifierRoom { get; set; }
    }

    /// <summary>
    /// Contains authentication and identity info for tournament referees.
    /// </summary>
    [Table("referees")]
    public class RefereeInfo
    {
        [Key]
        [Column("id")]
        public int Id { get; set; }

        [Column("display_name")]
        public string DisplayName { get; set; }

        [Column("discord_id")]
        public ulong DiscordID { get; set; }

        [Column("osu_id")]
        public int OsuID { get; set; }

        [Column("irc_password")]
        public string IRC { get; set; }
    }

    /// <summary>
    /// Represents a single score entry parsed from a match.
    /// </summary>
    public class ScoreResults
    {
        [Key]
        [Column("id")]
        public int Id { get; set; }
        
        [Column("round_id")]
        public int RoundId { get; set; }
        
        [Column("user_id")]
        public int UserId { get; set; }
        
        [Column("slot")]
        public string Slot { get; set; }
        
        [Column("score")]
        public int Score { get; set; }
        
        [Column("accuracy")]
        public float Accuracy { get; set; }
        
        [Column("max_combo")]
        public int MaxCombo { get; set; }
        
        [Column("grade")]
        public string Grade  { get; set; }
        
        [ForeignKey("UserId")]
        public User Team { get; set; }
        
        [ForeignKey("RoundId")]
        public Round Round { get; set; }
    }

    public class RoundBeatmap
    {
        public int BeatmapID { get; set; }
        public string Slot { get; set; }
    }

    public class RoundChoice
    {
        public string Slot { get; set; }
        
        /// <summary>
        /// The team that made this choice.
        /// </summary>
        public TeamColor TeamColor { get; set; }
    }
    
    /// <summary>
    /// Indicates which team an action belongs to.
    /// </summary>
    public enum TeamColor
    {
        TeamBlue,
        TeamRed,

        /// <summary>Use only for initialization or neutral states.</summary>
        None
    };

    /// <summary>
    /// Defines the ban strategy for a round.
    /// </summary>
    public enum BansType
    {
        /// <summary>Standard snake draft or fixed order.</summary>
        SpanishShowdown = 0,
        Other = 1,
    };

    /// <summary>
    /// Differentiates between the two behavioral modes of the tournament: Elimination Stage (1v1 matches with bans/picks) and Qualifier Stage (pool play with no bans/picks).
    /// </summary>
    public enum MatchType
    {
        EliminationStage = 0,
        QualifiersStage = 1,
    };
}