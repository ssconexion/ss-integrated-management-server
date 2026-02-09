using Microsoft.EntityFrameworkCore;

namespace ss.Internal.Management.Server.AutoRef;

public class ModelsContext : DbContext
{
    public DbSet<Models.MatchRoom> MatchRooms { get; set; }
    public DbSet<Models.RefereeInfo> Referees { get; set; }
    public DbSet<Models.QualifierRoom> QualifierRooms { get; set; }
    public DbSet<Models.PlayerInfo> Players { get; set; }
    public DbSet<Models.TeamInfo> Users { get; set; }
    public DbSet<Models.Round> Rounds { get; set; }
    public DbSet<Models.ScoreResults> Scores { get; set; }


    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        //optionsBuilder.UseNpgsql(Environment.GetEnvironmentVariable("POSTGRESQL_CONNECTION_STRING") ?? throw new InvalidOperationException());
        optionsBuilder.UseNpgsql("Host=localhost;Database=ss;Username=ss;Password=ss;");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // IMPORTANTE: En producción, los toTable se invierten para evitar creación de tablas nuevas
        // Cómo se tendría que ver?
        // 
        // - Development:
        //   entity.ToTable("matches");
        //   //entity.ToTable("matches", t => t.ExcludeFromMigrations());
        //
        // - Producción:
        //   //entity.ToTable("matches");
        //   entity.ToTable("matches", t => t.ExcludeFromMigrations());
        //
        // Hay 7 de estos casos, asegúrate de revisarlos todos antes de nada.

        modelBuilder.Entity<Models.MatchRoom>(e =>
        {
            e.ToTable("match_rooms");
            //e.ToTable("match_rooms", t => t.ExcludeFromMigrations());
            
            e.OwnsMany(r => r.BannedMaps, b => b.ToJson("banned_maps"));
            e.OwnsMany(r => r.PickedMaps, b => b.ToJson("picked_maps"));
        });
        
        modelBuilder.Entity<Models.QualifierRoom>().ToTable("qualifier_rooms");
        //modelBuilder.Entity<Models.QualifierRoom>().ToTable("qualifier_rooms", t => t.ExcludeFromMigrations());
        
        modelBuilder.Entity<Models.PlayerInfo>().ToTable("player");
        //modelBuilder.Entity<Models.PlayerInfo>().ToTable("player", t => t.ExcludeFromMigrations());

        modelBuilder.Entity<Models.TeamInfo>(e =>
        {
            e.ToTable("user");
            //e.ToTable("user", t => t.ExcludeFromMigrations());

            e.Navigation(t => t.OsuData).AutoInclude();
        });

        modelBuilder.Entity<Models.Round>(e =>
        {
            e.ToTable("round");
            //e.ToTable("round", t => t.ExcludeFromMigrations());

            e.OwnsMany(r => r.MapPool, b => b.ToJson("map_pool"));
        });

        modelBuilder.Entity<Models.RefereeInfo>().ToTable("referees");
        //modelBuilder.Entity<Models.RefereeInfo>().ToTable("referees", t => t.ExcludeFromMigrations());

        modelBuilder.Entity<Models.OsuUser>().ToTable("osu_user");
        //modelBuilder.Entity<Models.RefereeInfo>().ToTable("referees", t => t.ExcludeFromMigrations());
        
        modelBuilder.Entity<Models.ScoreResults>().ToTable("scores");
        //modelBuilder.Entity<Models.ScoreResults>().ToTable("scores", t => t.ExcludeFromMigrations());
    }
}