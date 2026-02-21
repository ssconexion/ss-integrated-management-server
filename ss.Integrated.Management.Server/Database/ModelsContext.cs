using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ss.Internal.Management.Server.AutoRef;

public class ModelsContext : DbContext
{
    public DbSet<Models.MatchRoom> MatchRooms { get; set; }
    public DbSet<Models.RefereeInfo> Referees { get; set; }
    public DbSet<Models.QualifierRoom> QualifierRooms { get; set; }
    public DbSet<Models.Player> Players { get; set; }
    public DbSet<Models.User> Users { get; set; }
    public DbSet<Models.Round> Rounds { get; set; }
    public DbSet<Models.ScoreResults> Scores { get; set; }


    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseNpgsql(Environment.GetEnvironmentVariable("POSTGRESQL_CONNECTION_STRING") ?? throw new InvalidOperationException());
        //optionsBuilder.UseNpgsql("Host=localhost;Database=ss26db;Username=ss;Password=ss;");

        optionsBuilder.LogTo(Console.WriteLine, Microsoft.Extensions.Logging.LogLevel.Information);

        optionsBuilder.EnableSensitiveDataLogging();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        
        modelBuilder.Entity<Models.MatchRoom>(e =>
        {
            e.ToTable("match_rooms", t => t.ExcludeFromMigrations());
            
            e.OwnsMany(r => r.BannedMaps, b => b.ToJson("banned_maps"));
            e.OwnsMany(r => r.PickedMaps, b => b.ToJson("picked_maps"));
        });
        
        modelBuilder.Entity<Models.QualifierRoom>().ToTable("qualifier_rooms", t => t.ExcludeFromMigrations());
        
        modelBuilder.Entity<Models.Player>().ToTable("players", t => t.ExcludeFromMigrations());

        modelBuilder.Entity<Models.User>(e =>
        {
            e.ToTable("users", t => t.ExcludeFromMigrations());

            e.Navigation(t => t.OsuData).AutoInclude();
        });

        modelBuilder.Entity<Models.Round>(e =>
        {
            e.ToTable("rounds", t => t.ExcludeFromMigrations());

            e.OwnsMany(r => r.MapPool, b => b.ToJson("map_pool"));
        });
        
        modelBuilder.Entity<Models.RefereeInfo>().ToTable("referees", t => t.ExcludeFromMigrations());
        
        modelBuilder.Entity<Models.OsuUser>().ToTable("osu_users", t => t.ExcludeFromMigrations());
        
        modelBuilder.Entity<Models.ScoreResults>().ToTable("scores", t => t.ExcludeFromMigrations());
    }
}