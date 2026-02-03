namespace ss.Internal.Management.Server.AutoRef
{
    public partial class Match
    {
        public int ID;
        public MatchType Type;

        public Team Team;
        public int BestOf;

    }

    public partial class Team
    {
        public string TeamName;
        public int ScoreCount;
        
    }

    public enum MatchType
    {
        Qualifiers,
        Elimination,
    }
}

