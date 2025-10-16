using Microsoft.EntityFrameworkCore;
using SubathonManager.Core.Models;
using SubathonManager.Core.Enums;
using SubathonManager.Core;

namespace SubathonManager.Data
{
    public class AppDbContext : DbContext
    {
        public DbSet<Route> Routes => Set<Route>();
        public DbSet<Widget> Widgets => Set<Widget>();

        public DbSet<CssVariable> CssVariables => Set<CssVariable>();

        public DbSet<SubathonEvent> SubathonEvents { get; set; }
        public DbSet<SubathonValue> SubathonValues { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            var dbPath = Config.GetDatabasePath();
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath));
            optionsBuilder.UseSqlite($"Data Source={dbPath}");
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Route>()
                .HasMany(r => r.Widgets)
                .WithOne(w => w.Route)
                .HasForeignKey(w => w.RouteId)
                .OnDelete(DeleteBehavior.Cascade); // deleting route deletes its widgets

            modelBuilder.Entity<Widget>()
                .HasMany(w => w.CssVariables)
                .WithOne(cv => cv.Widget)
                .HasForeignKey(cv => cv.WidgetId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<SubathonEvent>()
                .HasKey(e => new { e.Id, e.Source });

            modelBuilder.Entity<SubathonValue>()
                .HasKey(sv => new { sv.EventType, sv.Meta });
        }

        public override int SaveChanges()
        {
            UpdateTimestamps();
            return base.SaveChanges();
        }

        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            UpdateTimestamps();
            return await base.SaveChangesAsync(cancellationToken);
        }

        private void UpdateTimestamps()
        {
            var now = DateTime.UtcNow;

            foreach (var entry in ChangeTracker.Entries<Route>())
            {
                if (entry.State == EntityState.Added)
                {
                    entry.Entity.CreatedTimestamp = now;
                    entry.Entity.UpdatedTimestamp = now;
                }
                else if (entry.State == EntityState.Modified)
                {
                    entry.Entity.UpdatedTimestamp = now;
                }
            }

            // If a widget or cssvariable changes, update its parent route’s timestamp
            foreach (var entry in ChangeTracker.Entries<Widget>())
            {
                if (entry.State == EntityState.Added || entry.State == EntityState.Modified ||
                    entry.State == EntityState.Deleted)
                {
                    var route = Routes.FirstOrDefault(r => r.Id == entry.Entity.RouteId);
                    if (route != null)
                        route.UpdatedTimestamp = now;
                }
            }

            foreach (var entry in ChangeTracker.Entries<CssVariable>())
            {
                if (entry.State == EntityState.Added || entry.State == EntityState.Modified ||
                    entry.State == EntityState.Deleted)
                {
                    var widget = Widgets.FirstOrDefault(w => w.Id == entry.Entity.WidgetId);
                    if (widget != null)
                    {
                        var route = Routes.FirstOrDefault(r => r.Id == widget.RouteId);
                        if (route != null)
                            route.UpdatedTimestamp = now;
                    }
                }
            }
        }
        
        public static void SeedDefaultValues(AppDbContext db)
        {
            var defaults = new List<SubathonValue>
            {
                new SubathonValue { EventType = SubathonEventType.TwitchSub, Meta = "1000", Seconds = 60, Points = 1 },
                new SubathonValue { EventType = SubathonEventType.TwitchSub, Meta = "2000", Seconds = 120, Points = 2 },
                new SubathonValue { EventType = SubathonEventType.TwitchSub, Meta = "3000", Seconds = 300, Points = 5 },
                new SubathonValue { EventType = SubathonEventType.TwitchGiftSub, Meta = "1000", Seconds = 60, Points = 1 },
                new SubathonValue { EventType = SubathonEventType.TwitchGiftSub, Meta = "2000", Seconds = 120, Points = 2 },
                new SubathonValue { EventType = SubathonEventType.TwitchGiftSub, Meta = "3000", Seconds = 300, Points = 5 },
                new SubathonValue { EventType = SubathonEventType.TwitchCheer, Seconds = 0.12 },
                new SubathonValue { EventType = SubathonEventType.TwitchFollow, Seconds = 0 },
                new SubathonValue { EventType = SubathonEventType.TwitchRaid, Seconds = 0 }
            };

            foreach (var def in defaults)
            {
                // only add if not exists
                if (!db.SubathonValues.Any(sv => sv.EventType == def.EventType && sv.Meta == def.Meta))
                {
                    db.SubathonValues.Add(def);
                }
            }

            db.SaveChanges();
        }
    }
}