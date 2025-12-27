using Moq;
using System.Collections.Concurrent;
using SubathonManager.Services;
using System.Reflection;
using SubathonManager.Core.Models;
using SubathonManager.Core.Enums;
using Microsoft.Extensions.Logging;
using System.Net;
using Moq.Protected;
namespace SubathonManager.Tests.ServicesUnitTests;

[Collection("ServicesTests")]
public class DiscordWebhookServiceTests
{
    private static IConfig MockConfig(Dictionary<(string, string), string>? values = null)
    {
        var mock = new Mock<IConfig>();
        
        mock.Setup(c => c.Get("Discord", "Events.WebhookUrl", It.IsAny<string>()))
            .Returns("https://eventUrl");
        mock.Setup(c => c.Get("Discord", "WebhookUrl", It.IsAny<string>()))
            .Returns("https://webhookUrl");

        mock.Setup(c => c.Get(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string s, string k, string d) => d);
 
        mock.Setup(c => c.Get("Discord", "Events.WebhookUrl", ""))
            .Returns("https://eventUrl");
        mock.Setup(c => c.Get("Discord", "WebhookUrl", ""))
            .Returns("https://webhookUrl");
        mock.Setup(c => c.Get("Discord", "Events.Log.TwitchSub", "false"))
            .Returns("true");
        mock.Setup(c => c.Get("Discord", "Events.Log.StreamElementsDonation", "false"))
            .Returns("true");
        mock.Setup(c => c.Get("Discord", "Events.Log.Command", "false"))
            .Returns("true");

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
    
    [Fact]
    public void LoadFromConfig_SetsPropertiesCorrectly()
    {
        var mockConfig = MockConfig();

        var service = new DiscordWebhookService(null, mockConfig, SetupCurrencyService());
        var field = typeof(DiscordWebhookService).GetField("_eventWebhookUrl", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.Equal("https://eventUrl", field!.GetValue(service));
    }
    
    [Fact]
    public void OnSubathonEventProcessed_QueuesEventsBasedOnAuditTypes()
    {
        var mockConfig = new Mock<IConfig>();
        mockConfig.Setup(c => c.Get(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).Returns("true");
    
        var service = new DiscordWebhookService(null, mockConfig.Object, SetupCurrencyService());
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
        var mockConfig = MockConfig();
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
        var mockConfig = MockConfig();
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
        var mockConfig = new Mock<IConfig>();
        mockConfig.Setup(c => c.Get(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(""); 

        var service = new DiscordWebhookService(null, mockConfig.Object, SetupCurrencyService());

        Exception? ex = Record.Exception(() => service.SendErrorEvent("ERROR", 
            "TestSource", "Test message", DateTime.UtcNow));
        Assert.Null(ex);
    }

    [Fact]
    public void Dispose_UnsubscribesEvents()
    {
        var mockConfig = MockConfig();
        var service = new DiscordWebhookService(null, mockConfig, SetupCurrencyService());

        Exception? ex = Record.Exception(() => service.Dispose());
        Assert.Null(ex);
    }
    
    [Fact]
    public void OnSubathonEventDeleted_QueuesSingleDeletedEvent()
    {
        var mockConfig = MockConfig();
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
        var mockConfig = MockConfig();
        var service = new DiscordWebhookService(null, mockConfig, SetupCurrencyService());

        var method = typeof(DiscordWebhookService)
            .GetMethod("OnCustomEvent", BindingFlags.NonPublic | BindingFlags.Instance)!;

        Exception? ex = Record.Exception(() => method.Invoke(service, new object?[] { "Test message" }));
        Assert.Null(ex);
    }

    [Fact]
    public void BuildErrorDescription_ReturnsCorrectStringWithException()
    {
        var mockConfig = MockConfig();
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
        var mockConfig = MockConfig();
        var service = new DiscordWebhookService(null, mockConfig, SetupCurrencyService());

        var method = typeof(DiscordWebhookService)
            .GetMethod("BuildErrorDescription", BindingFlags.NonPublic | BindingFlags.Instance)!;

        string result = (string)method.Invoke(service, new object?[] { "Test message", null })!;

        Assert.Contains("**Message:** Test message", result);
        Assert.Contains("_No exception details provided._", result);
    }

}