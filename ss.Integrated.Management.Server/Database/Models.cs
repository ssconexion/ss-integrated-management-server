using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ss.Internal.Management.Server.AutoRef;

public class Models
{
    [Table("matches")]
    public class Match
    {
        [Key]
        [Column("id")]
        public string Id { get; set; }
        
        [Column("match_type")]
        public int Type { get; set; }
        
        [Column("round")]
        public RoundSnapshot Round { get; set; }
        
        [Column("team_red")]
        public TeamSnapshot TeamRed { get; set; }
        
        [Column("team_blue")]
        public TeamSnapshot TeamBlue { get; set; }
        
        [Column("start_time")]
        public DateTime StartTime { get; set; }
        
        [Column("is_over")]
        public bool IsOver { get; set; }
        
        [Column("referee")]
        public RefereeSnapshot Referee { get; set; }
    }

    [Table("round")]
    public class Round
    {
        [Column("id")]
        public int Id { get; set; }
        
        [Column("display_name")]
        public string DisplayName { get; set; }
        
        [Column("ban_rounds")]
        public int BanRounds { get; set; }
        
        [Column("ban_mode")]
        public BansType Mode { get; set; }
        
        [Column("best_of")]
        public int BestOf { get; set; }
        
        [Column("mappool")]
        public List<RoundBeatmap> MapPool { get; set; }
    }

    [Table("user")]
    public class TeamInfo
    {
        [Key]
        [Column("id")]
        public string Id { get; set; }
        
        [Column("osu_id")]
        public int OsuID { get; set; }
        
        [Column("discord_id")]
        public string DiscordID { get; set; }
        
        [ForeignKey("OsuID")]
        public virtual OsuUser OsuData { get; set; }

        [NotMapped]
        public string DisplayName => OsuData.DisplayName ?? "Desconocido";
    }

    [Table("osu_user")]
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
        [Column("id")]
        public int Id { get; set; }
        
        [Column("display_name")]
        public string DisplayName { get; set; }
        
        [Column("discord_id")]
        public int DiscordID { get; set; }
        
        [Column("osu_id")]
        public int OsuID { get; set; }
        
        [Column("irc")]
        public string IRC { get; set; }
    }
    
    public class TeamSnapshot
    {
        public string Id { get; set; }
        public int OsuID { get; set; }
        public string DiscordID { get; set; }
        public string DisplayName { get; set; }
    }

    public class RoundSnapshot
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public int BestOf { get; set; }
        public int BanRounds { get; set; }
        public BansType Mode { get; set; }
        public List<RoundBeatmap> MapPool { get; set; } = [];
    }

    public class RefereeSnapshot
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string IRC { get; set; }
    }

    public class RoundBeatmap
    {
        public int BeatmapID { get; set; }
        public string slot { get; set; }
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