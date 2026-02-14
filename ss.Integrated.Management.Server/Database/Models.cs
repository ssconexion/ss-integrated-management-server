using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ss.Internal.Management.Server.AutoRef;

public class Models
{
    [Table("match_rooms")]
    public class MatchRoom
    {
        [Key]
        [Column("id")]
        public string Id { get; set; }

        [Column("round_id")]
        public int RoundId { get; set; }

        [Column("team_red_id")]
        public int TeamRedId { get; set; }

        [Column("team_blue_id")]
        public int TeamBlueId { get; set; }

        [Column("referee_id")]
        public int? RefereeId { get; set; }

        [Column("start_time")]
        public DateTime? StartTime { get; set; }

        [Column("end_time")]
        public DateTime? EndTime { get; set; }

        [Column("banned_maps")]
        public List<RoundChoice>? BannedMaps { get; set; }
        
        [Column("picked_maps")]
        public List<RoundChoice>? PickedMaps { get; set; }

        [ForeignKey("RoundId")]
        public virtual Round Round { get; set; }

        [ForeignKey("TeamRedId")]
        public virtual User TeamRed { get; set; }

        [ForeignKey("TeamBlueId")]
        public virtual User TeamBlue { get; set; }

        [ForeignKey("RefereeId")]
        public virtual RefereeInfo Referee { get; set; }
    }

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

        [Column("requested_by")]
        public int? RequestedBy { get; set; }

        [Column("is_approved")]
        public bool? Approved { get; set; }
        
        [ForeignKey("RefereeId")]
        public virtual RefereeInfo Referee { get; set; }

        [ForeignKey("RoundId")]
        public virtual Round Round { get; set; }

        [ForeignKey("RequestedBy")]
        public virtual User RequestUser { get; set; }

    }

    [Table("rounds")]
    public class Round
    {
        [Key]
        [Column("id")]
        public int Id { get; set; }

        [Column("name")]
        public string DisplayName { get; set; }

        [Column("ban_rounds")]
        public int BanRounds { get; set; }

        [Column("ban_mode")]
        public BansType Mode { get; set; }

        [Column("best_of")]
        public int BestOf { get; set; }

        [Column("map_pool")]
        public List<RoundBeatmap> MapPool { get; set; }
    }

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
        public TeamColor TeamColor { get; set; }
    }

    public enum TeamColor
    {
        TeamBlue,
        TeamRed,
    }

    public enum BansType
    {
        SpanishShowdown = 0,
        Other = 1,
    }

    public enum MatchType
    {
        EliminationStage = 0,
        QualifiersStage = 1,
    }

}