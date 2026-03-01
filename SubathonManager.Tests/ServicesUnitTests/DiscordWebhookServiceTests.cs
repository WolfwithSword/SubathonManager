using Moq;
using System.Collections.Concurrent;
using SubathonManager.Services;
using System.Reflection;
using SubathonManager.Core.Models;
using SubathonManager.Core.Enums;
using Microsoft.Extensions.Logging;
using System.Net;
using Moq.Protected;
using SubathonManager.Tests.Utility;

namespace SubathonManager.Tests.ServicesUnitTests;

[Collection("ServicesTests")]
public class DiscordWebhookServiceTests
{
    // TODO mock webserver for urls posting?
    
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

        var mockConfig = MockConfig.MakeMockConfig(new()
        {
            { ("Currency", "Primary"), "USD" },
            { ("Discord", "Events.WebhookUrl"), "https://eventUrl" },
            { ("Discord", "WebhookUrl"), "https://webhookUrl" },
            { ("Discord", "Events.Log.TwitchSub"), "false" },
            { ("Discord", "Events.Log.StreamElementsDonation"), "false" },
            { ("Discord", "Events.Log.Command"), "false" },
        });
        var currencyMock = new CurrencyService(loggerMock.Object, mockConfig, httpClient);
        currencyMock.SetRates(new Dictionary<string, double>
        {
            { "USD", 1.0 }, { "GBP", 0.9 }, { "CAD", 0.8 }, { "TWD", 0.6 }, { "AUD", 0.5 }
        });
        return currencyMock;
    }
    
    [Fact]
    public async Task LoadFromConfig_SetsPropertiesCorrectly()
    {     
        var mockConfig = MockConfig.MakeMockConfig(new()
        {
            { ("Currency", "Primary"), "USD" },
            { ("Discord", "Events.WebhookUrl"), "https://eventUrl" },
            { ("Discord", "WebhookUrl"), "https://webhookUrl" },
            { ("Discord", "Events.Log.TwitchSub"), "false" },
            { ("Discord", "Events.Log.StreamElementsDonation"), "false" },
            { ("Discord", "Events.Log.Command"), "false" },
        });

        var service = new DiscordWebhookService(null, mockConfig, SetupCurrencyService());
        await service.StartAsync();
        var field = typeof(DiscordWebhookService).GetField("_eventWebhookUrl", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.Equal("https://eventUrl", field!.GetValue(service));
        await service.StopAsync();
    }
    
    [Fact]
    public void OnSubathonEventProcessed_QueuesEventsBasedOnAuditTypes()
    {
        
        var mockConfig = MockConfig.MakeMockConfig(new()
        {
            { ("Currency", "Primary"), "USD" },
            { ("Discord", "Events.WebhookUrl"), "https://eventUrl" },
            { ("Discord", "WebhookUrl"), "https://webhookUrl" },
            { ("Discord", "Events.Log.TwitchSub"), "true" },
            { ("Discord", "Events.Log.StreamElementsDonation"), "true" },
            { ("Discord", "Events.Log.Command"), "true" },
        });
    
        var service = new DiscordWebhookService(null, mockConfig, SetupCurrencyService());
        service.LoadFromConfig();

        var subEvent = new SubathonEvent { EventType = SubathonEventType.Command, Source = SubathonEventSource.Twitch };
    
        typeof(DiscordWebhookService)
            .GetMethod("OnSubathonEventProcessed", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(service, new object?[] { subEvent, true });

        var queueField = typeof(DiscordWebhookService).GetField("_eventQueue", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var queue = (ConcurrentQueue<SubathonEvent>)queueField.GetValue(service)!;

        Assert.Single(queue);
    }

    [Fact]
    public void OnSubathonEventProcessed_DoesNotQueueIgnoredEvents()
    {
        var mockConfig = MockConfig.MakeMockConfig(new()
        {
            { ("Currency", "Primary"), "USD" },
            { ("Discord", "Events.WebhookUrl"), "https://eventUrl" },
            { ("Discord", "WebhookUrl"), "https://webhookUrl" },
            { ("Discord", "Events.Log.TwitchSub"), "true" },
            { ("Discord", "Events.Log.StreamElementsDonation"), "true" },
            { ("Discord", "Events.Log.Command"), "true" },
        });
        var service = new DiscordWebhookService(null, mockConfig, SetupCurrencyService());

        var subEvent = new SubathonEvent 
        { 
            EventType = SubathonEventType.TwitchGiftSub, 
            Source = SubathonEventSource.Simulated
        };

        typeof(DiscordWebhookService)
            .GetMethod("OnSubathonEventProcessed", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(service, new object?[] { subEvent, true });

        var queueField = typeof(DiscordWebhookService)
            .GetField("_eventQueue", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var queue = (ConcurrentQueue<SubathonEvent>)queueField.GetValue(service)!;

        Assert.Empty(queue);
    }

    [Fact]
    public void BuildEventDescription_ReturnsCorrectString()
    {
        var mockConfig = MockConfig.MakeMockConfig(new()
        {
            { ("Currency", "Primary"), "USD" },
            { ("Discord", "Events.WebhookUrl"), "https://eventUrl" },
            { ("Discord", "WebhookUrl"), "https://webhookUrl" },
            { ("Discord", "Events.Log.TwitchSub"), "true" },
            { ("Discord", "Events.Log.StreamElementsDonation"), "true" },
            { ("Discord", "Events.Log.Command"), "true" },
        });
        var service = new DiscordWebhookService(null, mockConfig, SetupCurrencyService());

        var subEvent = new SubathonEvent
        {
            User = "TestUser",
            Value = "100",
            Currency = "USD",
            Source= SubathonEventSource.StreamElements,
            EventType = SubathonEventType.StreamElementsDonation,
            Command = SubathonCommandType.None,
            MultiplierPoints = 2,
            MultiplierSeconds = 3,
            CurrentPoints = 50,
            CurrentTime = 120
        };

        var method = typeof(DiscordWebhookService)
            .GetMethod("BuildEventDescription", BindingFlags.NonPublic | BindingFlags.Instance)!;

        string result = (string)method.Invoke(service, new object?[] { subEvent })!;
        Assert.Contains("**User:** TestUser", result);
        Assert.Contains("**Value:** 100 USD", result);
    }

    [Fact]
    public void SendErrorEvent_NoWebhook_DoesNotThrow()
    {
        var mockConfig = MockConfig.MakeMockConfig(new()
        {
            { ("Currency", "Primary"), "USD" },
            { ("Discord", "Events.WebhookUrl"), "" },
            { ("Discord", "WebhookUrl"), "" },
            { ("Discord", "Events.Log.TwitchSub"), "true" },
            { ("Discord", "Events.Log.StreamElementsDonation"), "true" },
            { ("Discord", "Events.Log.Command"), "true" },
        });
        
        var service = new DiscordWebhookService(null, mockConfig, SetupCurrencyService());

        Exception? ex = Record.Exception(() => service.SendErrorEvent("ERROR", 
            "TestSource", "Test message", DateTime.UtcNow));
        Assert.Null(ex);
    }
    
    [Fact]
    public async Task SendErrorEvent_MockServer()
    {
        
        await using var webserver = new MockWebServerHost().OnPost("/mywebhook", "", statusCode: 201);
        
        var mockConfig = MockConfig.MakeMockConfig(new()
        {
            { ("Currency", "Primary"), "USD" },
            { ("Discord", "Events.WebhookUrl"), webserver.BaseUrl + "mywebhook" },
            { ("Discord", "WebhookUrl"), webserver.BaseUrl + "mywebhook" },
            { ("Discord", "Events.Log.TwitchSub"), "true" },
            { ("Discord", "Events.Log.StreamElementsDonation"), "true" },
            { ("Discord", "Events.Log.Command"), "true" },
        });
        
        var service = new DiscordWebhookService(null, mockConfig, SetupCurrencyService());

        Exception? ex = Record.Exception(() => service.SendErrorEvent("ERROR", 
            "TestSource", "Test message", DateTime.UtcNow));
        Assert.Null(ex);
        await Task.Delay(TimeSpan.FromSeconds(1)); //
        Assert.Equal(1, webserver.PostCallCount);
    }

    [Fact]
    public void Dispose_UnsubscribesEvents()
    {
        var mockConfig = MockConfig.MakeMockConfig(new()
        {
            { ("Currency", "Primary"), "USD" },
            { ("Discord", "Events.WebhookUrl"), "https://eventUrl" },
            { ("Discord", "WebhookUrl"), "https://webhookUrl" },
            { ("Discord", "Events.Log.TwitchSub"), "true" },
            { ("Discord", "Events.Log.StreamElementsDonation"), "true" },
            { ("Discord", "Events.Log.Command"), "true" },
        });
        var service = new DiscordWebhookService(null, mockConfig, SetupCurrencyService());

        Exception? ex = Record.Exception(() => service.Dispose());
        Assert.Null(ex);
    }
    
    [Fact]
    public void OnSubathonEventDeleted_QueuesSingleDeletedEvent()
    {
        var mockConfig = MockConfig.MakeMockConfig(new()
        {
            { ("Currency", "Primary"), "USD" },
            { ("Discord", "Events.WebhookUrl"), "https://eventUrl" },
            { ("Discord", "WebhookUrl"), "https://webhookUrl" },
            { ("Discord", "Events.Log.TwitchSub"), "true" },
            { ("Discord", "Events.Log.StreamElementsDonation"), "true" },
            { ("Discord", "Events.Log.Command"), "true" },
        });
        var service = new DiscordWebhookService(null, mockConfig, SetupCurrencyService());

        var subEvent = new SubathonEvent
        {
            EventType = SubathonEventType.TwitchSub,
            Source = SubathonEventSource.Twitch,
            Value = "1000",
            Amount = 1
        };

        typeof(DiscordWebhookService)
            .GetMethod("OnSubathonEventDeleted", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(service, new object?[] { new List<SubathonEvent> { subEvent } });

        var queueField = typeof(DiscordWebhookService)
            .GetField("_eventQueue", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var queue = (ConcurrentQueue<SubathonEvent>)queueField.GetValue(service)!;

        Assert.Single(queue);
        queue.TryDequeue(out var queuedEvent);
        Assert.Contains("[DELETED]", queuedEvent!.Value);
    }

    [Fact]
    public void OnCustomEvent_DoesNotThrow()
    {
        var mockConfig = MockConfig.MakeMockConfig(new()
        {
            { ("Currency", "Primary"), "USD" },
            { ("Discord", "Events.WebhookUrl"), "https://eventUrl" },
            { ("Discord", "WebhookUrl"), "https://webhookUrl" },
            { ("Discord", "Events.Log.TwitchSub"), "true" },
            { ("Discord", "Events.Log.StreamElementsDonation"), "true" },
            { ("Discord", "Events.Log.Command"), "true" },
        });
        var service = new DiscordWebhookService(null, mockConfig, SetupCurrencyService());

        var method = typeof(DiscordWebhookService)
            .GetMethod("OnCustomEvent", BindingFlags.NonPublic | BindingFlags.Instance)!;

        Exception? ex = Record.Exception(() => method.Invoke(service, new object?[] { "Test message" }));
        Assert.Null(ex);
    }

    [Fact]
    public void BuildErrorDescription_ReturnsCorrectStringWithException()
    {
        var mockConfig = MockConfig.MakeMockConfig(new()
        {
            { ("Currency", "Primary"), "USD" },
            { ("Discord", "Events.WebhookUrl"), "https://eventUrl" },
            { ("Discord", "WebhookUrl"), "https://webhookUrl" },
            { ("Discord", "Events.Log.TwitchSub"), "true" },
            { ("Discord", "Events.Log.StreamElementsDonation"), "true" },
            { ("Discord", "Events.Log.Command"), "true" },
        });
        var service = new DiscordWebhookService(null, mockConfig, SetupCurrencyService());

        var ex = new InvalidOperationException("Test exception");

        var method = typeof(DiscordWebhookService)
            .GetMethod("BuildErrorDescription", BindingFlags.NonPublic | BindingFlags.Instance)!;

        string result = (string)method.Invoke(service, new object?[] { "Test message", ex })!;

        Assert.Contains("**Message:** Test message", result);
        Assert.Contains("**Exception:** `InvalidOperationException`", result);
        Assert.Contains("**Details:** Test exception", result);
    }

    [Fact]
    public void BuildErrorDescription_NoException_FormatsMessage()
    {
        var mockConfig = MockConfig.MakeMockConfig(new()
        {
            { ("Currency", "Primary"), "USD" },
            { ("Discord", "Events.WebhookUrl"), "https://eventUrl" },
            { ("Discord", "WebhookUrl"), "https://webhookUrl" },
            { ("Discord", "Events.Log.TwitchSub"), "true" },
            { ("Discord", "Events.Log.StreamElementsDonation"), "true" },
            { ("Discord", "Events.Log.Command"), "true" },
        });
        var service = new DiscordWebhookService(null, mockConfig, SetupCurrencyService());

        var method = typeof(DiscordWebhookService)
            .GetMethod("BuildErrorDescription", BindingFlags.NonPublic | BindingFlags.Instance)!;

        string result = (string)method.Invoke(service, new object?[] { "Test message", null })!;

        Assert.Contains("**Message:** Test message", result);
        Assert.Contains("_No exception details provided._", result);
    }

}