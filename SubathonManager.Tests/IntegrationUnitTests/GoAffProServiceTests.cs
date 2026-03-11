using System.Reflection;
using Microsoft.Extensions.Logging;
using Moq;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Models;
using SubathonManager.Integration;
using SubathonManager.Tests.Utility;

namespace SubathonManager.Tests.IntegrationUnitTests;

[Collection("IntegrationEventTests")]
public class GoAffProServiceTests
{
    private static SubathonEvent CaptureEvent(Action trigger)
    {
        SubathonEvent? captured = null;
        void EventCaptureHandler(SubathonEvent e) => captured = e;

        SubathonEvents.SubathonEventCreated += EventCaptureHandler;
        try
        {
            trigger();
            return captured!;
        }
        finally
        {
            SubathonEvents.SubathonEventCreated -= EventCaptureHandler;
        }
    }
    
    private static async Task<(bool?, SubathonEventSource, string, string)> CaptureIntegrationEvent(Func<Task> trigger)
    {

        bool? status = null;
        SubathonEventSource source = SubathonEventSource.Unknown;
        string name = string.Empty;
        string service = string.Empty;
        
        void EventCaptureHandler(bool b, SubathonEventSource s, string n, string se)
        {
            status = b;
            source = s;
            name = n;
            service = se;
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
    [InlineData(GoAffProSource.UwUMarket, 10.99, 2.99, 1, 
        GoAffProModes.Dollar, "USD", "2.99|USD", "10.99", SubathonEventType.UwUMarketOrder)]
    [InlineData(GoAffProSource.GamerSupps, 20.99, 6.99, 3, 
        GoAffProModes.Item, "items", "6.99|USD", "3", SubathonEventType.GamerSuppsOrder)]
    [InlineData(GoAffProSource.GamerSupps, 20.99, 6.99, 3, 
        GoAffProModes.Order, "order", "6.99|USD", "New", SubathonEventType.GamerSuppsOrder)]
    public void SimulateOrder_RaisesEvent(GoAffProSource store, decimal total, decimal commission, int quantity, 
        GoAffProModes type, string expectedCurrency, string expectedSecondaryValue, string expectedValue, SubathonEventType expectedEventType)
    {
        var logger = new Mock<ILogger<GoAffProService>>();
        Dictionary<(string, string), string> values = new()
        {
            { ("GoAffPro", $"{store}.Enabled"), "True" },
            { ("GoAffPro", $"{store}.Mode"), $"{type}" },
            { ("GoAffPro", $"{store}.CommissionAsDonation"), "true" },
        };

        GoAffProService service = new GoAffProService(logger.Object, MockConfig.MakeMockConfig(values));
        
        typeof(SubathonEvents)
            .GetField("SubathonEventCreated", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
        var ev = CaptureEvent(() => service.SimulateOrder(total, quantity, commission, store));
        

        Assert.Equal(expectedEventType, ev.EventType);
        Assert.Equal(SubathonEventSubType.OrderLike, ev.EventType.GetSubType());
        Assert.Equal(expectedCurrency, ev.Currency);
        Assert.Equal(expectedValue, ev.Value);
        Assert.Equal(expectedSecondaryValue, ev.SecondaryValue);
        Assert.Equal($"SYSTEM {store}", ev.User);
    }
    
        
    [Theory]
    [InlineData(GoAffProSource.Unknown, 10.99, 2.99, 1, 
        GoAffProModes.Dollar,  "True")]
    [InlineData(GoAffProSource.UwUMarket, 10.99, 2.99, 1, 
        GoAffProModes.Dollar, "False")]
    [InlineData(GoAffProSource.GamerSupps, 10.99, 2.99, 1, 
        GoAffProModes.Item, "False")]
    public void SimulateOrder_BadPath_DoesNotRaiseEvent(GoAffProSource store, decimal total, decimal commission, int quantity, 
        GoAffProModes type, string enabledString)
    {
        var logger = new Mock<ILogger<GoAffProService>>();
        Dictionary<(string, string), string> values = new()
        {
            { ("GoAffPro", $"{store}.Enabled"), enabledString},
            { ("GoAffPro", $"{store}.Mode"), $"{type}" },
            { ("GoAffPro", $"{store}.CommissionAsDonation"), "true" },
        };

        GoAffProService service = new GoAffProService(logger.Object, MockConfig.MakeMockConfig(values));
        
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
            { ("GoAffPro", "UwUMarket.CommissionAsDonation"), "true" },
            { ("GoAffPro", "Email"), email},
            { ("GoAffPro", "Password"), password},
        };
        var service = new GoAffProService(logger.Object, MockConfig.MakeMockConfig(values));
        
        typeof(IntegrationEvents)
            .GetField("RaiseConnectionUpdate", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
        bool? status = null; 
        SubathonEventSource? source;
        string? name;
        string? serviceName;
        (status, source, name, serviceName) = await CaptureIntegrationEvent(  () => service.StartAsync());
        
        // no true should ever raise, but falses for all of the others
        Assert.NotNull(status);
        Assert.False(status);
        
        await service.StopAsync();
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
            { ("GoAffPro", "Email"), email},
            { ("GoAffPro", "Password"), password},
        };
        var service = new GoAffProService(logger.Object, MockConfig.MakeMockConfig(values));
        service.Endpoint = new Uri(webserver.BaseUrl);
        
        typeof(IntegrationEvents)
            .GetField("RaiseConnectionUpdate", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
        
        
        bool? status = null; 
        SubathonEventSource? source;
        string? name;
        string? serviceName;
        
        (status, source, name, serviceName) = await CaptureIntegrationEvent( () => service.StartAsync());
        
        Assert.NotNull(status);
        Assert.True(status);
        await service.StopAsync();
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
            { ("GoAffPro", "UwUMarket.CommissionAsDonation"), "true" },
            { ("GoAffPro", "Email"), "test@example.com"},
            { ("GoAffPro", "Password"),  "p4$$w0rd"},
        };
        var service = new GoAffProService(logger.Object, MockConfig.MakeMockConfig(values));
        service.Endpoint = new Uri(webserver.BaseUrl);
        
        typeof(IntegrationEvents)
            .GetField("RaiseConnectionUpdate", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
        
        
        bool? status = null; 
        SubathonEventSource? source;
        string? name;
        string? serviceName;
        
        (status, source, name, serviceName) = await CaptureIntegrationEvent( () => service.StartAsync());
        
        Assert.NotNull(status);
        Assert.False(status);
        await service.StopAsync();
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
            { ("GoAffPro", "Email"), "test@example.com"},
            { ("GoAffPro", "Password"),  "p4$$w0rd"},
        };
        var service = new GoAffProService(logger.Object, MockConfig.MakeMockConfig(values));
        service.MaxRetries = 1;
        service.Endpoint = new Uri(webserver.BaseUrl);
        
        typeof(IntegrationEvents)
            .GetField("RaiseConnectionUpdate", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
        
        
        bool? status = null; 
        SubathonEventSource? source;
        string? name;
        string? serviceName;
        
        (status, source, name, serviceName) = await CaptureIntegrationEvent( () => service.StartAsync());
        
        Assert.NotNull(status);
        Assert.False(status);
        await service.StopAsync();
        Assert.Equal(1, webserver.PostCallCount);
        Assert.Equal(2, webserver.GetCallCount);
    }
}