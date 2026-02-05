using Microsoft.EntityFrameworkCore;

namespace ss.Internal.Management.Server.AutoRef;

public class ModelsContext : DbContext
{
    public DbSet<Models.Match> Matches { get; set; }
    public DbSet<Models.RefereeInfo> Referees { get; set; }
    
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) => optionsBuilder.UseNpgsql("Host=localhost;Database=ss;Username=ss;Password=ss;");
    
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // TODO deshacer comentarios
        
        modelBuilder.Entity<Models.Match>(entity =>
        {
            entity.ToTable("matches");
            
            entity.OwnsOne(m => m.Round, roundSnapshotBuilder => 
            {
                roundSnapshotBuilder.ToJson();
                roundSnapshotBuilder.OwnsMany(r => r.MapPool); 
            });
            
            // snapshots
            entity.OwnsOne(m => m.Round, b => b.ToJson());
            entity.OwnsOne(m => m.TeamRed, b => b.ToJson());
            entity.OwnsOne(m => m.TeamBlue, b => b.ToJson());
            entity.OwnsOne(m => m.Referee, b => b.ToJson());
        });
    
        // real shit
        modelBuilder.Entity<Models.TeamInfo>(e => {
            e.ToTable("user"); 
            e.HasOne(t => t.OsuData).WithMany().HasForeignKey(t => t.OsuID);
            e.Navigation(t => t.OsuData).AutoInclude();
        });

        modelBuilder.Entity<Models.Round>(e => {
            e.ToTable("round");
            e.OwnsMany(r => r.MapPool, b => b.ToJson());
        });

        modelBuilder.Entity<Models.RefereeInfo>().ToTable("referees");
        modelBuilder.Entity<Models.OsuUser>().ToTable("osu_user");
    }
}