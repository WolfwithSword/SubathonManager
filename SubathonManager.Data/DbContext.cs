using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using System.Text;
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
        public DbSet<JsVariable> JsVariables => Set<JsVariable>();

        public DbSet<SubathonEvent> SubathonEvents { get; set; }
        public DbSet<SubathonValue> SubathonValues { get; set; }
        
        public DbSet<SubathonData> SubathonDatas { get; set; }
        public DbSet<MultiplierData> MultiplierDatas { get; set; }
        
        public DbSet<SubathonGoal> SubathonGoals { get; set; }
        public DbSet<SubathonGoalSet> SubathonGoalSets { get; set; }
        
        public AppDbContext(DbContextOptions<AppDbContext> options)
            : base(options)
        {
        }
        
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            if (!optionsBuilder.IsConfigured) // allows migrations to be added still in dev
            {
                var dbPath = Config.DatabasePath;
                Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
                optionsBuilder.UseSqlite($"Data Source={dbPath}");
            }
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
            
            modelBuilder.Entity<Widget>()
                .HasMany(w => w.JsVariables)
                .WithOne(cv => cv.Widget)
                .HasForeignKey(cv => cv.WidgetId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<SubathonEvent>()
                .HasKey(e => new { e.Id, e.Source });
            
            modelBuilder.Entity<SubathonEvent>()
                .HasOne(e => e.LinkedSubathon)
                .WithMany() 
                .HasForeignKey(e => e.SubathonId)
                .IsRequired(false); 

            modelBuilder.Entity<SubathonValue>()
                .HasKey(sv => new { sv.EventType, sv.Meta });
            
            modelBuilder.Entity<SubathonGoalSet>()
                .HasMany(s => s.Goals)
                .WithOne(g => g.LinkedGoalSet)
                .HasForeignKey(g => g.GoalSetId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<SubathonData>().HasOne(s => s.Multiplier)
                .WithOne(m => m.LinkedSubathon).HasForeignKey<MultiplierData>(m => m.SubathonId)
                .OnDelete(DeleteBehavior.Cascade);
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

            // If a widget or cssvariable/jsvariable changes, update its parent route’s timestamp
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
            
            foreach (var entry in ChangeTracker.Entries<JsVariable>())
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
        
        public static async Task<List<SubathonEvent>> GetSubathonCurrencyEvents(AppDbContext db)
        {
            SubathonData? subathon = await db.SubathonDatas.AsNoTracking().FirstOrDefaultAsync(s => s.IsActive);
            if (subathon == null) return new List<SubathonEvent>();
            
            List<SubathonEvent> events = await db.SubathonEvents.AsNoTracking().Where(e => !string.IsNullOrWhiteSpace(e.Currency)
                && e.EventType != SubathonEventType.Command && !string.IsNullOrWhiteSpace(e.Value))
                .ToListAsync();
            events = events.Where(e => e.EventType.IsCurrencyDonation()).ToList();
            return events;
        }
        
        public static async Task ActiveEventsToCsv(AppDbContext db)
        {
            SubathonData? subathon = await db.SubathonDatas.AsNoTracking().FirstOrDefaultAsync(s => s.IsActive);
            if (subathon == null) return;
            List<SubathonEvent> events = await db.SubathonEvents.Where(ev => ev.SubathonId == subathon.Id)
                    .ToListAsync();

            string exportDir = Path.Combine(Config.DataFolder, "exports");
            Directory.CreateDirectory(exportDir);
            string filepath = $"{exportDir}/subathon-{subathon.Id}.csv";
            
            var sb = new StringBuilder();
            sb.AppendLine("Id,Source,Type,Command,User,Seconds Value,Points Value,Value,Currency,Amount,Multiplier Seconds,Multiplier Points,Processed,Final Seconds Added,Final Points Added,Timestamp");
            foreach (var e in events)
            {
                sb.AppendLine(string.Join(",",
                    e.Id,
                    Utils.EscapeCsv(e.Source.ToString()),
                    Utils.EscapeCsv(e.EventType.ToString()),
                    Utils.EscapeCsv(e.Command.ToString()),
                    Utils.EscapeCsv(e.User),
                    e.SecondsValue,
                    e.PointsValue,
                    Utils.EscapeCsv(e.Value),
                    Utils.EscapeCsv(e.Currency),
                    e.Amount,
                    e.MultiplierSeconds,
                    e.MultiplierPoints,
                    e.ProcessedToSubathon,
                    e.GetFinalSecondsValueRaw(),
                    e.GetFinalPointsValue(),
                    e.EventTimestamp.ToString("yyyy-MM-dd HH:mm:ss")
                ));
            }
            await File.WriteAllTextAsync(filepath, sb.ToString(), Encoding.UTF8);
            
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exportDir,
                    UseShellExecute = true,
                    Verb = "open"
                });
            }
            catch {/**/}
        }

        public static async Task PauseAllTimers(AppDbContext db)
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE SubathonDatas SET IsPaused = 1");
        }

        public static async Task ResetPowerHour(AppDbContext db)
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE MultiplierDatas SET Multiplier = 1, Duration = null, ApplyToSeconds=false, ApplyToPoints=false, FromHypeTrain=false ");
        }
        
        public static async Task DisableAllTimers(AppDbContext db)
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE SubathonDatas SET IsActive = 0");
            await PauseAllTimers(db);
        }

        public static async Task UpdateSubathonCurrency(AppDbContext db, string currency)
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE SubathonDatas SET Currency = {0} WHERE IsActive = 1", currency);
        }
        
        public static async Task UpdateSubathonMoney(AppDbContext db, double money, Guid subathonId)
        {
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE SubathonDatas SET MoneySum = {0} WHERE IsActive = 1 and Id = {1}", money, subathonId);
            
            var subathon = await db.SubathonDatas.Include(s=> s.Multiplier)
                .AsNoTracking().FirstOrDefaultAsync(s => s.IsActive);
            if (subathon != null)
                SubathonManager.Core.Events.SubathonEvents.RaiseSubathonDataUpdate(subathon!, DateTime.Now);

            var goalSet = await db.SubathonGoalSets.AsNoTracking()
                .Include(x => x.Goals).Where(x => x.IsActive)
                .FirstOrDefaultAsync();
            if (subathon != null && goalSet != null && goalSet.Goals.Any() && goalSet.Type == GoalsType.Money)
                Core.Events.SubathonEvents.RaiseSubathonGoalListUpdated(goalSet.Goals, subathon.GetRoundedMoneySum());
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
                new SubathonValue { EventType = SubathonEventType.TwitchRaid, Seconds = 0 },
                new SubathonValue { EventType = SubathonEventType.StreamElementsDonation, Seconds = 12}, // per 1 unit/dollar of default currency
                new SubathonValue { EventType = SubathonEventType.StreamLabsDonation, Seconds = 12}, // per 1 unit/dollar of default currency
                new SubathonValue { EventType = SubathonEventType.YouTubeSuperChat, Seconds = 12}, // per 1 unit/dollar of default currency
                new SubathonValue { EventType = SubathonEventType.YouTubeMembership, Meta = "DEFAULT", Seconds = 60, Points = 1},
                new SubathonValue { EventType = SubathonEventType.YouTubeGiftMembership, Meta = "DEFAULT", Seconds = 60, Points = 1},
                new SubathonValue { EventType = SubathonEventType.TwitchCharityDonation, Seconds = 12}, // per 1 unit/dollar of default currency
                new SubathonValue { EventType = SubathonEventType.ExternalDonation, Seconds = 12}, // per 1 unit/dollar of default currency
                new SubathonValue { EventType = SubathonEventType.KoFiDonation, Seconds = 12}, // per 1 unit/dollar of default currency
                new SubathonValue { EventType = SubathonEventType.KoFiSub, Meta = "DEFAULT", Seconds = 60, Points = 1}, // per 1 unit/dollar of default currency
            };

            foreach (var def in defaults)
            {
                // only add if not exists
                if (!db.SubathonValues.Any(sv => sv.EventType == def.EventType && sv.Meta == def.Meta))
                {
                    db.SubathonValues.Add(def);
                }
            }

            if (!db.SubathonDatas.Any(s => s.IsActive))
            {
                SubathonData subathon = new SubathonData();
                TimeSpan initialMs = TimeSpan.FromHours(8);
                subathon.MillisecondsCumulative += (int)initialMs.TotalMilliseconds;
                subathon.IsPaused = true;
                db.SubathonDatas.Add(subathon);
            }

            if (!db.SubathonGoalSets.Any(s => s.IsActive))
            {
                SubathonGoalSet goalSet = new SubathonGoalSet();
                db.SubathonGoalSets.Add(goalSet);
            }

            if (db.SubathonGoalSets.Any(s => s.Type == null))
            {
                db.SubathonGoalSets.Where(s => s.Type == null)
                    .ExecuteUpdate(s =>
                        s.SetProperty(x => x.Type, GoalsType.Points));
            }
            
            db.SaveChanges();
        }
    }
}