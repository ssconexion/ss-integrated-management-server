using Microsoft.EntityFrameworkCore;

namespace ss.Internal.Management.Server.AutoRef;

public class ModelsContext : DbContext
{
    public DbSet<Models.Match> Matches { get; set; }
    public DbSet<Models.RefereeInfo> Referees { get; set; }
    
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) => optionsBuilder.UseNpgsql(Environment.GetEnvironmentVariable("POSTGRESQL_CONNECTION_STRING") ?? throw new InvalidOperationException());
    
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
        // Hay 5 de estos casos, asegúrate de revisarlos todos antes de nada.
        
        modelBuilder.Entity<Models.Match>(entity =>
        { 
            
            entity.ToTable("matches");
            //entity.ToTable("matches", t => t.ExcludeFromMigrations());
            
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
            //e.ToTable("user", t => t.ExcludeFromMigrations());
            
            e.HasOne(t => t.OsuData).WithMany().HasForeignKey(t => t.OsuID);
            e.Navigation(t => t.OsuData).AutoInclude();
        });

        modelBuilder.Entity<Models.Round>(e => {
            
            e.ToTable("round");
            //e.ToTable("round", t => t.ExcludeFromMigrations());
            
            e.OwnsMany(r => r.MapPool, b => b.ToJson());
        });

        modelBuilder.Entity<Models.RefereeInfo>().ToTable("referees");
        //modelBuilder.Entity<Models.RefereeInfo>().ToTable("referees", t => t.ExcludeFromMigrations());
        
        modelBuilder.Entity<Models.OsuUser>().ToTable("osu_user");
        //modelBuilder.Entity<Models.RefereeInfo>().ToTable("referees", t => t.ExcludeFromMigrations());
    }
}