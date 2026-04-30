using Moq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SubathonManager.Services;
using SubathonManager.Data;
using SubathonManager.Core.Models;
using SubathonManager.Core.Enums;
using System.Net;
using System.Reflection;
using Moq.Protected;
using SubathonManager.Tests.Utility;
// ReSharper disable RedundantAssignment
// ReSharper disable NotAccessedVariable
// ReSharper disable UnusedVariable
// ReSharper disable NullableWarningSuppressionIsUsed
// ReSharper disable SuggestVarOrType_BuiltInTypes

namespace SubathonManager.Tests.ServicesUnitTests;

[Collection("SequentialParallel")]
public class EventServiceTests
{
    
    // ReSharper disable once InconsistentNaming
    internal static CurrencyService SetupCurrencyService()
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
        var currencyMock = new CurrencyService(loggerMock.Object, MockConfig.MakeMockConfig(new Dictionary<(string, string), string>
        {
            { ("Currency", "Primary"), "USD" }
        }), httpClient);
        currencyMock.SetRates(new Dictionary<string, double>
        {
            { "USD", 1.0 }, { "GBP", 0.9 }, { "CAD", 0.8 }, { "TWD", 0.6 }, { "AUD", 0.5 }
        });
        return currencyMock;
    }

    internal static async Task<(EventService, DbContextOptions<AppDbContext>,
        Microsoft.Data.Sqlite.SqliteConnection)> SetupServiceWithDb(int initialPoints = 10, bool isLocked = true, bool showEventsState = false, bool allowPointsLocked = true)
    {
        var dbName = $"test_{Guid.NewGuid():N}";
        var connectionString = $"DataSource={dbName};Mode=Memory;Cache=Shared";
        //var connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:"); //;Cache=Shared");
        var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        
        await using (var db = new AppDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            db.SubathonDatas.Add(new SubathonData
            {
                Id = Guid.NewGuid(), Points = initialPoints, IsActive = true, IsLocked = isLocked, Currency = "USD"
            });
            db.SubathonGoalSets.Add(new SubathonGoalSet
                { Id = Guid.NewGuid(), IsActive = true, Goals = [new SubathonGoal() { Text = "New Goal", Points = 1 }] });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            AppDbContext.SeedDefaultValues(db);
        }

        var factoryMock = new Mock<IDbContextFactory<AppDbContext>>();
        factoryMock
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new AppDbContext(options));
        typeof(Core.Events.SubathonEvents)
            .GetField("SubathonGoalCompleted", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
        typeof(Core.Events.SubathonEvents)
            .GetField("SubathonDataUpdate", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
        typeof(Core.Events.SubathonEvents)
            .GetField("SubathonEventCreated", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
        typeof(Core.Events.SubathonEvents)
            .GetField("SubathonEventProcessed", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
        typeof(Core.Events.SubathonEvents)
            .GetField("SubathonGoalListUpdated", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
        typeof(Core.Events.ErrorMessageEvents)
            .GetField("ErrorEventOccured", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);

        var service = new EventService(factoryMock.Object, Mock.Of<ILogger<EventService>>(), MockConfig.MakeMockConfig(
            new Dictionary<(string, string), string>
            {
                { ("Currency", "Primary"), "USD" },
                { ("Currency", "BitsLikeAsDonation"), "True" },
                { ("GoAffPro", "UwUMarket.CommissionAsDonation"), "True" },
                { ("GoAffPro", "GamerSupps.CommissionAsDonation"), "False" },
                { ("GoAffPro", "UwUMarket.Mode"), "Dollar" },
                { ("GoAffPro", "GamerSupps.Mode"), "Item" },
                { ("Twitch", "HypeTrainMultiplier.Enabled"), "True" },
                { ("Twitch", "HypeTrainMultiplier.Points"), "True" },
                { ("Twitch", "HypeTrainMultiplier.Time"), "True" },
                { ("Twitch", "HypeTrainMultiplier.Multiplier"), "2" },
                { ("App", "OtherValuesWhenLocked"), allowPointsLocked.ToString()},
                { ("App", "ShowLockedEvents"), showEventsState.ToString()}
            }), SetupCurrencyService());
        await service.StartAsync();

        return (service, options, connection);
    }

    internal static Task RunUndoAndWait(EventService service, AppDbContext db, List<SubathonEvent> events, bool doAll = false)
    {
        var tcs = new TaskCompletionSource<bool>();
        Action<List<SubathonEvent>>? handler = null;
        handler = _ =>
        {
            Core.Events.SubathonEvents.SubathonEventsDeleted -= handler!;
            tcs.TrySetResult(true);
        };
        Core.Events.SubathonEvents.SubathonEventsDeleted += handler;
        var task = service.UndoSimulatedEvents(db, events, doAll);
        return Task.WhenAny(tcs.Task, task, Task.Delay(3000));
    }

    [Fact]
    public async Task FetchValidCurrencies_Test()
    {
        var (service, options, conn) = await SetupServiceWithDb(10);

        var data = service.ValidEventCurrencies();
        Assert.NotNull(data);
        Assert.NotEmpty(data);
        await service.StopAsync(TestContext.Current.CancellationToken);
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
        var sub = await checkDb.SubathonDatas.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(15, sub.Points);

        await service.StopAsync(TestContext.Current.CancellationToken);
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
        var sub = await db.SubathonDatas.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(5, sub.Points);

        await service.StopAsync(TestContext.Current.CancellationToken);
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
        var sub = await db.SubathonDatas.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(100, sub.Points);

        await service.StopAsync(TestContext.Current.CancellationToken);
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
        var sub = await db.SubathonDatas.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(120_000, sub.MillisecondsCumulative);

        await service.StopAsync(TestContext.Current.CancellationToken);
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
        var sub = await db.SubathonDatas.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(-30_000, sub.MillisecondsCumulative);

        await service.StopAsync(TestContext.Current.CancellationToken);
        await conn.CloseAsync();
    }

    [Fact]
    public async Task DonationEvent_CalculatesPointsAndSeconds()
    {
        var (service, options, conn) = await SetupServiceWithDb(allowPointsLocked: false);
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
        var sub = await db.SubathonDatas.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        sub.IsLocked = false;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
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

        sub = await db.SubathonDatas.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(sub.Points > 0);
        Assert.True(sub.MillisecondsCumulative > 0);

        await service.StopAsync(TestContext.Current.CancellationToken);
        await conn.CloseAsync();
    }

    [Fact]
    public async Task OrderEvent_WithCommission()
    {
        var (service, options, conn) = await SetupServiceWithDb(0, false);
        var ev = new SubathonEvent
        {
            Id = Guid.NewGuid(),
            EventType = SubathonEventType.UwUMarketOrder,
            Currency = "USD",
            Value = "10.00",
            SecondaryValue = "2.00|USD"
        };

        var (processed, _) = await service.ProcessSubathonEvent(ev);
        await service.StopAsync(TestContext.Current.CancellationToken);
        Assert.True(processed);

        await using var db = new AppDbContext(options);
        var sub = await db.SubathonDatas.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        sub.IsLocked = false;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        sub = await db.SubathonDatas.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(sub.Points > 0);
        Assert.True(sub.MillisecondsCumulative > 0);
        Assert.True(sub.MoneySum > 0);
        Assert.Equal(2.00, sub.MoneySum);

        var ev2 = await db.SubathonEvents.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("USD", ev2.Currency);
        Assert.True(ev2.ProcessedToSubathon);
        Assert.Equal(0, ev2.PointsValue);
        Assert.Equal(12 * 10, ev2.SecondsValue);

        await conn.CloseAsync();
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(1, 3)]
    public async Task OrderEvent_ByItemsNoCommission(int pointsValue, int itemCount)
    {
        var (service, options, conn) = await SetupServiceWithDb(0, false);
        var ev = new SubathonEvent
        {
            Id = Guid.NewGuid(),
            EventType = SubathonEventType.GamerSuppsOrder,
            Currency = "items",
            Value = itemCount.ToString(),
            Amount = itemCount,
            SecondaryValue = "2.00|USD"
        };

        await using var db = new AppDbContext(options);
        var value = await db.SubathonValues.Where(x => x.EventType == SubathonEventType.GamerSuppsOrder).FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        value.Points = pointsValue;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        
        var (processed, _) = await service.ProcessSubathonEvent(ev);
        Assert.True(processed);

        var sub = await db.SubathonDatas.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        sub.IsLocked = false;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        sub = await db.SubathonDatas.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        if (pointsValue * itemCount > 0)
            Assert.True(sub.Points > 0);
        else
        {
            Assert.False(sub.Points > 0);
        }
        Assert.True(sub.MillisecondsCumulative > 0);
        Assert.False(sub.MoneySum > 0);

        var ev2 = await db.SubathonEvents.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("items", ev2.Currency);
        Assert.True(ev2.ProcessedToSubathon);
        Assert.Equal(pointsValue * itemCount, ev2.PointsValue);
        Assert.Equal(12 * 3, ev2.SecondsValue);

        await service.StopAsync(TestContext.Current.CancellationToken);
        await conn.CloseAsync();
    }

    [Fact]
    public async Task OrderEvent_ByOrderNoCommission()
    {
        //
        var (service, options, conn) = await SetupServiceWithDb(0, false);
        var ev = new SubathonEvent
        {
            Id = Guid.NewGuid(),
            EventType = SubathonEventType.GamerSuppsOrder,
            Currency = "order",
            Value = "New",
            SecondaryValue = "2.00|USD"
        };

        var (processed, _) = await service.ProcessSubathonEvent(ev);
        Assert.True(processed);

        await using var db = new AppDbContext(options);
        var sub = await db.SubathonDatas.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        sub.IsLocked = false;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        sub = await db.SubathonDatas.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(sub.Points > 0);
        Assert.True(sub.MillisecondsCumulative > 0);
        Assert.False(sub.MoneySum > 0);

        var ev2 = await db.SubathonEvents.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("order", ev2.Currency);
        Assert.True(ev2.ProcessedToSubathon);
        Assert.Equal(0, ev2.PointsValue);
        Assert.Equal(12, ev2.SecondsValue);

        await service.StopAsync(TestContext.Current.CancellationToken);
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

        await service.StopAsync(TestContext.Current.CancellationToken);
        await conn.CloseAsync();
    }

    [Fact]
    public async Task Event_WithLockedSubathon_DoesNotUpdatePoints()
    {
        var (service, options, conn) = await SetupServiceWithDb(allowPointsLocked: false);

        await using var db = new AppDbContext(options);
        var sub = await db.SubathonDatas.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        sub.IsLocked = true;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

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

        await service.StopAsync(TestContext.Current.CancellationToken);
        await conn.CloseAsync();
    }
    
    [Fact]
    public async Task DeleteOrderTypeSubathonEvent_RemovesPointsAndTimeAndMoney()
    {
        var (service, options, conn) = await SetupServiceWithDb(0, false);

        await using (var db = new AppDbContext(options))
        {
            var sub = await db.SubathonDatas.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(0, sub.Points);
            Assert.Equal(0, sub.MillisecondsCumulative);
        }

        var ev = new SubathonEvent
        {
            Id = Guid.NewGuid(),
            EventType = SubathonEventType.UwUMarketOrder,
            SecondaryValue = "2.00|USD",
            Value = "10.00",
            Currency = "USD"
        };

        var (processed, _) = await service.ProcessSubathonEvent(ev);
        await service.StopAsync(TestContext.Current.CancellationToken);
        Assert.True(processed);

        await using var checkDb1 = new AppDbContext(options);
        var sub2 = await checkDb1.SubathonDatas.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(0, sub2.Points);
        Assert.Equal(12 * 10 * 1000, sub2.MillisecondsCumulative);
        Assert.True(sub2.MoneySum > 0);
        Assert.Equal(2, sub2.MoneySum);

        await using var serviceDb = new AppDbContext(options);
        await service.DeleteSubathonEvent(serviceDb, ev);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await using var checkDb2 = new AppDbContext(options);
        var subUpdated = await checkDb2.SubathonDatas.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(0, subUpdated.Points);
        Assert.Equal(0, subUpdated.MillisecondsCumulative);
        Assert.Equal(0, subUpdated.MoneySum);

        await conn.CloseAsync();
    }

    [Fact]
    public async Task UndoSimulatedEvents_RemovesSingleEvent()
    {
        var (service, options, conn) = await SetupServiceWithDb(0);

        SubathonEvent ev;
        await using (var db = new AppDbContext(options))
        {
            var sub = await db.SubathonDatas.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
            sub.IsLocked = false;
            ev = new SubathonEvent
            {
                Id = Guid.NewGuid(), SubathonId = sub.Id, Source = SubathonEventSource.Simulated,
                User = "SYSTEM", EventType = SubathonEventType.KoFiDonation, Value = "10",
                Currency = "USD", PointsValue = 5, SecondsValue = 10, ProcessedToSubathon = true
            };
            db.SubathonEvents.Add(ev);
            sub.Points += (int) ev.PointsValue;
            sub.MillisecondsCumulative += (int) ev.SecondsValue * 1000;
            sub.MoneySum += 10;
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var serviceDb = new AppDbContext(options);
        await service.StopAsync(TestContext.Current.CancellationToken);
        await RunUndoAndWait(service, serviceDb, [ev]);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await using var checkDb = new AppDbContext(options);
        var subUpdated = await checkDb.SubathonDatas.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(0, subUpdated.Points);
        Assert.Equal(0, subUpdated.MillisecondsCumulative);
        Assert.Equal(0, subUpdated.MoneySum);

        await conn.CloseAsync();
    }

    [Fact]
    public async Task UndoSimulatedEvents_RemovesSingleOrderCommissionEvent()
    {
        var (service, options, conn) = await SetupServiceWithDb(0);

        SubathonEvent ev;
        await using (var db = new AppDbContext(options))
        {
            var sub = await db.SubathonDatas.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
            sub.IsLocked = false;
            ev = new SubathonEvent
            {
                Id = Guid.NewGuid(), SubathonId = sub.Id, Source = SubathonEventSource.Simulated,
                User = "SYSTEM", EventType = SubathonEventType.UwUMarketOrder, Value = "10",
                Currency = "USD", SecondaryValue = "2.00|USD", PointsValue = 5, SecondsValue = 10,
                ProcessedToSubathon = true
            };
            db.SubathonEvents.Add(ev);
            sub.Points += (int) ev.PointsValue;
            sub.MillisecondsCumulative += (int) ev.SecondsValue * 1000;
            sub.MoneySum += 2;
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var serviceDb = new AppDbContext(options);
        await service.StopAsync(TestContext.Current.CancellationToken);
        await RunUndoAndWait(service, serviceDb, [ev]);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await using var checkDb = new AppDbContext(options);
        var subUpdated = await checkDb.SubathonDatas.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(0, subUpdated.Points);
        Assert.Equal(0, subUpdated.MillisecondsCumulative);
        Assert.Equal(0, subUpdated.MoneySum);

        await conn.CloseAsync();
    }

    [Fact]
    public async Task UndoSimulatedEvents_RemovesSingleBitsCheerEvent()
    {
        var (service, options, conn) = await SetupServiceWithDb(0);

        SubathonEvent ev;
        await using (var db = new AppDbContext(options))
        {
            var sub = await db.SubathonDatas.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
            sub.IsLocked = false;
            ev = new SubathonEvent
            {
                Id = Guid.NewGuid(), SubathonId = sub.Id, Source = SubathonEventSource.Simulated,
                User = "SYSTEM", EventType = SubathonEventType.TwitchCheer, Value = "30",
                Currency = "bits", PointsValue = 5, SecondsValue = 10, ProcessedToSubathon = true
            };
            db.SubathonEvents.Add(ev);
            sub.Points += (int) ev.PointsValue;
            sub.MillisecondsCumulative += (int) ev.SecondsValue * 1000;
            sub.MoneySum += 0.3;
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var serviceDb = new AppDbContext(options);
        await service.StopAsync(TestContext.Current.CancellationToken);
        await RunUndoAndWait(service, serviceDb, [ev]);

        await using var checkDb = new AppDbContext(options);
        var subUpdated = await checkDb.SubathonDatas.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(0, subUpdated.Points);
        Assert.Equal(0, subUpdated.MillisecondsCumulative);
        Assert.Equal(0, subUpdated.MoneySum);

        await conn.CloseAsync();
    }

    [Fact]
    public async Task HypeTrainStart_UpdatesMultiplier()
    {
        var (service, options, conn) = await SetupServiceWithDb(0, false);
        var ev = new SubathonEvent
        {
            Id = Guid.NewGuid(),
            EventType = SubathonEventType.TwitchHypeTrain,
            Value = "start"
        };
        var (processed, dupe) = await service.ProcessSubathonEvent(ev);
        Assert.True(processed);

        await using var db = new AppDbContext(options);
        var ev2 = await db.SubathonEvents.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(ev2.ProcessedToSubathon);
        Assert.Contains("start | x2", ev2.Value);
        await service.StopAsync(TestContext.Current.CancellationToken);
    }
    
    
    [Fact]
    public async Task HypeTrain_UpdatesMultiplier()
    {
        var (service, options, conn) = await SetupServiceWithDb(0, false);
        var ev = new SubathonEvent
        {
            Id = Guid.NewGuid(),
            EventType = SubathonEventType.TwitchHypeTrain,
            Value = "start"
        };
        var (processed, dupe) = await service.ProcessSubathonEvent(ev);
        Assert.True(processed);

        await using (var checkDb1 = new AppDbContext(options))
        {
            var ev2 = await checkDb1.SubathonEvents.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(ev2.ProcessedToSubathon);
            Assert.Contains(ev2.Value, "start | x2 Points Time");
            var mult1Check = await checkDb1.MultiplierDatas.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(mult1Check.IsRunning());
        }

        var ev3 = new SubathonEvent
        {
            Id = Guid.NewGuid(),
            EventType = SubathonEventType.TwitchHypeTrain,
            Value = "progress",
            Amount = 2
        };
        (processed, dupe) = await service.ProcessSubathonEvent(ev3);
        Assert.True(processed);
        
        var ev4 = new SubathonEvent
        {
            Id = Guid.NewGuid(),
            EventType = SubathonEventType.TwitchHypeTrain,
            Value = "end",
            Amount = 3
        };
        (processed, dupe) = await service.ProcessSubathonEvent(ev4);
        Assert.True(processed);
        await service.StopAsync(TestContext.Current.CancellationToken);

        await using var checkDb2 = new AppDbContext(options);
        var mult2 = await checkDb2.MultiplierDatas.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(mult2.IsRunning());
    }


    [Fact]
    public async Task HypeTrainEnd_ResetsMultiplier()
    {
        var (service, options, conn) = await SetupServiceWithDb();
        await using var db = new AppDbContext(options);
        var sub = await db.SubathonDatas.Include(subathonData => subathonData.Multiplier).FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        sub.Multiplier.FromHypeTrain = true;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var ev = new SubathonEvent
        {
            Id = Guid.NewGuid(),
            EventType = SubathonEventType.TwitchHypeTrain,
            Value = "end"
        };
        var (processed, dupe) = await service.ProcessSubathonEvent(ev);
        Assert.True(processed);
        await service.StopAsync(TestContext.Current.CancellationToken);
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
        var sub = await db.SubathonDatas.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(expected, sub.IsLocked);
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DonationEvent_InvalidCurrency_SetsCurrencyUnknown()
    {
        var (service, options, conn) = await SetupServiceWithDb();
        var ev = new SubathonEvent { Id = Guid.NewGuid(), EventType = SubathonEventType.KoFiDonation, Value="10", Currency="" };
        var (processed, _) = await service.ProcessSubathonEvent(ev);

        Assert.False(processed);
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    
    [Fact]
    public async Task DonationEvent_InvalidCurrency_SetsCurrencyUnknown2()
    {
        var (service, options, conn) = await SetupServiceWithDb();
        var ev = new SubathonEvent { Id = Guid.NewGuid(), EventType = SubathonEventType.KoFiDonation, Value="10", Currency="XXX" };
        var (processed, _) = await service.ProcessSubathonEvent(ev);

        Assert.False(processed);
        Assert.Equal("XXX", ev.Currency);
        await service.StopAsync(TestContext.Current.CancellationToken);
    }
    
    [Fact]
    public async Task DonationAdjustment_Event_SetsPointsAndSecondsZero()
    {
        var (service, options, conn) = await SetupServiceWithDb(0, false);
        var ev = new SubathonEvent { Id = Guid.NewGuid(), EventType = SubathonEventType.DonationAdjustment, Value="10", Currency="USD" };
        var (processed, _) = await service.ProcessSubathonEvent(ev);
        await service.StopAsync(TestContext.Current.CancellationToken);
        Assert.True(processed);
        Assert.Equal(0, ev.PointsValue);
        Assert.Equal(0, ev.SecondsValue);
    }

    [Fact]
    public async Task DonationEvent_ReversedSubathon_AffectsSecondsNegatively()
    {
        var (service, options, conn) = await SetupServiceWithDb();
        await using var db = new AppDbContext(options);
        var sub = await db.SubathonDatas.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        sub.IsLocked = false;
        sub.MillisecondsCumulative = 0;
        sub.MillisecondsElapsed = 100_000;
        sub.MillisecondsCumulative = 0;
        sub.MillisecondsElapsed = 0;
        sub.MillisecondsCumulative = 0;
        sub.MillisecondsElapsed = 0;

        sub.ReversedTime = true;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var ev = new SubathonEvent { Id = Guid.NewGuid(), EventType = SubathonEventType.KoFiDonation, Value="10", Currency="USD" };
        var (processed, _) = await service.ProcessSubathonEvent(ev);
        await service.StopAsync(TestContext.Current.CancellationToken);
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

        var sub = await db.SubathonDatas.Include(s => s.Multiplier).FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
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

        await service.StopAsync(TestContext.Current.CancellationToken);
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

        var sub = await db.SubathonDatas.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(sub.MoneySum > 0);

        await service.StopAsync(TestContext.Current.CancellationToken);
        await conn.CloseAsync();
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DonationEvent_ReversedSubathon_NegatesTime()
    {
        var (service, options, conn) = await SetupServiceWithDb();
        await using var db = new AppDbContext(options);

        var sub = await db.SubathonDatas.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        sub.IsLocked = false;
        sub.ReversedTime = true;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

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

        await service.StopAsync(TestContext.Current.CancellationToken);
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

        await service.StopAsync(TestContext.Current.CancellationToken);
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

        await service.StopAsync(TestContext.Current.CancellationToken);
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

        await service.StopAsync(TestContext.Current.CancellationToken);
        await conn.CloseAsync();
    }

    [Fact]
    public async Task ProcessSubathonEvent_NoActiveSubathon_ReturnsFalseFalse()
    {
        var (service, options, conn) = await SetupServiceWithDb(0, false);
        await using var db = new AppDbContext(options);
        var sub = await db.SubathonDatas.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        sub.IsActive = false;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var ev = new SubathonEvent
        {
            Id = Guid.NewGuid(),
            EventType = SubathonEventType.KoFiDonation,
            Currency = "USD",
            Value = "10"
        };
        
        db.ChangeTracker.Clear();
        var (processed, dupe) = await service.ProcessSubathonEvent(ev);
        Assert.False(processed);
        Assert.False(dupe);

        await service.StopAsync(TestContext.Current.CancellationToken);
        await conn.CloseAsync();
    }

    [Theory]
    
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProcessSubathonEvent_SubathonLocked_NonHypeTrain_SavesButReturnsFalse(bool allowValuesWhenLocked)
    {
        var (service, options, conn) = await SetupServiceWithDb(0, true, false, allowValuesWhenLocked);

        var ev = new SubathonEvent
        {
            Id = Guid.NewGuid(),
            EventType = SubathonEventType.KoFiDonation,
            Currency = "USD",
            Value = "10"
        };

        var (processed, dupe) = await service.ProcessSubathonEvent(ev);
        Assert.Equal(processed, allowValuesWhenLocked);
        Assert.False(dupe);

        await using var db = new AppDbContext(options);
        var stored = await db.SubathonEvents.FirstOrDefaultAsync(e => e.Id == ev.Id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(stored);
        Assert.Equal(stored.ProcessedToSubathon, allowValuesWhenLocked);
        Assert.NotNull(stored.SubathonId);
        Assert.True(stored.CurrentTime >= 0);
        Assert.True(stored.CurrentPoints >= 0);

        await service.StopAsync(TestContext.Current.CancellationToken);
        await conn.CloseAsync();
    }

    [Theory]
    [InlineData(SubathonEventType.ExternalSub)]
    [InlineData(SubathonEventType.DonationAdjustment)]
    public async Task ProcessSubathonEvent_SkipsSubathonValueLookup_ForExemptTypes(SubathonEventType eventType)
    {
        var (service, options, conn) = await SetupServiceWithDb(0, false);

        var ev = new SubathonEvent
        {
            Id = Guid.NewGuid(),
            EventType = eventType,
            Currency = "USD",
            Value = "10",
            PointsValue = eventType == SubathonEventType.ExternalSub ? 5 : 0,
            SecondsValue = eventType == SubathonEventType.ExternalSub ? 60 : 0,
            Source = SubathonEventSource.External
        };

        var (processed, dupe) = await service.ProcessSubathonEvent(ev);
        Assert.True(processed);
        Assert.False(dupe);

        await service.StopAsync(TestContext.Current.CancellationToken);
        await conn.CloseAsync();
    }

    [Fact]
    public async Task ProcessSubathonEvent_NoSubathonValue_ReturnsFalseFalse()
    {
        var (service, options, conn) = await SetupServiceWithDb(0, false);

        await using var db = new AppDbContext(options);
        db.SubathonValues.RemoveRange(db.SubathonValues.Where(v => v.EventType == SubathonEventType.KoFiDonation));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var ev = new SubathonEvent
        {
            Id = Guid.NewGuid(),
            EventType = SubathonEventType.KoFiDonation,
            Currency = "USD",
            Value = "10"
        };

        var (processed, dupe) = await service.ProcessSubathonEvent(ev);
        Assert.False(processed);
        Assert.False(dupe);

        await service.StopAsync(TestContext.Current.CancellationToken);
        await conn.CloseAsync();
    }

    [Fact]
    public async Task ProcessSubathonEvent_InvalidCurrencyOnCurrencyDonation_ZeroesValues()
    {
        var (service, options, conn) = await SetupServiceWithDb(0, false);

        var ev = new SubathonEvent
        {
            Id = Guid.NewGuid(),
            EventType = SubathonEventType.KoFiDonation,
            Currency = "ZZZ",
            Value = "10"
        };

        var (processed, dupe) = await service.ProcessSubathonEvent(ev);
        Assert.False(processed);

        await using var db = new AppDbContext(options);
        var saved = await db.SubathonEvents.FirstOrDefaultAsync(e => e.Id == ev.Id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(saved);
        Assert.Equal(0, saved.PointsValue);
        Assert.Equal(0, saved.SecondsValue);
        Assert.False(saved.ProcessedToSubathon);

        await service.StopAsync(TestContext.Current.CancellationToken);
        await conn.CloseAsync();
    }

    [Fact]
    public async Task ProcessSubathonEvent_ZeroPointsAndSeconds_WithNonUnknownCurrency_SetsProcessedTrue()
    {
        var (service, options, conn) = await SetupServiceWithDb(0, false);

        var ev = new SubathonEvent
        {
            Id = Guid.NewGuid(),
            EventType = SubathonEventType.ExternalSub,
            Currency = "sub",
            Value = "1000",
            Source = SubathonEventSource.External,
            PointsValue = 0,
            SecondsValue = 0
        };

        var (processed, _) = await service.ProcessSubathonEvent(ev);
        Assert.True(processed);

        await using var db = new AppDbContext(options);
        var saved = await db.SubathonEvents.FirstOrDefaultAsync(e => e.Id == ev.Id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(saved);
        Assert.True(saved.ProcessedToSubathon);

        await service.StopAsync(TestContext.Current.CancellationToken);
        await conn.CloseAsync();
    }

    [Fact]
    public async Task ProcessSubathonEvent_ExistingDupeEvent_UpdatesRatherThanAdds()
    {
        var (service, options, conn) = await SetupServiceWithDb(0, false);

        var ev = new SubathonEvent
        {
            Id = Guid.NewGuid(),
            EventType = SubathonEventType.KoFiDonation,
            Currency = "USD",
            Value = "10",
            ProcessedToSubathon = false // first pass
        };

        await using (var db = new AppDbContext(options))
        {
            db.SubathonEvents.Add(ev);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var ev2 = new SubathonEvent
        {
            Id = ev.Id, //
            Source = ev.Source,
            EventType = SubathonEventType.KoFiDonation,
            Currency = "USD",
            Value = "10"
        };

        var (processed, dupe) = await service.ProcessSubathonEvent(ev2);
        Assert.False(dupe); // false because ProcessedToSubathon was false on the first, i.e., reprocessed
        Assert.True(processed);

        await service.StopAsync(TestContext.Current.CancellationToken);
        await conn.CloseAsync();
    }

    [Fact]
    public async Task ProcessSubathonEvent_WasReversed_DecrementsMilliseconds()
    {
        var (service, options, conn) = await SetupServiceWithDb(0, false);
        await using var db = new AppDbContext(options);

        var sub = await db.SubathonDatas.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        sub.ReversedTime = true;
        sub.MillisecondsCumulative = 500_000;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var ev = new SubathonEvent
        {
            Id = Guid.NewGuid(),
            EventType = SubathonEventType.KoFiDonation,
            Currency = "USD",
            Value = "10"
        };

        var (processed, _) = await service.ProcessSubathonEvent(ev);
        Assert.True(processed);

        await db.Entry(sub).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.True(sub.MillisecondsCumulative < 500_000);

        await service.StopAsync(TestContext.Current.CancellationToken);
        await conn.CloseAsync();
    }

    [Theory]
    [InlineData(SubathonEventType.UwUMarketOrder, true)]
    [InlineData(SubathonEventType.GamerSuppsOrder, false)]
    public async Task ProcessSubathonEvent_OrderCommission_MoneySum_Branches(
        SubathonEventType orderType, bool commissionAsDonation)
    {
        var (service, options, conn) = await SetupServiceWithDb(0, false);

        var ev = new SubathonEvent
        {
            Id = Guid.NewGuid(),
            EventType = orderType,
            Currency = "USD",
            Value = "20.00",
            SecondaryValue = "5.00|USD"
        };

        var (processed, _) = await service.ProcessSubathonEvent(ev);
        Assert.True(processed);

        await using var db = new AppDbContext(options);
        var sub = await db.SubathonDatas.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);

        if (commissionAsDonation)
            Assert.True(sub.MoneySum > 0);
        else
            Assert.Equal(0, sub.MoneySum);

        await service.StopAsync(TestContext.Current.CancellationToken);
        await conn.CloseAsync();
    }

    [Fact]
    public async Task ProcessSubathonEvent_CurrencyDonation_AddsMoney()
    {
        var (service, options, conn) = await SetupServiceWithDb(0, false);

        var ev = new SubathonEvent
        {
            Id = Guid.NewGuid(),
            EventType = SubathonEventType.KoFiDonation,
            Currency = "USD",
            Value = "25.00"
        };

        var (processed, _) = await service.ProcessSubathonEvent(ev);
        Assert.True(processed);

        await using var db = new AppDbContext(options);
        var sub = await db.SubathonDatas.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(sub.MoneySum > 0);
        Assert.Equal(25.0, sub.MoneySum!.Value, 2);

        await service.StopAsync(TestContext.Current.CancellationToken);
        await conn.CloseAsync();
    }
    
    [Fact]
    public async Task ProcessSubathonEvent_MoneyChangedAfterSave_RaisesDataUpdate()
    {
        var (service, options, conn) = await SetupServiceWithDb(0, false);

        var ev = new SubathonEvent
        {
            Id = Guid.NewGuid(),
            EventType = SubathonEventType.KoFiDonation,
            Currency = "USD",
            Value = "50"
        };

        bool updateRaised = false;
        void OnDataUpdate(SubathonData _, DateTime __) { updateRaised = true; }
        Core.Events.SubathonEvents.SubathonDataUpdate += OnDataUpdate;

        var (processed, _) = await service.ProcessSubathonEvent(ev);
        Assert.True(processed);
        Assert.True(updateRaised);

        Core.Events.SubathonEvents.SubathonDataUpdate -= OnDataUpdate;

        await service.StopAsync(TestContext.Current.CancellationToken);
        await conn.CloseAsync();
    }

    [Fact]
    public async Task Command_AddMoney_SetsEventTypeToDonationAdjustment()
    {
        var (service, options, conn) = await SetupServiceWithDb(0, false);

        var ev = new SubathonEvent
        {
            Id = Guid.NewGuid(),
            EventType = SubathonEventType.Command,
            Command = SubathonCommandType.AddMoney,
            Value = "15.00",
            Currency = "USD"
        };

        var (processed, _) = await service.ProcessSubathonEvent(ev);
        Assert.True(processed);
        Assert.Equal(SubathonEventType.DonationAdjustment, ev.EventType);

        await service.StopAsync(TestContext.Current.CancellationToken);
        await conn.CloseAsync();
    }

    [Fact]
    public async Task Command_SubtractMoney_NegatesValue()
    {
        var (service, options, conn) = await SetupServiceWithDb(0, false);

        var ev = new SubathonEvent
        {
            Id = Guid.NewGuid(),
            EventType = SubathonEventType.Command,
            Command = SubathonCommandType.SubtractMoney,
            Value = "10.00",
            Currency = "USD"
        };

        var (processed, _) = await service.ProcessSubathonEvent(ev);
        Assert.True(processed);

        await service.StopAsync(TestContext.Current.CancellationToken);
        await using var db = new AppDbContext(options);
        var saved = await db.SubathonEvents.FirstOrDefaultAsync(e => e.Id == ev.Id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(saved);
        Assert.True(double.Parse(saved.Value) < 0);

        await conn.CloseAsync();
    }

    [Fact]
    public async Task Command_SetTime_ReversedSubathon_UpdatesElapsed()
    {
        var (service, options, conn) = await SetupServiceWithDb(0, false);
        await using var db = new AppDbContext(options);
        var sub = await db.SubathonDatas.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        sub.ReversedTime = true;
        sub.MillisecondsCumulative = 1_000_000;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var ev = new SubathonEvent
        {
            Id = Guid.NewGuid(),
            EventType = SubathonEventType.Command,
            Command = SubathonCommandType.SetTime,
            SecondsValue = 300
        };

        var (processed, _) = await service.ProcessSubathonEvent(ev);
        Assert.True(processed);

        await service.StopAsync(TestContext.Current.CancellationToken);
        await conn.CloseAsync();
    }

    [Theory]
    [InlineData(SubathonCommandType.SetMultiplier, "notanumber|xs|true|true", false)]
    [InlineData(SubathonCommandType.SetMultiplier, "2|xs|notabool|true", false)]
    [InlineData(SubathonCommandType.SetMultiplier, "2|xs|true|notabool", false)]
    public async Task Command_SetMultiplier_InvalidData_ReturnsFalse(SubathonCommandType cmd, string value,
        bool expected)
    {
        var (service, options, conn) = await SetupServiceWithDb(0, false);

        var ev = new SubathonEvent
        {
            Id = Guid.NewGuid(),
            EventType = SubathonEventType.Command,
            Command = cmd,
            Value = value
        };

        var (processed, dupe) = await service.ProcessSubathonEvent(ev);
        await service.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(expected, processed);
        Assert.False(dupe);
        await conn.CloseAsync();
    }

    [Fact]
    public async Task Command_SetMultiplier_WithDuration_SetsMultiplier()
    {
        var (service, options, conn) = await SetupServiceWithDb(0, false);

        var ev = new SubathonEvent
        {
            Id = Guid.NewGuid(),
            EventType = SubathonEventType.Command,
            Command = SubathonCommandType.SetMultiplier,
            Value = "3|1h|true|true"
        };

        var (processed, _) = await service.ProcessSubathonEvent(ev);
        Assert.True(processed);

        await using var db = new AppDbContext(options);
        var sub = await db.SubathonDatas.Include(s => s.Multiplier).FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(3, sub.Multiplier.Multiplier);
        Assert.NotNull(sub.Multiplier.Duration);

        await service.StopAsync(TestContext.Current.CancellationToken);
        await conn.CloseAsync();
    }
}

[Collection("Sequential")]
public class EventServiceSequentialTests
{
    
    [Theory]
    [InlineData(0, 300, 0)]
    [InlineData(1, 300, 3)]
    [InlineData(1, 50, 0)]
    public async Task BitsCheerEvent_WithPoints(int pointsValue, int bitsCount, int expectedPoints)
    {
        var (service, options, conn) = await EventServiceTests.SetupServiceWithDb(0, false);
        var ev = new SubathonEvent
        {
            Id = Guid.NewGuid(),
            EventType = SubathonEventType.TwitchCheer,
            Currency = "bits",
            Value = bitsCount.ToString(),
        };
        await using var db = new AppDbContext(options);
        var value = await db.SubathonValues.Where(x => x.EventType == SubathonEventType.TwitchCheer).FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        value.Points = pointsValue;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();
        
        var (processed, _) = await service.ProcessSubathonEvent(ev);
        Assert.True(processed);

        var sub = await db.SubathonDatas.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        sub.IsLocked = false;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        sub = await db.SubathonDatas.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        if (expectedPoints > 0)
            Assert.True(sub.Points > 0);
        else
            Assert.False(sub.Points > 0);
        Assert.True(sub.MillisecondsCumulative > 0);
        Assert.True(sub.MoneySum > 0);

        var ev2 = await db.SubathonEvents.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("bits", ev2.Currency);
        Assert.True(ev2.ProcessedToSubathon);
        Assert.Equal(expectedPoints, ev2.PointsValue);
        Assert.True(ev2.SecondsValue > 0);
        Assert.True(ev2.SecondsValue < 100);

        await service.StopAsync(TestContext.Current.CancellationToken);
        await conn.CloseAsync();
    }
    
    [Fact]
    public async Task DonationEvent_InvalidCurrency()
    {
        var (service, options, conn) = await EventServiceTests.SetupServiceWithDb(0, false);
        var ev = new SubathonEvent
        {
            Id = Guid.NewGuid(),
            EventType = SubathonEventType.KoFiDonation,
            Currency = "EEE",
            Value = "10"
        };

        var (processed, _) = await service.ProcessSubathonEvent(ev);
        await service.StopAsync(TestContext.Current.CancellationToken);
        Assert.False(processed); // will have 0 values

        await using var db = new AppDbContext(options);
        var sub = await db.SubathonDatas.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(sub.Points > 0);
        Assert.False(sub.MillisecondsCumulative > 0);

        var ev2 = await db.SubathonEvents.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("EEE", ev2.Currency);
        Assert.False(ev2.ProcessedToSubathon);
        Assert.Equal(0, ev2.PointsValue);
        Assert.Equal(0, ev2.SecondsValue);

        await conn.CloseAsync();
    }

    [Fact]
    public async Task DonationEvent_InvalidCurrency2()
    {
        var (service, options, conn) = await EventServiceTests.SetupServiceWithDb(0, false);
        var ev = new SubathonEvent
        {
            Id = Guid.NewGuid(),
            EventType = SubathonEventType.KoFiDonation,
            Currency = "",
            Value = "10"
        };

        var (processed, _) = await service.ProcessSubathonEvent(ev);
        await service.StopAsync(TestContext.Current.CancellationToken);
        Assert.False(processed); // will have 0 values

        await using var db = new AppDbContext(options);

        var sub = await db.SubathonDatas.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(sub.Points > 0);
        Assert.False(sub.MillisecondsCumulative > 0);

        var ev2 = await db.SubathonEvents.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("???", ev2.Currency);
        Assert.False(ev2.ProcessedToSubathon);
        Assert.Equal(0, ev2.PointsValue);
        Assert.Equal(0, ev2.SecondsValue);
        await conn.CloseAsync();
    }

    
    [Fact]
    public async Task DeleteSubathonEvent_SubtractPoints_Command_ReversesPoints()
    {
        var (service, options, conn) = await EventServiceTests.SetupServiceWithDb(0, false);

        SubathonEvent ev;
        await using (var db = new AppDbContext(options))
        {
            var sub = await db.SubathonDatas.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
            sub.Points = 15; // already subtracted
            ev = new SubathonEvent
            {
                Id = Guid.NewGuid(), SubathonId = sub.Id, EventType = SubathonEventType.Command,
                Command = SubathonCommandType.SubtractPoints, PointsValue = 5, SecondsValue = 0,
                Source = SubathonEventSource.Command, ProcessedToSubathon = true
            };
            db.SubathonEvents.Add(ev);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var serviceDb = new AppDbContext(options);
        await service.DeleteSubathonEvent(serviceDb, ev);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await using var checkDb = new AppDbContext(options);
        var subUpdated = await checkDb.SubathonDatas.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(20, subUpdated.Points);

        await service.StopAsync(TestContext.Current.CancellationToken);
        await conn.CloseAsync();
    }
    
    [Fact]
    public async Task DeleteSubathonEvent_SubtractTime_Command_ReversesMilliseconds()
    {
        var (service, options, conn) = await EventServiceTests.SetupServiceWithDb(0, false);

        SubathonEvent ev;
        await using (var db = new AppDbContext(options))
        {
            var sub = await db.SubathonDatas.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
            sub.MillisecondsCumulative = 30_000; // already subtracted
            ev = new SubathonEvent
            {
                Id = Guid.NewGuid(), SubathonId = sub.Id, EventType = SubathonEventType.Command,
                Command = SubathonCommandType.SubtractTime, SecondsValue = 30, PointsValue = 0,
                Source = SubathonEventSource.Command, ProcessedToSubathon = true
            };
            db.SubathonEvents.Add(ev);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var serviceDb = new AppDbContext(options);
        await service.DeleteSubathonEvent(serviceDb, ev);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await using var checkDb = new AppDbContext(options);
        var subUpdated = await checkDb.SubathonDatas.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(60_000, subUpdated.MillisecondsCumulative);

        await service.StopAsync(TestContext.Current.CancellationToken);
        await conn.CloseAsync();
    }
    
    [Theory]
    [InlineData(SubathonCommandType.SetPoints)]
    [InlineData(SubathonCommandType.SetTime)]
    public async Task DeleteSubathonEvent_SetPointsOrTime_LogsWarningAndReturns(SubathonCommandType cmd)
    {
        var (service, options, conn) = await EventServiceTests.SetupServiceWithDb(0, false);

        SubathonEvent ev;
        await using (var db = new AppDbContext(options))
        {
            var sub = await db.SubathonDatas.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
            ev = new SubathonEvent
            {
                Id = Guid.NewGuid(), SubathonId = sub.Id, EventType = SubathonEventType.Command,
                Command = cmd, PointsValue = cmd == SubathonCommandType.SetPoints ? 10 : 0,
                SecondsValue = cmd == SubathonCommandType.SetTime ? 60 : 0,
                ProcessedToSubathon = true, Source = SubathonEventSource.Command
            };
            db.SubathonEvents.Add(ev);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        bool errorRaised = false;
        void OnError(string level, string _, string __, DateTime ___) { if (level == "WARN") errorRaised = true; }
        Core.Events.ErrorMessageEvents.ErrorEventOccured += OnError;

        await using var serviceDb = new AppDbContext(options);
        await service.DeleteSubathonEvent(serviceDb, ev);

        Core.Events.ErrorMessageEvents.ErrorEventOccured -= OnError;

        Assert.True(errorRaised);

        await using var checkDb = new AppDbContext(options);
        var stillExists = await checkDb.SubathonEvents.FirstOrDefaultAsync(e => e.Id == ev.Id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(stillExists);

        await service.StopAsync(TestContext.Current.CancellationToken);
        await conn.CloseAsync();
    }
    
    [Fact]
    public async Task DeleteSubathonEvent_WasReversed_InvertsMilliseconds()
    {
        var (service, options, conn) = await EventServiceTests.SetupServiceWithDb(0, false);

        SubathonEvent ev;
        await using (var db = new AppDbContext(options))
        {
            var sub = await db.SubathonDatas.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
            sub.MillisecondsCumulative = -30_000; // already decremented by reversed event
            ev = new SubathonEvent
            {
                Id = Guid.NewGuid(), SubathonId = sub.Id, EventType = SubathonEventType.KoFiDonation,
                Currency = "USD", Value = "10", SecondsValue = 30, PointsValue = 0,
                ProcessedToSubathon = true, WasReversed = true, Command = SubathonCommandType.None
            };
            db.SubathonEvents.Add(ev);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var serviceDb = new AppDbContext(options);
        await service.DeleteSubathonEvent(serviceDb, ev);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await using var checkDb = new AppDbContext(options);
        var subUpdated = await checkDb.SubathonDatas.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(subUpdated.MillisecondsCumulative >= 0);

        await service.StopAsync(TestContext.Current.CancellationToken);
        await conn.CloseAsync();
    }
    
    [Fact]
    public async Task DeleteSubathonEvent_NotProcessed_ZeroesAllValues()
    {
        var (service, options, conn) = await EventServiceTests.SetupServiceWithDb(0, false);

        SubathonEvent ev;
        await using (var db = new AppDbContext(options))
        {
            var sub = await db.SubathonDatas.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
            sub.Points = 10;
            sub.MillisecondsCumulative = 60_000;
            ev = new SubathonEvent
            {
                Id = Guid.NewGuid(), SubathonId = sub.Id, EventType = SubathonEventType.KoFiDonation,
                Currency = "USD", Value = "10", SecondsValue = 30, PointsValue = 5, ProcessedToSubathon = false
            };
            db.SubathonEvents.Add(ev);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var serviceDb = new AppDbContext(options);
        await service.DeleteSubathonEvent(serviceDb, ev);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await using var checkDb = new AppDbContext(options);
        var subUpdated = await checkDb.SubathonDatas.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(10, subUpdated.Points);
        Assert.Equal(60_000, subUpdated.MillisecondsCumulative);

        await service.StopAsync(TestContext.Current.CancellationToken);
        await conn.CloseAsync();
    }
    
    [Fact]
    public async Task DeleteSubathonEvent_BitsAsDonation_RemovesMoney()
    {
        var (service, options, conn) = await EventServiceTests.SetupServiceWithDb(0, false);

        SubathonEvent ev;
        await using (var db = new AppDbContext(options))
        {
            var sub = await db.SubathonDatas.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
            sub.MoneySum = 0.30;
            ev = new SubathonEvent
            {
                Id = Guid.NewGuid(), SubathonId = sub.Id, EventType = SubathonEventType.TwitchCheer,
                Currency = "bits", Value = "30", SecondsValue = 5, PointsValue = 0,
                ProcessedToSubathon = true, Source = SubathonEventSource.Twitch
            };
            db.SubathonEvents.Add(ev);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var serviceDb = new AppDbContext(options);
        await service.DeleteSubathonEvent(serviceDb, ev);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        await using var checkDb = new AppDbContext(options);
        var subUpdated = await checkDb.SubathonDatas.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(subUpdated.MoneySum <= 0);

        await service.StopAsync(TestContext.Current.CancellationToken);
        await conn.CloseAsync();
    }
    
    [Fact]
    public async Task DeleteSubathonEvent_WrongSubathonId_ReturnsEarly()
    {
        var (service, options, conn) = await EventServiceTests.SetupServiceWithDb(0, false);

        await using (var db = new AppDbContext(options))
        {
            var sub = await db.SubathonDatas.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
            sub.Points = 10;
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var ev = new SubathonEvent
        {
            Id = Guid.NewGuid(), SubathonId = Guid.NewGuid(),
            EventType = SubathonEventType.KoFiDonation, PointsValue = 5, SecondsValue = 30, ProcessedToSubathon = true
        };

        await using var serviceDb = new AppDbContext(options);
        await service.DeleteSubathonEvent(serviceDb, ev);

        await using var checkDb = new AppDbContext(options);
        var subUnchanged = await checkDb.SubathonDatas.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(10, subUnchanged.Points);

        await service.StopAsync(TestContext.Current.CancellationToken);
        await conn.CloseAsync();
    }
    
    [Fact]
    public async Task DeleteSubathonEvent_NullSubathonId_ReturnsEarly()
    {
        var (service, options, conn) = await EventServiceTests.SetupServiceWithDb(0, false);

        await using (var db = new AppDbContext(options))
        {
            var sub = await db.SubathonDatas.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
            sub.Points = 10;
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var ev = new SubathonEvent
        {
            Id = Guid.NewGuid(), SubathonId = null,
            EventType = SubathonEventType.KoFiDonation, PointsValue = 5, ProcessedToSubathon = true
        };

        await using var serviceDb = new AppDbContext(options);
        await service.DeleteSubathonEvent(serviceDb, ev);

        await using var checkDb = new AppDbContext(options);
        var subUnchanged = await checkDb.SubathonDatas.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(10, subUnchanged.Points);

        await service.StopAsync(TestContext.Current.CancellationToken);
        await conn.CloseAsync();
    }

    [Fact]
    public async Task UndoSimulatedEvents_DoAll_RemovesAllSimulatedEvents()
    {
        var (service, options, conn) = await EventServiceTests.SetupServiceWithDb(0, false);
        Guid subId;
        await using (var db = new AppDbContext(options))
        {
            var sub = await db.SubathonDatas.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
            subId = sub.Id;
            sub.IsLocked = false;
            sub.Points = 20;
            sub.MillisecondsCumulative = 120_000;
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

            for (int i = 0; i < 3; i++)
            {
                db.SubathonEvents.Add(new SubathonEvent
                {
                    Id = Guid.NewGuid(),
                    SubathonId = sub.Id,
                    Source = SubathonEventSource.Simulated,
                    User = "SYSTEM",
                    EventType = SubathonEventType.KoFiDonation,
                    Value = "5",
                    Currency = "USD",
                    PointsValue = 5,
                    SecondsValue = 30,
                    ProcessedToSubathon = true
                });
            }

            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        } // db disposed fyi

        await service.StopAsync(TestContext.Current.CancellationToken);
        await using var serviceDb = new AppDbContext(options);
        await EventServiceTests.RunUndoAndWait(service, serviceDb, [], doAll: true);

        await using var checkDb = new AppDbContext(options);
        var subUpdated = await checkDb.SubathonDatas.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        var remaining = await checkDb.SubathonEvents
            .Where(e => e.Source == SubathonEventSource.Simulated).ToListAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(remaining);
        Assert.Equal(5, subUpdated.Points);

        await conn.CloseAsync();
    }

    [Fact]
    public async Task UndoSimulatedEvents_SkipsEventsFromOtherSubathon()
    {
        var (service, options, conn) = await EventServiceTests.SetupServiceWithDb(0, false);

        SubathonEvent evFromOtherSubathon;
        await using (var db = new AppDbContext(options))
        {
            var sub = await db.SubathonDatas.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
            sub.Points = 10;
            sub.MillisecondsCumulative = 60_000;
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

            evFromOtherSubathon = new SubathonEvent
            {
                Id = Guid.NewGuid(),
                SubathonId = Guid.NewGuid(), // different subathon
                Source = SubathonEventSource.Simulated,
                User = "SYSTEM",
                EventType = SubathonEventType.KoFiDonation,
                Value = "10",
                Currency = "USD",
                PointsValue = 10,
                SecondsValue = 60,
                ProcessedToSubathon = true
            };
        }

        await service.StopAsync(TestContext.Current.CancellationToken);
        await using var serviceDb = new AppDbContext(options);
        await EventServiceTests.RunUndoAndWait(service, serviceDb, [evFromOtherSubathon]);

        await using var checkDb = new AppDbContext(options);
        var subUnchanged = await checkDb.SubathonDatas.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(10, subUnchanged.Points);
        Assert.Equal(60_000, subUnchanged.MillisecondsCumulative);

        await conn.CloseAsync();
    }
    
    [Fact]
    public async Task UndoSimulatedEvents_SkipsUnprocessedEvents()
    {
        var (service, options, conn) = await EventServiceTests.SetupServiceWithDb(0, false);

        SubathonEvent unprocessedEv;
        await using (var db = new AppDbContext(options))
        {
            var sub = await db.SubathonDatas.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
            sub.Points = 10;
            sub.MillisecondsCumulative = 60_000;
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

            unprocessedEv = new SubathonEvent
            {
                Id = Guid.NewGuid(),
                SubathonId = sub.Id,
                Source = SubathonEventSource.Simulated,
                User = "SYSTEM",
                EventType = SubathonEventType.KoFiDonation,
                Value = "10",
                Currency = "USD",
                PointsValue = 10,
                SecondsValue = 60,
                ProcessedToSubathon = false
            };
            db.SubathonEvents.Add(unprocessedEv);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await service.StopAsync(TestContext.Current.CancellationToken);
        await using var serviceDb = new AppDbContext(options);
        await EventServiceTests.RunUndoAndWait(service, serviceDb, [unprocessedEv]);

        await using var checkDb = new AppDbContext(options);
        var subUnchanged = await checkDb.SubathonDatas.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(10, subUnchanged.Points);
        Assert.Equal(60_000, subUnchanged.MillisecondsCumulative);

        await conn.CloseAsync();
    }
    
    
    [Fact]
    public async Task TwitchSubEvent_DoNotDupeEvent()
    {
        var (service, options, conn) = await EventServiceTests.SetupServiceWithDb();
        await using var db = new AppDbContext(options);

        var sub = await db.SubathonDatas.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        sub.IsLocked = false;
        sub.IsPaused = true;
        sub.ReversedTime = false;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var initialSubTime = sub.MillisecondsCumulative;
        var initialSubPoints = sub.Points;
        
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
        await Task.Delay(25, TestContext.Current.CancellationToken).ConfigureAwait(true);
        await db.Entry(sub).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(sub.Points, initialSubPoints + 1);
        Assert.Equal(sub.MillisecondsCumulative, initialSubTime + (60 * 1000));
        
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
        await db.Entry(sub).ReloadAsync(TestContext.Current.CancellationToken);
        
        Assert.Equal(sub.Points, initialSubPoints + 1);
        Assert.Equal(sub.MillisecondsCumulative, initialSubTime + (60 * 1000));

        await Task.Delay(25, TestContext.Current.CancellationToken);
        
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
        
        await db.Entry(sub).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(sub.Points, initialSubPoints + 2);
        Assert.Equal(sub.MillisecondsCumulative, initialSubTime + (2 * 60 * 1000));
                
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
        await service.StopAsync(TestContext.Current.CancellationToken);
        Assert.True(processed4);
        await db.Entry(sub).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(sub.Points, initialSubPoints + 7);
        Assert.Equal(sub.MillisecondsCumulative, initialSubTime + (7 * 60 * 1000));
        await conn.CloseAsync();
    }
    
    [Theory]
    [InlineData(SubathonCommandType.Pause, true)]
    [InlineData(SubathonCommandType.Resume, false)]
    public async Task Command_PauseResume_UpdatesIsPaused(SubathonCommandType cmd, bool expected)
    {
        var (service, options, conn) = await EventServiceTests.SetupServiceWithDb(0, false);
        var ev = new SubathonEvent { Id = Guid.NewGuid(), EventType = SubathonEventType.Command, Command = cmd };
        var (processed, _) = await service.ProcessSubathonEvent(ev);
        await service.StopAsync(TestContext.Current.CancellationToken);
        Assert.True(processed);

        await using var db = new AppDbContext(options);
        var sub = await db.SubathonDatas.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(expected, sub.IsPaused);
    }

    [Fact]
    public async Task Command_StopMultiplier_SetsMultiplierToOne()
    {
        var (service, options, conn) = await EventServiceTests.SetupServiceWithDb();
        await using var db = new AppDbContext(options);
        var sub = await db.SubathonDatas.Include(subathonData => subathonData.Multiplier).FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        sub.Multiplier.Multiplier = 5;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var ev = new SubathonEvent { Id = Guid.NewGuid(), EventType = SubathonEventType.Command, Command = SubathonCommandType.StopMultiplier };
        var (processed, _) = await service.ProcessSubathonEvent(ev);
        await service.StopAsync(TestContext.Current.CancellationToken);
        Assert.True(processed);

        await db.Entry(sub).ReloadAsync(TestContext.Current.CancellationToken);
        await db.Entry(sub.Multiplier).ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, sub.Multiplier.Multiplier);
    }
    
    
    [Fact]
    public async Task BitsCheerEvent_AsDonation()
    {
        var (service, options, conn) = await EventServiceTests.SetupServiceWithDb(0, false);
        var ev = new SubathonEvent
        {
            Id = Guid.NewGuid(),
            EventType = SubathonEventType.TwitchCheer,
            Currency = "bits",
            Value = "30",
        };

        var (processed, _) = await service.ProcessSubathonEvent(ev);
        Assert.True(processed);

        await using var db = new AppDbContext(options);
        var sub = await db.SubathonDatas.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        sub.IsLocked = false;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        sub = await db.SubathonDatas.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(sub.Points > 0);
        Assert.True(sub.MillisecondsCumulative > 0);
        Assert.True(sub.MoneySum > 0);

        var ev2 = await db.SubathonEvents.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("bits", ev2.Currency);
        Assert.True(ev2.ProcessedToSubathon);
        Assert.Equal(0, ev2.PointsValue);
        Assert.True(ev2.SecondsValue > 0);
        Assert.True(ev2.SecondsValue < 10);

        await service.StopAsync(TestContext.Current.CancellationToken);
        await conn.CloseAsync();
    }
    
    
    [Fact]
    public async Task DeleteSubathonEvent_RemovesPointsAndTime()
    {
        var (service, options, conn) = await EventServiceTests.SetupServiceWithDb(0, false);

        Guid subId;
        SubathonEvent ev;
        await using (var db = new AppDbContext(options))
        {
            var sub = await db.SubathonDatas.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
            subId = sub.Id;
            Assert.Equal(0, sub.Points);
            Assert.Equal(0, sub.MillisecondsCumulative);
            sub.IsLocked = false;
            sub.Points += 10;
            sub.MillisecondsCumulative += 120 * 1000;
            ev = new SubathonEvent
            {
                Id = Guid.NewGuid(), SubathonId = sub.Id, EventType = SubathonEventType.ExternalSub,
                PointsValue = 10, SecondsValue = 120, Source = SubathonEventSource.External, ProcessedToSubathon = true
            };
            db.SubathonEvents.Add(ev);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var serviceDb = new AppDbContext(options);
        await service.DeleteSubathonEvent(serviceDb, ev);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);

        await using var checkDb = new AppDbContext(options);
        var subUpdated = await checkDb.SubathonDatas.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(0, subUpdated.Points);
        Assert.Equal(0, subUpdated.MillisecondsCumulative);
        await conn.CloseAsync();
    }

}