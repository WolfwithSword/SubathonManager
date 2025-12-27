using SubathonManager.Core.Models;
using SubathonManager.Core.Enums;
using Microsoft.EntityFrameworkCore;
using SubathonManager.Data;
namespace SubathonManager.Tests.DataUnitTests;

[Collection("Sequential")]
public class DbContextTests
{
    private AppDbContext CreateInMemorySqliteDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("DataSource=:memory:;Cache=Shared")
            .Options;
        var db = new AppDbContext(options);
        db.Database.OpenConnection();
        db.Database.EnsureCreated();
        return db;
    }
    
    private AppDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new AppDbContext(options);
        return db;
    }

    [Fact]
    public async Task Deleting_Route_Deletes_Widgets_And_Css()
    {
        await using var db = CreateInMemoryDb();

        var route = new Route() { Name = "Test" };
        var widget = new Widget("W", "test.html") { Route = route };
        widget.CssVariables.Add(new CssVariable { Name = "color", Value = "red" });

        db.Routes.Add(route);
        await db.SaveChangesAsync();

        db.Routes.Remove(route);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.Empty(db.Widgets);
        Assert.Empty(db.CssVariables);
    }
    
    [Fact]
    public async Task SubathonValue_Composite_Key_Is_Enforced()
    {
        await using var db = CreateInMemorySqliteDb();

        db.SubathonValues.Add(new SubathonValue {
            EventType = SubathonEventType.TwitchSub,
            Meta = "1000"
        });
        
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        db.SubathonValues.Add(new SubathonValue {
            EventType = SubathonEventType.TwitchSub,
            Meta = "1000"
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
    
    [Fact]
    public async Task Saving_Widget_Updates_Parent_Route_Timestamp()
    {
        var db = CreateInMemorySqliteDb();

        var route = new Route();
        db.Routes.Add(route);
        await db.SaveChangesAsync();

        var original = route.UpdatedTimestamp;

        var widget = new Widget("W", "x.html") { RouteId = route.Id };
        db.Widgets.Add(widget);
        await db.SaveChangesAsync();

        Assert.True(route.UpdatedTimestamp > original);
        var original2 = route.UpdatedTimestamp;
        
        var jsVar = new JsVariable { Name = "jsVar", Value = "1", WidgetId = widget.Id };
        db.JsVariables.Add(jsVar);
        await db.SaveChangesAsync();
        Assert.True(route.UpdatedTimestamp > original2);
    }

    [Fact]
    public async Task GetSubathonCurrencyEvents_Returns_Only_Currency_Events()
    {
        var db = CreateInMemoryDb();

        var subathon = new SubathonData { IsActive = true };
        db.SubathonDatas.Add(subathon);

        db.SubathonEvents.Add(new SubathonEvent {
            SubathonId = subathon.Id,
            EventType = SubathonEventType.KoFiDonation,
            Currency = "USD",
            Value = "5",
            ProcessedToSubathon = true
        });

        await db.SaveChangesAsync();

        var result = await AppDbContext.GetSubathonCurrencyEvents(db);

        Assert.Single(result);
    }

    [Fact]
    public async Task ActiveEventsToCsv_Writes_File()
    {
        var db = CreateInMemoryDb();

        var subathon = new SubathonData { IsActive = true };
        db.SubathonDatas.Add(subathon);
        db.SubathonEvents.Add(new SubathonEvent {
            SubathonId = subathon.Id,
            EventType = SubathonEventType.KoFiDonation,
            Currency = "USD",
            Value = "5",
            ProcessedToSubathon = true
        });

        await db.SaveChangesAsync();

        await AppDbContext.ActiveEventsToCsv(db);

        var files = Directory.GetFiles(Path.Combine(Config.DataFolder, "exports"));
        Assert.Single(files);
        File.Delete(files[0]);
        
    }
    
    [Fact]
    public async Task UpdateSubathonCurrency_Updates_Active_Subathon()
    {
        await using var db = CreateInMemorySqliteDb();

        var subathon = new SubathonData
        {
            IsActive = true,
            Currency = "USD"
        };

        db.SubathonDatas.Add(subathon);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await AppDbContext.UpdateSubathonCurrency(db, "CAD");

        var updated = await db.SubathonDatas.FirstAsync();
        Assert.Equal("CAD", updated.Currency);
    }
    
    [Fact]
    public async Task PauseAllTimers_Sets_IsPaused()
    {
        await using var db = CreateInMemorySqliteDb();

        var subathon = new SubathonData
        {
            IsActive = true,
            IsPaused = false
        };

        db.SubathonDatas.Add(subathon);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await AppDbContext.PauseAllTimers(db);
        db.ChangeTracker.Clear();
        var updated = await db.SubathonDatas.FirstAsync();
        Assert.True(updated.IsPaused);
    }
    
    [Fact]
    public async Task Saving_Widget_Without_Route_Fails()
    {
        await using var db = CreateInMemorySqliteDb();

        var widget = new Widget("W", "x.html")
        {
            RouteId = Guid.NewGuid()
        };

        db.Widgets.Add(widget);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
    
    [Fact]
    public async Task Duplicate_Route_Entities_Fail()
    {
        await using var db = CreateInMemorySqliteDb();
        var guid = Guid.NewGuid();
        db.Routes.Add(new Route { Name = "Test", Id = guid });

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            db.Routes.Add(new Route { Name = "Test", Id = guid });
            await db.SaveChangesAsync();
        });
    }
    
    [Fact]
    public async Task Updating_Widget_Path_Does_Not_Update_Route_Timestamp()
    {
        var db = CreateInMemorySqliteDb();

        var route = new Route();
        db.Routes.Add(route);
        await db.SaveChangesAsync();

        var widget = new Widget("W", "x.html") { RouteId = route.Id };
        db.Widgets.Add(widget);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var timestamp = route.UpdatedTimestamp;

        widget.HtmlPath = "y.html";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.Equal(timestamp, route.UpdatedTimestamp);
    }

    [Fact]
    public async Task GetSubathonCurrencyEvents_No_Active_Subathon_Returns_Empty()
    {
        var db = CreateInMemoryDb();

        db.SubathonDatas.Add(new SubathonData { IsActive = false });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await AppDbContext.GetSubathonCurrencyEvents(db);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetSubathonCurrencyEvents_Unprocessed_Excluded()
    {
        var db = CreateInMemoryDb();

        var subathon = new SubathonData { IsActive = true };
        db.SubathonDatas.Add(subathon);

        db.SubathonEvents.Add(new SubathonEvent {
            SubathonId = subathon.Id,
            EventType = SubathonEventType.KoFiDonation,
            Currency = "USD",
            Value = "5",
            ProcessedToSubathon = false
        });

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await AppDbContext.GetSubathonCurrencyEvents(db);

        Assert.Empty(result);
    }
    
    [Fact]
    public async Task PauseAllTimers_No_Active_Subathon_No_Change()
    {
        await using var db = CreateInMemorySqliteDb();

        db.SubathonDatas.Add(new SubathonData {
            IsActive = false,
            IsPaused = false
        });

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await AppDbContext.PauseAllTimers(db);
        db.ChangeTracker.Clear();

        var subathon = await db.SubathonDatas.FirstAsync();
        Assert.False(subathon.IsActive);
        Assert.True(subathon.IsPaused);
    }

    [Fact]
    public async Task UpdateSubathonCurrency_Ignores_Inactive()
    {
        await using var db = CreateInMemorySqliteDb();

        db.SubathonDatas.Add(new SubathonData {
            IsActive = false,
            Currency = "USD"
        });

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await AppDbContext.UpdateSubathonCurrency(db, "CAD");
        db.ChangeTracker.Clear();

        var subathon = await db.SubathonDatas.FirstAsync();
        Assert.Equal("USD", subathon.Currency);
    }

    [Theory]
    [InlineData(SubathonEventType.TwitchSub, "1000")]
    [InlineData(SubathonEventType.YouTubeMembership, "DEFAULT")]
    [InlineData(SubathonEventType.KoFiSub, "DEFAULT")]
    public async Task CurrencyEvents_Ignore_Non_Currency_Types(SubathonEventType type, string value)
    {
        var db = CreateInMemoryDb();

        var subathon = new SubathonData { IsActive = true };
        db.SubathonDatas.Add(subathon);

        db.SubathonEvents.Add(new SubathonEvent {
            SubathonId = subathon.Id,
            EventType = type,
            Currency = "sub",
            Value = value,
            Amount = 1,
            ProcessedToSubathon = true
        });

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await AppDbContext.GetSubathonCurrencyEvents(db);
        Assert.Empty(result);
    }
    
    [Theory]
    [InlineData(SubathonEventType.TwitchCharityDonation)]
    [InlineData(SubathonEventType.YouTubeSuperChat)]
    [InlineData(SubathonEventType.KoFiDonation)]
    public async Task CurrencyEvents_Contains_Currency_Types(SubathonEventType type)
    {
        var db = CreateInMemorySqliteDb();

        var subathon = new SubathonData { IsActive = true };
        db.SubathonDatas.Add(subathon);

        db.SubathonEvents.Add(new SubathonEvent {
            SubathonId = subathon.Id,
            EventType = type,
            Currency = "CAD",
            Value = "10.00",
            ProcessedToSubathon = true
        });

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await AppDbContext.GetSubathonCurrencyEvents(db);
        Assert.Single(result);
    }

    [Fact]
    public async Task ResetPowerHour_ResetsMultiplierData()
    {
        var db = CreateInMemorySqliteDb();

        db.MultiplierDatas.Add(new MultiplierData { Multiplier = 5, ApplyToSeconds = true });
        await db.SaveChangesAsync();

        db.ChangeTracker.Clear();
        await AppDbContext.ResetPowerHour(db);

        var updated = await db.MultiplierDatas.FirstAsync();
        Assert.Equal(1, updated.Multiplier);
        Assert.False(updated.ApplyToSeconds);
        Assert.False(updated.ApplyToPoints);
        Assert.Null(updated.Duration);
        Assert.False(updated.FromHypeTrain);
    }

    [Fact]
    public async Task DisableAllTimers_Sets_IsActiveFalse_And_IsPaused()
    {
        var db = CreateInMemorySqliteDb();

        db.SubathonDatas.Add(new SubathonData { IsActive = true, IsPaused = false });
        await db.SaveChangesAsync();

        await AppDbContext.DisableAllTimers(db);

        db.ChangeTracker.Clear();
        var sub = await db.SubathonDatas.FirstAsync();
        Assert.False(sub.IsActive);
        Assert.True(sub.IsPaused);
    }
    
    
    [Fact]
    public void SeedDefaultValues_AddsMissingValuesAndSubathonData()
    {
        var db = CreateInMemorySqliteDb();

        // no seed
        Assert.Empty(db.SubathonValues);
        Assert.Empty(db.SubathonDatas);
        Assert.Empty(db.SubathonGoalSets);

        AppDbContext.SeedDefaultValues(db);

        // seeded
        Assert.NotEmpty(db.SubathonValues);
        Assert.NotEmpty(db.SubathonDatas);
        Assert.NotEmpty(db.SubathonGoalSets);

        AppDbContext.SeedDefaultValues(db);
        Assert.Equal(db.SubathonValues.Count(), db.SubathonValues.Distinct().Count());
    }
    
    [Fact]
    public async Task MigrateLegacyData_FixesNullProperties()
    {
        var db = CreateInMemorySqliteDb();

        db.SubathonGoalSets.Add(new SubathonGoalSet { Type = null });
        db.SubathonDatas.Add(new SubathonData { ReversedTime = null });
        await db.SaveChangesAsync();

        db.ChangeTracker.Clear();
        var method = typeof(AppDbContext).GetMethod("MigrateLegacyData", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        method.Invoke(null, new object[] { db });

        var goalSet = await db.SubathonGoalSets.FirstAsync();
        var subathon = await db.SubathonDatas.FirstAsync();

        Assert.Equal(GoalsType.Points, goalSet.Type);
        Assert.False(subathon.ReversedTime);
    }
    
    [Fact]
    public async Task UpdateSubathonMoney_UpdatesValueAndRaisesEvents()
    {
        var db = CreateInMemorySqliteDb();

        var multiplier = new MultiplierData();
        var subathon = new SubathonData { IsActive = true, Multiplier = multiplier };
        db.SubathonDatas.Add(subathon);
        await db.SaveChangesAsync();

        var goalSet = new SubathonGoalSet { IsActive = true };
        var goal = new SubathonGoal { GoalSetId = goalSet.Id };
        goalSet.Goals.Add(goal);
        db.SubathonGoalSets.Add(goalSet);
        await db.SaveChangesAsync();
        
        await db.UpdateSubathonMoney(100.5, subathon.Id);

        var updatedSubathon = await db.SubathonDatas.Include(s => s.Multiplier).FirstAsync();
        Assert.Equal(subathon.MillisecondsCumulative, updatedSubathon.MillisecondsCumulative);
        Assert.Equal(multiplier.Id, updatedSubathon.Multiplier.Id);

        var fetchedGoalSet = await db.SubathonGoalSets.Include(g => g.Goals).FirstAsync();
        Assert.Single(fetchedGoalSet.Goals);
    }
}