using System.Reflection;
using System.Text;
using System.Web;
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

namespace SubathonManager.Tests.IntegrationUnitTests;

[Collection("SharedEventBusTests")]
public class KoFiServiceTests
{
    public KoFiServiceTests()
    {
        typeof(IntegrationEvents)
            .GetField("ConnectionUpdated", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
    }

    private static (KoFiService service, DevTunnelsService devTunnels) MakeService(
        Dictionary<(string, string), string>? configValues = null)
    {
        var logger = new Mock<ILogger<KoFiService>>();
        var dtLogger = new Mock<ILogger<DevTunnelsService>>();
        var mockClient = new Mock<IDevTunnelsClient>();

        // Block CreateOrUpdateTunnelAsync so the fire-and-forget StartTunnelAsync
        // call inside StartAsync does not race with test assertions.
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
        _ = httpFactory.Setup(f => f.CreateClient(nameof(KoFiService))).Returns(new HttpClient());

        IConfig config = MockConfig.MakeMockConfig(configValues);
        var koFi = new KoFiService(logger.Object, config, httpFactory.Object, devTunnels);
        return (koFi, devTunnels);
    }

    private static byte[] BuildKoFiBody(string token, string type, string fromName,
        string amount, string currency, string messageId,
        bool isSubscriptionPayment = false, bool isFirstSubscriptionPayment = false,
        string? tierName = null)
    {
        string json = $@"{{
          ""verification_token"":""{token}"",
          ""message_id"":""{messageId}"",
          ""timestamp"":""2024-01-01T12:00:00+00:00"",
          ""type"":""{type}"",
          ""is_public"":true,
          ""from_name"":""{fromName}"",
          ""message"":""Test message"",
          ""amount"":""{amount}"",
          ""url"":""https://ko-fi.com/"",
          ""email"":""test@test.com"",
          ""currency"":""{currency}"",
          ""is_subscription_payment"":{(isSubscriptionPayment ? "true" : "false")},
          ""is_first_subscription_payment"":{(isFirstSubscriptionPayment ? "true" : "false")},
          ""kofi_transaction_id"":""ABC123"",
          ""shop_items"":null,
          ""tier_name"":{(tierName != null ? $@"""{tierName}""" : "null")}
        }}";

        return Encoding.UTF8.GetBytes("data=" + HttpUtility.UrlEncode(json));
    }

    private static IReadOnlyDictionary<string, string> DefaultHeaders => new Dictionary<string, string>
    {
        { "Content-Type", "application/x-www-form-urlencoded" }
    };

    [Fact]
    public async Task StartAsync_NoToken_BroadcastsDisabled()
    {
        (KoFiService? service, DevTunnelsService _) = MakeService();

        bool? status = null;
        void Handler(IntegrationConnection conn)
        {
            if (conn.Source == SubathonEventSource.KoFiWebhook)
            {
                status = conn.Status;
            }
        }

        IntegrationEvents.ConnectionUpdated += Handler;
        try
        {
            await service.StartAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            IntegrationEvents.ConnectionUpdated -= Handler;
        }

        Assert.False(status);
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StartAsync_WithToken_BroadcastsEnabled()
    {
        (KoFiService? service, DevTunnelsService _) = MakeService(new Dictionary<(string, string), string>
        {
            { ("KoFi", "VerificationToken"), "my-token" },
        });

        bool? status = null;
        void Handler(IntegrationConnection conn)
        {
            if (conn.Source == SubathonEventSource.KoFiWebhook)
            {
                status = conn.Status;
            }
        }

        IntegrationEvents.ConnectionUpdated += Handler;
        try
        {
            await service.StartAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            IntegrationEvents.ConnectionUpdated -= Handler;
        }

        Assert.True(status);
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StopAsync_BroadcastsDisabled()
    {
        (KoFiService? service, DevTunnelsService _) = MakeService(new Dictionary<(string, string), string>
        {
            { ("KoFi", "VerificationToken"), "my-token" },
        });

        await service.StartAsync(TestContext.Current.CancellationToken);

        bool? status = null;
        void Handler(IntegrationConnection conn)
        {
            if (conn.Source == SubathonEventSource.KoFiWebhook)
            {
                status = conn.Status;
            }
        }

        IntegrationEvents.ConnectionUpdated += Handler;
        try
        {
            await service.StopAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            IntegrationEvents.ConnectionUpdated -= Handler;
        }

        Assert.False(status);
    }

    [Fact]
    public async Task HandleWebhookAsync_NoConfiguredToken_DoesNotRaiseEvent()
    {
        (KoFiService? service, DevTunnelsService _) = MakeService();
        await service.StartAsync(TestContext.Current.CancellationToken);

        typeof(SubathonEvents)
            .GetField("SubathonEventCreated", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);

        byte[] body = BuildKoFiBody("any-token", "Donation", "Test User", "5.00", "USD", Guid.NewGuid().ToString());

        SubathonEvent? captured = null;
        void EventHandler(SubathonEvent e) => captured = e;
        SubathonEvents.SubathonEventCreated += EventHandler;
        await service.HandleWebhookAsync(body, DefaultHeaders, TestContext.Current.CancellationToken);
        SubathonEvents.SubathonEventCreated -= EventHandler;

        Assert.Null(captured);
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task HandleWebhookAsync_WrongToken_DoesNotRaiseEvent()
    {
        (KoFiService? service, DevTunnelsService _) = MakeService(new Dictionary<(string, string), string>
        {
            { ("KoFi", "VerificationToken"), "correct-token" },
        });
        await service.StartAsync(TestContext.Current.CancellationToken);

        typeof(SubathonEvents)
            .GetField("SubathonEventCreated", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);

        byte[] body = BuildKoFiBody("wrong-token", "Donation", "Test User", "5.00", "USD", Guid.NewGuid().ToString());

        SubathonEvent? captured = null;
        void EventHandler(SubathonEvent e) => captured = e;
        SubathonEvents.SubathonEventCreated += EventHandler;
        await service.HandleWebhookAsync(body, DefaultHeaders, TestContext.Current.CancellationToken);
        SubathonEvents.SubathonEventCreated -= EventHandler;

        Assert.Null(captured);
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task HandleWebhookAsync_ValidDonation_RaisesKoFiDonationEvent()
    {
        const string token = "test-token";
        (KoFiService? service, DevTunnelsService _) = MakeService(new Dictionary<(string, string), string>
        {
            { ("KoFi", "VerificationToken"), token },
        });
        await service.StartAsync(TestContext.Current.CancellationToken);

        typeof(SubathonEvents)
            .GetField("SubathonEventCreated", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);

        byte[] body = BuildKoFiBody(token, "Donation", "Wolf", "10.00", "USD", Guid.NewGuid().ToString());

        SubathonEvent? captured = null;
        void EventHandler(SubathonEvent e) => captured = e;
        SubathonEvents.SubathonEventCreated += EventHandler;
        await service.HandleWebhookAsync(body, DefaultHeaders, TestContext.Current.CancellationToken);
        SubathonEvents.SubathonEventCreated -= EventHandler;

        Assert.NotNull(captured);
        Assert.Equal(SubathonEventType.KoFiDonation, captured.EventType);
        Assert.Equal(SubathonEventSource.KoFiWebhook, captured.Source);
        Assert.Equal("Wolf", captured.User);
        Assert.Equal("10.00", captured.Value);
        Assert.Equal("USD", captured.Currency);

        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(true, "Gold Tier")]
    [InlineData(false, "Silver")]
    public async Task HandleWebhookAsync_ValidSubscription_RaisesKoFiSubEvent(
        bool isFirst, string tierName)
    {
        const string token = "test-token";
        (KoFiService? service, DevTunnelsService _) = MakeService(new Dictionary<(string, string), string>
        {
            { ("KoFi", "VerificationToken"), token },
        });
        await service.StartAsync(TestContext.Current.CancellationToken);

        typeof(SubathonEvents)
            .GetField("SubathonEventCreated", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);

        byte[] body = BuildKoFiBody(token, "Subscription", "Supporter", "5.00", "USD",
            Guid.NewGuid().ToString(),
            isSubscriptionPayment: true,
            isFirstSubscriptionPayment: isFirst,
            tierName: tierName);

        SubathonEvent? captured = null;
        void EventHandler(SubathonEvent e) => captured = e;
        SubathonEvents.SubathonEventCreated += EventHandler;
        await service.HandleWebhookAsync(body, DefaultHeaders, TestContext.Current.CancellationToken);
        SubathonEvents.SubathonEventCreated -= EventHandler;

        Assert.NotNull(captured);
        Assert.Equal(SubathonEventType.KoFiSub, captured.EventType);
        Assert.Equal(SubathonEventSource.KoFiWebhook, captured.Source);
        Assert.Equal("Supporter", captured.User);
        Assert.Equal(tierName, captured.Value);
        Assert.Equal("member", captured.Currency);

        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task HandleWebhookAsync_ValidShopOrder_RaisesKoFiShopOrderEvent()
    {
        const string token = "test-token";
        (KoFiService? service, DevTunnelsService _) = MakeService(new Dictionary<(string, string), string>
        {
            { ("KoFi", "VerificationToken"), token },
        });
        await service.StartAsync(TestContext.Current.CancellationToken);

        typeof(SubathonEvents)
            .GetField("SubathonEventCreated", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);

        byte[] body = BuildKoFiBody(token, "Shop Order", "Buyer", "25.00", "USD", Guid.NewGuid().ToString());

        SubathonEvent? captured = null;
        void EventHandler(SubathonEvent e) => captured = e;
        SubathonEvents.SubathonEventCreated += EventHandler;
        await service.HandleWebhookAsync(body, DefaultHeaders, TestContext.Current.CancellationToken);
        SubathonEvents.SubathonEventCreated -= EventHandler;

        Assert.NotNull(captured);
        Assert.Equal(SubathonEventType.KoFiShopOrder, captured.EventType);
        Assert.Equal(SubathonEventSource.KoFiWebhook, captured.Source);
        Assert.Equal("Buyer", captured.User);
        Assert.Equal("25.00", captured.Value);
        Assert.Equal("USD", captured.Currency);

        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task HandleWebhookAsync_ValidCommission_RaisesKoFiCommissionEvent()
    {
        const string token = "test-token";
        (KoFiService? service, DevTunnelsService _) = MakeService(new Dictionary<(string, string), string>
        {
            { ("KoFi", "VerificationToken"), token },
        });
        await service.StartAsync(TestContext.Current.CancellationToken);

        typeof(SubathonEvents)
            .GetField("SubathonEventCreated", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);

        byte[] body = BuildKoFiBody(token, "Commission", "Artist", "50.00", "USD", Guid.NewGuid().ToString());

        SubathonEvent? captured = null;
        void EventHandler(SubathonEvent e) => captured = e;
        SubathonEvents.SubathonEventCreated += EventHandler;
        await service.HandleWebhookAsync(body, DefaultHeaders, TestContext.Current.CancellationToken);
        SubathonEvents.SubathonEventCreated -= EventHandler;

        Assert.NotNull(captured);
        Assert.Equal(SubathonEventType.KoFiCommissionOrder, captured.EventType);
        Assert.Equal(SubathonEventSource.KoFiWebhook, captured.Source);
        Assert.Equal("Artist", captured.User);
        Assert.Equal("50.00", captured.Value);
        Assert.Equal("USD", captured.Currency);

        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task HandleWebhookAsync_ForwardsToConfiguredUrl()
    {
        const string token = "test-token";

        await using MockWebServerHost mockServer = new MockWebServerHost().OnPost("/forward", "", statusCode: 200);

        var logger = new Mock<ILogger<KoFiService>>();
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
        _ = httpFactory.Setup(f => f.CreateClient(nameof(KoFiService))).Returns(new HttpClient());

        IConfig config = MockConfig.MakeMockConfig(new Dictionary<(string, string), string>
        {
            { ("KoFi", "VerificationToken"), token },
            { ("KoFi", "ForwardUrls"), mockServer.BaseUrl.TrimEnd('/') + "/forward" },
        });
        var service = new KoFiService(logger.Object, config, httpFactory.Object, devTunnels);

        await service.StartAsync(TestContext.Current.CancellationToken);

        byte[] body = BuildKoFiBody(token, "Donation", "Test", "5.00", "USD", Guid.NewGuid().ToString());
        await service.HandleWebhookAsync(body, DefaultHeaders, TestContext.Current.CancellationToken);

        Assert.Equal(1, mockServer.PostCallCount);

        await service.StopAsync(TestContext.Current.CancellationToken);
    }
}
