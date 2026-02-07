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

        [Column("start_time")]
        public DateTime StartTime { get; set; }

        [Column("is_over")]
        public bool IsOver { get; set; }

        [Column("referee_id")]
        public int? RefereeId { get; set; }

        [ForeignKey("RoundId")]
        public virtual Round Round { get; set; }

        [ForeignKey("TeamRedId")]
        public virtual TeamInfo TeamRed { get; set; }

        [ForeignKey("TeamBlueId")]
        public virtual TeamInfo TeamBlue { get; set; }

        [ForeignKey("RefereeId")]
        public virtual RefereeInfo Referee { get; set; }
    }

    [Table("qualifier_rooms")]
    public class QualifierRoom
    {
        [Key]
        [Column("id")]
        public string Id { get; set; }
        
        [Column("start_time")]
        public DateTime StartTime { get; set; }
        
        [Column("is_over")]
        public bool IsOver { get; set; }
        
        [Column("referee_id")]
        public int? RefereeId { get; set; }
        
        [Column("round_id")]
        public int RoundId { get; set; }
        
        [Column("requested_by")]
        public TeamInfo? RequestedBy { get; set; }
        
        [Column("is_approved")]
        public bool Approved { get; set; }
        
        [ForeignKey("RefereeId")]
        public virtual RefereeInfo Referee { get; set; }

        [ForeignKey("RoundId")]
        public virtual Round Round { get; set; }

        [ForeignKey("RequestedBy")]
        public virtual TeamInfo RequestUser { get; set; }

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
    public class TeamInfo
    {
        [Key]
        [Column("id")]
        public int Id { get; set; }

        [Column("osu_id")]
        public int OsuID { get; set; }

        [Column("discord_id")]
        public string DiscordID { get; set; }

        [ForeignKey("OsuID")]
        public virtual OsuUser OsuData { get; set; }

        [NotMapped]
        public string DisplayName => OsuData.DisplayName ?? "Desconocido";
    }

    [Table("players")]
    public class PlayerInfo
    {
        [Key]
        [Column("id")]
        public int Id { get; set; }

        [Column("user_id")]
        public int UserId { get; set; }

        [Column("qualifier_room_id")]
        public string QualifiersRoom { get; set; }

        [ForeignKey("UserId")]
        public TeamInfo User { get; set; }
    }

    [Table("osu_users")]
    public class OsuUser
    {
        [Key]
        [Column("id")]
        public int Id { get; set; }

        [Column("username")]
        public string DisplayName { get; set; }
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

    public class RoundBeatmap
    {
        public int BeatmapID { get; set; }
        public string Slot { get; set; }
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
