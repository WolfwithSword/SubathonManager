using System.Reactive.Disposables;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Data;
using SubathonManager.Services;
// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.Tests.ServicesUnitTests;

[Collection("SequentialParallel")]
public class PromptOrchestratorServiceTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Mock<IDbContextFactory<AppDbContext>> _factoryMock;

    public PromptOrchestratorServiceTests()
    {
        var dbName = $"prompt_test_{Guid.NewGuid():N}";
        _connection = new SqliteConnection($"DataSource={dbName};Mode=Memory;Cache=Shared");
        _connection.Open();

        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var db = new AppDbContext(_options);
        db.Database.EnsureCreated();

        _factoryMock = new Mock<IDbContextFactory<AppDbContext>>();
        _factoryMock
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new AppDbContext(_options));

        ClearStaticEvents();
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
        ClearStaticEvents();
    }

    private static void ClearStaticEvents()
    {
        var fields = new[]
        {
            "SubathonDataUpdate", "SubathonEventProcessed", "PromptRunStarted",
            "PromptRunUpdate", "PromptRunProgressUpdated", "PromptRunCancelRequested",
            "PromptRunNowRequested", "PromptSetEnabledChanged"
        };
        foreach (var field in fields)
            typeof(SubathonEvents)
                .GetField(field, BindingFlags.Static | BindingFlags.NonPublic)
                ?.SetValue(null, null);
    }

    private AppDbContext CreateDb() => new(_options);

    private async Task<(SubathonData subathon, SubathonPromptSet set, SubathonPrompt prompt)>
        SeedBasicAsync(
            SubathonPromptType type = SubathonPromptType.Points,
            long promptValue = 100,
            int quantity = 5,
            bool infinite = false,
            bool promptEnabled = true,
            TimeSpan? duration = null,
            TimeSpan? interval = null,
            TimeSpan? cooldown = null,
            SubathonPromptSubType subType = SubathonPromptSubType.Default,
            SubathonEventType? filterEventType = null,
            string? filterMeta = null,
            int initialPoints = 0)
    {
        await using var db = CreateDb();

        var subathon = new SubathonData
        {
            Id = Guid.NewGuid(),
            IsActive = true,
            IsPaused = false,
            IsLocked = false,
            Points = initialPoints,
            Currency = "CAD"
        };

        var prompt = new SubathonPrompt
        {
            Id = Guid.NewGuid(),
            Text = "Test Prompt",
            Value = promptValue,
            Quantity = quantity,
            IsInfinite = infinite,
            Enabled = promptEnabled,
            Type = type,
            SubType = subType,
            FilterEventType = filterEventType,
            FilterMeta = filterMeta,
            CompletionDuration = duration ?? TimeSpan.FromMinutes(5),
        };

        var set = new SubathonPromptSet
        {
            Id = Guid.NewGuid(),
            Name = "Test Set",
            IsActive = true,
            Enabled = true,
            Interval = interval ?? TimeSpan.FromMinutes(20),
            Cooldown = cooldown ?? TimeSpan.Zero,
            RandomOffset = TimeSpan.Zero,
            Prompts = [prompt]
        };

        db.SubathonDatas.Add(subathon);
        db.SubathonPromptSets.Add(set);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return (subathon, set, prompt);
    }

    private PromptOrchestratorService CreateService(MockTimerService? timer = null)
    {
        timer ??= new MockTimerService();
        return new PromptOrchestratorService(
            _factoryMock.Object,
            timer,
            Mock.Of<ILogger<PromptOrchestratorService>>());
    }
    
    private static bool CallEventMatchesPrompt(SubathonEvent ev, SubathonPrompt prompt)
    {
        var method = typeof(PromptOrchestratorService)
            .GetMethod("EventMatchesPrompt", BindingFlags.Static | BindingFlags.NonPublic)!;
        return (bool)method.Invoke(null, [ev, prompt])!;
    }

    [Theory]
    [InlineData(SubathonPromptType.Points, true)]
    [InlineData(SubathonPromptType.Follows, false)]
    public void EventMatchesPrompt_Points_RequiresPositivePointsValue(SubathonPromptType type, bool expectMatch)
    {
        var prompt = new SubathonPrompt { Type = type };
        var ev = new SubathonEvent { EventType = SubathonEventType.TwitchSub, PointsValue = 1, Value="1000" };
        Assert.Equal(expectMatch, CallEventMatchesPrompt(ev, prompt));
    }

    [Fact]
    public void EventMatchesPrompt_Points_ZeroPoints_DoesNotMatch()
    {
        var prompt = new SubathonPrompt { Type = SubathonPromptType.Points };
        var ev = new SubathonEvent { EventType = SubathonEventType.PicartoFollow, PointsValue = 0};
        Assert.False(CallEventMatchesPrompt(ev, prompt));
    }

    [Fact]
    public void EventMatchesPrompt_Money_MatchesCurrencyDonation()
    {
        var prompt = new SubathonPrompt { Type = SubathonPromptType.Money };
        var ev = new SubathonEvent { EventType = SubathonEventType.StreamElementsDonation, Value = "10.00", Currency = "CAD"};
        Assert.True(CallEventMatchesPrompt(ev, prompt));
    }

    [Fact]
    public void EventMatchesPrompt_Money_DoesNotMatchNonDonation()
    {
        var prompt = new SubathonPrompt { Type = SubathonPromptType.Money };
        var ev = new SubathonEvent { EventType = SubathonEventType.TwitchFollow };
        Assert.False(CallEventMatchesPrompt(ev, prompt));
    }

    [Fact]
    public void EventMatchesPrompt_Follows_MatchesTwitchFollow()
    {
        var prompt = new SubathonPrompt { Type = SubathonPromptType.Follows };
        var ev = new SubathonEvent { EventType = SubathonEventType.TwitchFollow };
        Assert.True(CallEventMatchesPrompt(ev, prompt));
    }

    [Fact]
    public void EventMatchesPrompt_Subs_Default_MatchesBothSubTypes()
    {
        var prompt = new SubathonPrompt { Type = SubathonPromptType.Subs, SubType = SubathonPromptSubType.Default };
        var normalSub = new SubathonEvent { EventType = SubathonEventType.TwitchSub, Value="1000" };
        var giftSub   = new SubathonEvent { EventType = SubathonEventType.TwitchGiftSub, Value="2000" };
        Assert.True(CallEventMatchesPrompt(normalSub, prompt));
        Assert.True(CallEventMatchesPrompt(giftSub, prompt));
    }

    [Fact]
    public void EventMatchesPrompt_Subs_NormalSubs_ExcludesGifts()
    {
        var prompt = new SubathonPrompt { Type = SubathonPromptType.Subs, SubType = SubathonPromptSubType.NormalSubs };
        var giftSub = new SubathonEvent { EventType = SubathonEventType.TwitchGiftSub, Value = "1000"};
        Assert.False(CallEventMatchesPrompt(giftSub, prompt));
    }

    [Fact]
    public void EventMatchesPrompt_Subs_GiftSubs_ExcludesNormal()
    {
        var prompt = new SubathonPrompt { Type = SubathonPromptType.Subs, SubType = SubathonPromptSubType.GiftSubs };
        var normalSub = new SubathonEvent { EventType = SubathonEventType.TwitchSub, Value = "1000" };
        Assert.False(CallEventMatchesPrompt(normalSub, prompt));
    }

    [Fact]
    public void EventMatchesPrompt_Subs_GiftSubs_MatchesGift()
    {
        var prompt = new SubathonPrompt { Type = SubathonPromptType.Subs, SubType = SubathonPromptSubType.GiftSubs };
        var giftSub = new SubathonEvent { EventType = SubathonEventType.TwitchGiftSub, Value = "1000"};
        Assert.True(CallEventMatchesPrompt(giftSub, prompt));
    }

    [Fact]
    public void EventMatchesPrompt_Event_MatchesExactEventType()
    {
        var prompt = new SubathonPrompt
        {
            Type = SubathonPromptType.Event,
            FilterEventType = SubathonEventType.TwitchCheer,
            SubType = SubathonPromptSubType.Default
        };
        var ev      = new SubathonEvent { EventType = SubathonEventType.TwitchCheer, Amount=100 };
        var wrongEv = new SubathonEvent { EventType = SubathonEventType.TwitchSub, Value="1000" };
        Assert.True(CallEventMatchesPrompt(ev, prompt));
        Assert.False(CallEventMatchesPrompt(wrongEv, prompt));
    }

    [Fact]
    public void EventMatchesPrompt_Event_ByTier_MatchesCorrectMeta()
    {
        var prompt = new SubathonPrompt
        {
            Type = SubathonPromptType.Event,
            FilterEventType = SubathonEventType.TwitchSub,
            SubType = SubathonPromptSubType.ByTier,
            FilterMeta = "1000"
        };
        var matchEv  = new SubathonEvent { EventType = SubathonEventType.TwitchSub, Value = "1000" };
        var wrongEv  = new SubathonEvent { EventType = SubathonEventType.TwitchSub, Value = "2000" };
        Assert.True(CallEventMatchesPrompt(matchEv, prompt));
        Assert.False(CallEventMatchesPrompt(wrongEv, prompt));
    }

    [Fact]
    public void EventMatchesPrompt_NullEventType_ReturnsFalse()
    {
        var prompt = new SubathonPrompt { Type = SubathonPromptType.Points };
        var ev = new SubathonEvent { EventType = null, PointsValue = 100 };
        Assert.False(CallEventMatchesPrompt(ev, prompt));
    }

    private static long CallGetEventDelta(SubathonEvent ev, SubathonPrompt prompt)
    {
        var method = typeof(PromptOrchestratorService)
            .GetMethod("GetEventDelta", BindingFlags.Static | BindingFlags.NonPublic)!;
        return (long)method.Invoke(null, [ev, prompt])!;
    }

    [Fact]
    public void GetEventDelta_Points_ReturnsPointsValue()
    {
        var prompt = new SubathonPrompt { Type = SubathonPromptType.Points };
        var ev = new SubathonEvent { PointsValue = 250 };
        Assert.Equal(250, CallGetEventDelta(ev, prompt));
    }

    [Fact]
    public void GetEventDelta_Money_ReturnsZero()
    {
        var prompt = new SubathonPrompt { Type = SubathonPromptType.Money };
        var ev = new SubathonEvent { Value = "99.99", Currency = "CAD"};
        Assert.Equal(0, CallGetEventDelta(ev, prompt));
    }

    [Fact]
    public void GetEventDelta_Follows_ReturnsOne()
    {
        var prompt = new SubathonPrompt { Type = SubathonPromptType.Follows };
        var ev = new SubathonEvent();
        Assert.Equal(1, CallGetEventDelta(ev, prompt));
    }

    [Fact]
    public void GetEventDelta_Tokens_ParsesValue()
    {
        var prompt = new SubathonPrompt { Type = SubathonPromptType.Tokens };
        var ev = new SubathonEvent { Value = "500", Amount = 500, EventType=SubathonEventType.BlerpBits};
        Assert.Equal(500, CallGetEventDelta(ev, prompt));
    }

    [Fact]
    public void GetEventDelta_Tokens_InvalidValue_ReturnsZero()
    {
        var prompt = new SubathonPrompt { Type = SubathonPromptType.Tokens };
        var ev = new SubathonEvent { Value = "notanumber" };
        Assert.Equal(0, CallGetEventDelta(ev, prompt));
    }

    [Fact]
    public void GetEventDelta_Orders_Default_ReturnsOne()
    {
        var prompt = new SubathonPrompt { Type = SubathonPromptType.Orders, SubType = SubathonPromptSubType.Default };
        var ev = new SubathonEvent { Amount = 3 };
        Assert.Equal(1, CallGetEventDelta(ev, prompt));
    }

    [Fact]
    public void GetEventDelta_Orders_Items_ReturnsAmount()
    {
        var prompt = new SubathonPrompt { Type = SubathonPromptType.Orders, SubType = SubathonPromptSubType.Items };
        var ev = new SubathonEvent { Amount = 3 };
        Assert.Equal(3, CallGetEventDelta(ev, prompt));
    }

    [Fact]
    public void GetEventDelta_Subs_ReturnsAmount()
    {
        var prompt = new SubathonPrompt { Type = SubathonPromptType.Subs };
        var ev = new SubathonEvent { Amount = 5 };
        Assert.Equal(5, CallGetEventDelta(ev, prompt));
    }

    [Fact]
    public void GetEventDelta_Event_Default_ReturnsOne()
    {
        var prompt = new SubathonPrompt { Type = SubathonPromptType.Event, SubType = SubathonPromptSubType.Default };
        var ev = new SubathonEvent { Amount = 10 };
        Assert.Equal(1, CallGetEventDelta(ev, prompt));
    }

    [Fact]
    public void GetEventDelta_Event_Items_ReturnsAmount()
    {
        var prompt = new SubathonPrompt { Type = SubathonPromptType.Event, SubType = SubathonPromptSubType.Items };
        var ev = new SubathonEvent { Amount = 7 };
        Assert.Equal(7, CallGetEventDelta(ev, prompt));
    }
    
    private static TimeSpan CallBuildInterval(PromptOrchestratorService svc, SubathonPromptSet set)
    {
        var method = typeof(PromptOrchestratorService)
            .GetMethod("BuildInterval", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (TimeSpan)method.Invoke(svc, [set])!;
    }

    [Fact]
    public void BuildInterval_NoOffset_ReturnsExactInterval()
    {
        var svc = CreateService();
        var set = new SubathonPromptSet { Interval = TimeSpan.FromMinutes(20), RandomOffset = TimeSpan.Zero };
        var result = CallBuildInterval(svc, set);
        Assert.Equal(TimeSpan.FromMinutes(20), result);
    }

    [Fact]
    public void BuildInterval_WithOffset_StaysWithinBounds()
    {
        var svc = CreateService();
        var interval = TimeSpan.FromMinutes(20);
        var offset   = TimeSpan.FromMinutes(5);
        var set = new SubathonPromptSet { Interval = interval, RandomOffset = offset };

        // test randomness
        for (int i = 0; i < 200; i++)
        {
            var result = CallBuildInterval(svc, set);
            Assert.True(result >= TimeSpan.FromMinutes(15), $"Too low: {result}");
            Assert.True(result <= TimeSpan.FromMinutes(25), $"Too high: {result}");
        }
    }

    [Fact]
    public void BuildInterval_ResultNeverBelowOneSecond()
    {
        var svc = CreateService();
        var set = new SubathonPromptSet
        {
            Interval = TimeSpan.FromSeconds(1),
            RandomOffset = TimeSpan.FromMinutes(60)
        };
        for (int i = 0; i < 200; i++)
        {
            var result = CallBuildInterval(svc, set);
            Assert.True(result >= TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public async Task GetCurrentCountAsync_Points_ReturnsSubathonPoints()
    {
        await using var db = CreateDb();
        db.SubathonDatas.Add(new SubathonData { IsActive = true, Points = 42, Currency = "USD" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var prompt = new SubathonPrompt { Type = SubathonPromptType.Points };
        var result = await PromptOrchestratorService.GetCurrentCountAsync(db, prompt);
        Assert.Equal(42, result);
    }

    [Fact]
    public async Task GetCurrentCountAsync_NoActiveSubathon_ReturnsZero()
    {
        await using var db = CreateDb();
        var prompt = new SubathonPrompt { Type = SubathonPromptType.Points };
        var result = await PromptOrchestratorService.GetCurrentCountAsync(db, prompt);
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task GetCurrentCountAsync_Follows_CountsFollowEvents()
    {
        await using var db = CreateDb();
        db.SubathonDatas.Add(new SubathonData { IsActive = true, Points = 0, Currency = "USD" });
        db.SubathonEvents.Add(new SubathonEvent { EventType = SubathonEventType.TwitchFollow, ProcessedToSubathon = true, Amount = 1 });
        db.SubathonEvents.Add(new SubathonEvent { EventType = SubathonEventType.TwitchFollow, ProcessedToSubathon = true, Amount = 1 });
        db.SubathonEvents.Add(new SubathonEvent { EventType = SubathonEventType.TwitchSub, ProcessedToSubathon = true, Amount = 1 }); // should not count
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var prompt = new SubathonPrompt { Type = SubathonPromptType.Follows };
        var result = await PromptOrchestratorService.GetCurrentCountAsync(db, prompt);
        Assert.Equal(2, result);
    }

    [Fact]
    public async Task GetCurrentCountAsync_Tokens_SumsValues()
    {
        await using var db = CreateDb();
        db.SubathonDatas.Add(new SubathonData { IsActive = true, Points = 0, Currency = "USD" });
        db.SubathonEvents.Add(new SubathonEvent { EventType = SubathonEventType.TwitchCheer, ProcessedToSubathon = true, Value = "100", Amount = 100 });
        db.SubathonEvents.Add(new SubathonEvent { EventType = SubathonEventType.TwitchCheer, ProcessedToSubathon = true, Value = "250", Amount = 250 });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var prompt = new SubathonPrompt { Type = SubathonPromptType.Tokens };
        var result = await PromptOrchestratorService.GetCurrentCountAsync(db, prompt);
        Assert.Equal(350, result);
    }

    [Fact]
    public async Task GetCurrentCountAsync_Subs_SumsAmount()
    {
        await using var db = CreateDb();
        db.SubathonDatas.Add(new SubathonData { IsActive = true, Points = 0, Currency = "USD" });
        db.SubathonEvents.Add(new SubathonEvent { EventType = SubathonEventType.TwitchSub, ProcessedToSubathon = true, Amount = 1 });
        db.SubathonEvents.Add(new SubathonEvent { EventType = SubathonEventType.TwitchGiftSub, ProcessedToSubathon = true, Amount = 5 });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var prompt = new SubathonPrompt { Type = SubathonPromptType.Subs, SubType = SubathonPromptSubType.Default };
        var result = await PromptOrchestratorService.GetCurrentCountAsync(db, prompt);
        Assert.Equal(6, result);
    }

    [Fact]
    public async Task GetCurrentCountAsync_Subs_GiftOnly_ExcludesNormal()
    {
        await using var db = CreateDb();
        db.SubathonDatas.Add(new SubathonData { IsActive = true, Points = 0, Currency = "USD" });
        db.SubathonEvents.Add(new SubathonEvent { EventType = SubathonEventType.TwitchSub, ProcessedToSubathon = true, Amount = 1 });
        db.SubathonEvents.Add(new SubathonEvent { EventType = SubathonEventType.TwitchGiftSub, ProcessedToSubathon = true, Amount = 3 });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var prompt = new SubathonPrompt { Type = SubathonPromptType.Subs, SubType = SubathonPromptSubType.GiftSubs };
        var result = await PromptOrchestratorService.GetCurrentCountAsync(db, prompt);
        Assert.Equal(3, result);
    }

    [Fact]
    public async Task GetCurrentCountAsync_Orders_Default_CountsOrders()
    {
        await using var db = CreateDb();
        db.SubathonDatas.Add(new SubathonData { IsActive = true, Points = 0, Currency = "USD" });
        db.SubathonEvents.Add(new SubathonEvent { EventType = SubathonEventType.UwUMarketOrder, ProcessedToSubathon = true, Amount = 1 });
        db.SubathonEvents.Add(new SubathonEvent { EventType = SubathonEventType.UwUMarketOrder, ProcessedToSubathon = true, Amount = 3 });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var prompt = new SubathonPrompt { Type = SubathonPromptType.Orders, SubType = SubathonPromptSubType.Default };
        var result = await PromptOrchestratorService.GetCurrentCountAsync(db, prompt);
        Assert.Equal(2, result); // count of orders, not items
    }

    [Fact]
    public async Task GetCurrentCountAsync_Orders_Items_SumsAmounts()
    {
        await using var db = CreateDb();
        db.SubathonDatas.Add(new SubathonData { IsActive = true, Points = 0, Currency = "USD" });
        db.SubathonEvents.Add(new SubathonEvent { EventType = SubathonEventType.UwUMarketOrder, ProcessedToSubathon = true, Amount = 2 });
        db.SubathonEvents.Add(new SubathonEvent { EventType = SubathonEventType.UwUMarketOrder, ProcessedToSubathon = true, Amount = 4 });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var prompt = new SubathonPrompt { Type = SubathonPromptType.Orders, SubType = SubathonPromptSubType.Items };
        var result = await PromptOrchestratorService.GetCurrentCountAsync(db, prompt);
        Assert.Equal(6, result);
    }

    [Fact]
    public async Task OnSubathonDataUpdate_ActiveUnpausedUnlocked_StartsScheduler()
    {
        var (_, set, _) = await SeedBasicAsync();
        var timer = new MockTimerService();
        var svc = CreateService(timer);
        await svc.StartAsync(TestContext.Current.CancellationToken);

        SubathonEvents.RaiseSubathonDataUpdate(
            new SubathonData { IsActive = true, IsPaused = false, IsLocked = false },
            DateTime.Now);

        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.True(timer.IsRegistered("prompt-interval"));
    }

    [Fact]
    public async Task OnSubathonDataUpdate_Paused_UnregistersInterval()
    {
        var (_, set, _) = await SeedBasicAsync();
        var timer = new MockTimerService();
        var svc = CreateService(timer);
        await svc.StartAsync(TestContext.Current.CancellationToken);

        SubathonEvents.RaiseSubathonDataUpdate(
            new SubathonData { IsActive = true, IsPaused = false, IsLocked = false }, DateTime.Now);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.True(timer.IsRegistered("prompt-interval"));

        SubathonEvents.RaiseSubathonDataUpdate(
            new SubathonData { IsActive = true, IsPaused = true, IsLocked = false }, DateTime.Now);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(timer.IsRegistered("prompt-interval"));
    }

    [Fact]
    public async Task OnSubathonDataUpdate_Locked_UnregistersInterval()
    {
        var (_, set, _) = await SeedBasicAsync();
        var timer = new MockTimerService();
        var svc = CreateService(timer);
        await svc.StartAsync(TestContext.Current.CancellationToken);

        SubathonEvents.RaiseSubathonDataUpdate(
            new SubathonData { IsActive = true, IsPaused = false, IsLocked = false }, DateTime.Now);
        await Task.Delay(200, TestContext.Current.CancellationToken);

        SubathonEvents.RaiseSubathonDataUpdate(
            new SubathonData { IsActive = true, IsPaused = false, IsLocked = true }, DateTime.Now);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(timer.IsRegistered("prompt-interval"));
    }

    [Fact]
    public async Task OnSubathonDataUpdate_DisabledSet_DoesNotRegisterInterval()
    {
        await using var db = CreateDb();
        db.SubathonDatas.Add(new SubathonData { IsActive = true, IsPaused = false, IsLocked = false, Points = 0, Currency = "USD" });
        var disabledSet = new SubathonPromptSet
        {
            IsActive = true, Enabled = false,
            Interval = TimeSpan.FromMinutes(5), RandomOffset = TimeSpan.Zero
        };
        db.SubathonPromptSets.Add(disabledSet);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var timer = new MockTimerService();
        var svc = CreateService(timer);
        await svc.StartAsync(TestContext.Current.CancellationToken);

        SubathonEvents.RaiseSubathonDataUpdate(
            new SubathonData { IsActive = true, IsPaused = false, IsLocked = false }, DateTime.Now);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.False(timer.IsRegistered("prompt-interval"));
    }

    [Fact]
    public async Task OnCancelRequested_ActiveRun_SetsStatusCancelled()
    {
        var (_, set, prompt) = await SeedBasicAsync();
        var run = new SubathonPromptRun
        {
            PromptId = prompt.Id, SetId = set.Id,
            Status = SubathonPromptRunStatus.Active,
            StartedAt = DateTime.Now,
            ExpiresAt = DateTime.Now.AddMinutes(5),
            SnapshotTargetValue = 100
        };
        await using (var db = CreateDb()) { db.SubathonPromptRuns.Add(run); await db.SaveChangesAsync(TestContext.Current.CancellationToken); }

        var timer = new MockTimerService();
        var svc = CreateService(timer);
        await svc.StartAsync(TestContext.Current.CancellationToken);

        SubathonEvents.RaisePromptRunCancelRequested();
        await Task.Delay(300, TestContext.Current.CancellationToken);

        await using var verifyDb = CreateDb();
        var updated = await verifyDb.SubathonPromptRuns.FindAsync([run.Id], TestContext.Current.CancellationToken);
        Assert.Equal(SubathonPromptRunStatus.Cancelled, updated!.Status);
        Assert.NotNull(updated.EndedAt);
    }

    [Fact]
    public async Task OnCancelRequested_NoActiveRun_DoesNotThrow()
    {
        await SeedBasicAsync();
        var svc = CreateService();
        await svc.StartAsync(TestContext.Current.CancellationToken);

        var ex = await Record.ExceptionAsync(async () =>
        {
            SubathonEvents.RaisePromptRunCancelRequested();
            await Task.Delay(200, TestContext.Current.CancellationToken);
        });
        Assert.Null(ex);
    }

    [Fact]
    public async Task OnCancelRequested_UnregistersDurationTimer()
    {
        var (_, set, prompt) = await SeedBasicAsync();
        var run = new SubathonPromptRun
        {
            PromptId = prompt.Id, SetId = set.Id,
            Status = SubathonPromptRunStatus.Active,
            StartedAt = DateTime.Now, ExpiresAt = DateTime.Now.AddMinutes(5),
            SnapshotTargetValue = 100
        };
        await using (var db = CreateDb()) { db.SubathonPromptRuns.Add(run); await db.SaveChangesAsync(TestContext.Current.CancellationToken); }

        var timer = new MockTimerService();
        timer.Register("prompt-duration", TimeSpan.FromMinutes(5), _ => Task.CompletedTask);
        var svc = CreateService(timer);
        await svc.StartAsync(TestContext.Current.CancellationToken);

        SubathonEvents.RaisePromptRunCancelRequested();
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.False(timer.IsRegistered("prompt-duration"));
    }
    
    [Fact]
    public async Task OnRunNowRequested_CreatesNewActiveRun()
    {
        var (_, set, prompt) = await SeedBasicAsync();
        await using (var db = CreateDb())
        {
            db.SubathonDatas.Add(new SubathonData { IsActive = true, Points = 0, Currency = "USD" });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var timer = new MockTimerService();
        var svc = CreateService(timer);
        await svc.StartAsync(TestContext.Current.CancellationToken);

        SubathonEvents.RaisePromptRunNowRequested(prompt.Id);
        await Task.Delay(400, TestContext.Current.CancellationToken);

        await using var verifyDb = CreateDb();
        var run = await verifyDb.SubathonPromptRuns
            .FirstOrDefaultAsync(r => r.PromptId == prompt.Id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(run);
        Assert.Equal(SubathonPromptRunStatus.Active, run.Status);
    }

    [Fact]
    public async Task OnRunNowRequested_CancelsExistingActiveRun()
    {
        var (_, set, prompt) = await SeedBasicAsync();
        await using (var db = CreateDb())
        {
            db.SubathonDatas.Add(new SubathonData { IsActive = true, Points = 0, Currency = "USD" });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var existingRun = new SubathonPromptRun
        {
            PromptId = prompt.Id, SetId = set.Id,
            Status = SubathonPromptRunStatus.Active,
            StartedAt = DateTime.Now, ExpiresAt = DateTime.Now.AddMinutes(5),
            SnapshotTargetValue = 50
        };
        await using (var db = CreateDb()) { db.SubathonPromptRuns.Add(existingRun); await db.SaveChangesAsync(TestContext.Current.CancellationToken); }

        var timer = new MockTimerService();
        var svc = CreateService(timer);
        await svc.StartAsync(TestContext.Current.CancellationToken);

        SubathonEvents.RaisePromptRunNowRequested(prompt.Id);
        await Task.Delay(400, TestContext.Current.CancellationToken);

        await using var verifyDb = CreateDb();
        var old = await verifyDb.SubathonPromptRuns.FindAsync([existingRun.Id], TestContext.Current.CancellationToken);
        Assert.Equal(SubathonPromptRunStatus.Cancelled, old!.Status);
    }

    [Fact]
    public async Task OnRunNowRequested_InvalidPromptId_DoesNotCreateRun()
    {
        await SeedBasicAsync();
        var svc = CreateService();
        await svc.StartAsync(TestContext.Current.CancellationToken);

        var bogusId = Guid.NewGuid();
        SubathonEvents.RaisePromptRunNowRequested(bogusId);
        await Task.Delay(300, TestContext.Current.CancellationToken);

        await using var verifyDb = CreateDb();
        Assert.Empty(verifyDb.SubathonPromptRuns);
    }

    [Fact]
    public async Task OnPromptSetEnabledChanged_Disabled_CancelsActiveRun()
    {
        var (_, set, prompt) = await SeedBasicAsync();
        var run = new SubathonPromptRun
        {
            PromptId = prompt.Id, SetId = set.Id,
            Status = SubathonPromptRunStatus.Active,
            StartedAt = DateTime.Now, ExpiresAt = DateTime.Now.AddMinutes(5),
            SnapshotTargetValue = 100
        };
        await using (var db = CreateDb()) { db.SubathonPromptRuns.Add(run); await db.SaveChangesAsync(TestContext.Current.CancellationToken); }

        var svc = CreateService();
        await svc.StartAsync(TestContext.Current.CancellationToken);

        SubathonEvents.RaisePromptSetEnabledChanged(false);
        await Task.Delay(300, TestContext.Current.CancellationToken);

        await using var verifyDb = CreateDb();
        var updated = await verifyDb.SubathonPromptRuns.FindAsync([run.Id], TestContext.Current.CancellationToken);
        Assert.Equal(SubathonPromptRunStatus.Cancelled, updated!.Status);
    }

    [Fact]
    public async Task OnPromptSetEnabledChanged_Disabled_UnregistersAllTimers()
    {
        await SeedBasicAsync();
        var timer = new MockTimerService();
        timer.Register("prompt-interval", TimeSpan.FromMinutes(20), _ => Task.CompletedTask);
        timer.Register("prompt-duration", TimeSpan.FromMinutes(5),  _ => Task.CompletedTask);
        timer.Register("prompt-cooldown", TimeSpan.FromMinutes(2),  _ => Task.CompletedTask);

        var svc = CreateService(timer);
        await svc.StartAsync(TestContext.Current.CancellationToken);

        SubathonEvents.RaisePromptSetEnabledChanged(false);
        await Task.Delay(200, TestContext.Current.CancellationToken);

        Assert.False(timer.IsRegistered("prompt-interval"));
        Assert.False(timer.IsRegistered("prompt-duration"));
        Assert.False(timer.IsRegistered("prompt-cooldown"));
    }

    [Fact]
    public async Task SubathonEventProcessed_NotProcessed_IsIgnored()
    {
        var (_, set, prompt) = await SeedBasicAsync(type: SubathonPromptType.Points, promptValue: 100);
        await using (var db = CreateDb())
        {
            db.SubathonDatas.Add(new SubathonData { IsActive = true, Points = 50, Currency = "USD" });
            db.SubathonPromptRuns.Add(new SubathonPromptRun
            {
                PromptId = prompt.Id, SetId = set.Id, Status = SubathonPromptRunStatus.Active,
                StartedAt = DateTime.Now, ExpiresAt = DateTime.Now.AddMinutes(5),
                SnapshotTargetValue = 100, BaselineCount = 0
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var svc = CreateService();
        await svc.StartAsync(TestContext.Current.CancellationToken);

        SubathonEvents.RaiseSubathonEventProcessed(
            new SubathonEvent { EventType = SubathonEventType.TwitchSub, PointsValue = 50 },
            wasEffective: false);
        await Task.Delay(300, TestContext.Current.CancellationToken);

        await using var verifyDb = CreateDb();
        var run = await verifyDb.SubathonPromptRuns.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(SubathonPromptRunStatus.Active, run.Status); // unchanged
    }

    [Fact]
    public async Task SubathonEventProcessed_ProgressBelowTarget_RaisesProgressEvent()
    {
        var (_, set, prompt) = await SeedBasicAsync(type: SubathonPromptType.Points, promptValue: 100, initialPoints: 50);
        var runId = Guid.NewGuid();
        await using (var db = CreateDb())
        {
            db.SubathonPromptRuns.Add(new SubathonPromptRun
            {
                Id = runId, PromptId = prompt.Id, SetId = set.Id,
                Status = SubathonPromptRunStatus.Active,
                StartedAt = DateTime.Now, ExpiresAt = DateTime.Now.AddMinutes(5),
                SnapshotTargetValue = 100, BaselineCount = 0
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        long? capturedProgress = null;
        SubathonEvents.PromptRunProgressUpdated += (run, p) => capturedProgress = p;

        var svc = CreateService();
        await svc.StartAsync(TestContext.Current.CancellationToken);

        SubathonEvents.RaiseSubathonEventProcessed(
            new SubathonEvent { EventType = SubathonEventType.TwitchSub, PointsValue = 50 },
            wasEffective: true);
        await Task.Delay(400, TestContext.Current.CancellationToken);

        Assert.NotNull(capturedProgress);
        Assert.Equal(50, capturedProgress);
    }

    [Fact]
    public async Task SubathonEventProcessed_ProgressMeetsTarget_CompletesRun()
    {
        var (_, set, prompt) = await SeedBasicAsync(type: SubathonPromptType.Points, promptValue: 100, initialPoints: 100);
        var runId = Guid.NewGuid();
        await using (var db = CreateDb())
        {
            db.SubathonPromptRuns.Add(new SubathonPromptRun
            {
                Id = runId, PromptId = prompt.Id, SetId = set.Id,
                Status = SubathonPromptRunStatus.Active,
                StartedAt = DateTime.Now, ExpiresAt = DateTime.Now.AddMinutes(5),
                SnapshotTargetValue = 100, BaselineCount = 0
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        SubathonPromptRun? completedRun = null;
        SubathonEvents.PromptRunUpdate += (run, _) => completedRun = run;

        var svc = CreateService();
        await svc.StartAsync(TestContext.Current.CancellationToken);

        SubathonEvents.RaiseSubathonEventProcessed(
            new SubathonEvent { EventType = SubathonEventType.TwitchSub, PointsValue = 100 },
            wasEffective: true);
        await Task.Delay(400, TestContext.Current.CancellationToken);

        Assert.NotNull(completedRun);
        Assert.Equal(SubathonPromptRunStatus.Completed, completedRun!.Status);

        await using var verifyDb = CreateDb();
        var saved = await verifyDb.SubathonPromptRuns.FindAsync([runId], TestContext.Current.CancellationToken);
        Assert.Equal(SubathonPromptRunStatus.Completed, saved!.Status);
        Assert.NotNull(saved.EndedAt);
    }

    [Fact]
    public async Task SubathonEventProcessed_Completion_DecrementsQuantity()
    {
        var (_, set, prompt) = await SeedBasicAsync(
            type: SubathonPromptType.Points, promptValue: 10, quantity: 3, infinite: false, initialPoints: 10);
        await using (var db = CreateDb())
        {
            db.SubathonPromptRuns.Add(new SubathonPromptRun
            {
                PromptId = prompt.Id, SetId = set.Id, Status = SubathonPromptRunStatus.Active,
                StartedAt = DateTime.Now, ExpiresAt = DateTime.Now.AddMinutes(5),
                SnapshotTargetValue = 10, BaselineCount = 0
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var svc = CreateService();
        await svc.StartAsync(TestContext.Current.CancellationToken);

        SubathonEvents.RaiseSubathonEventProcessed(
            new SubathonEvent { EventType = SubathonEventType.TwitchSub, PointsValue = 10 },
            wasEffective: true);
        await Task.Delay(500, TestContext.Current.CancellationToken);

        await using var verifyDb = CreateDb();
        var updatedPrompt = await verifyDb.SubathonPrompts.FindAsync([prompt.Id], TestContext.Current.CancellationToken);
        Assert.Equal(2, updatedPrompt!.Quantity);
    }

    [Fact]
    public async Task SubathonEventProcessed_Completion_InfinitePrompt_QuantityUnchanged()
    {
        var (_, set, prompt) = await SeedBasicAsync(
            type: SubathonPromptType.Points, promptValue: 10, quantity: 5, infinite: true);
        await using (var db = CreateDb())
        {
            db.SubathonDatas.Add(new SubathonData { IsActive = true, Points = 10, Currency = "USD" });
            db.SubathonPromptRuns.Add(new SubathonPromptRun
            {
                PromptId = prompt.Id, SetId = set.Id, Status = SubathonPromptRunStatus.Active,
                StartedAt = DateTime.Now, ExpiresAt = DateTime.Now.AddMinutes(5),
                SnapshotTargetValue = 10, BaselineCount = 0
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var svc = CreateService();
        await svc.StartAsync(TestContext.Current.CancellationToken);

        SubathonEvents.RaiseSubathonEventProcessed(
            new SubathonEvent { EventType = SubathonEventType.TwitchSub, PointsValue = 10 },
            wasEffective: true);
        await Task.Delay(500, TestContext.Current.CancellationToken);

        await using var verifyDb = CreateDb();
        var updatedPrompt =
            await verifyDb.SubathonPrompts.FindAsync([prompt.Id],
                TestContext.Current.CancellationToken);
        Assert.Equal(5, updatedPrompt!.Quantity);
    }

    [Fact]
    public async Task SubathonEventProcessed_NoActiveRun_IsIgnored()
    {
        await SeedBasicAsync();
        bool progressFired = false;
        SubathonEvents.PromptRunProgressUpdated += (_, _) => progressFired = true;

        var svc = CreateService();
        await svc.StartAsync(TestContext.Current.CancellationToken);

        SubathonEvents.RaiseSubathonEventProcessed(
            new SubathonEvent { EventType = SubathonEventType.TwitchSub, PointsValue = 100 },
            wasEffective: true);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.False(progressFired);
    }

    [Fact]
    public async Task SubathonEventProcessed_EventDoesNotMatchPromptType_IsIgnored()
    {
        var (_, set, prompt) = await SeedBasicAsync(type: SubathonPromptType.Follows, promptValue: 5);
        await using (var db = CreateDb())
        {
            db.SubathonDatas.Add(new SubathonData { IsActive = true, Points = 0, Currency = "USD" });
            db.SubathonPromptRuns.Add(new SubathonPromptRun
            {
                PromptId = prompt.Id, SetId = set.Id, Status = SubathonPromptRunStatus.Active,
                StartedAt = DateTime.Now, ExpiresAt = DateTime.Now.AddMinutes(5),
                SnapshotTargetValue = 5, BaselineCount = 0
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        bool progressFired = false;
        SubathonEvents.PromptRunProgressUpdated += (_, _) => progressFired = true;

        var svc = CreateService();
        await svc.StartAsync(TestContext.Current.CancellationToken);

        SubathonEvents.RaiseSubathonEventProcessed(
            new SubathonEvent { EventType = SubathonEventType.TwitchSub, PointsValue = 999 },
            wasEffective: true);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.False(progressFired);
    }

    [Fact]
    public async Task SubathonEventProcessed_ExpiredRun_IsIgnored()
    {
        var (_, set, prompt) = await SeedBasicAsync(type: SubathonPromptType.Points, promptValue: 100);
        await using (var db = CreateDb())
        {
            db.SubathonDatas.Add(new SubathonData { IsActive = true, Points = 50, Currency = "USD" });
            db.SubathonPromptRuns.Add(new SubathonPromptRun
            {
                PromptId = prompt.Id, SetId = set.Id, Status = SubathonPromptRunStatus.Active,
                StartedAt = DateTime.Now.AddMinutes(-10),
                ExpiresAt = DateTime.Now.AddMinutes(-1),
                SnapshotTargetValue = 100, BaselineCount = 0
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        bool updateFired = false;
        SubathonEvents.PromptRunUpdate += (_, _) => updateFired = true;

        var svc = CreateService();
        await svc.StartAsync(TestContext.Current.CancellationToken);

        SubathonEvents.RaiseSubathonEventProcessed(
            new SubathonEvent { EventType = SubathonEventType.TwitchSub, PointsValue = 50 },
            wasEffective: true);
        await Task.Delay(300, TestContext.Current.CancellationToken);

        await using var verifyDb = CreateDb();
        var saved = await verifyDb.SubathonPromptRuns.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(SubathonPromptRunStatus.Active, saved.Status);
        Assert.False(updateFired);
    }

    [Fact]
    public async Task TryStartScheduler_ActiveExpiredRun_ExpiresAndStartsIntervalOrCooldown()
    {
        var (_, set, prompt) = await SeedBasicAsync();
        await using (var db = CreateDb())
        {
            db.SubathonDatas.Add(new SubathonData { IsActive = true, Points = 0, Currency = "USD" });
            db.SubathonPromptRuns.Add(new SubathonPromptRun
            {
                PromptId = prompt.Id, SetId = set.Id, Status = SubathonPromptRunStatus.Active,
                StartedAt = DateTime.Now.AddMinutes(-30),
                ExpiresAt = DateTime.Now.AddMinutes(-10),
                SnapshotTargetValue = 100, BaselineCount = 0
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var timer = new MockTimerService();
        var svc = CreateService(timer);
        await svc.StartAsync(TestContext.Current.CancellationToken);

        SubathonEvents.RaiseSubathonDataUpdate(
            new SubathonData { IsActive = true, IsPaused = false, IsLocked = false }, DateTime.Now);
        await Task.Delay(400, TestContext.Current.CancellationToken);

        await using var verifyDb = CreateDb();
        var run = await verifyDb.SubathonPromptRuns.FirstAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(SubathonPromptRunStatus.Expired, run.Status);
        Assert.True(timer.IsRegistered("prompt-interval") || timer.IsRegistered("prompt-cooldown"));
    }

    [Fact]
    public async Task TryStartScheduler_ActiveNonExpiredRun_ResumesDurationTimer()
    {
        var (_, set, prompt) = await SeedBasicAsync(duration: TimeSpan.FromMinutes(10));
        await using (var db = CreateDb())
        {
            db.SubathonDatas.Add(new SubathonData { IsActive = true, Points = 0, Currency = "USD" });
            db.SubathonPromptRuns.Add(new SubathonPromptRun
            {
                PromptId = prompt.Id, SetId = set.Id, Status = SubathonPromptRunStatus.Active,
                StartedAt = DateTime.Now.AddMinutes(-2),
                ExpiresAt = DateTime.Now.AddMinutes(8),
                SnapshotTargetValue = 100, BaselineCount = 0
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var timer = new MockTimerService();
        var svc = CreateService(timer);
        await svc.StartAsync(TestContext.Current.CancellationToken);

        SubathonEvents.RaiseSubathonDataUpdate(
            new SubathonData { IsActive = true, IsPaused = false, IsLocked = false }, DateTime.Now);
        await Task.Delay(400, TestContext.Current.CancellationToken);

        Assert.True(timer.IsRegistered("prompt-duration"));
        Assert.False(timer.IsRegistered("prompt-interval"));
    }

    [Fact]
    public async Task TryStartScheduler_NoActiveRun_RegistersInterval()
    {
        var (_, set, _) = await SeedBasicAsync();
        await using (var db = CreateDb())
        {
            db.SubathonDatas.Add(new SubathonData { IsActive = true, Points = 0, Currency = "USD" });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var timer = new MockTimerService();
        var svc = CreateService(timer);
        await svc.StartAsync(TestContext.Current.CancellationToken);

        SubathonEvents.RaiseSubathonDataUpdate(
            new SubathonData { IsActive = true, IsPaused = false, IsLocked = false }, DateTime.Now);
        await Task.Delay(400, TestContext.Current.CancellationToken);

        Assert.True(timer.IsRegistered("prompt-interval"));
    }

    [Fact]
    public async Task Cooldown_WhenConfigured_RegistersCooldownNotInterval()
    {
        var (_, set, prompt) = await SeedBasicAsync(cooldown: TimeSpan.FromMinutes(2));

        var run = new SubathonPromptRun
        {
            PromptId = prompt.Id, SetId = set.Id, Status = SubathonPromptRunStatus.Active,
            StartedAt = DateTime.Now, ExpiresAt = DateTime.Now.AddMinutes(5),
            SnapshotTargetValue = 100
        };
        await using (var db = CreateDb())
        {
            db.SubathonDatas.Add(new SubathonData { IsActive = true, Points = 0, Currency = "USD" });
            db.SubathonPromptRuns.Add(run);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var timer = new MockTimerService();
        var svc = CreateService(timer);
        await svc.StartAsync(TestContext.Current.CancellationToken);

        var runningField = typeof(PromptOrchestratorService)
            .GetField("_subathonRunning", BindingFlags.Instance | BindingFlags.NonPublic)!;
        runningField.SetValue(svc, true);

        SubathonEvents.RaisePromptRunCancelRequested();
        await Task.Delay(400, TestContext.Current.CancellationToken);

        Assert.True(timer.IsRegistered("prompt-cooldown"));
        Assert.False(timer.IsRegistered("prompt-interval"));
    }

    [Fact]
    public async Task Dispose_UnregistersAllTimers()
    {
        await SeedBasicAsync();
        var timer = new MockTimerService();
        timer.Register("prompt-interval", TimeSpan.FromMinutes(20), _ => Task.CompletedTask);
        timer.Register("prompt-duration", TimeSpan.FromMinutes(5),  _ => Task.CompletedTask);

        var svc = CreateService(timer);
        await svc.StartAsync(TestContext.Current.CancellationToken);

        svc.Dispose();

        Assert.False(timer.IsRegistered("prompt-interval"));
        Assert.False(timer.IsRegistered("prompt-duration"));
    }

    [Fact]
    public async Task Dispose_AfterDispose_EventsNoLongerTriggerHandler()
    {
        await SeedBasicAsync();
        var timer = new MockTimerService();
        var svc = CreateService(timer);
        await svc.StartAsync(TestContext.Current.CancellationToken);
        svc.Dispose();

        var ex = await Record.ExceptionAsync(async () =>
        {
            SubathonEvents.RaiseSubathonDataUpdate(
                new SubathonData { IsActive = true, IsPaused = false, IsLocked = false }, DateTime.Now);
            SubathonEvents.RaisePromptRunCancelRequested();
            await Task.Delay(200, TestContext.Current.CancellationToken);
        });

        Assert.Null(ex);
        Assert.False(timer.IsRegistered("prompt-interval"));
    }

    [Fact]
    public async Task IntervalElapsed_NoPickablePrompts_ReschedulesInterval()
    {
        var (_, set, _) = await SeedBasicAsync(promptEnabled: false, quantity: 0);
        await using (var db = CreateDb())
        {
            db.SubathonDatas.Add(new SubathonData { IsActive = true, Points = 0, Currency = "USD" });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var timer = new MockTimerService();
        var svc = CreateService(timer);
        await svc.StartAsync(TestContext.Current.CancellationToken);

        var runningField = typeof(PromptOrchestratorService)
            .GetField("_subathonRunning", BindingFlags.Instance | BindingFlags.NonPublic)!;
        runningField.SetValue(svc, true);

        var method = typeof(PromptOrchestratorService)
            .GetMethod("OnIntervalElapsedAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)method.Invoke(svc, null)!;

        Assert.True(timer.IsRegistered("prompt-interval"));
        await using var verifyDb = CreateDb();
        Assert.Empty(verifyDb.SubathonPromptRuns);
    }
}

public class MockTimerService : ITimerService
{
    private readonly Dictionary<string, (TimeSpan Delay, Func<CancellationToken, Task> Callback)> _registered = new();
    private readonly Lock _lock = new();

    public IDisposable Register(string key, TimeSpan delay, Func<CancellationToken, Task> callback)
    {
        lock (_lock) _registered[key] = (delay, callback);
        return new BooleanDisposable();
    }

    public IDisposable Register(string key, TimeSpan delay, Action callback)
        => Register(key, delay, _ => { callback(); return Task.CompletedTask; });

    public void Unregister(string key)
    {
        lock (_lock) _registered.Remove(key);
    }

    public bool IsRegistered(string key)
    {
        lock (_lock) return _registered.ContainsKey(key);
    }

    public async Task FireAsync(string key, CancellationToken ct = default)
    {
        // unused for now
        Func<CancellationToken, Task>? cb;
        lock (_lock)
        {
            if (!_registered.TryGetValue(key, out var entry)) return;
            cb = entry.Callback;
        }
        await cb(ct);
    }
}