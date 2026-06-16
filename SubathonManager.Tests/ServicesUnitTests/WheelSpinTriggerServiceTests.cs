using System.Net;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Models;
using SubathonManager.Data;
using SubathonManager.Services;
using SubathonManager.Tests.Utility;
// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.Tests.ServicesUnitTests;

[Collection("NonParallel")]
public class WheelSpinTriggerServiceTests
{
    private static CurrencyService MakeCurrencyService()
    {
        var handlerMock = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .Returns<HttpRequestMessage, CancellationToken>((_, _) =>
                Task.FromResult(new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent("{}")
                }));

        var currency = new CurrencyService(
            new Mock<ILogger<CurrencyService>>().Object,
            MockConfig.MakeMockConfig(new() { { ("Currency", "Primary"), "USD" } }),
            new HttpClient(handlerMock.Object));
        currency.SetRates(new Dictionary<string, double>
            { { "USD", 1.0 }, { "GBP", 0.9 }, { "CAD", 0.8 } });
        return currency;
    }

    private static async Task<(WheelSpinTriggerService service, DbContextOptions<AppDbContext> options,
        Microsoft.Data.Sqlite.SqliteConnection conn)> SetupServiceWithDb()
    {
        var dbName = $"test_{Guid.NewGuid():N}";
        var connection = new Microsoft.Data.Sqlite.SqliteConnection($"DataSource={dbName};Mode=Memory;Cache=Shared");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using (var db = new AppDbContext(options))
            await db.Database.EnsureCreatedAsync();

        var factoryMock = new Mock<IDbContextFactory<AppDbContext>>();
        factoryMock
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new AppDbContext(options));

        typeof(SubathonEvents)
            .GetField("SubathonEventProcessed", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
        typeof(WheelEvents)
            .GetField("WheelSpinTriggerFired", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
        typeof(WheelEvents)
            .GetField("OnSpinsOwedUpdateFromEvent", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);

        var service = new WheelSpinTriggerService(factoryMock.Object, MakeCurrencyService());
        await service.StartAsync();
        return (service, options, connection);
    }

    [Fact]
    public async Task CommandEvent_IsSkipped()
    {
        var (service, options, conn) = await SetupServiceWithDb();
        await using (var db = new AppDbContext(options))
        {
            db.WheelSpinTriggers.Add(new WheelSpinTrigger
                { EventType = SubathonEventType.TwitchSub, IsEnabled = true, SpinsToAdd = 1 });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        bool fired = false;
        WheelEvents.WheelSpinTriggerFired += (_, _, _) => fired = true;

        SubathonEvents.RaiseSubathonEventProcessed(new SubathonEvent
        {
            Id = Guid.NewGuid(),
            EventType = SubathonEventType.TwitchSub,
            Command = SubathonCommandType.AddPoints,
            Value = "1000", User = "TestUser"
        }, true);

        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.False(fired);

        await service.StopAsync(TestContext.Current.CancellationToken);
        await conn.CloseAsync();
    }

    [Theory]
    [InlineData(SubathonEventType.TwitchFollow)]
    [InlineData(SubathonEventType.TwitchRaid)]
    [InlineData(SubathonEventType.TwitchHypeTrain)]
    public async Task IgnoredSubType_IsSkipped(SubathonEventType eventType)
    {
        var (service, options, conn) = await SetupServiceWithDb();
        await using (var db = new AppDbContext(options))
        {
            db.WheelSpinTriggers.Add(new WheelSpinTrigger
                { EventType = eventType, IsEnabled = true, SpinsToAdd = 1 });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        bool fired = false;
        WheelEvents.WheelSpinTriggerFired += (_, _, _) => fired = true;

        SubathonEvents.RaiseSubathonEventProcessed(new SubathonEvent
        {
            Id = Guid.NewGuid(), EventType = eventType, Command = SubathonCommandType.None
        }, true);

        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.False(fired);

        await service.StopAsync(TestContext.Current.CancellationToken);
        await conn.CloseAsync();
    }

    [Theory]
    [InlineData("")]
    [InlineData("???")]
    public async Task DonationWithBadCurrency_IsSkipped(string currency)
    {
        var (service, options, conn) = await SetupServiceWithDb();
        await using (var db = new AppDbContext(options))
        {
            db.WheelSpinTriggers.Add(new WheelSpinTrigger
            {
                EventType = SubathonEventType.KoFiDonation, IsEnabled = true,
                SpinsToAdd = 1, MoneyThreshold = 5, Currency = "USD"
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        bool fired = false;
        WheelEvents.WheelSpinTriggerFired += (_, _, _) => fired = true;

        SubathonEvents.RaiseSubathonEventProcessed(new SubathonEvent
        {
            Id = Guid.NewGuid(),
            EventType = SubathonEventType.KoFiDonation,
            Command = SubathonCommandType.None,
            Currency = currency,
            Value = "10"
        }, true);

        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.False(fired);

        await service.StopAsync(TestContext.Current.CancellationToken);
        await conn.CloseAsync();
    }

    [Fact]
    public async Task NoMatchingTriggerInDb_DoesNotFire()
    {
        var (service, _, conn) = await SetupServiceWithDb();

        bool fired = false;
        WheelEvents.WheelSpinTriggerFired += (_, _, _) => fired = true;

        SubathonEvents.RaiseSubathonEventProcessed(new SubathonEvent
        {
            Id = Guid.NewGuid(),
            EventType = SubathonEventType.TwitchSub,
            Command = SubathonCommandType.None,
            Value = "1000", User = "TestUser"
        }, true);

        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.False(fired);

        await service.StopAsync(TestContext.Current.CancellationToken);
        await conn.CloseAsync();
    }

    [Fact]
    public async Task DisabledTrigger_DoesNotFire()
    {
        var (service, options, conn) = await SetupServiceWithDb();
        await using (var db = new AppDbContext(options))
        {
            db.WheelSpinTriggers.Add(new WheelSpinTrigger
                { EventType = SubathonEventType.TwitchSub, IsEnabled = false, SpinsToAdd = 1 });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        bool fired = false;
        WheelEvents.WheelSpinTriggerFired += (_, _, _) => fired = true;

        SubathonEvents.RaiseSubathonEventProcessed(new SubathonEvent
        {
            Id = Guid.NewGuid(),
            EventType = SubathonEventType.TwitchSub,
            Command = SubathonCommandType.None,
            Value = "1000", User = "TestUser"
        }, true);

        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.False(fired);

        await service.StopAsync(TestContext.Current.CancellationToken);
        await conn.CloseAsync();
    }

    [Fact]
    public async Task SubLike_TierMismatch_DoesNotFire()
    {
        var (service, options, conn) = await SetupServiceWithDb();
        await using (var db = new AppDbContext(options))
        {
            db.WheelSpinTriggers.Add(new WheelSpinTrigger
            {
                EventType = SubathonEventType.TwitchSub, IsEnabled = true,
                SpinsToAdd = 1, TierValue = "3000"
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        bool fired = false;
        WheelEvents.WheelSpinTriggerFired += (_, _, _) => fired = true;

        SubathonEvents.RaiseSubathonEventProcessed(new SubathonEvent
        {
            Id = Guid.NewGuid(),
            EventType = SubathonEventType.TwitchSub,
            Command = SubathonCommandType.None,
            Value = "1000", User = "TestUser"
        }, true);

        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.False(fired);

        await service.StopAsync(TestContext.Current.CancellationToken);
        await conn.CloseAsync();
    }

    [Fact]
    public async Task SubLike_TierMatch_Fires_WithCorrectSpinsAndHistory()
    {
        var (service, options, conn) = await SetupServiceWithDb();
        var triggerId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        await using (var db = new AppDbContext(options))
        {
            db.WheelSpinTriggers.Add(new WheelSpinTrigger
            {
                Id = triggerId,
                EventType = SubathonEventType.TwitchSub, IsEnabled = true,
                SpinsToAdd = 2, TierValue = "1000"
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        WheelSpinTrigger? firedTrigger = null;
        WheelSpinTriggerHistory? firedHistory = null;
        int firedNewSpins = 0;
        var tcs = new TaskCompletionSource<bool>();
        WheelEvents.WheelSpinTriggerFired += (t, h, s) =>
        {
            firedTrigger = t; firedHistory = h; firedNewSpins = s;
            tcs.TrySetResult(true);
        };

        SubathonEvents.RaiseSubathonEventProcessed(new SubathonEvent
        {
            Id = eventId,
            EventType = SubathonEventType.TwitchSub,
            Command = SubathonCommandType.None,
            Value = "1000", User = "StreamerFan",
            Source = SubathonEventSource.Twitch
        }, true);

        await Task.WhenAny(tcs.Task, Task.Delay(2000, TestContext.Current.CancellationToken));
        Assert.True(tcs.Task.IsCompleted, "WheelSpinTriggerFired did not fire");
        Assert.Equal(triggerId, firedTrigger!.Id);
        Assert.Equal("StreamerFan", firedHistory!.TriggerUser);
        Assert.Equal(2, firedHistory.SpinsAdded);
        Assert.Equal(2, firedNewSpins);

        await service.StopAsync(TestContext.Current.CancellationToken);
        await conn.CloseAsync();
    }

    [Fact]
    public async Task SubLike_NullTierValue_MatchesAnyTier()
    {
        var (service, options, conn) = await SetupServiceWithDb();
        await using (var db = new AppDbContext(options))
        {
            db.WheelSpinTriggers.Add(new WheelSpinTrigger
            {
                EventType = SubathonEventType.TwitchSub, IsEnabled = true,
                SpinsToAdd = 1, TierValue = null
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var tcs = new TaskCompletionSource<bool>();
        WheelEvents.WheelSpinTriggerFired += (_, _, _) => tcs.TrySetResult(true);

        SubathonEvents.RaiseSubathonEventProcessed(new SubathonEvent
        {
            Id = Guid.NewGuid(),
            EventType = SubathonEventType.TwitchSub,
            Command = SubathonCommandType.None,
            Value = "3000", User = "Tier3User"
        }, true);

        await Task.WhenAny(tcs.Task, Task.Delay(2000, TestContext.Current.CancellationToken));
        Assert.True(tcs.Task.IsCompleted, "WheelSpinTriggerFired did not fire for any-tier match");

        await service.StopAsync(TestContext.Current.CancellationToken);
        await conn.CloseAsync();
    }

    [Fact]
    public async Task ProcessTriggers_UpdatesSpinsOwedInDb()
    {
        var (service, options, conn) = await SetupServiceWithDb();
        await using (var db = new AppDbContext(options))
        {
            db.WheelSpinTriggers.Add(new WheelSpinTrigger
                { EventType = SubathonEventType.TwitchSub, IsEnabled = true, SpinsToAdd = 3 });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var tcs = new TaskCompletionSource<bool>();
        WheelEvents.WheelSpinTriggerFired += (_, _, _) => tcs.TrySetResult(true);

        SubathonEvents.RaiseSubathonEventProcessed(new SubathonEvent
        {
            Id = Guid.NewGuid(), EventType = SubathonEventType.TwitchSub,
            Command = SubathonCommandType.None, Value = "1000", User = "TestUser"
        }, true);

        await Task.WhenAny(tcs.Task, Task.Delay(2000, TestContext.Current.CancellationToken));
        Assert.True(tcs.Task.IsCompleted);

        await using var checkDb = new AppDbContext(options);
        Assert.Equal(3, StateValueHelper.Get<int>(checkDb, StateKeys.WheelSpinsOwed, 0));

        await service.StopAsync(TestContext.Current.CancellationToken);
        await conn.CloseAsync();
    }

    [Fact]
    public async Task ProcessTriggers_SavesHistoryToDb()
    {
        var (service, options, conn) = await SetupServiceWithDb();
        var triggerId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        await using (var db = new AppDbContext(options))
        {
            db.WheelSpinTriggers.Add(new WheelSpinTrigger
                { Id = triggerId, EventType = SubathonEventType.TwitchSub, IsEnabled = true, SpinsToAdd = 1 });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var tcs = new TaskCompletionSource<bool>();
        WheelEvents.WheelSpinTriggerFired += (_, _, _) => tcs.TrySetResult(true);

        SubathonEvents.RaiseSubathonEventProcessed(new SubathonEvent
        {
            Id = eventId, EventType = SubathonEventType.TwitchSub,
            Command = SubathonCommandType.None, Value = "1000",
            User = "HistoryUser", Source = SubathonEventSource.Twitch
        }, true);

        await Task.WhenAny(tcs.Task, Task.Delay(2000, TestContext.Current.CancellationToken));
        Assert.True(tcs.Task.IsCompleted);

        await using var checkDb = new AppDbContext(options);
        var history = checkDb.WheelSpinTriggerHistories.FirstOrDefault();
        Assert.NotNull(history);
        Assert.Equal(triggerId, history.TriggerId);
        Assert.Equal("HistoryUser", history.TriggerUser);
        Assert.Equal(SubathonEventSource.Twitch, history.TriggerSource);
        Assert.Equal(1, history.SpinsAdded);
        Assert.Equal(eventId, history.SubathonEventId);

        await service.StopAsync(TestContext.Current.CancellationToken);
        await conn.CloseAsync();
    }

    [Fact]
    public async Task ProcessTriggers_AccumulatesSpinsOverMultipleFires()
    {
        var (service, options, conn) = await SetupServiceWithDb();
        await using (var db = new AppDbContext(options))
        {
            db.WheelSpinTriggers.Add(new WheelSpinTrigger
                { EventType = SubathonEventType.TwitchSub, IsEnabled = true, SpinsToAdd = 2 });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        for (int i = 0; i < 3; i++)
        {
            var tcs = new TaskCompletionSource<bool>();
            WheelEvents.WheelSpinTriggerFired += (_, _, _) => tcs.TrySetResult(true);

            SubathonEvents.RaiseSubathonEventProcessed(new SubathonEvent
            {
                Id = Guid.NewGuid(), EventType = SubathonEventType.TwitchSub,
                Command = SubathonCommandType.None, Value = "1000", User = $"User{i}"
            }, true);

            await Task.WhenAny(tcs.Task, Task.Delay(2000, TestContext.Current.CancellationToken));
            Assert.True(tcs.Task.IsCompleted, $"Fire #{i} did not complete");
        }

        await using var checkDb = new AppDbContext(options);
        Assert.Equal(6, StateValueHelper.Get<int>(checkDb, StateKeys.WheelSpinsOwed, 0));

        await service.StopAsync(TestContext.Current.CancellationToken);
        await conn.CloseAsync();
    }

    [Fact]
    public async Task GiftSub_NoCountThreshold_ReturnsSpinsToAddFlat()
    {
        var (service, options, conn) = await SetupServiceWithDb();
        await using (var db = new AppDbContext(options))
        {
            db.WheelSpinTriggers.Add(new WheelSpinTrigger
            {
                EventType = SubathonEventType.TwitchGiftSub, IsEnabled = true,
                SpinsToAdd = 5, CountThreshold = null
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        int spinsAdded = 0;
        var tcs = new TaskCompletionSource<bool>();
        WheelEvents.WheelSpinTriggerFired += (_, h, _) => { spinsAdded = h.SpinsAdded; tcs.TrySetResult(true); };

        SubathonEvents.RaiseSubathonEventProcessed(new SubathonEvent
        {
            Id = Guid.NewGuid(), EventType = SubathonEventType.TwitchGiftSub,
            Command = SubathonCommandType.None, Amount = 10, User = "GifterA"
        }, true);

        await Task.WhenAny(tcs.Task, Task.Delay(2000, TestContext.Current.CancellationToken));
        Assert.True(tcs.Task.IsCompleted);
        Assert.Equal(5, spinsAdded);

        await service.StopAsync(TestContext.Current.CancellationToken);
        await conn.CloseAsync();
    }

    [Theory]
    [InlineData(10, 5, 2, 4)]  // 10/5=2 * 2spins = 4
    [InlineData(15, 5, 1, 3)]  // 15/5=3 * 1spin  = 3
    [InlineData(3,  5, 1, 0)]  // 3/5=0 -> no fire
    public async Task GiftSub_WithCountThreshold_ComputesMultiplier(int giftCount, int threshold, int spinsToAdd, int expectedSpins)
    {
        var (service, options, conn) = await SetupServiceWithDb();
        await using (var db = new AppDbContext(options))
        {
            db.WheelSpinTriggers.Add(new WheelSpinTrigger
            {
                EventType = SubathonEventType.TwitchGiftSub, IsEnabled = true,
                SpinsToAdd = spinsToAdd, CountThreshold = threshold
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        if (expectedSpins > 0)
        {
            int actual = 0;
            var tcs = new TaskCompletionSource<bool>();
            WheelEvents.WheelSpinTriggerFired += (_, h, _) => { actual = h.SpinsAdded; tcs.TrySetResult(true); };

            SubathonEvents.RaiseSubathonEventProcessed(new SubathonEvent
            {
                Id = Guid.NewGuid(), EventType = SubathonEventType.TwitchGiftSub,
                Command = SubathonCommandType.None, Amount = giftCount, User = "Gifter"
            }, true);

            await Task.WhenAny(tcs.Task, Task.Delay(2000, TestContext.Current.CancellationToken));
            Assert.True(tcs.Task.IsCompleted);
            Assert.Equal(expectedSpins, actual);
        }
        else
        {
            bool fired = false;
            WheelEvents.WheelSpinTriggerFired += (_, _, _) => fired = true;

            SubathonEvents.RaiseSubathonEventProcessed(new SubathonEvent
            {
                Id = Guid.NewGuid(), EventType = SubathonEventType.TwitchGiftSub,
                Command = SubathonCommandType.None, Amount = giftCount, User = "Gifter"
            }, true);

            await Task.Delay(200, TestContext.Current.CancellationToken);
            Assert.False(fired);
        }

        await service.StopAsync(TestContext.Current.CancellationToken);
        await conn.CloseAsync();
    }

    [Fact]
    public async Task Token_NoCountThreshold_DoesNotFire()
    {
        var (service, options, conn) = await SetupServiceWithDb();
        await using (var db = new AppDbContext(options))
        {
            db.WheelSpinTriggers.Add(new WheelSpinTrigger
            {
                EventType = SubathonEventType.TwitchCheer, IsEnabled = true,
                SpinsToAdd = 1, CountThreshold = null
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        bool fired = false;
        WheelEvents.WheelSpinTriggerFired += (_, _, _) => fired = true;

        SubathonEvents.RaiseSubathonEventProcessed(new SubathonEvent
        {
            Id = Guid.NewGuid(), EventType = SubathonEventType.TwitchCheer,
            Command = SubathonCommandType.None, Value = "500", Currency = "bits"
        }, true);

        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.False(fired);

        await service.StopAsync(TestContext.Current.CancellationToken);
        await conn.CloseAsync();
    }

    [Fact]
    public async Task Token_WithCountThreshold_ComputesMultiplier()
    {
        var (service, options, conn) = await SetupServiceWithDb();
        await using (var db = new AppDbContext(options))
        {
            db.WheelSpinTriggers.Add(new WheelSpinTrigger
            {
                EventType = SubathonEventType.TwitchCheer, IsEnabled = true,
                SpinsToAdd = 2, CountThreshold = 100
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        int actual = 0;
        var tcs = new TaskCompletionSource<bool>();
        WheelEvents.WheelSpinTriggerFired += (_, h, _) => { actual = h.SpinsAdded; tcs.TrySetResult(true); };

        SubathonEvents.RaiseSubathonEventProcessed(new SubathonEvent
        {
            Id = Guid.NewGuid(), EventType = SubathonEventType.TwitchCheer,
            Command = SubathonCommandType.None, Value = "350", Currency = "bits"
        }, true); // 350/100=3 * 2 = 6

        await Task.WhenAny(tcs.Task, Task.Delay(2000, TestContext.Current.CancellationToken));
        Assert.True(tcs.Task.IsCompleted);
        Assert.Equal(6, actual);

        await service.StopAsync(TestContext.Current.CancellationToken);
        await conn.CloseAsync();
    }

    [Fact]
    public async Task Token_BelowCountThreshold_DoesNotFire()
    {
        var (service, options, conn) = await SetupServiceWithDb();
        await using (var db = new AppDbContext(options))
        {
            db.WheelSpinTriggers.Add(new WheelSpinTrigger
            {
                EventType = SubathonEventType.TwitchCheer, IsEnabled = true,
                SpinsToAdd = 1, CountThreshold = 100
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        bool fired = false;
        WheelEvents.WheelSpinTriggerFired += (_, _, _) => fired = true;

        SubathonEvents.RaiseSubathonEventProcessed(new SubathonEvent
        {
            Id = Guid.NewGuid(), EventType = SubathonEventType.TwitchCheer,
            Command = SubathonCommandType.None, Value = "50", Currency = "bits"
        }, true); // 50/100=0 -> no fire

        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.False(fired);

        await service.StopAsync(TestContext.Current.CancellationToken);
        await conn.CloseAsync();
    }

    [Fact]
    public async Task Donation_NoMoneyThreshold_DoesNotFire()
    {
        var (service, options, conn) = await SetupServiceWithDb();
        await using (var db = new AppDbContext(options))
        {
            db.WheelSpinTriggers.Add(new WheelSpinTrigger
            {
                EventType = SubathonEventType.KoFiDonation, IsEnabled = true,
                SpinsToAdd = 1, MoneyThreshold = null, Currency = "USD"
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        bool fired = false;
        WheelEvents.WheelSpinTriggerFired += (_, _, _) => fired = true;

        SubathonEvents.RaiseSubathonEventProcessed(new SubathonEvent
        {
            Id = Guid.NewGuid(), EventType = SubathonEventType.KoFiDonation,
            Command = SubathonCommandType.None, Currency = "USD", Value = "50"
        }, true);

        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.False(fired);

        await service.StopAsync(TestContext.Current.CancellationToken);
        await conn.CloseAsync();
    }

    [Fact]
    public async Task Donation_BelowMoneyThreshold_DoesNotFire()
    {
        var (service, options, conn) = await SetupServiceWithDb();
        await using (var db = new AppDbContext(options))
        {
            db.WheelSpinTriggers.Add(new WheelSpinTrigger
            {
                EventType = SubathonEventType.KoFiDonation, IsEnabled = true,
                SpinsToAdd = 1, MoneyThreshold = 50, Currency = "USD"
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        bool fired = false;
        WheelEvents.WheelSpinTriggerFired += (_, _, _) => fired = true;

        SubathonEvents.RaiseSubathonEventProcessed(new SubathonEvent
        {
            Id = Guid.NewGuid(), EventType = SubathonEventType.KoFiDonation,
            Command = SubathonCommandType.None, Currency = "USD", Value = "5"
        }, true); // 5/50=0 -> no fire

        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.False(fired);

        await service.StopAsync(TestContext.Current.CancellationToken);
        await conn.CloseAsync();
    }

    [Fact]
    public async Task Donation_WithMoneyThreshold_ComputesMultiplier()
    {
        var (service, options, conn) = await SetupServiceWithDb();
        await using (var db = new AppDbContext(options))
        {
            db.WheelSpinTriggers.Add(new WheelSpinTrigger
            {
                EventType = SubathonEventType.KoFiDonation, IsEnabled = true,
                SpinsToAdd = 1, MoneyThreshold = 10, Currency = "USD"
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        int actual = 0;
        var tcs = new TaskCompletionSource<bool>();
        WheelEvents.WheelSpinTriggerFired += (_, h, _) => { actual = h.SpinsAdded; tcs.TrySetResult(true); };

        SubathonEvents.RaiseSubathonEventProcessed(new SubathonEvent
        {
            Id = Guid.NewGuid(), EventType = SubathonEventType.KoFiDonation,
            Command = SubathonCommandType.None, Currency = "USD", Value = "25"
        }, true); // (int)(25/10)=2 * 1 = 2

        await Task.WhenAny(tcs.Task, Task.Delay(2000, TestContext.Current.CancellationToken));
        Assert.True(tcs.Task.IsCompleted);
        Assert.Equal(2, actual);

        await service.StopAsync(TestContext.Current.CancellationToken);
        await conn.CloseAsync();
    }

    [Fact]
    public async Task Order_NoThreshold_ReturnsFlatSpins()
    {
        var (service, options, conn) = await SetupServiceWithDb();
        await using (var db = new AppDbContext(options))
        {
            db.WheelSpinTriggers.Add(new WheelSpinTrigger
            {
                EventType = SubathonEventType.UwUMarketOrder, IsEnabled = true,
                SpinsToAdd = 4, CountThreshold = null, MoneyThreshold = null
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        int actual = 0;
        var tcs = new TaskCompletionSource<bool>();
        WheelEvents.WheelSpinTriggerFired += (_, h, _) => { actual = h.SpinsAdded; tcs.TrySetResult(true); };

        SubathonEvents.RaiseSubathonEventProcessed(new SubathonEvent
        {
            Id = Guid.NewGuid(), EventType = SubathonEventType.UwUMarketOrder,
            Command = SubathonCommandType.None, Amount = 99, Value = "100.00", Currency = "USD"
        }, true);

        await Task.WhenAny(tcs.Task, Task.Delay(2000, TestContext.Current.CancellationToken));
        Assert.True(tcs.Task.IsCompleted);
        Assert.Equal(4, actual);

        await service.StopAsync(TestContext.Current.CancellationToken);
        await conn.CloseAsync();
    }

    [Fact]
    public async Task Order_WithCountThreshold_ComputesItemMultiplier()
    {
        var (service, options, conn) = await SetupServiceWithDb();
        await using (var db = new AppDbContext(options))
        {
            db.WheelSpinTriggers.Add(new WheelSpinTrigger
            {
                EventType = SubathonEventType.UwUMarketOrder, IsEnabled = true,
                SpinsToAdd = 1, CountThreshold = 3
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        int actual = 0;
        var tcs = new TaskCompletionSource<bool>();
        WheelEvents.WheelSpinTriggerFired += (_, h, _) => { actual = h.SpinsAdded; tcs.TrySetResult(true); };

        SubathonEvents.RaiseSubathonEventProcessed(new SubathonEvent
        {
            Id = Guid.NewGuid(), EventType = SubathonEventType.UwUMarketOrder,
            Command = SubathonCommandType.None, Amount = 9
        }, true); // 9/3=3 * 1 = 3

        await Task.WhenAny(tcs.Task, Task.Delay(2000, TestContext.Current.CancellationToken));
        Assert.True(tcs.Task.IsCompleted);
        Assert.Equal(3, actual);

        await service.StopAsync(TestContext.Current.CancellationToken);
        await conn.CloseAsync();
    }

    [Fact]
    public async Task Order_WithMoneyThreshold_ComputesCurrencyMultiplier()
    {
        var (service, options, conn) = await SetupServiceWithDb();
        await using (var db = new AppDbContext(options))
        {
            db.WheelSpinTriggers.Add(new WheelSpinTrigger
            {
                EventType = SubathonEventType.UwUMarketOrder, IsEnabled = true,
                SpinsToAdd = 1, MoneyThreshold = 10, Currency = "USD"
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        int actual = 0;
        var tcs = new TaskCompletionSource<bool>();
        WheelEvents.WheelSpinTriggerFired += (_, h, _) => { actual = h.SpinsAdded; tcs.TrySetResult(true); };

        SubathonEvents.RaiseSubathonEventProcessed(new SubathonEvent
        {
            Id = Guid.NewGuid(), EventType = SubathonEventType.UwUMarketOrder,
            Command = SubathonCommandType.None, Value = "30.00", Currency = "USD"
        }, true); // (int)(30/10)=3 * 1 = 3

        await Task.WhenAny(tcs.Task, Task.Delay(2000, TestContext.Current.CancellationToken));
        Assert.True(tcs.Task.IsCompleted);
        Assert.Equal(3, actual);

        await service.StopAsync(TestContext.Current.CancellationToken);
        await conn.CloseAsync();
    }

    [Fact]
    public async Task Dispose_DoesNotThrow()
    {
        var (service, _, conn) = await SetupServiceWithDb();
        Exception? ex = Record.Exception(() => service.Dispose());
        Assert.Null(ex);
        await conn.CloseAsync();
    }

    [Fact]
    public async Task StopAsync_UnsubscribesFromEventBus()
    {
        var (service, options, conn) = await SetupServiceWithDb();
        await using (var db = new AppDbContext(options))
        {
            db.WheelSpinTriggers.Add(new WheelSpinTrigger
                { EventType = SubathonEventType.TwitchSub, IsEnabled = true, SpinsToAdd = 1 });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await service.StopAsync(TestContext.Current.CancellationToken);

        bool fired = false;
        WheelEvents.WheelSpinTriggerFired += (_, _, _) => fired = true;

        SubathonEvents.RaiseSubathonEventProcessed(new SubathonEvent
        {
            Id = Guid.NewGuid(), EventType = SubathonEventType.TwitchSub,
            Command = SubathonCommandType.None, Value = "1000", User = "TestUser"
        }, true);

        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.False(fired);

        await conn.CloseAsync();
    }
}
