using System.Reflection;
using System.Text;
using System.Text.Json;
using DevTunnels.Client;
using DevTunnels.Client.Tunnels;
using Fourthwall.Client.Events;
using Fourthwall.Client.Generated.Models;
using Fourthwall.Client.Generated.Models.Openapi.Model;
using Fourthwall.Client.Generated.Models.Openapi.Model.DonationV1;
using Fourthwall.Client.Generated.Models.Openapi.Model.GiftPurchaseV1;
using Fourthwall.Client.Generated.Models.Openapi.Model.MembershipSupporterV1;
using Fourthwall.Client.Generated.Models.Openapi.Model.MembershipSupporterV1.Subscription;
using Fourthwall.Client.Generated.Models.Openapi.Model.OfferAbstractV1;
using Fourthwall.Client.Generated.Models.Openapi.Model.OfferAbstractV1.OfferVariantAbstractV1;
using Fourthwall.Client.Generated.Models.Openapi.Model.OrderV1;
using Fourthwall.Client.Generated.Models.Openapi.Model.OrderV1.Source;
using Microsoft.Extensions.Logging;
using Moq;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Objects;
using SubathonManager.Integration;
using SubathonManager.Tests.Utility;
using Amounts = Fourthwall.Client.Generated.Models.Openapi.Model.DonationV1.Amounts;

// ReSharper disable NullableWarningSuppressionIsUsed
namespace SubathonManager.Tests.IntegrationUnitTests;

[Collection("SharedEventBusTests")]
public class FourthWallServiceTests
{
    public FourthWallServiceTests()
    {
        typeof(IntegrationEvents)
            .GetField("ConnectionUpdated", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
    }

    private static (FourthWallService service, DevTunnelsService devTunnels) MakeService(
        Dictionary<(string, string), string>? configValues = null)
    {
        var logger = new Mock<ILogger<FourthWallService>>();
        var dtLogger = new Mock<ILogger<DevTunnelsService>>();
        var mockClient = new Mock<IDevTunnelsClient>();

        mockClient.Setup(c => c.CreateOrUpdateTunnelAsync(
                It.IsAny<string>(), It.IsAny<DevTunnelOptions>(), It.IsAny<CancellationToken>()))
            .Returns<string, DevTunnelOptions, CancellationToken>(
                async (_, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return new DevTunnelStatus(); });

        IConfig dtConfig = MockConfig.MakeMockConfig(new Dictionary<(string, string), string>
        {
            { ("Server", "Port"), "14040" },
        });
        var devTunnels = new DevTunnelsService(dtLogger.Object, dtConfig, mockClient.Object);

        var httpFactory = new Mock<IHttpClientFactory>();
        httpFactory.Setup(f => f.CreateClient(nameof(FourthWallService))).Returns(new HttpClient());

        IConfig config = MockConfig.MakeMockConfig(configValues);
        var service = new FourthWallService(logger.Object, config, httpFactory.Object, devTunnels);

        service.OpenBrowser = _ => { };

        return (service, devTunnels);
    }

    private static FourthwallDonationWebhookEvent MakeDonationEvent(
        string username = "Supporter", double amount = 10.00, string currency = "USD",
        bool testMode = false, string? id = null)
    {
        var data = new DonationV1
        {
            Id = id ?? Guid.NewGuid().ToString(),
            Username = username,
            Message = "test",
            CreatedAt = DateTimeOffset.UtcNow,
            Amounts = new Amounts
            {
                Total = new Money { Value = amount, Currency = currency }
            }
        };
        return new FourthwallDonationWebhookEvent
        {
            Id = Guid.NewGuid().ToString(),
            Data = data,
            CreatedAt = DateTimeOffset.UtcNow,
            TestMode = testMode,
            WebhookId = "", ShopId = "", Type = "DONATION", ApiVersion = ""
        };
    }

    private static FourthwallOrderPlacedWebhookEvent MakeOrderEvent(
        string username = "Customer", double subtotal = 25.00, string currency = "USD",
        int quantity = 2, double unitPrice = 12.50, double unitCost = 8.00,
        string orderType = "ORDER", bool testMode = false, string? id = null)
    {
        var variant = new OfferVariantWithQuantityV1
        {
            Quantity = quantity,
            Price = new Money { Value = unitPrice * quantity, Currency = currency },
            UnitPrice = new Money { Value = unitPrice, Currency = currency },
            Cost = new Money { Value = unitCost * quantity, Currency = currency },
            UnitCost = new Money { Value = unitCost, Currency = currency }
        };
        var offer = new OfferOrderV1 { Variant = variant };
        var data = new OrderV1
        {
            Id = id ?? Guid.NewGuid().ToString(),
            Username = username,
            Message = "test",
            CreatedAt = DateTimeOffset.UtcNow,
            Status = OrderV1_status.CONFIRMED,
            Source = new OrderV1.OrderV1_source
            {
                Order = new Order { Type = orderType }
            },
            Offers = new List<OfferOrderV1> { offer },
            Amounts = new OrderAmounts
            {
                Subtotal = new Money { Value = subtotal, Currency = currency },
                Total = new Money { Value = subtotal * 1.10, Currency = currency },
                Donation = new Money { Value = 0, Currency = currency },
                Tax = new Money { Value = subtotal * 0.10, Currency = currency },
                Shipping = new Money { Value = 5.00, Currency = currency }
            }
        };
        return new FourthwallOrderPlacedWebhookEvent
        {
            Id = Guid.NewGuid().ToString(),
            Data = data,
            CreatedAt = DateTimeOffset.UtcNow,
            TestMode = testMode,
            WebhookId = "", ShopId = "", Type = "ORDER_PLACED", ApiVersion = ""
        };
    }

    private static FourthwallGiftPurchaseWebhookEvent MakeGiftOrderEvent(
        string username = "Gifter", double subtotal = 20.00, double profit = 13.40,
        string currency = "USD", int quantity = 1, bool testMode = false, string? id = null)
    {
        var data = new GiftPurchaseV1
        {
            Id = id ?? Guid.NewGuid().ToString(),
            Username = username,
            Message = "gift",
            CreatedAt = DateTimeOffset.UtcNow,
            Quantity = quantity,
            Offer = new OfferGiftPurchaseV1(),
            Gifts = new List<GiftPurchaseV1.GiftPurchaseV1_gifts>(),
            Amounts = new Fourthwall.Client.Generated.Models.Openapi.Model.GiftPurchaseV1.Amounts
            {
                Subtotal = new Money { Value = subtotal, Currency = currency },
                Profit = new Money { Value = profit, Currency = currency },
                Tax = new Money { Value = subtotal * 0.10, Currency = currency }
            }
            
        };
        return new FourthwallGiftPurchaseWebhookEvent
        {
            Id = Guid.NewGuid().ToString(),
            Data = data,
            CreatedAt = DateTimeOffset.UtcNow,
            TestMode = testMode,
            WebhookId = "", ShopId = "", Type = "GIFT_PURCHASE", ApiVersion = ""
        };
    }

    private static FourthwallSubscriptionPurchasedWebhookEvent MakeSubscriptionPurchasedEvent(
        string nickname = "Member", string tierId = "tier-1", double amount = 5.00,
        string currency = "USD",
        MembershipTierVariantV1_interval interval = MembershipTierVariantV1_interval.MONTHLY,
        bool testMode = false, string? id = null)
    {
        var variant = new MembershipTierVariantV1
        {
            TierId = tierId,
            Interval = interval,
            Amount = new Money { Value = amount, Currency = currency }
        };
        var data = new MembershipSupporterV1
        {
            Id = id ?? Guid.NewGuid().ToString(),
            Nickname = nickname,
            CreatedAt = DateTimeOffset.UtcNow,
            Subscription = new MembershipSupporterV1.MembershipSupporterV1_subscription
            {
                Active = new Active { Variant = variant }
            }
        };
        return new FourthwallSubscriptionPurchasedWebhookEvent
        {
            Id = Guid.NewGuid().ToString(),
            Data = data,
            CreatedAt = DateTimeOffset.UtcNow,
            TestMode = testMode,
            WebhookId = "", ShopId = "", Type = "SUBSCRIPTION_PURCHASED", ApiVersion = ""
        };
    }

    private static FourthwallSubscriptionChangedWebhookEvent MakeSubscriptionChangedEvent(
        string nickname = "Member", string tierId = "tier-1", double amount = 5.00,
        string currency = "USD",
        MembershipTierVariantV1_interval interval = MembershipTierVariantV1_interval.MONTHLY,
        bool testMode = false, string? id = null)
    {
        var variant = new MembershipTierVariantV1
        {
            TierId = tierId,
            Interval = interval,
            Amount = new Money { Value = amount, Currency = currency }
        };
        var data = new MembershipSupporterV1
        {
            Id = id ?? Guid.NewGuid().ToString(),
            Nickname = nickname,
            CreatedAt = DateTimeOffset.UtcNow,
            Subscription = new MembershipSupporterV1.MembershipSupporterV1_subscription
            {
                Active = new Active { Variant = variant }
            }
        };
        return new FourthwallSubscriptionChangedWebhookEvent
        {
            Id = Guid.NewGuid().ToString(),
            Data = data,
            CreatedAt = DateTimeOffset.UtcNow,
            TestMode = testMode,
            WebhookId = "", ShopId = "", Type = "SUBSCRIPTION_CHANGED", ApiVersion = ""
        };
    }

    [Fact]
    public async Task StartAsync_NoTokenFile_BroadcastsDisabled()
    {
        (FourthWallService service, _) = MakeService();

        bool? status = null;
        void Handler(IntegrationConnection conn)
        {
            if (conn.Source == SubathonEventSource.FourthWall) status = conn.Status;
        }

        IntegrationEvents.ConnectionUpdated += Handler;
        try { await service.StartAsync(TestContext.Current.CancellationToken); }
        finally { IntegrationEvents.ConnectionUpdated -= Handler; }

        Assert.False(status);
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StopAsync_BroadcastsDisabled()
    {
        (FourthWallService service, _) = MakeService();
        await service.StartAsync(TestContext.Current.CancellationToken);

        bool? status = null;
        void Handler(IntegrationConnection conn)
        {
            if (conn.Source == SubathonEventSource.FourthWall) status = conn.Status;
        }

        IntegrationEvents.ConnectionUpdated += Handler;
        try { await service.StopAsync(TestContext.Current.CancellationToken); }
        finally { IntegrationEvents.ConnectionUpdated -= Handler; }

        Assert.False(status);
    }

    [Fact]
    public void MapToSubathonEvent_Donation_MapsCorrectly()
    {
        (FourthWallService service, _) = MakeService();
        var fwEvent = MakeDonationEvent(username: "Wolf", amount: 15.50, currency: "USD");

        var ev = service.MapToSubathonEvent(fwEvent);

        Assert.NotNull(ev);
        Assert.Equal(SubathonEventType.FourthWallDonation, ev.EventType);
        Assert.Equal(SubathonEventSource.FourthWall, ev.Source);
        Assert.Equal("Wolf", ev.User);
        Assert.Equal("15.50", ev.Value);
        Assert.Equal("USD", ev.Currency);
    }

    [Fact]
    public void MapToSubathonEvent_Donation_SystemUsername_SetsSimulatedSource()
    {
        (FourthWallService service, _) = MakeService();
        var fwEvent = MakeDonationEvent(username: "SYSTEM", amount: 5.00);

        var ev = service.MapToSubathonEvent(fwEvent);

        Assert.NotNull(ev);
        Assert.Equal(SubathonEventSource.Simulated, ev.Source);
        Assert.Equal(SubathonEventType.FourthWallDonation, ev.EventType);
    }

    [Fact]
    public void MapToSubathonEvent_Donation_TestMode_SetsTestUsername()
    {
        (FourthWallService service, _) = MakeService();
        var fwEvent = MakeDonationEvent(username: "RealUser", amount: 5.00, testMode: true);

        var ev = service.MapToSubathonEvent(fwEvent);

        Assert.NotNull(ev);
        Assert.Equal("FourthWall Test", ev.User);
        Assert.Equal(SubathonEventSource.FourthWall, ev.Source);
    }

    [Fact]
    public void MapToSubathonEvent_Donation_MultiWordUsername_UsesFirstWordOnly()
    {
        (FourthWallService service, _) = MakeService();
        var fwEvent = MakeDonationEvent(username: "John Doe");

        var ev = service.MapToSubathonEvent(fwEvent);

        Assert.NotNull(ev);
        Assert.Equal("John", ev.User);
    }

    [Theory]
    [InlineData("Dollar", "25.00", "USD")]
    [InlineData("Item",   "2",     "items")]
    [InlineData("Order",  "New",   "order")]
    public void MapToSubathonEvent_Order_RespectsModeConfig(
        string mode, string expectedValue, string expectedCurrency)
    {
        (FourthWallService service, _) = MakeService(new Dictionary<(string, string), string>
        {
            { ("FourthWall", $"{SubathonEventType.FourthWallOrder}.Mode"), mode }
        });
        var fwEvent = MakeOrderEvent(username: "Buyer", subtotal: 25.00, quantity: 2);

        var ev = service.MapToSubathonEvent(fwEvent);

        Assert.NotNull(ev);
        Assert.Equal(SubathonEventType.FourthWallOrder, ev.EventType);
        Assert.Equal(SubathonEventSource.FourthWall, ev.Source);
        Assert.Equal("Buyer", ev.User);
        Assert.Equal(expectedValue, ev.Value);
        Assert.Equal(expectedCurrency, ev.Currency);
        Assert.Equal(2, ev.Amount);
    }

    [Fact]
    public void MapToSubathonEvent_Order_SecondaryValue_ContainsProfitAndCurrency()
    {
        (FourthWallService service, _) = MakeService();
        // price=12.50×2=25, cost=8×2=16 -> profit=9.00; donation=0 -> totalDirect=9.00
        var fwEvent = MakeOrderEvent(subtotal: 25.00, quantity: 2, unitPrice: 12.50, unitCost: 8.00, currency: "USD");

        var ev = service.MapToSubathonEvent(fwEvent);

        Assert.NotNull(ev);
        Assert.Equal("9.00|USD", ev.SecondaryValue);
    }

    [Fact]
    public void MapToSubathonEvent_Order_NonOrderType_ReturnsNull()
    {
        (FourthWallService service, _) = MakeService();
        var fwEvent = MakeOrderEvent(orderType: "SUBSCRIPTION");

        var ev = service.MapToSubathonEvent(fwEvent);

        Assert.Null(ev);
    }

    [Fact]
    public void MapToSubathonEvent_Order_SamplesOrder_SetsInternalSamplesUser_AndZeroProfit()
    {
        (FourthWallService service, _) = MakeService();
        var fwEvent = MakeOrderEvent(username: "Anyone", orderType: "SAMPLES_ORDER",
            subtotal: 30.00, unitPrice: 15.00, unitCost: 8.00, quantity: 2);

        var ev = service.MapToSubathonEvent(fwEvent);

        Assert.NotNull(ev);
        Assert.Equal("Internal Samples", ev.User);
        Assert.Equal("0.00|USD", ev.SecondaryValue);
    }

    [Fact]
    public void MapToSubathonEvent_Order_SystemUsername_SetsSimulatedSource()
    {
        (FourthWallService service, _) = MakeService();
        var fwEvent = MakeOrderEvent(username: "SYSTEM");

        var ev = service.MapToSubathonEvent(fwEvent);

        Assert.NotNull(ev);
        Assert.Equal(SubathonEventSource.Simulated, ev.Source);
    }
    
    [Theory]
    [InlineData("Dollar",  "20.00",    "USD")]
    [InlineData("Item",    "3",        "items")]
    [InlineData("Order",   "New Gift", "order")]
    public void MapToSubathonEvent_GiftOrder_RespectsModeConfig(
        string mode, string expectedValue, string expectedCurrency)
    {
        (FourthWallService service, _) = MakeService(new Dictionary<(string, string), string>
        {
            { ("FourthWall", $"{SubathonEventType.FourthWallGiftOrder}.Mode"), mode }
        });
        var fwEvent = MakeGiftOrderEvent(username: "Gifter", subtotal: 20.00, profit: 13.40,
            currency: "USD", quantity: 3);

        var ev = service.MapToSubathonEvent(fwEvent);

        Assert.NotNull(ev);
        Assert.Equal(SubathonEventType.FourthWallGiftOrder, ev.EventType);
        Assert.Equal(SubathonEventSource.FourthWall, ev.Source);
        Assert.Equal("Gifter", ev.User);
        Assert.Equal(expectedValue, ev.Value);
        Assert.Equal(expectedCurrency, ev.Currency);
        Assert.Equal(3, ev.Amount);
    }

    [Fact]
    public void MapToSubathonEvent_GiftOrder_SecondaryValue_ContainsProfitAndCurrency()
    {
        (FourthWallService service, _) = MakeService();
        var fwEvent = MakeGiftOrderEvent(subtotal: 20.00, profit: 13.40, currency: "USD");

        var ev = service.MapToSubathonEvent(fwEvent);

        Assert.NotNull(ev);
        Assert.Equal("13.40|USD", ev.SecondaryValue);
    }

    [Fact]
    public void MapToSubathonEvent_GiftOrder_SystemUsername_SetsSimulatedSource()
    {
        (FourthWallService service, _) = MakeService();
        var fwEvent = MakeGiftOrderEvent(username: "SYSTEM");

        var ev = service.MapToSubathonEvent(fwEvent);

        Assert.NotNull(ev);
        Assert.Equal(SubathonEventSource.Simulated, ev.Source);
    }

    [Fact]
    public void MapToSubathonEvent_MembershipPurchased_Monthly_MapsCorrectly()
    {
        (FourthWallService service, _) = MakeService();
        service.MembershipNames["tier-gold"] = "Gold";
        var fwEvent = MakeSubscriptionPurchasedEvent(nickname: "Fan", tierId: "tier-gold",
            amount: 9.99, currency: "USD", interval: MembershipTierVariantV1_interval.MONTHLY);

        var ev = service.MapToSubathonEvent(fwEvent);

        Assert.NotNull(ev);
        Assert.Equal(SubathonEventType.FourthWallMembership, ev.EventType);
        Assert.Equal(SubathonEventSource.FourthWall, ev.Source);
        Assert.Equal("Fan", ev.User);
        Assert.Equal("Gold", ev.Value);
        Assert.Equal("member", ev.Currency);
        Assert.Equal(1, ev.Amount);
        Assert.Equal("9.99|USD", ev.SecondaryValue);
    }

    [Fact]
    public void MapToSubathonEvent_MembershipPurchased_Annual_SetsAmountTo12()
    {
        (FourthWallService service, _) = MakeService();
        service.MembershipNames["tier-silver"] = "Silver";
        var fwEvent = MakeSubscriptionPurchasedEvent(nickname: "Patron", tierId: "tier-silver",
            amount: 49.99, currency: "USD", interval: MembershipTierVariantV1_interval.ANNUAL);

        var ev = service.MapToSubathonEvent(fwEvent);

        Assert.NotNull(ev);
        Assert.Equal(12, ev.Amount);
        Assert.Equal("Silver", ev.Value);
    }

    [Fact]
    public void MapToSubathonEvent_MembershipPurchased_UnknownTierId_FallsBackToDefault()
    {
        (FourthWallService service, _) = MakeService();
        var fwEvent = MakeSubscriptionPurchasedEvent(tierId: "tier-unknown");

        var ev = service.MapToSubathonEvent(fwEvent);

        Assert.NotNull(ev);
        Assert.Equal("DEFAULT", ev.Value);
    }

    [Fact]
    public void MapToSubathonEvent_MembershipPurchased_NullActiveSubscription_ReturnsNull()
    {
        (FourthWallService service, _) = MakeService();
        var fwEvent = MakeSubscriptionPurchasedEvent();
        fwEvent.Data!.Subscription!.Active = null;

        var ev = service.MapToSubathonEvent(fwEvent);

        Assert.Null(ev);
    }

    [Fact]
    public void MapToSubathonEvent_MembershipPurchased_TestMode_UsesTestUsername()
    {
        (FourthWallService service, _) = MakeService();
        var fwEvent = MakeSubscriptionPurchasedEvent(nickname: "RealFan", testMode: true);

        var ev = service.MapToSubathonEvent(fwEvent);

        Assert.NotNull(ev);
        Assert.Equal("FourthWall Test", ev.User);
    }

    [Fact]
    public void MapToSubathonEvent_MembershipChanged_MapsCorrectly()
    {
        (FourthWallService service, _) = MakeService();
        service.MembershipNames["tier-bronze"] = "Bronze";
        var fwEvent = MakeSubscriptionChangedEvent(nickname: "OldFan", tierId: "tier-bronze",
            amount: 4.99, currency: "CAD", interval: MembershipTierVariantV1_interval.MONTHLY);

        var ev = service.MapToSubathonEvent(fwEvent);

        Assert.NotNull(ev);
        Assert.Equal(SubathonEventType.FourthWallMembership, ev.EventType);
        Assert.Equal(SubathonEventSource.FourthWall, ev.Source);
        Assert.Equal("OldFan", ev.User);
        Assert.Equal("Bronze", ev.Value);
        Assert.Equal("member", ev.Currency);
        Assert.Equal(1, ev.Amount);
        Assert.Equal("4.99|CAD", ev.SecondaryValue);
    }

    [Fact]
    public void MapToSubathonEvent_MembershipChanged_NullActiveSubscription_ReturnsNull()
    {
        (FourthWallService service, _) = MakeService();
        var fwEvent = MakeSubscriptionChangedEvent();
        fwEvent.Data!.Subscription!.Active = null;

        var ev = service.MapToSubathonEvent(fwEvent);

        Assert.Null(ev);
    }

    [Fact]
    public void MapToSubathonEvent_MembershipChanged_Annual_SetsAmountTo12()
    {
        (FourthWallService service, _) = MakeService();
        service.MembershipNames["tier-plat"] = "Platinum";
        var fwEvent = MakeSubscriptionChangedEvent(tierId: "tier-plat",
            interval: MembershipTierVariantV1_interval.ANNUAL);

        var ev = service.MapToSubathonEvent(fwEvent);

        Assert.NotNull(ev);
        Assert.Equal(12, ev.Amount);
    }

    [Fact]
    public void MapToSubathonEvent_SameOrderId_ProducesSameEventGuid()
    {
        (FourthWallService service, _) = MakeService();
        string sharedId = Guid.NewGuid().ToString();

        var ev1 = service.MapToSubathonEvent(MakeOrderEvent(id: sharedId));
        var ev2 = service.MapToSubathonEvent(MakeOrderEvent(id: sharedId));

        Assert.NotNull(ev1);
        Assert.NotNull(ev2);
        Assert.Equal(ev1!.Id, ev2!.Id);
    }

    [Fact]
    public void MapToSubathonEvent_MembershipPurchased_TestMode_ProducesUniqueGuidEachTime()
    {
        (FourthWallService service, _) = MakeService();
        string sharedId = Guid.NewGuid().ToString();

        var ev1 = service.MapToSubathonEvent(MakeSubscriptionPurchasedEvent(id: sharedId, testMode: true));
        var ev2 = service.MapToSubathonEvent(MakeSubscriptionPurchasedEvent(id: sharedId, testMode: true));

        Assert.NotNull(ev1);
        Assert.NotNull(ev2);
        Assert.NotEqual(ev1!.Id, ev2!.Id);
    }

    [Fact]
    public async Task HandleWebhookAsync_ForwardsToConfiguredUrl()
    {
        await using var mockServer = new MockWebServerHost().OnPost("/fw-forward", "", statusCode: 200);

        var logger = new Mock<ILogger<FourthWallService>>();
        var dtLogger = new Mock<ILogger<DevTunnelsService>>();
        var mockClient = new Mock<IDevTunnelsClient>();
        mockClient.Setup(c => c.CreateOrUpdateTunnelAsync(
                It.IsAny<string>(), It.IsAny<DevTunnelOptions>(), It.IsAny<CancellationToken>()))
            .Returns<string, DevTunnelOptions, CancellationToken>(
                async (_, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return new DevTunnelStatus(); });

        IConfig dtConfig = MockConfig.MakeMockConfig(new Dictionary<(string, string), string>
            { { ("Server", "Port"), "14040" } });
        var devTunnels = new DevTunnelsService(dtLogger.Object, dtConfig, mockClient.Object);

        var httpFactory = new Mock<IHttpClientFactory>();
        httpFactory.Setup(f => f.CreateClient(nameof(FourthWallService))).Returns(new HttpClient());

        IConfig config = MockConfig.MakeMockConfig(new Dictionary<(string, string), string>
        {
            { ("FourthWall", "ForwardUrls"), mockServer.BaseUrl.TrimEnd('/') + "/fw-forward" },
        });

        var service = new FourthWallService(logger.Object, config, httpFactory.Object, devTunnels);
        service.OpenBrowser = _ => { };

        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "DONATION" }));
        var headers = new Dictionary<string, string> { { "Content-Type", "application/json" } };

        await service.HandleWebhookAsync(body, headers, TestContext.Current.CancellationToken);

        Assert.Equal(1, mockServer.PostCallCount);

        await service.StopAsync(TestContext.Current.CancellationToken);
    }
}