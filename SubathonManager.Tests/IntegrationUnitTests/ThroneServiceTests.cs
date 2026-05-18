using System.Reflection;
using DevTunnels.Client;
using DevTunnels.Client.Tunnels;
using Microsoft.Extensions.Logging;
using Moq;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;
using SubathonManager.Integration;
using SubathonManager.Tests.Utility;

// ReSharper disable NullableWarningSuppressionIsUsed
namespace SubathonManager.Tests.IntegrationUnitTests;

[Collection("SharedEventBusTests")]
public class ThroneServiceTests
{
    public ThroneServiceTests()
    {
        typeof(IntegrationEvents)
            .GetField("ConnectionUpdated", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
        typeof(SubathonEvents)
            .GetField("SubathonEventCreated", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
    }
    
    private static (ThroneService service, DevTunnelsService devTunnels) MakeService(
        Dictionary<(string, string), string>? configValues = null)
    {
        var logger = new Mock<ILogger<ThroneService>>();
        var dtLogger = new Mock<ILogger<DevTunnelsService>>();
        var mockClient = new Mock<IDevTunnelsClient>();

        mockClient.Setup(c => c.CreateOrUpdateTunnelAsync(
                It.IsAny<string>(), It.IsAny<DevTunnelOptions>(), It.IsAny<CancellationToken>()))
            .Returns<string, DevTunnelOptions, CancellationToken>(
                async (_, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return new DevTunnelStatus(); });

        IConfig dtConfig = MockConfig.MakeMockConfig(new Dictionary<(string, string), string>
        {
            { ("Server", "Port"), "14040" }
        });
        var devTunnels = new DevTunnelsService(dtLogger.Object, dtConfig, mockClient.Object);

        IConfig config = MockConfig.MakeMockConfig(configValues);
        var service = new ThroneService(logger.Object, config, devTunnels);

        return (service, devTunnels);
    }

    private static string BuildGiftPurchasedJson(
        string eventId, string gifterUsername, string itemName,
        int priceCents, string currency = "USD")
    {
        return $$"""
                 {
                     "event_id": "{{eventId}}",
                     "event_type": "gift_purchased",
                     "data": {
                         "creator_id": "creator-1",
                         "creator_username": "Creator",
                         "gifter_username": "{{gifterUsername}}",
                         "message": "Enjoy!",
                         "item_name": "{{itemName}}",
                         "item_thumbnail_url": "https://example.com/img.png",
                         "is_surprise_gift": true,
                         "price": {{priceCents}},
                         "currency": "{{currency}}"
                     }
                 }
                 """;
    }

    private static string BuildContributionPurchasedJson(
        string eventId, string gifterUsername, string itemName,
        int amountCents, string currency = "USD")
    {
        return $$"""
                 {
                     "event_id": "{{eventId}}",
                     "event_type": "contribution_purchased",
                     "data": {
                         "creator_id": "creator-1",
                         "creator_username": "Creator",
                         "gifter_username": "{{gifterUsername}}",
                         "message": "Keep it up!",
                         "item_name": "{{itemName}}",
                         "item_thumbnail_url": "https://example.com/img.png",
                         "amount": {{amountCents}},
                         "currency": "{{currency}}"
                     }
                 }
                 """;
    }

    private static string BuildCrowdfundedJson(
        string eventId, string itemName, int priceCents, string currency = "USD")
    {
        return $$"""
                 {
                     "event_id": "{{eventId}}",
                     "event_type": "gift_crowdfunded",
                     "data": {
                         "creator_id": "creator-1",
                         "creator_username": "Creator",
                         "item_name": "{{itemName}}",
                         "item_thumbnail_url": "https://example.com/img.png",
                         "is_surprise_gift": false,
                         "price": {{priceCents}},
                         "currency": "{{currency}}"
                     }
                 }
                 """;
    }

    private static SubathonEvent? CaptureEvent(Action action)
    {
        SubathonEvent? captured = null;
        void Handler(SubathonEvent e) => captured = e;
        SubathonEvents.SubathonEventCreated += Handler;
        action();
        SubathonEvents.SubathonEventCreated -= Handler;
        return captured;
    }

    [Fact]
    public async Task StartAsync_WhenDisabled_BroadcastsDisconnectedStatus()
    {
        (ThroneService service, _) = MakeService();

        bool? status = null;
        void Handler(IntegrationConnection conn)
        {
            if (conn.Source == SubathonEventSource.Throne) status = conn.Status;
        }

        IntegrationEvents.ConnectionUpdated += Handler;
        try { await service.StartAsync(TestContext.Current.CancellationToken); }
        finally { IntegrationEvents.ConnectionUpdated -= Handler; }

        Assert.False(status);
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StartAsync_WhenEnabled_DoesNotImmediatelyBroadcastConnected()
    {
        (ThroneService service, _) = MakeService(new Dictionary<(string, string), string>
        {
            { ("Throne", "Enabled"), "True" }
        });

        bool receivedTrue = false;
        void Handler(IntegrationConnection conn)
        {
            if (conn is { Source: SubathonEventSource.Throne, Status: true }) receivedTrue = true;
        }

        IntegrationEvents.ConnectionUpdated += Handler;
        try { await service.StartAsync(TestContext.Current.CancellationToken); }
        finally { IntegrationEvents.ConnectionUpdated -= Handler; }

        Assert.False(receivedTrue);
        await service.StopAsync(TestContext.Current.CancellationToken);
    }
    
    
    [Fact]
    public async Task StartAsync_WhenDisabled()
    {
        (ThroneService service, _) = MakeService(new Dictionary<(string, string), string>
        {
            { ("Throne", "Enabled"), "False" }
        });

        bool receivedFalse = false;
        void Handler(IntegrationConnection conn)
        {
            if (conn is { Source: SubathonEventSource.Throne, Status: false }) receivedFalse = true;
        }

        IntegrationEvents.ConnectionUpdated += Handler;
        try { await service.StartAsync(TestContext.Current.CancellationToken); }
        finally { IntegrationEvents.ConnectionUpdated -= Handler; }

        Assert.True(receivedFalse);
        await service.StopAsync(TestContext.Current.CancellationToken);
    }


    [Fact]
    public async Task StopAsync_BroadcastsDisconnectedStatus()
    {
        (ThroneService service, _) = MakeService(new Dictionary<(string, string), string>
        {
            { ("Throne", "Enabled"), "True" }
        });

        await service.StartAsync(TestContext.Current.CancellationToken);

        bool? status = null;
        void Handler(IntegrationConnection conn)
        {
            if (conn.Source == SubathonEventSource.Throne) status = conn.Status;
        }

        IntegrationEvents.ConnectionUpdated += Handler;
        try { await service.StopAsync(TestContext.Current.CancellationToken); }
        finally { IntegrationEvents.ConnectionUpdated -= Handler; }

        Assert.False(status);
    }

    [Fact]
    public async Task HandleWebhookAsync_WhenDisabled_DoesNotRaiseEvent()
    {
        (ThroneService service, _) = MakeService(); // Enabled = false by default

        var json = BuildGiftPurchasedJson(Guid.NewGuid().ToString(), "Gifter", "Cat Plushie", 1000);
        var body = System.Text.Encoding.UTF8.GetBytes(json);
        var headers = new Dictionary<string, string>
        {
            { "X-Signature-Timestamp", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString() },
            { "X-Signature-Ed25519", "deadbeef" }
        };

        var ev = CaptureEvent(() =>
            service.HandleWebhookAsync(body, headers, TestContext.Current.CancellationToken).GetAwaiter().GetResult());

        Assert.Null(ev);
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task HandleWebhookAsync_MissingTimestampHeader_DoesNotRaiseEvent()
    {
        (ThroneService service, _) = MakeService(new Dictionary<(string, string), string>
        {
            { ("Throne", "Enabled"), "True" }
        });

        var body = "{}"u8.ToArray();
        var headers = new Dictionary<string, string>
        {
            { "X-Signature-Ed25519", "deadbeef" }
            // no X-Signature-Timestamp
        };

        var ev = CaptureEvent(() =>
            service.HandleWebhookAsync(body, headers, TestContext.Current.CancellationToken).GetAwaiter().GetResult());

        Assert.Null(ev);
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task HandleWebhookAsync_MissingSignatureHeader_DoesNotRaiseEvent()
    {
        (ThroneService service, _) = MakeService(new Dictionary<(string, string), string>
        {
            { ("Throne", "Enabled"), "True" }
        });

        var body = "{}"u8.ToArray();
        var headers = new Dictionary<string, string>
        {
            { "X-Signature-Timestamp", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString() }
            // no X-Signature-Ed25519
        };

        var ev = CaptureEvent(() =>
            service.HandleWebhookAsync(body, headers, TestContext.Current.CancellationToken).GetAwaiter().GetResult());

        Assert.Null(ev);
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task HandleWebhookAsync_StaleTimestamp_DoesNotRaiseEvent()
    {
        (ThroneService service, _) = MakeService(new Dictionary<(string, string), string>
        {
            { ("Throne", "Enabled"), "True" }
        });

        var staleTimestamp = DateTimeOffset.UtcNow.AddMinutes(-15).ToUnixTimeSeconds().ToString();
        var body = "{}"u8.ToArray();
        var headers = new Dictionary<string, string>
        {
            { "X-Signature-Timestamp", staleTimestamp },
            { "X-Signature-Ed25519", "deadbeef" }
        };

        var ev = CaptureEvent(() =>
            service.HandleWebhookAsync(body, headers, TestContext.Current.CancellationToken).GetAwaiter().GetResult());

        Assert.Null(ev);
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task HandleWebhookAsync_InvalidSignature_DoesNotRaiseEvent()
    {
        (ThroneService service, _) = MakeService(new Dictionary<(string, string), string>
        {
            { ("Throne", "Enabled"), "True" }
        });

        var json = BuildGiftPurchasedJson(Guid.NewGuid().ToString(), "Gifter", "Hat", 500);
        var body = System.Text.Encoding.UTF8.GetBytes(json);
        var headers = new Dictionary<string, string>
        {
            { "X-Signature-Timestamp", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString() },
            { "X-Signature-Ed25519", "0000000000000000000000000000000000000000000000000000000000000000" +
                                     "0000000000000000000000000000000000000000000000000000000000000000" }
        };

        var ev = CaptureEvent(() =>
            service.HandleWebhookAsync(body, headers, TestContext.Current.CancellationToken).GetAwaiter().GetResult());

        Assert.Null(ev);
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void ProcessData_GiftPurchased_DollarMode_MapsCorrectly()
    {
        (ThroneService service, _) = MakeService(new Dictionary<(string, string), string>
        {
            { ("Throne", $"{SubathonEventType.ThroneGiftPurchase}.Mode"), "Dollar" }
        });

        var eventId = Guid.NewGuid().ToString();
        var json = BuildGiftPurchasedJson(eventId, "WolfGifter", "Cat Plushie", 1000, "USD");

        var ev = CaptureEvent(() => service.ProcessData(json));

        Assert.NotNull(ev);
        Assert.Equal(SubathonEventType.ThroneGiftPurchase, ev.EventType);
        Assert.Equal(SubathonEventSource.Throne, ev.Source);
        Assert.Equal("WolfGifter", ev.User);
        Assert.Equal("10.00", ev.Value);
        Assert.Equal("USD", ev.Currency);
        Assert.Equal(1, ev.Amount);
        Assert.Equal("Cat Plushie", ev.SecondaryValue);
        Assert.Equal("Cat Plushie", ev.TertiaryValue);
    }

    [Fact]
    public void ProcessData_GiftPurchased_DollarMode_PriceConvertedFromCents()
    {
        (ThroneService service, _) = MakeService(new Dictionary<(string, string), string>
        {
            { ("Throne", $"{SubathonEventType.ThroneGiftPurchase}.Mode"), "Dollar" }
        });

        var json = BuildGiftPurchasedJson(Guid.NewGuid().ToString(), "Gifter", "Mug", 2550, "GBP");

        var ev = CaptureEvent(() => service.ProcessData(json));

        Assert.NotNull(ev);
        Assert.Equal("25.50", ev.Value);
        Assert.Equal("GBP", ev.Currency);
    }

    [Fact]
    public void ProcessData_GiftPurchased_ItemMode_UsesItemNameAsValue()
    {
        (ThroneService service, _) = MakeService(new Dictionary<(string, string), string>
        {
            { ("Throne", $"{SubathonEventType.ThroneGiftPurchase}.Mode"), "Item" }
        });

        var json = BuildGiftPurchasedJson(Guid.NewGuid().ToString(), "Gifter", "Rainbow Hoodie", 4999, "USD");

        var ev = CaptureEvent(() => service.ProcessData(json));

        Assert.NotNull(ev);
        Assert.Equal(SubathonEventType.ThroneGiftPurchase, ev.EventType);
        Assert.Equal("Rainbow Hoodie", ev.Value);
        Assert.Equal("Gifter", ev.User);
        Assert.Equal("item", ev.Currency);
        Assert.Equal(1, ev.Amount);
    }

    [Fact]
    public void ProcessData_GiftPurchased_ItemMode_NullItemName_FallsBackToOne()
    {
        (ThroneService service, _) = MakeService(new Dictionary<(string, string), string>
        {
            { ("Throne", $"{SubathonEventType.ThroneGiftPurchase}.Mode"), "Item" }
        });

        var json = $$"""
                     {
                         "event_id": "{{Guid.NewGuid()}}",
                         "event_type": "gift_purchased",
                         "data": {
                             "gifter_username": "Anon",
                             "currency": "USD",
                             "price": 100000
                         }
                     }
                     """;

        var ev = CaptureEvent(() => service.ProcessData(json));

        Assert.NotNull(ev);
        Assert.Equal("1", ev.Value);
        Assert.Equal("item", ev.Currency);
        Assert.Equal("Anon", ev.User);
    }

    [Fact]
    public void ProcessData_GiftPurchased_IsSim_SetsSimulatedSource()
    {
        (ThroneService service, _) = MakeService();

        var json = BuildGiftPurchasedJson(Guid.NewGuid().ToString(), "SYSTEM", "Test Gift", 500);

        var ev = CaptureEvent(() => service.ProcessData(json, isSim: true));

        Assert.NotNull(ev);
        Assert.Equal(SubathonEventSource.Simulated, ev.Source);
    }

    [Fact]
    public void ProcessData_ContributionPurchased_MapsCorrectly()
    {
        (ThroneService service, _) = MakeService();

        var eventId = Guid.NewGuid().ToString();
        var json = BuildContributionPurchasedJson(eventId, "Gifter", "Expensive Lego", 750, "USD");

        var ev = CaptureEvent(() => service.ProcessData(json));

        Assert.NotNull(ev);
        Assert.Equal(SubathonEventType.ThroneGiftContribution, ev.EventType);
        Assert.Equal(SubathonEventSource.Throne, ev.Source);
        Assert.Equal("Gifter", ev.User);
        Assert.Equal("7.50", ev.Value);
        Assert.Equal("USD", ev.Currency);
        Assert.Equal("Expensive Lego", ev.TertiaryValue);
    }

    [Fact]
    public void ProcessData_ContributionPurchased_DifferentCurrency_UsesCorrectCurrency()
    {
        (ThroneService service, _) = MakeService();

        var json = BuildContributionPurchasedJson(Guid.NewGuid().ToString(), "Gifter", "Campaign", 5000, "EUR");

        var ev = CaptureEvent(() => service.ProcessData(json));

        Assert.NotNull(ev);
        Assert.Equal("50.00", ev.Value);
        Assert.Equal("EUR", ev.Currency);
    }

    [Fact]
    public void ProcessData_ContributionPurchased_IsSim_SetsSimulatedSource()
    {
        (ThroneService service, _) = MakeService();

        var json = BuildContributionPurchasedJson(Guid.NewGuid().ToString(), "SYSTEM", "Sim Drive", 1000);

        var ev = CaptureEvent(() => service.ProcessData(json, isSim: true));

        Assert.NotNull(ev);
        Assert.Equal(SubathonEventSource.Simulated, ev.Source);
        Assert.Equal(SubathonEventType.ThroneGiftContribution, ev.EventType);
    }

    [Fact]
    public void ProcessData_GiftCrowdfunded_MapsCorrectly()
    {
        (ThroneService service, _) = MakeService();

        var eventId = Guid.NewGuid().ToString();
        var json = BuildCrowdfundedJson(eventId, "Gold or something", 20000, "USD");

        var ev = CaptureEvent(() => service.ProcessData(json));

        Assert.NotNull(ev);
        Assert.Equal(SubathonEventType.ThroneCrowdGiftComplete, ev.EventType);
        Assert.Equal(SubathonEventSource.Throne, ev.Source);
        Assert.Equal("Gold or something", ev.User); 
        Assert.Equal("200.00", ev.Value);
        Assert.Equal("USD", ev.Currency);
    }

    [Fact]
    public void ProcessData_GiftCrowdfunded_NullItemName_FallsBackToDefault()
    {
        (ThroneService service, _) = MakeService();

        var json = $$"""
                     {
                         "event_id": "{{Guid.NewGuid()}}",
                         "event_type": "gift_crowdfunded",
                         "data": {
                             "currency": "USD",
                             "price": 5000
                         }
                     }
                     """;

        var ev = CaptureEvent(() => service.ProcessData(json));

        Assert.NotNull(ev);
        Assert.Equal("Crowdfunding Complete!", ev.User); // fake user
        Assert.Equal("50.00", ev.Value);
    }

    [Fact]
    public void ProcessData_GiftCrowdfunded_IsSim_SetsSimulatedSource()
    {
        (ThroneService service, _) = MakeService();

        var json = BuildCrowdfundedJson(Guid.NewGuid().ToString(), "Chair", 10000);

        var ev = CaptureEvent(() => service.ProcessData(json, isSim: true));

        Assert.NotNull(ev);
        Assert.Equal(SubathonEventSource.Simulated, ev.Source);
    }

    [Fact]
    public void ProcessData_UnknownEventType_DoesNotRaiseEvent()
    {
        (ThroneService service, _) = MakeService();

        var json = $$"""
                     {
                         "event_id": "{{Guid.NewGuid()}}",
                         "event_type": "some_future_event",
                         "data": {
                             "gifter_username": "Gifter",
                             "currency": "USD",
                             "price": 10000
                         }
                     }
                     """;

        var ev = CaptureEvent(() => service.ProcessData(json));

        Assert.Null(ev);
    }

    [Fact]
    public void ProcessData_NullPayload_DoesNotThrowOrRaiseEvent()
    {
        (ThroneService service, _) = MakeService();

        var ev = CaptureEvent(() => service.ProcessData("null"));

        Assert.Null(ev);
    }

    [Fact]
    public void ProcessData_EmptyDataObject_DoesNotRaiseEvent()
    {
        (ThroneService service, _) = MakeService();

        var json = """{ "event_id": "abc", "event_type": "gift_purchased", "data": "" }""";

        var ev = CaptureEvent(() => service.ProcessData(json));

        Assert.Null(ev);
    }

    [Fact]
    public void ProcessData_GiftPurchased_EventIdBecomesSubathonEventId()
    {
        (ThroneService service, _) = MakeService();

        var knownGuid = Guid.NewGuid();
        var json = BuildGiftPurchasedJson(knownGuid.ToString(), "Gifter", "Jacket", 3000);

        var ev = CaptureEvent(() => service.ProcessData(json));

        Assert.NotNull(ev);
        Assert.Equal(knownGuid, ev.Id);
    }

    [Fact]
    public void ProcessData_GiftPurchased_NullGifterUsername_FallsBackToDefaultUser()
    {
        (ThroneService service, _) = MakeService();

        var json = $$"""
                     {
                         "event_id": "{{Guid.NewGuid()}}",
                         "event_type": "gift_purchased",
                         "data": {
                             "item_name": "Mystery Box",
                             "currency": "USD",
                             "price": 50000
                         }
                     }
                     """;

        var ev = CaptureEvent(() => service.ProcessData(json));

        Assert.NotNull(ev);
        Assert.Equal("Crowdfunding Complete!", ev.User);
    }

    [Fact]
    public void WebhookPath_IsCorrectValue()
    {
        (ThroneService service, _) = MakeService();
        Assert.Equal("/api/webhooks/throne", service.WebhookPath);
    }

    [Fact]
    public async Task StopAsync_AfterStart_BroadcastsCorrectSource()
    {
        (ThroneService service, _) = MakeService();
        await service.StartAsync(TestContext.Current.CancellationToken);

        SubathonEventSource? capturedSource = null;
        void Handler(IntegrationConnection conn) => capturedSource = conn.Source;

        IntegrationEvents.ConnectionUpdated += Handler;
        try { await service.StopAsync(TestContext.Current.CancellationToken); }
        finally { IntegrationEvents.ConnectionUpdated -= Handler; }

        Assert.Equal(SubathonEventSource.Throne, capturedSource);
    }

    [Theory]
    [InlineData("Dollar", "25.00", "USD")]
    [InlineData("Item",   "Fancy Mug", "item")]
    public void ProcessData_GiftPurchased_RespectsModeConfig(
        string mode, string expectedValue, string expectedCurrency)
    {
        (ThroneService service, _) = MakeService(new Dictionary<(string, string), string>
        {
            { ("Throne", $"{SubathonEventType.ThroneGiftPurchase}.Mode"), mode }
        });

        var json = BuildGiftPurchasedJson(Guid.NewGuid().ToString(), "Gifter", "Fancy Mug", 2500, "USD");

        var ev = CaptureEvent(() => service.ProcessData(json));

        Assert.NotNull(ev);
        Assert.Equal(expectedValue, ev.Value);
        Assert.Equal(expectedCurrency, ev.Currency);
    }
}