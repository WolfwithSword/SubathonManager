using Moq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SubathonManager.Services;
using SubathonManager.Data;
using SubathonManager.Core.Models;
using SubathonManager.Core.Enums;
using System.Net;
using Moq.Protected;
using IniParser.Model;

namespace SubathonManager.Tests.ServicesUnitTests;

[Collection("Sequential")]
public class EventServiceTests
{
    private static IConfig MockConfig(Dictionary<(string, string), string>? values = null)
    {
        var mock = new Mock<IConfig>();
        
        mock.Setup(c => c.Get(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string s, string k, string d) =>
                values != null && values.TryGetValue((s, k), out var v) ? v : d);
            
        var kd0 = new KeyData("Primary");
        kd0.Value = "USD";
        mock.Setup(c => c.GetSection("Currency")).Returns(() =>
        {
            var kdc = new KeyDataCollection();
            kdc.AddKey(kd0);
            return kdc;
        });
        return mock.Object;
    }

    private static CurrencyService SetupCurrencyService()
    {
        var jsonResponse = @"{
                ""usd"": {""code"": ""USD"", ""rate"": 1.0},
                ""gbp"": {""code"": ""GBP"", ""rate"": 0.9},
                ""cad"": {""code"": ""GBP"", ""rate"": 0.8},
                ""twd"": {""code"": ""GBP"", ""rate"": 0.7},
                ""aud"": {""code"": ""GBP"", ""rate"": 0.6},
            }";
        var handlerMock = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .Returns<HttpRequestMessage, CancellationToken>((req, ct) =>
                Task.FromResult(new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent(jsonResponse)
                }));

        var httpClient = new HttpClient(handlerMock.Object);

        var loggerMock = new Mock<ILogger<CurrencyService>>();
        var mockConfig = MockConfig(new()
        {
            { ("Currency", "Primary"), "USD" }
        });
        var currencyMock = new CurrencyService(loggerMock.Object, mockConfig, httpClient);
        currencyMock.SetRates(new Dictionary<string, double>
        {
            { "USD", 1.0 }, { "GBP", 0.9 }, { "CAD", 0.8 }, { "TWD", 0.6 }, { "AUD", 0.5 }
        });
        return currencyMock;
    }

    private static async Task<(EventService, DbContextOptions<AppDbContext>, 
        Microsoft.Data.Sqlite.SqliteConnection)> SetupServiceWithDb(int initialPoints = 10, bool isLocked = true)
    {
        var connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:;Cache=Shared");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        await using (var db = new AppDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            db.SubathonDatas.Add(new SubathonData { Id = Guid.NewGuid(), Points = initialPoints, IsActive = true, IsLocked = isLocked, Currency="USD" });
            db.SubathonGoalSets.Add(new SubathonGoalSet { Id = Guid.NewGuid(), IsActive = true, Goals = new List<SubathonGoal>() });
            await db.SaveChangesAsync();
            AppDbContext.SeedDefaultValues(db);
        }

        var factoryMock = new Mock<IDbContextFactory<AppDbContext>>();
        factoryMock.Setup(f => f.CreateDbContextAsync(default))
            .ReturnsAsync(() => new AppDbContext(options));

        var service = new EventService(factoryMock.Object, Mock.Of<ILogger<EventService>>(), MockConfig(), SetupCurrencyService());

        return (service, options, connection);
    }

    [Fact]
    public async Task AddPointsCommand_Works()
    {
        var (service, options, conn) = await SetupServiceWithDb(10);

        var ev = new SubathonEvent
        {
            Id = Guid.NewGuid(),
            EventType = SubathonEventType.Command,
            Command = SubathonCommandType.AddPoints,
            PointsValue = 5
        };

        var (processed, dupe) = await service.ProcessSubathonEvent(ev);

        Assert.True(processed);
        Assert.False(dupe);

        await using var checkDb = new AppDbContext(options);
        var sub = await checkDb.SubathonDatas.FirstAsync();
        Assert.Equal(15, sub.Points);

        await conn.CloseAsync();
    }
    
    [Fact]
    public async Task SubtractPointsCommand_Works()
    {
        var (service, options, conn) = await SetupServiceWithDb();

        var ev = new SubathonEvent
        {
            Id = Guid.NewGuid(),
            EventType = SubathonEventType.Command,
            Command = SubathonCommandType.SubtractPoints,
            PointsValue = 5
        };

        var (processed, _) = await service.ProcessSubathonEvent(ev);

        Assert.True(processed);

        await using var db = new AppDbContext(options);
        var sub = await db.SubathonDatas.FirstAsync();
        Assert.Equal(5, sub.Points);

        await conn.CloseAsync();
    }
    
    [Fact]
    public async Task SetPointsCommand_Works()
    {
        var (service, options, conn) = await SetupServiceWithDb();

        var ev = new SubathonEvent
        {
            Id = Guid.NewGuid(),
            EventType = SubathonEventType.Command,
            Command = SubathonCommandType.SetPoints,
            PointsValue = 100
        };

        var (processed, _) = await service.ProcessSubathonEvent(ev);

        Assert.True(processed);

        await using var db = new AppDbContext(options);
        var sub = await db.SubathonDatas.FirstAsync();
        Assert.Equal(100, sub.Points);

        await conn.CloseAsync();
    }
    
    [Fact]
    public async Task AddTimeCommand_Works()
    {
        var (service, options, conn) = await SetupServiceWithDb();

        var ev = new SubathonEvent
        {
            Id = Guid.NewGuid(),
            EventType = SubathonEventType.Command,
            Command = SubathonCommandType.AddTime,
            SecondsValue = 120
        };

        var (processed, _) = await service.ProcessSubathonEvent(ev);
        Assert.True(processed);

        await using var db = new AppDbContext(options);
        var sub = await db.SubathonDatas.FirstAsync();
        Assert.Equal(120_000, sub.MillisecondsCumulative);

        await conn.CloseAsync();
    }

    [Fact]
    public async Task SubtractTimeCommand_Works()
    {
        var (service, options, conn) = await SetupServiceWithDb();

        var ev = new SubathonEvent
        {
            Id = Guid.NewGuid(),
            EventType = SubathonEventType.Command,
            Command = SubathonCommandType.SubtractTime,
            SecondsValue = 30
        };

        var (processed, _) = await service.ProcessSubathonEvent(ev);
        Assert.True(processed);

        await using var db = new AppDbContext(options);
        var sub = await db.SubathonDatas.FirstAsync();
        Assert.Equal(-30_000, sub.MillisecondsCumulative);

        await conn.CloseAsync();
    }  
    
    [Fact]
    public async Task DonationEvent_CalculatesPointsAndSeconds()
    {
        var (service, options, conn) = await SetupServiceWithDb();
        var ev = new SubathonEvent
        {
            Id = Guid.NewGuid(),
            EventType = SubathonEventType.KoFiDonation,
            Currency = "USD",
            Value = "10"
        };

        var (processed, _) = await service.ProcessSubathonEvent(ev);
        Assert.False(processed);
        
        await using var db = new AppDbContext(options);
        var sub = await db.SubathonDatas.FirstAsync();
        sub.IsLocked = false;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        
        var ev2 = new SubathonEvent
        {
            Id = Guid.NewGuid(),
            EventType = SubathonEventType.KoFiDonation,
            Currency = "USD",
            Value = "12.22"
        };
        
        (processed, _) = await service.ProcessSubathonEvent(ev2);
        Assert.True(processed);
        
        sub = await db.SubathonDatas.FirstAsync();
        Assert.True(sub.Points > 0);
        Assert.True(sub.MillisecondsCumulative > 0);

        await conn.CloseAsync();
    }
    
    [Fact]
    public async Task DuplicateEvent_IsDetected()
    {
        var (service, options, conn) = await SetupServiceWithDb();

        var ev = new SubathonEvent
        {
            Id = Guid.NewGuid(),
            EventType = SubathonEventType.Command,
            Command = SubathonCommandType.AddPoints,
            PointsValue = 5
        };

        var (processed, dupe) = await service.ProcessSubathonEvent(ev);
        Assert.True(processed);
        Assert.False(dupe);

        var (processed2, dupe2) = await service.ProcessSubathonEvent(ev);
        Assert.False(processed2);
        Assert.True(dupe2);

        await conn.CloseAsync();
    }
    
    [Fact]
    public async Task Event_WithLockedSubathon_DoesNotUpdatePoints()
    {
        var (service, options, conn) = await SetupServiceWithDb();

        await using var db = new AppDbContext(options);
        var sub = await db.SubathonDatas.FirstAsync();
        sub.IsLocked = true;
        await db.SaveChangesAsync();

        var ev = new SubathonEvent
        {
            Id = Guid.NewGuid(),
            EventType = SubathonEventType.KoFiDonation,
            Value = "10.00",
            Currency = "USD"
        };

        var (processed, dupe) = await service.ProcessSubathonEvent(ev);
        Assert.False(processed); 
        Assert.False(dupe);

        await conn.CloseAsync();
    }
    
    [Fact]
    public async Task DeleteSubathonEvent_RemovesPointsAndTime()
    {
        var (service, options, conn) = await SetupServiceWithDb(0);

        await using var db = new AppDbContext(options);
        var sub = await db.SubathonDatas.FirstAsync();
        sub.IsLocked = false;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.Equal(0, sub.Points);
        Assert.Equal(0, sub.MillisecondsCumulative);

        var ev = new SubathonEvent
        {
            Id = Guid.NewGuid(),
            SubathonId = sub.Id,
            EventType = SubathonEventType.ExternalSub,
            PointsValue = 10,
            SecondsValue = 120,
            Source = SubathonEventSource.External,
            ProcessedToSubathon = true
        };
        
        var sub1 = await db.SubathonDatas.FirstAsync();
        sub1.Points += (int) ev.PointsValue;
        sub1.MillisecondsCumulative += (int) ev.SecondsValue * 1000;
        db.SubathonEvents.Add(ev);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        
        var sub2 = await db.SubathonDatas.FirstAsync();
        Assert.Equal(10, sub2.Points);
        Assert.Equal(120000, sub2.MillisecondsCumulative);

        service.DeleteSubathonEvent(db, ev);
        await Task.Delay(50);
        db.ChangeTracker.Clear();

        var subUpdated = await db.SubathonDatas.FirstAsync();
        Assert.Equal(0, subUpdated.Points);
        Assert.Equal(0, subUpdated.MillisecondsCumulative);
    }
    
    [Fact]
    public async Task UndoSimulatedEvents_RemovesSingleEvent()
    {
        var (service, options, conn) = await SetupServiceWithDb(0);
        await using var db = new AppDbContext(options);

        var sub = await db.SubathonDatas.FirstAsync();
        sub.IsLocked = false;
        await db.SaveChangesAsync();

        var ev = new SubathonEvent
        {
            Id = Guid.NewGuid(),
            SubathonId = sub.Id,
            Source = SubathonEventSource.Simulated,
            User = "SYSTEM",
            EventType = SubathonEventType.KoFiDonation,
            Value = "10",
            Currency = "USD",
            PointsValue = 5,
            SecondsValue = 10,
            ProcessedToSubathon = true
        };

        sub.Points += (int) ev.PointsValue;
        sub.MillisecondsCumulative += (int) ev.SecondsValue * 1000;
        sub.MoneySum += 10;
        db.SubathonEvents.Add(ev);
        await db.SaveChangesAsync();

        var subUpdated = await db.SubathonDatas.FirstAsync();
        Assert.NotEqual(0, subUpdated.Points);
        Assert.NotEqual(0, subUpdated.MillisecondsCumulative);
        Assert.NotEqual(0, subUpdated.MoneySum);
        
        await Task.Run(() => service.UndoSimulatedEvents(db, new List<SubathonEvent> { ev }));
        await Task.Delay(50);
        db.ChangeTracker.Clear();
        
        var subUpdated2 = await db.SubathonDatas.FirstAsync();
        Assert.Equal(0, subUpdated2.Points);
        Assert.Equal(0, subUpdated2.MillisecondsCumulative);
        Assert.Equal(0, subUpdated2.MoneySum);
    }
    
    [Fact]
    public async Task HypeTrainStart_UpdatesMultiplier()
    {
        var (service, options, conn) = await SetupServiceWithDb();
        var ev = new SubathonEvent
        {
            Id = Guid.NewGuid(),
            EventType = SubathonEventType.TwitchHypeTrain,
            Value = "start"
        };
        var (processed, dupe) = await service.ProcessSubathonEvent(ev);
        Assert.True(processed);
    }

    [Fact]
    public async Task HypeTrainEnd_ResetsMultiplier()
    {
        var (service, options, conn) = await SetupServiceWithDb();
        await using var db = new AppDbContext(options);
        var sub = await db.SubathonDatas.FirstAsync();
        sub.Multiplier.FromHypeTrain = true;
        await db.SaveChangesAsync();

        var ev = new SubathonEvent
        {
            Id = Guid.NewGuid(),
            EventType = SubathonEventType.TwitchHypeTrain,
            Value = "end"
        };
        var (processed, dupe) = await service.ProcessSubathonEvent(ev);
        Assert.True(processed);
    }

    [Theory]
    [InlineData(SubathonCommandType.Lock, true)]
    [InlineData(SubathonCommandType.Unlock, false)]
    public async Task Command_LockUnlock_UpdatesIsLocked(SubathonCommandType cmd, bool expected)
    {
        var (service, options, conn) = await SetupServiceWithDb();
        var ev = new SubathonEvent { Id = Guid.NewGuid(), EventType = SubathonEventType.Command, Command = cmd };
        var (processed, _) = await service.ProcessSubathonEvent(ev);
        Assert.True(processed);

        await using var db = new AppDbContext(options);
        var sub = await db.SubathonDatas.FirstAsync();
        Assert.Equal(expected, sub.IsLocked);
    }
    
    [Theory]
    [InlineData(SubathonCommandType.Pause, true)]
    [InlineData(SubathonCommandType.Resume, false)]
    public async Task Command_PauseResume_UpdatesIsPaused(SubathonCommandType cmd, bool expected)
    {
        var (service, options, conn) = await SetupServiceWithDb();
        var ev = new SubathonEvent { Id = Guid.NewGuid(), EventType = SubathonEventType.Command, Command = cmd };
        var (processed, _) = await service.ProcessSubathonEvent(ev);
        Assert.True(processed);

        await using var db = new AppDbContext(options);
        var sub = await db.SubathonDatas.FirstAsync();
        Assert.Equal(expected, sub.IsPaused);
    }

    [Fact]
    public async Task Command_StopMultiplier_SetsMultiplierToOne()
    {
        var (service, options, conn) = await SetupServiceWithDb();
        await using var db = new AppDbContext(options);
        var sub = await db.SubathonDatas.FirstAsync();
        sub.Multiplier.Multiplier = 5;
        await db.SaveChangesAsync();

        var ev = new SubathonEvent { Id = Guid.NewGuid(), EventType = SubathonEventType.Command, Command = SubathonCommandType.StopMultiplier };
        var (processed, _) = await service.ProcessSubathonEvent(ev);
        Assert.True(processed);

        sub = await db.SubathonDatas.Include(s => s.Multiplier).FirstAsync();
        Assert.Equal(1, sub.Multiplier.Multiplier);
    }

    [Fact]
    public async Task DonationEvent_InvalidCurrency_SetsCurrencyUnknown()
    {
        var (service, options, conn) = await SetupServiceWithDb();
        var ev = new SubathonEvent { Id = Guid.NewGuid(), EventType = SubathonEventType.KoFiDonation, Value="10", Currency="" };
        var (processed, _) = await service.ProcessSubathonEvent(ev);

        Assert.False(processed);
    }

    
    [Fact]
    public async Task DonationEvent_InvalidCurrency_SetsCurrencyUnknown2()
    {
        var (service, options, conn) = await SetupServiceWithDb();
        var ev = new SubathonEvent { Id = Guid.NewGuid(), EventType = SubathonEventType.KoFiDonation, Value="10", Currency="XXX" };
        var (processed, _) = await service.ProcessSubathonEvent(ev);

        Assert.False(processed);
        Assert.Equal("XXX", ev.Currency);
    }
    
    [Fact]
    public async Task DonationAdjustment_Event_SetsPointsAndSecondsZero()
    {
        var (service, options, conn) = await SetupServiceWithDb(0, false);
        var ev = new SubathonEvent { Id = Guid.NewGuid(), EventType = SubathonEventType.DonationAdjustment, Value="10", Currency="USD" };
        var (processed, _) = await service.ProcessSubathonEvent(ev);

        Assert.True(processed);
        Assert.Equal(0, ev.PointsValue);
        Assert.Equal(0, ev.SecondsValue);
    }

    [Fact]
    public async Task DonationEvent_ReversedSubathon_AffectsSecondsNegatively()
    {
        var (service, options, conn) = await SetupServiceWithDb();
        await using var db = new AppDbContext(options);
        var sub = await db.SubathonDatas.FirstAsync();
        sub.IsLocked = false;
        sub.MillisecondsCumulative = 0;
        sub.MillisecondsElapsed = 100_000;
        sub.MillisecondsCumulative = 0;
        sub.MillisecondsElapsed = 0;
        sub.MillisecondsCumulative = 0;
        sub.MillisecondsElapsed = 0;

        sub.ReversedTime = true;
        await db.SaveChangesAsync();

        var ev = new SubathonEvent { Id = Guid.NewGuid(), EventType = SubathonEventType.KoFiDonation, Value="10", Currency="USD" };
        var (processed, _) = await service.ProcessSubathonEvent(ev);
        Assert.True(processed);
    }
    
    [Theory]
    [InlineData(SubathonCommandType.SetTime, 5000)]
    [InlineData(SubathonCommandType.SetMultiplier, 0)]
    [InlineData(SubathonCommandType.StopMultiplier, 0)]
    public async Task AdditionalCommands_Work(SubathonCommandType cmd, int value)
    {
        var (service, options, conn) = await SetupServiceWithDb(0, false);
        await using var db = new AppDbContext(options);

        var ev = new SubathonEvent
        {
            Id = Guid.NewGuid(),
            EventType = SubathonEventType.Command,
            Command = cmd,
            SecondsValue = value,
            Value = cmd == SubathonCommandType.SetMultiplier ? "2|xs|true|false" : $"{value}"
        };

        var (processed, _) = await service.ProcessSubathonEvent(ev);
        Assert.True(processed);

        var sub = await db.SubathonDatas.Include(s => s.Multiplier).FirstAsync();
        switch (cmd)
        {
            case SubathonCommandType.SetTime:
                Assert.Equal(ev.SecondsValue * 1000, sub.MillisecondsCumulative + sub.MillisecondsElapsed);
                break;
            case SubathonCommandType.SetMultiplier:
                Assert.Equal(2, sub.Multiplier.Multiplier);
                break;
            case SubathonCommandType.StopMultiplier:
                Assert.Equal(1, sub.Multiplier.Multiplier);
                break;
        }

        await conn.CloseAsync();
    }

    [Fact]
    public async Task DonationEvent_AltCurrencyModifier_AppliesCorrectly()
    {
        var (service, options, conn) = await SetupServiceWithDb(0, false);
        await using var db = new AppDbContext(options);

        var ev = new SubathonEvent
        {
            Id = Guid.NewGuid(),
            EventType = SubathonEventType.KoFiDonation,
            Value = "100",
            Currency = "USD"
        };

        var (processed, _) = await service.ProcessSubathonEvent(ev);
        Assert.True(processed);

        var sub = await db.SubathonDatas.FirstAsync();
        Assert.True(sub.MoneySum > 0);

        await conn.CloseAsync();
    }
    
    [Fact]
    public async Task DonationEvent_ReversedSubathon_NegatesTime()
    {
        var (service, options, conn) = await SetupServiceWithDb();
        await using var db = new AppDbContext(options);

        var sub = await db.SubathonDatas.FirstAsync();
        sub.IsLocked = false;
        sub.ReversedTime = true;
        await db.SaveChangesAsync();

        var ev = new SubathonEvent
        {
            Id = Guid.NewGuid(),
            EventType = SubathonEventType.KoFiDonation,
            Value = "10",
            Currency = "USD"
        };

        var (processed, _) = await service.ProcessSubathonEvent(ev);
        Assert.True(processed);
        Assert.True(ev.WasReversed);

        await conn.CloseAsync();
    }
    
    [Fact]
    public async Task TwitchSubEvent_DoNotDupeEvent()
    {
        var (service, options, conn) = await SetupServiceWithDb();
        await using var db = new AppDbContext(options);

        var sub = await db.SubathonDatas.FirstAsync();
        sub.IsLocked = false;
        sub.ReversedTime = false;
        await db.SaveChangesAsync();

        var ev = new SubathonEvent
        {
            Id = Guid.NewGuid(),
            EventType = SubathonEventType.TwitchSub,
            Value = "1000",
            Currency = "sub",
            User = "TestUser",
            Source = SubathonEventSource.Twitch,
        };
        
        var (processed, _) = await service.ProcessSubathonEvent(ev);
        Assert.True(processed);
        await Task.Delay(25);
        
        var ev2 = new SubathonEvent
        {
            Id = Guid.NewGuid(),
            EventType = SubathonEventType.TwitchSub,
            Value = "1000",
            Currency = "sub",
            User = "TestUser",
            Source = SubathonEventSource.Twitch
        };
        
        var (processed2, _) = await service.ProcessSubathonEvent(ev2);
        Assert.False(processed2);

        await Task.Delay(25);
        
        var ev3 = new SubathonEvent
        {
            Id = Guid.NewGuid(),
            EventType = SubathonEventType.TwitchSub,
            Value = "1000",
            Currency = "sub",
            User = "TestUser2",
            Source = SubathonEventSource.Twitch
        };
        
        var (processed3, _) = await service.ProcessSubathonEvent(ev3);
        Assert.True(processed3);
        
                
        var ev4 = new SubathonEvent
        {
            Id = Guid.NewGuid(),
            EventType = SubathonEventType.TwitchSub,
            Value = "3000",
            Currency = "sub",
            User = "TestUser",
            Source = SubathonEventSource.Twitch
        };
        
        var (processed4, _) = await service.ProcessSubathonEvent(ev4);
        Assert.True(processed4);
        
        await conn.CloseAsync();
    }
    
    [Fact]
    public async Task DonationAdjustment_Event_SetsPointsAndSecondsZero_ProcessedTrue()
    {
        var (service, options, conn) = await SetupServiceWithDb(0, false);
        var ev = new SubathonEvent
        {
            Id = Guid.NewGuid(),
            EventType = SubathonEventType.DonationAdjustment,
            Value = "50",
            Currency = "USD"
        };

        var (processed, _) = await service.ProcessSubathonEvent(ev);

        Assert.True(processed);
        Assert.Equal(0, ev.PointsValue);
        Assert.Equal(0, ev.SecondsValue);

        await conn.CloseAsync();
    }
    
    [Fact]
    public async Task HypeTrainProgress_UpdatesMultiplier()
    {
        var (service, options, conn) = await SetupServiceWithDb();
        var ev = new SubathonEvent
        {
            Id = Guid.NewGuid(),
            EventType = SubathonEventType.TwitchHypeTrain,
            Value = "progress",
            Amount = 7
        };

        var (processed, _) = await service.ProcessSubathonEvent(ev);
        Assert.True(processed);

        await conn.CloseAsync();
    }
    
    [Theory]
    [InlineData(SubathonCommandType.SetPoints, -5)]
    [InlineData(SubathonCommandType.AddPoints, 0)]
    [InlineData(SubathonCommandType.SubtractPoints, 0)]
    public async Task Command_NegativeOrZeroPoints_ReturnsFalseFalse(SubathonCommandType cmd, int pts)
    {
        var (service, options, conn) = await SetupServiceWithDb();
        var ev = new SubathonEvent
        {
            Id = Guid.NewGuid(),
            EventType = SubathonEventType.Command,
            Command = cmd,
            PointsValue = pts
        };

        var (processed, dupe) = await service.ProcessSubathonEvent(ev);

        Assert.False(processed);
        Assert.False(dupe);

        await conn.CloseAsync();
    }
}
