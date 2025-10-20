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
        
        public DbSet<SubathonData> SubathonDatas { get; set; }
        
        public DbSet<SubathonGoal> SubathonGoals { get; set; }
        public DbSet<SubathonGoalSet> SubathonGoalSets { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            var dbPath = Config.GetDatabasePath();
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
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

        public static async Task PauseAllTimers(AppDbContext db)
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE SubathonDatas SET IsPaused = 1");
        }
        
        public static async Task DisableAllTimers(AppDbContext db)
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE SubathonDatas SET IsActive = 0");
            await PauseAllTimers(db);
        }

        public static async Task<bool> ProcessSubathonEvent(SubathonEvent ev)
        {
            await using var db = new AppDbContext();
            SubathonData? subathon = await db.SubathonDatas.FirstOrDefaultAsync(s => s.IsActive);
            SubathonEvent? dupeCheck = await db.SubathonEvents.SingleOrDefaultAsync(s => s.Id == ev.Id 
                && s.Source == ev.Source);
            if (dupeCheck != null && dupeCheck.ProcessedToSubathon) return false;
            
            if (subathon == null || subathon.IsLocked) // we only do math if it's unlocked, otherwise, only link
            {
                ev.ProcessedToSubathon = false;
                if (subathon != null)
                    ev.SubathonId = subathon.Id;
                db.Add(ev);
                await db.SaveChangesAsync();
                return false;
            }
            
            int initialPoints = subathon.Points;
            
            ev.SubathonId = subathon.Id;
            ev.Multiplier = subathon.Multiplier;
            ev.CurrentTime = (int)subathon.TimeRemaining().TotalMilliseconds;
            SubathonValue? subathonValue = null;
            if (ev.Source != SubathonEventSource.Command)
            {
                subathonValue = await db.SubathonValues.FirstOrDefaultAsync(v =>
                    v.EventType == ev.EventType && (v.Meta == ev.Value || v.Meta == string.Empty));

                if (subathonValue == null)
                {
                    return false;
                }
            }

            if (ev.Source != SubathonEventSource.Command)
            {
                if (double.TryParse(ev.Value, out var parsedValue) && ev.Currency != "sub")
                {
                    ev.SecondsValue = parsedValue * subathonValue!.Seconds;
                }
                else if (ev.Currency == "sub")
                {
                    ev.SecondsValue = subathonValue!.Seconds;
                }
                
                ev.PointsValue = subathonValue!.Points;
            }
            
            int affected = 0;
            if (ev.SecondsValue != 0)
                affected += await db.Database.ExecuteSqlRawAsync(
                    "UPDATE SubathonDatas SET MillisecondsCumulative = MillisecondsCumulative + {0}" +
                    " WHERE IsActive = 1 AND IsLocked = 0 AND Id = {1} " +
                    "AND MillisecondsCumulative - MillisecondsElapsed > 0", 
                   (int) TimeSpan.FromSeconds(ev.GetFinalSecondsValue()).TotalMilliseconds, subathon.Id);
            
            if (ev.PointsValue != 0)
                affected += await db.Database.ExecuteSqlRawAsync(
                    "UPDATE SubathonDatas SET Points = Points + {0}" +
                    " WHERE IsActive = 1 AND IsLocked = 0 AND Id = {1} " +
                    "AND MillisecondsCumulative - MillisecondsElapsed > 0", 
                    (int) ev.GetFinalPointsValue(), subathon.Id);

            if (ev.PointsValue == 0 && ev.SecondsValue == 0)
            {
                ev.ProcessedToSubathon = true;
            }
            
            if (affected > 0)
            {
                ev.ProcessedToSubathon = true;
                await db.Entry(subathon).ReloadAsync();
                // i dont think we need to queue these
                Core.Events.SubathonEvents.RaiseSubathonDataUpdate(subathon, DateTime.Now);
                if (subathon.Points != initialPoints)
                {
                    // look for last completed goal according to initial, and then reg points. If diff, push either completed update, or list update if undo
                    SubathonGoalSet? goalSet = await db.SubathonGoalSets.Include(g=> g.Goals).FirstOrDefaultAsync(g => g.IsActive);
                    if (goalSet != null)
                    {
                        SubathonGoal? prevCompletedGoal1 = goalSet.Goals
                            .OrderByDescending(g => g.Points)
                            .FirstOrDefault(g => g.Points <= initialPoints);
                        
                        SubathonGoal? prevCompletedGoal2 = goalSet.Goals
                            .OrderByDescending(g => g.Points)
                            .FirstOrDefault(g => g.Points <= subathon.Points);

                        if (prevCompletedGoal1 != null && prevCompletedGoal2 != null &&
                            prevCompletedGoal1.Id != prevCompletedGoal2.Id)
                        {
                            // TODO test if goalcompleted works separately
                            // the else is for if we undid stuff
                            if (prevCompletedGoal1.Points <= prevCompletedGoal2.Points)
                                Core.Events.SubathonEvents.RaiseSubathonGoalCompleted(prevCompletedGoal2, subathon.Points);
                            else
                                Core.Events.SubathonEvents.RaiseSubathonGoalListUpdated(goalSet.Goals);
                        }
                    }
                }
            }

            if (dupeCheck != null)
                db.Update(ev);
            else 
                db.Add(ev);
            
            await db.SaveChangesAsync();
            return affected > 0 || (ev.ProcessedToSubathon);
        }

        public static async void DeleteSubathonEvent(SubathonEvent ev)
        {
            if (ev.SubathonId == null) return;
            
            using var db = new AppDbContext();

            SubathonData? subathon = await db.SubathonDatas.FirstOrDefaultAsync(s => s.IsActive);
            if (ev.SubathonId != subathon?.Id) return;
            
            int initialPoints = subathon?.Points ?? 0;
            
            long msToRemove = (long) ev.GetFinalSecondsValue() * 1000; 
            int pointsToRemove = (int) ev.GetFinalPointsValue();
            
            int affected = 0;
            if (msToRemove != 0)
                affected += await db.Database.ExecuteSqlRawAsync(
                    "UPDATE SubathonDatas SET MillisecondsCumulative = MillisecondsCumulative - {0}" +
                    " WHERE IsActive = 1 AND Id = {1} " 
                    , msToRemove, ev.SubathonId);
            
            if (pointsToRemove != 0)
                affected += await db.Database.ExecuteSqlRawAsync(
                    "UPDATE SubathonDatas SET Points = Points - {0}" +
                    " WHERE IsActive = 1 AND Id = {1} ", 
                    pointsToRemove, ev.SubathonId);

            await db.Entry(ev).ReloadAsync();
            db.Remove(ev);
            await db.SaveChangesAsync();
            Core.Events.SubathonEvents.RaiseSubathonEventsDeleted();
            
            if (affected > 0)
            {
                await db.Entry(subathon!).ReloadAsync();
                Core.Events.SubathonEvents.RaiseSubathonDataUpdate(subathon!, DateTime.Now);
                if (subathon!.Points != initialPoints)
                {
                    SubathonGoalSet? goalSet = await db.SubathonGoalSets.Include(g=> g.Goals).FirstOrDefaultAsync(g => g.IsActive);
                    if (goalSet != null)
                    {
                        SubathonGoal? prevCompletedGoal1 = goalSet.Goals
                            .OrderByDescending(g => g.Points)
                            .FirstOrDefault(g => g.Points <= initialPoints);
                        
                        SubathonGoal? prevCompletedGoal2 = goalSet.Goals
                            .OrderByDescending(g => g.Points)
                            .FirstOrDefault(g => g.Points <= subathon.Points);

                        if (prevCompletedGoal1 != null && prevCompletedGoal2 != null &&
                            prevCompletedGoal1.Id != prevCompletedGoal2.Id)
                        {
                            // TODO test if goalcompleted works separately
                            // the else is for if we undid stuff
                            if (prevCompletedGoal1.Points <= prevCompletedGoal2.Points)
                                Core.Events.SubathonEvents.RaiseSubathonGoalCompleted(prevCompletedGoal2, subathon.Points);
                            else
                                Core.Events.SubathonEvents.RaiseSubathonGoalListUpdated(goalSet.Goals);
                        }
                    }
                }
            }
        }

        public static async void UndoSimulatedEvents(List<SubathonEvent> events, bool doAll = false)
        {
            // Remove simulated events from the active subathon only
            // idea - recent events in main page can have "remove" option each that invoke this with a list of 1 
            using var db = new AppDbContext();
            
            int pointsToRemove = 0;
            long msToRemove = 0;

            SubathonData? subathon = await db.SubathonDatas.FirstOrDefaultAsync(s => s.IsActive);
            if (subathon == null) return;
            
            int initialPoints = subathon.Points; // TODO (think done, needs test) if we go under a completed goal, invoke list updated instead
            
            if (doAll)
            {
                // Do All for current subathon
                events = await db.SubathonEvents.Where(e => e.SubathonId == subathon.Id 
                                                            && e.Source == SubathonEventSource.Simulated)
                    .ToListAsync();
                
            }

            foreach (SubathonEvent ev in events)
            {
                if (ev.SubathonId != subathon.Id) continue;
                msToRemove += (long) ev.GetFinalSecondsValue() * 1000;
                pointsToRemove +=  (int) ev.GetFinalPointsValue();
            }
            
            int affected = 0;
            if (msToRemove != 0)
                affected += await db.Database.ExecuteSqlRawAsync(
                    "UPDATE SubathonDatas SET MillisecondsCumulative = MillisecondsCumulative - {0}" +
                    " WHERE IsActive = 1 AND Id = {1} " 
                    , msToRemove, subathon.Id);
            
            if (pointsToRemove != 0)
                affected += await db.Database.ExecuteSqlRawAsync(
                    "UPDATE SubathonDatas SET Points = Points - {0}" +
                    " WHERE IsActive = 1 AND Id = {1} ", 
                    pointsToRemove, subathon.Id);

            db.RemoveRange(events);
            await db.SaveChangesAsync();
            Core.Events.SubathonEvents.RaiseSubathonEventsDeleted();
            
            if (affected > 0)
            {
                await db.Entry(subathon).ReloadAsync();
                Core.Events.SubathonEvents.RaiseSubathonDataUpdate(subathon, DateTime.Now);
                if (subathon.Points != initialPoints)
                {
                    SubathonGoalSet? goalSet = await db.SubathonGoalSets.Include(g=> g.Goals).FirstOrDefaultAsync(g => g.IsActive);
                    if (goalSet != null)
                    {
                        SubathonGoal? prevCompletedGoal1 = goalSet.Goals
                            .OrderByDescending(g => g.Points)
                            .FirstOrDefault(g => g.Points <= initialPoints);
                        
                        SubathonGoal? prevCompletedGoal2 = goalSet.Goals
                            .OrderByDescending(g => g.Points)
                            .FirstOrDefault(g => g.Points <= subathon.Points);

                        if (prevCompletedGoal1 != null && prevCompletedGoal2 != null &&
                            prevCompletedGoal1.Id != prevCompletedGoal2.Id)
                        {
                            // TODO test if goalcompleted works separately
                            // the else is for if we undid stuff
                            if (prevCompletedGoal1.Points <= prevCompletedGoal2.Points)
                                Core.Events.SubathonEvents.RaiseSubathonGoalCompleted(prevCompletedGoal2, subathon.Points);
                            else
                                Core.Events.SubathonEvents.RaiseSubathonGoalListUpdated(goalSet.Goals);
                        }
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
                new SubathonValue { EventType = SubathonEventType.TwitchRaid, Seconds = 0 },
                new SubathonValue { EventType = SubathonEventType.StreamElementsDonation, Seconds = 12} // per 1 unit/dollar of given currency
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

            db.SaveChanges();
        }
    }
}