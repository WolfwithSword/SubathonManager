using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using System.Text;
using SubathonManager.Core.Models;
using SubathonManager.Core.Enums;
using SubathonManager.Core;
// ReSharper disable NullableWarningSuppressionIsUsed

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
        
        public DbSet<SubathonPromptSet> SubathonPromptSets { get; set; }
        public DbSet<SubathonPrompt> SubathonPrompts { get; set; }
        public DbSet<SubathonPromptRun> SubathonPromptRuns { get; set; }
        
        public DbSet<GoAffProStore> GoAffProStores { get; set; }
        
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
            
            modelBuilder.Entity<SubathonPromptSet>()
                .HasMany(s => s.Prompts)
                .WithOne(p => p.LinkedSet)
                .HasForeignKey(p => p.SetId)
                .OnDelete(DeleteBehavior.Cascade);
 
            modelBuilder.Entity<SubathonPromptRun>()
                .HasOne(r => r.LinkedPrompt)
                .WithMany()
                .HasForeignKey(r => r.PromptId)
                .OnDelete(DeleteBehavior.Cascade);
 
            modelBuilder.Entity<SubathonPromptRun>()
                .HasOne(r => r.LinkedSet)
                .WithMany()
                .HasForeignKey(r => r.SetId)
                .OnDelete(DeleteBehavior.NoAction);
            
            modelBuilder.Entity<SubathonPromptSet>()
                .Property(s => s.Interval)
                .HasConversion(ts => ts.Ticks, ticks => TimeSpan.FromTicks(ticks));
 
            modelBuilder.Entity<SubathonPromptSet>()
                .Property(s => s.RandomOffset)
                .HasConversion(ts => ts.Ticks, ticks => TimeSpan.FromTicks(ticks));
 
            modelBuilder.Entity<SubathonPromptSet>()
                .Property(s => s.Cooldown)
                .HasConversion(ts => ts.Ticks, ticks => TimeSpan.FromTicks(ticks));
 
            modelBuilder.Entity<SubathonPrompt>()
                .Property(p => p.CompletionDuration)
                .HasConversion(ts => ts.Ticks, ticks => TimeSpan.FromTicks(ticks));
            
            modelBuilder.Entity<GoAffProStore>()
                .HasKey(e => new { e.RowId });
            modelBuilder.Entity<GoAffProStore>()
                .HasIndex(s => new { s.SiteId })
                .IsUnique();
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

            List<SubathonEvent> events = await db.SubathonEvents.AsNoTracking().Where(e =>
                    e.SubathonId == subathon.Id && e.ProcessedToSubathon &&
                    !string.IsNullOrWhiteSpace(e.Currency)
                    && e.EventType != SubathonEventType.Command && !string.IsNullOrWhiteSpace(e.Value))
                .ToListAsync();

            bool includeBits = Utils.DonationSettings.TryGetValue("BitsLikeAsDonation",  out bool bitslike) && bitslike ;
            List<SubathonEventType> orderTypesToInclude = new List<SubathonEventType>();
            foreach (var goAffProSource in Enum.GetValues<GoAffProSource>().Where(ga => ga != GoAffProSource.Unknown && !ga.IsDisabled()))
            {
                bool asDonation = Utils.DonationSettings.TryGetValue($"{goAffProSource}", out  bool donation) && donation;
                if (asDonation && Enum.TryParse($"{goAffProSource}Order", out SubathonEventType eventType))
                    orderTypesToInclude.Add(eventType);
            }
            foreach (var orderEvent in Enum.GetValues<SubathonEventType>().Where(et =>
                         ((SubathonEventType?)et).IsOrder() && !et.IsDisabled()))
            {
                bool asDonation = Utils.DonationSettings.TryGetValue($"{orderEvent.ToString()?.Split("Order")[0]}", out  bool donation) && donation;
                if (asDonation)
                    orderTypesToInclude.Add(orderEvent);
            }
            
            events = events.Where(e => e.EventType != null && 
                                       (e.EventType.IsCurrencyDonation() ||
                                        (e.EventType.IsOrder() && orderTypesToInclude.Contains((SubathonEventType)e.EventType)) ||
                                        (includeBits && e.EventType.IsToken()))).ToList();
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
            sb.AppendLine("Id,Source,Type,Command,User,Seconds Value,Points Value,Value,Currency,Amount,Multiplier Seconds,Multiplier Points,Processed,Final Seconds Added,Final Points Added,Timestamp,Secondary Value,Event Meta Type, Event Meta Common Type");
            foreach (var e in events)
            {
                var val = e.Value;
                var commonMeta = string.IsNullOrWhiteSpace(e.EventTypeMeta) ? "" : e.EventTypeMeta;
                if (e.EventType is SubathonEventType.TwitchGiftSub or SubathonEventType.TwitchSub)
                {
                    val = e.Value switch
                    {
                        "1000" => "T1",
                        "2000" => "T2",
                        "3000" => "T3",
                        _ => val
                    };
                    commonMeta = val;
                }

                // if (e.EventType == SubathonEventType.GoAffProOrder)
                // {
                //     GoAffProStoreRegistry.TryGetBySiteId(int.Parse(e.EventTypeMeta!), out var store);
                //     if (store != null)
                //     {
                //         commonMeta = store.InternalName;
                //     }
                // }
                
                sb.AppendLine(string.Join(",",
                    e.Id,
                    Utils.EscapeCsv(e.Source.ToString()),
                    Utils.EscapeCsv(e.EventType.ToString()),
                    Utils.EscapeCsv(e.Command.ToString()),
                    Utils.EscapeCsv(e.User),
                    e.SecondsValue,
                    e.PointsValue,
                    Utils.EscapeCsv(val),
                    Utils.EscapeCsv(e.Currency),
                    e.Amount,
                    e.MultiplierSeconds,
                    e.MultiplierPoints,
                    e.ProcessedToSubathon,
                    e.GetFinalSecondsValueRaw(),
                    e.GetFinalPointsValue(),
                    e.EventTimestamp.ToString("yyyy-MM-dd HH:mm:ss"),
                    Utils.EscapeCsv(e.SecondaryValue),
                    Utils.EscapeCsv(e.EventTypeMeta),
                    Utils.EscapeCsv(commonMeta)
                ));
            }
            await File.WriteAllTextAsync(filepath, sb.ToString(), Encoding.UTF8);
            
            try
            {
                bool isTest =
                    AppDomain.CurrentDomain.GetAssemblies()
                        .Any(a => a.FullName!.StartsWith("xunit", StringComparison.OrdinalIgnoreCase));
                if (!isTest)
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = exportDir,
                        UseShellExecute = true,
                        Verb = "open"
                    });
                }
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
        
        public async Task UpdateSubathonMoney(double money, Guid subathonId)
        {
            await Database.ExecuteSqlRawAsync(
                "UPDATE SubathonDatas SET MoneySum = {0} WHERE IsActive = 1 and Id = {1}", money, subathonId);
            var subathon = await SubathonDatas.Include(s=> s.Multiplier)
                .AsNoTracking().FirstOrDefaultAsync(s => s.IsActive);
            if (subathon == null) return;
            Entry(subathon).State = EntityState.Detached;
            Entry(subathon.Multiplier).State = EntityState.Detached;
            Core.Events.SubathonEvents.RaiseSubathonDataUpdate(subathon, DateTime.Now);

            var goalSet = await SubathonGoalSets.AsNoTracking()
                .Include(x => x.Goals).Where(x => x.IsActive)
                .FirstOrDefaultAsync();
            if (goalSet != null && goalSet.Goals.Any() && goalSet.Type == GoalsType.Money)
            {
                Core.Events.SubathonEvents.RaiseSubathonGoalListUpdated(goalSet.Goals, 
                    subathon.GetRoundedMoneySum(), GoalsType.Money);
            }
        }
        
        public static void SeedDefaultValues(AppDbContext db)
        {
            var defaults = new List<SubathonValue>
            {
                new () { EventType = SubathonEventType.TwitchSub, Meta = "1000", Seconds = 60, Points = 1 },
                new () { EventType = SubathonEventType.TwitchSub, Meta = "2000", Seconds = 120, Points = 2 },
                new () { EventType = SubathonEventType.TwitchSub, Meta = "3000", Seconds = 300, Points = 5 },
                new () { EventType = SubathonEventType.TwitchGiftSub, Meta = "1000", Seconds = 60, Points = 1 },
                new () { EventType = SubathonEventType.TwitchGiftSub, Meta = "2000", Seconds = 120, Points = 2 },
                new () { EventType = SubathonEventType.TwitchGiftSub, Meta = "3000", Seconds = 300, Points = 5 },
                new () { EventType = SubathonEventType.TwitchCheer, Seconds = 0.12 },
                new () { EventType = SubathonEventType.TwitchFollow, Seconds = 0 },
                new () { EventType = SubathonEventType.TwitchRaid, Seconds = 0 },
                new () { EventType = SubathonEventType.StreamElementsDonation, Seconds = 12}, // per 1 unit/dollar of default currency
                new () { EventType = SubathonEventType.StreamLabsDonation, Seconds = 12}, // per 1 unit/dollar of default currency
                new () { EventType = SubathonEventType.YouTubeSuperChat, Seconds = 12}, // per 1 unit/dollar of default currency
                new () { EventType = SubathonEventType.YouTubeMembership, Meta = "DEFAULT", Seconds = 60, Points = 1},
                new () { EventType = SubathonEventType.YouTubeGiftMembership, Meta = "DEFAULT", Seconds = 60, Points = 1},
                new () { EventType = SubathonEventType.TwitchCharityDonation, Seconds = 12}, // per 1 unit/dollar of default currency
                new () { EventType = SubathonEventType.ExternalDonation, Seconds = 12}, // per 1 unit/dollar of default currency
                new () { EventType = SubathonEventType.KoFiDonation, Seconds = 12}, // per 1 unit/dollar of default currency
                new () { EventType = SubathonEventType.KoFiSub, Meta = "DEFAULT", Seconds = 60, Points = 1},
                new () { EventType = SubathonEventType.KoFiShopOrder, Seconds = 12}, // per 1 unit/dollar of default currency
                new () { EventType = SubathonEventType.KoFiCommissionOrder, Seconds = 12}, // per 1 unit/dollar of default currency
                new () { EventType = SubathonEventType.BlerpBeets, Seconds = 0.12 },
                new () { EventType = SubathonEventType.BlerpBits, Seconds = 0.12 },
                new () { EventType = SubathonEventType.PicartoSub, Meta = "T1", Seconds = 60, Points = 1 },
                new () { EventType = SubathonEventType.PicartoSub, Meta = "T2", Seconds = 120, Points = 2 },
                new () { EventType = SubathonEventType.PicartoSub, Meta = "T3", Seconds = 180, Points = 3 },
                new () { EventType = SubathonEventType.PicartoGiftSub, Meta = "T1", Seconds = 60, Points = 1 }, // you can only gift T1
                // PicartoMonth subs, treat as amount? = months * quantity = amount
                new () { EventType = SubathonEventType.PicartoTip, Seconds = 0.12 },
                new () { EventType = SubathonEventType.PicartoFollow, Seconds = 0 },
                // assuming defaults for order types are by Dollar, we set at 12s
                new () { EventType = SubathonEventType.ExternalSub, Meta = "DEFAULT", Seconds = 60, Points = 1},
                new () { EventType = SubathonEventType.YouTubeRedirect, Seconds = 0 },
                new () { EventType = SubathonEventType.FourthWallDonation, Seconds = 12},
                new () { EventType = SubathonEventType.FourthWallMembership, Meta = "DEFAULT", Seconds = 60, Points = 1},
                new () { EventType = SubathonEventType.FourthWallGiftOrder, Seconds = 12},
                new () { EventType = SubathonEventType.FourthWallOrder, Seconds = 12},
                new () { EventType = SubathonEventType.ThroneGiftContribution, Seconds = 12},
                new () { EventType = SubathonEventType.ThroneGiftPurchase, Seconds = 12},
                
                new () { EventType = SubathonEventType.GamerSuppsOrder,  Seconds = 12 },
                new () { EventType = SubathonEventType.UwUMarketOrder,  Seconds = 12 },
                new () { EventType = SubathonEventType.OrchidEightOrder,  Seconds = 12 },
                new () { EventType = SubathonEventType.KatDragonzOrder,  Seconds = 12 },
                new () { EventType = SubathonEventType.CheekySoapOrder,  Seconds = 12 },
                new () { EventType = SubathonEventType.SaucyBizOrder,  Seconds = 12 },
                new () { EventType = SubathonEventType.AdvancedGGOrder,  Seconds = 12 },
                new () { EventType = SubathonEventType.RogueEnergyOrder,  Seconds = 12 },
                new () { EventType = SubathonEventType.GFuelOrder,  Seconds = 12 },
                new () { EventType = SubathonEventType.NaturaPineOrder,  Seconds = 12 },
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
            
            if (!db.SubathonPromptSets.Any(s => s.IsActive))
            {
                db.SubathonPromptSets.Add(new SubathonPromptSet { IsActive = true });
            }

            MigrateLegacyData(db);
            SeedKnownGoAffProStores(db);
            db.SaveChanges();
        }

        private static void SeedKnownGoAffProStores(AppDbContext db)
        {
            var defaults = new List<GoAffProStore>
            {
                new () { SiteId = 165328, StoreName = "GamerSupps", EventName = "GamerSupps Order"},
                new () { SiteId = 132230, StoreName = "UwUMarket", EventName = "UwUMarket Order"},
                new () { SiteId = 7142837, StoreName = "Orchid Eight", EventName = "Orchid Eight Order"},
                new () { SiteId = 7160049, StoreName = "KatDragonz", EventName = "KatDragonz Order"},
                new () { SiteId = 7138531, StoreName = "Cheeky Soap", EventName = "Cheeky Soap Order"},
                new () { SiteId = 105752, StoreName = "Advanced GG", EventName = "Advanced GG Order"},
                new () { SiteId = 7014645, StoreName = "Rogue Energy", EventName = "Rogue Energy Order"},
                new () { SiteId = 7118656, StoreName = "Saucy Biz", EventName = "Saucy Biz Order"},
                new () { SiteId = 48808, StoreName = "GFuel", EventName = "GFuel Order"},
                new () { SiteId = 7132796, StoreName = "Natura Pine", EventName = "Natura Pine Order"},
            };
            
            foreach (var def in defaults)
            {
                if (db.GoAffProStores.Any(store => store.SiteId == def.SiteId && (store.StoreName != def.StoreName || store.EventName != def.EventName)))
                {
                    db.GoAffProStores.Update(def);
                }
                else if (!db.GoAffProStores.Any(store => store.SiteId == def.SiteId))
                {
                    db.GoAffProStores.Add(def);
                }
                
                // if (!db.SubathonValues.Any(sv => sv.EventType == SubathonEventType.GoAffProOrder 
                //                                  && sv.Meta == def.SiteId.ToString()))
                // {
                //     db.SubathonValues.Add( new ()
                //     {
                //         EventType =  SubathonEventType.GoAffProOrder, Seconds = 12, Meta = def.SiteId.ToString()
                //     });
                // }
            }
        }

        private static void MigrateLegacyData(AppDbContext db)
        {
            
            if (db.SubathonGoalSets.Any(s => s.Type == null))
            {
                db.SubathonGoalSets.Where(s => s.Type == null)
                    .ExecuteUpdate(s =>
                        s.SetProperty(x => x.Type, GoalsType.Points));
            }
            
            if (db.SubathonDatas.Any(s => s.ReversedTime == null))
            {
                db.SubathonDatas.Where(s => s.ReversedTime == null)
                    .ExecuteUpdate(s =>
                        s.SetProperty(x => x.ReversedTime, false));
            }
            
                    // int newGoAffProOrderInt = (int)SubathonEventType.GoAffProOrder;
                    // Dictionary<int, string> LegacyEventTypeToMeta = new()
                    // {
                    //     [(int)SubathonEventType.GamerSuppsOrder] = "165328",
                    //     [(int)SubathonEventType.UwUMarketOrder] = "132230",
                    //     [(int)SubathonEventType.OrchidEightOrder] = "7142837",
                    //     [(int)SubathonEventType.KatDragonzOrder] = "7160049",
                    //     [(int)SubathonEventType.CheekySoapOrder] = "7138531",
                    //     [(int)SubathonEventType.AdvancedGGOrder] = "105752",
                    //     [(int)SubathonEventType.RogueEnergyOrder] = "7014645",
                    //     [(int)SubathonEventType.SaucyBizOrder] = "7118656",
                    // };
                    //
                    // foreach (var (oldTypeInt, meta) in LegacyEventTypeToMeta)
                    // {
                    //     // SubathonValue rows
                    //     db.Database.ExecuteSqlRaw(
                    //         "UPDATE SubathonValues SET EventType = {0}, Meta = {1} WHERE EventType = {2} AND Meta = ''",
                    //         newGoAffProOrderInt, meta, oldTypeInt);
                    //
                    //     // SubathonEvent rows  
                    //     db.Database.ExecuteSqlRaw(
                    //         "UPDATE SubathonEvents SET EventType = {0}, EventTypeMeta = {1} WHERE EventType = {2}",
                    //         newGoAffProOrderInt, meta, oldTypeInt);
                    // }
                    //
                    // foreach (var subathonEventType in Enum.GetValues<SubathonEventType>().Where(x => ((SubathonEventType?)x).IsSubscription()))
                    // {
                    //     db.Database.ExecuteSqlRaw(
                    //         "UPDATE SubathonEvents SET EventTypeMeta = Value WHERE EventType = {0}",
                    //         (int)subathonEventType);
                    // }
            
            // var fontTypes = WidgetVariableTypeHelper.FontVariables.ToList();
            // var widgetsMissingFontTypes = db.Widgets
            //     .Where(w => !fontTypes.All(ft => w.JsVariables.Any(v => v.Type == ft)))
            //     .Include(w => w.JsVariables)
            //     .ToList();
            // if (widgetsMissingFontTypes.Count > 0)
            // {
            //     var newVars = new List<JsVariable>();
            //     foreach (var widget in widgetsMissingFontTypes)
            //     {
            //         var existingTypes = widget.JsVariables
            //             .Select(v => v.Type)
            //             .ToHashSet();
            //
            //         foreach (var fontVar in fontTypes.Where(ft => !existingTypes.Contains(ft)))
            //         {
            //             newVars.Add(new JsVariable
            //             {
            //                 WidgetId = widget.Id,
            //                 Widget = widget,
            //                 Type = fontVar,
            //                 Name = $"{fontVar}s",
            //                 Description = $"Custom font names to include from {fontVar}s, comma separated",
            //                 Value = string.Empty
            //             });
            //         }
            //     }
            //     if (newVars.Count > 0)
            //     {
            //         db.JsVariables.AddRange(newVars);
            //         db.SaveChanges();
            //     }
            // }
        }
    }
}