using System.Reflection;
using Microsoft.Extensions.Logging;
using Moq;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;
using SubathonManager.Core.Security;
using SubathonManager.Integration;
using SubathonManager.Tests.Utility;

namespace SubathonManager.Tests.IntegrationUnitTests;

[Collection("SharedEventBusTests")]
public class GoAffProServiceTests
{
    private static readonly GoAffProStore GamerSupps = new()
        { SiteId = 165328, StoreName = "GamerSupps", EventName = "GamerSupps Order" };
    private static readonly GoAffProStore UwUMarket = new()
        { SiteId = 132230, StoreName = "UwUMarket", EventName = "UwUMarket Order" };

    static GoAffProServiceTests()
    {
        GoAffProStoreRegistry.Register(GamerSupps);
        GoAffProStoreRegistry.Register(UwUMarket);
    }

    private static SubathonEvent? CaptureEvent(Action trigger) =>
        EventUtil.SubathonEventCapture.CaptureRequired(trigger);
    
    private static async Task<(bool?, SubathonEventSource, string, string)> CaptureIntegrationEvent(Func<Task> trigger)
    {
        
        bool? status = null;
        SubathonEventSource source = SubathonEventSource.Unknown;
        string name = string.Empty;
        string service = string.Empty;
        
        void EventCaptureHandler(IntegrationConnection conn)
        {
            status = conn.Status;
            source = conn.Source;
            name = conn.Name;
            service = conn.Service;
        }

        IntegrationEvents.ConnectionUpdated += EventCaptureHandler;
        try
        {
            await trigger();
            return (status, source, name, service);
        }
        finally
        {
            IntegrationEvents.ConnectionUpdated -= EventCaptureHandler;
        }
    }
    
    
    [Theory]
    [InlineData(132230, 10.99, 2.99, 1,
        OrderTypeModes.Dollar, "USD", "2.99|USD", "10.99")]
    [InlineData(165328, 20.99, 6.99, 3,
        OrderTypeModes.Item, "items", "6.99|USD", "3")]
    [InlineData(165328, 20.99, 6.99, 3,
        OrderTypeModes.Order, "order", "6.99|USD", "New")]
    public void SimulateOrder_RaisesEvent(int siteId, decimal total, decimal commission, int quantity,
        OrderTypeModes type, string expectedCurrency, string expectedSecondaryValue, string expectedValue)
    {
        Assert.True(GoAffProStoreRegistry.TryGetBySiteId(siteId, out var store));
        var logger = new Mock<ILogger<GoAffProService>>();
        Dictionary<(string, string), string> values = new()
        {
            { ("GoAffPro", $"{store.InternalName}.Enabled"), "True" },
            { ("GoAffPro", $"{store.InternalName}.Mode"), $"{type}" },
            { ("GoAffPro", $"{store.InternalName}.CommissionAsDonation"), "true" },
        };

        var storage = new InMemorySecureStorage(new()
        {
            [StorageKeys.GoAffProEmail] = "",
            [StorageKeys.GoAffProPassword] = "",
        });
        GoAffProService service = new GoAffProService(logger.Object, MockConfig.MakeMockConfig(values), storage);

        typeof(SubathonEvents)
            .GetField("SubathonEventCreated", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
        var ev = CaptureEvent(() => service.SimulateOrder(total, quantity, commission, store));

        Assert.NotNull(ev);
        Assert.Equal(SubathonEventType.GoAffProOrder, ev.EventType);
        Assert.Equal(siteId.ToString(), ev.EventTypeMeta);
        Assert.Equal(SubathonEventSubType.OrderLike, ev.EventType.GetSubType());
        Assert.Equal(expectedCurrency, ev.Currency);
        Assert.Equal(expectedValue, ev.Value);
        Assert.Equal(expectedSecondaryValue, ev.SecondaryValue);
        Assert.Equal($"SYSTEM {store.InternalName}", ev.User);
    }


    [Theory]
    [InlineData(null, 10.99, 2.99, 1,
        OrderTypeModes.Dollar,  "True")] // unknown store
    [InlineData(132230, 10.99, 2.99, 1,
        OrderTypeModes.Dollar, "False")]
    [InlineData(165328, 10.99, 2.99, 1,
        OrderTypeModes.Item, "False")]
    public void SimulateOrder_BadPath_DoesNotRaiseEvent(int? siteId, decimal total, decimal commission, int quantity,
        OrderTypeModes type, string enabledString)
    {
        GoAffProStore? store = null;
        if (siteId.HasValue)
            Assert.True(GoAffProStoreRegistry.TryGetBySiteId(siteId.Value, out store));
        var storeKey = store?.InternalName ?? "Unknown";
        var logger = new Mock<ILogger<GoAffProService>>();
        Dictionary<(string, string), string> values = new()
        {
            { ("GoAffPro", $"{storeKey}.Enabled"), enabledString},
            { ("GoAffPro", $"{storeKey}.Mode"), $"{type}" },
            { ("GoAffPro", $"{storeKey}.CommissionAsDonation"), "true" },
        };

        var storage = new InMemorySecureStorage(new()
        {
            [StorageKeys.GoAffProEmail] = "",
            [StorageKeys.GoAffProPassword] = "",
        });
        GoAffProService service = new GoAffProService(logger.Object, MockConfig.MakeMockConfig(values), storage);

        typeof(SubathonEvents)
            .GetField("SubathonEventCreated", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
        var ev = CaptureEvent(() => service.SimulateOrder(total, quantity, commission, store));

        Assert.Null(ev);
    }
    
    [Theory]
    [InlineData("", "Pass")]
    [InlineData("", "")]
    [InlineData("Email@example.com", "")]
    public async Task StartClient_ShouldNotRun_EmptyCreds(string email, string password)
    {
        var logger = new Mock<ILogger<GoAffProService>>();
        Dictionary<(string, string), string> values = new()
        {
            { ("GoAffPro", "UwUMarket.Enabled"), "True"},
            { ("GoAffPro", "UwUMarket.Mode"), "Dollar" },
            { ("GoAffPro", "UwUMarket.CommissionAsDonation"), "true" }
        };
        var storage = new InMemorySecureStorage(new()
        {
            [StorageKeys.GoAffProEmail] = email,
            [StorageKeys.GoAffProPassword] = password,
        });
        var service = new GoAffProService(logger.Object, MockConfig.MakeMockConfig(values), storage);
        
        typeof(IntegrationEvents)
            .GetField("RaiseConnectionUpdate", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
        bool? status = null;
        (status, _, _, _) = await CaptureIntegrationEvent(  () => service.StartAsync());
        
        // no true should ever raise, but falses for all of the others
        Assert.NotNull(status);
        Assert.False(status);
        
        await service.StopAsync(TestContext.Current.CancellationToken);
    }
    
    [Theory]
    [InlineData("test@example.com", "p4$$w0rd")]
    public async Task StartClient_Test(string email, string password)
    {
        await using var webserver = new MockWebServerHost().OnPost("/user/login", """
                                                                            {
                                                                              "access_token": "my_access_token"
                                                                            }
                                                                            """, statusCode: 201)
            .OnGet("/user/sites", """
                                  {
                                    "sites": [
                                      {
                                        "id": 132230,
                                        "name": "UwU Market",
                                        "logo": "https://creatives.goaffpro.com/132230/files/NDpAegaysoPA.png",
                                        "website": "https://uwumarket.us/",
                                        "status": "approved",
                                        "currency": "USD",
                                        "affiliate_portal": "https://uwumarket.goaffpro.com",
                                        "ref_code": "aiarfepl",
                                        "referral_link": "https://uwumarket.us/?ref=aiarfepl",
                                        "coupon": null
                                      },
                                      {
                                        "id": 165328,
                                        "name": "GamerSupps.GG",
                                        "logo": "https://creatives.goaffpro.com/165328/files/uHUzvTlLJX7n.png",
                                        "website": "https://gamersupps.gg/",
                                        "status": "approved",
                                        "currency": "USD",
                                        "affiliate_portal": "https://gamersupps.goaffpro.com",
                                        "ref_code": "BUGS",
                                        "referral_link": "https://gamersupps.gg/?ref=BUGS",
                                        "coupon": {
                                          "code": "BUGS",
                                          "discount_value": "",
                                          "discount_type": "percentage",
                                          "can_change": false
                                        }
                                      }
                                    ],
                                    "count": 2
                                  }
                                  """)
            .OnGet("/user/feed/orders", """
                                        {
                                            "orders": []
                                        }
            """);
        
        var logger = new Mock<ILogger<GoAffProService>>();
        Dictionary<(string, string), string> values = new()
        {
            { ("GoAffPro", "UwUMarket.Enabled"), "True"},
            { ("GoAffPro", "GamerSupps.Enabled"), "False"},
            { ("GoAffPro", "UwUMarket.Mode"), "Dollar" },
            { ("GoAffPro", "UwUMarket.CommissionAsDonation"), "true" },
        };
        var storage = new InMemorySecureStorage(new()
        {
            [StorageKeys.GoAffProEmail] = email,
            [StorageKeys.GoAffProPassword] = password,
        });
        var service = new GoAffProService(logger.Object, MockConfig.MakeMockConfig(values), storage);
        service.Endpoint = new Uri(webserver.BaseUrl);
        
        typeof(IntegrationEvents)
            .GetField("RaiseConnectionUpdate", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
        
        
        bool? status = null;

        (status, _, _, _) = await CaptureIntegrationEvent( () => service.StartAsync());
        
        Assert.NotNull(status);
        Assert.True(status);
        await service.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, webserver.PostCallCount);
        Assert.Equal(1, webserver.GetCallCount);
    }
    
    [Fact]
    public async Task StartClient_Test_ErrorAtLogin()
    {
        await using var webserver = new MockWebServerHost().OnPost("/user/login", "You suck", statusCode: 404);
        
        var logger = new Mock<ILogger<GoAffProService>>();
        Dictionary<(string, string), string> values = new()
        {
            { ("GoAffPro", "UwUMarket.Enabled"), "True"},
            { ("GoAffPro", "GamerSupps.Enabled"), "False"},
            { ("GoAffPro", "UwUMarket.Mode"), "Dollar" },
            { ("GoAffPro", "UwUMarket.CommissionAsDonation"), "true" }
        };
        
        var storage = new InMemorySecureStorage(new()
        {
            [StorageKeys.GoAffProEmail] = "test@example.com",
            [StorageKeys.GoAffProPassword] = "p4$$w0rd",
        });
        var service = new GoAffProService(logger.Object, MockConfig.MakeMockConfig(values), storage);
        service.Endpoint = new Uri(webserver.BaseUrl);
        
        typeof(IntegrationEvents)
            .GetField("RaiseConnectionUpdate", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
        
        
        bool? status = null;

        (status, _, _, _) = await CaptureIntegrationEvent( () => service.StartAsync());
        
        Assert.NotNull(status);
        Assert.False(status);
        await service.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, webserver.PostCallCount);
    }
    
    [Fact]
    public async Task ErrorFromSitesEndpoint_Test()
    {
        await using var webserver = new MockWebServerHost().OnPost("/user/login", """
                {
                  "access_token": "my_access_token"
                }
                """, statusCode: 201)
            .OnGet("/user/sites", "Failure", 500);
        
        var logger = new Mock<ILogger<GoAffProService>>();
        Dictionary<(string, string), string> values = new()
        {
            { ("GoAffPro", "UwUMarket.Enabled"), "True"},
            { ("GoAffPro", "GamerSupps.Enabled"), "False"},
            { ("GoAffPro", "UwUMarket.Mode"), "Dollar" },
            { ("GoAffPro", "UwUMarket.CommissionAsDonation"), "true" },
        };
        
        var storage = new InMemorySecureStorage(new()
        {
            [StorageKeys.GoAffProEmail] = "test@example.com",
            [StorageKeys.GoAffProPassword] = "p4$$w0rd",
        });
        var service = new GoAffProService(logger.Object, MockConfig.MakeMockConfig(values), storage);
        service.MaxRetries = 1;
        service.Endpoint = new Uri(webserver.BaseUrl);
        
        typeof(IntegrationEvents)
            .GetField("RaiseConnectionUpdate", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
        
        
        bool? status = null;

        (status, _, _, _) = await CaptureIntegrationEvent( () => service.StartAsync());
        
        Assert.NotNull(status);
        Assert.False(status);
        await service.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, webserver.PostCallCount);
        Assert.Equal(2, webserver.GetCallCount);
    }
}