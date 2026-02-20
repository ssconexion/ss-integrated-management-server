using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ss.Internal.Management.Server.AutoRef;

/// <summary>
/// Container for all Entity Framework Core data models.
/// </summary>
/// <remarks>
/// ## Database Schema (ER Diagram)
/// \dot
/// digraph DatabaseSchema {
///     // --- 1. GLOBAL SETTINGS ---
///     // 'ortho' gives you the clean right-angles
///     graph [rankdir=LR, splines=ortho, nodesep=0.5, ranksep=0.8, fontname="helvetica"];
///     
///     // --- 2. TABLE NODES (The "+ attr" aesthetic) ---
///     // shape=record allows us to use | to create rows
///     node [shape=record, style="filled,rounded", fillcolor="#EAFAF1", fontname="helvetica", fontsize=10, height=0.4, penwidth=1.0];
///     
///     MatchRoom [label="{ MatchRoom | + Id \l | + State \l }", fillcolor="#D5F5E3"];
///     QualRoom  [label="{ QualifierRoom | + Id \l | + StartTime \l }", fillcolor="#D5F5E3"];
///     Round     [label="{ Round | + Id \l | + BestOf \l }", fillcolor="#FADBD8"];
///     User      [label="{ User | + Id \l | + DiscordID \l }", fillcolor="#D6EAF8"];
///     OsuUser   [label="{ OsuUser | + Id \l | + Rank \l }", fillcolor="#D6EAF8"];
///     Player    [label="{ Player | + Id \l | + Availability \l }"];
///     Referee   [label="{ RefereeInfo | + Id \l | + IRC \l }", fillcolor="#FCF3CF"];
///     Score     [label="{ ScoreResults | + Id \l | + Score \l }", fillcolor="#FCF3CF"];
///
///     // --- 3. "ROUTER" NODES (The Secret Sauce) ---
///     // These are invisible nodes that act as labels. The lines go THROUGH them.
///     // This forces the lines to be straight and puts the text exactly where we want it.
///     node [shape=plaintext, style=none, fillcolor=none, width=0, height=0, fontsize=8, fontcolor="#5D6D7E"];
///     
///     // We define a label-node for each relationship
///     lbl_Red      [label="Red Team", fontcolor="#E74C3C"];
///     lbl_Blue     [label="Blue Team", fontcolor="#3498DB"];
///     lbl_Rules1   [label="Rules"];
///     lbl_Rules2   [label="Rules"];
///     lbl_Ref1     [label="Managed By"];
///     lbl_Ref2     [label="Managed By"];
///     lbl_Req      [label="Requested By"];
///     lbl_Player   [label="Player"];
///     lbl_IsA      [label="Is A"];
///     lbl_1to1     [label="1:1"];
///
///     // --- 4. EDGES ---
///     edge [fontname="helvetica", fontsize=8, color="#5D6D7E", arrowsize=0.7];
///
///     // MATCH -> TEAMS
///     // Step 1: Line from Match to Label (No Arrow)
///     edge [dir=none];
///     MatchRoom -> lbl_Red  [color="#E74C3C"];
///     MatchRoom -> lbl_Blue [color="#3498DB"];
///     // Step 2: Line from Label to User (Arrow)
///     edge [dir=forward];
///     lbl_Red  -> User [color="#E74C3C"];
///     lbl_Blue -> User [color="#3498DB"];
///
///     // MATCH -> RULES
///     edge [dir=none]; MatchRoom -> lbl_Rules1;
///     edge [dir=forward]; lbl_Rules1 -> Round;
///
///     // MATCH -> REF
///     edge [dir=none]; MatchRoom -> lbl_Ref1;
///     edge [dir=forward]; lbl_Ref1 -> Referee;
///
///     // QUALIFIERS
///     edge [dir=none]; QualRoom -> lbl_Rules2; QualRoom -> lbl_Ref2; QualRoom -> lbl_Req;
///     edge [dir=forward]; 
///     lbl_Rules2 -> Round; 
///     lbl_Ref2 -> Referee; 
///     lbl_Req -> User;
///
///     // USER SYSTEM
///     edge [dir=none]; User -> lbl_1to1; Player -> lbl_IsA;
///     edge [dir=forward]; lbl_1to1 -> OsuUser; lbl_IsA -> User;
///
///     // SCORES
///     edge [dir=none]; Score -> lbl_Player;
///     edge [dir=forward]; lbl_Player -> User;
/// }
/// \enddot
/// </remarks>
public class Models
{
    // ... ENUMS ...
    
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
    /// Differentiates between the two main modes of the tournament.
    /// </summary>
    public enum MatchType
    {
        EliminationStage = 0,
        QualifiersStage = 1,
    };

    // ... CLASSES ...

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
        public int? MpLinkId { get; set; }
        
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
        public TeamColor TeamColor { get; set; }
    }
}