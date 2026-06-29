using System.Net;
using System.Reflection;
using System.Text;
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
public class TangiaServiceTests
{
    public TangiaServiceTests()
    {
        typeof(IntegrationEvents)
            .GetField("ConnectionUpdated", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
    }

    private static SubathonEvent? CaptureEvent(Action trigger) =>
        EventUtil.SubathonEventCapture.CaptureRequired(trigger);

    private static TangiaService MakeService(string? eventKey = null, IHttpClientFactory? httpFactory = null)
    {
        var logger = new Mock<ILogger<TangiaService>>();
        var storage = new InMemorySecureStorage(eventKey != null
            ? new Dictionary<string, string> { [StorageKeys.TangiaEventKey] = eventKey }
            : null);
        var factory = httpFactory ?? new Mock<IHttpClientFactory>().Object;
        return new TangiaService(logger.Object, factory, storage);
    }

    private static (IHttpClientFactory Factory, MockHttpHandler Handler) MakeHttpFactory(
        HttpStatusCode statusCode, string? body = null, bool throwTimeout = false)
    {
        var handler = new MockHttpHandler(statusCode, body, throwTimeout);
        var mock = new Mock<IHttpClientFactory>();
        mock.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(handler));
        return (mock.Object, handler);
    }

    private static async Task<SubathonEvent?> CaptureEventFromPoll(TangiaService service, string key)
    {
        typeof(SubathonEvents)
            .GetField("SubathonEventCreated", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);

        SubathonEvent? captured = null;
        void Handler(SubathonEvent e) => captured = e;
        SubathonEvents.SubathonEventCreated += Handler;
        try
        {
            await service.PollOnceAsync(key, CancellationToken.None);
            return captured;
        }
        finally
        {
            SubathonEvents.SubathonEventCreated -= Handler;
        }
    }

    [Theory]
    [InlineData("https://overlays.tangia.co/stream-overlay/fullscreen/evt_a1a11AaAAaaAaAaa1Aa1A1?tp=a", "evt_a1a11AaAAaaAaAaa1Aa1A1")]
    [InlineData("https://overlays.tangia.co/stream-overlay/fullscreen/evt_abc123", "evt_abc123")]
    [InlineData("https://overlays.tangia.co/stream-overlay/fullscreen/evt_xyz?foo=bar&baz=qux", "evt_xyz")]
    public void TryParseEventKey_ValidUrl_ExtractsKey(string url, string expectedKey)
    {
        var result = TangiaService.TryParseEventKey(url, out var key);

        Assert.True(result);
        Assert.Equal(expectedKey, key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParseEventKey_EmptyOrWhitespace_ReturnsFalse(string url)
    {
        var result = TangiaService.TryParseEventKey(url, out var key);

        Assert.False(result);
        Assert.Equal(string.Empty, key);
    }

    [Fact]
    public void TryParseEventKey_NotAUrl_ReturnsFalse()
    {
        var result = TangiaService.TryParseEventKey("not-a-valid-url", out var key);

        Assert.False(result);
        Assert.Equal(string.Empty, key);
    }

    [Theory]
    [InlineData("https://overlays.tangia.co/stream-overlay/fullscreen/not_an_evt")]
    [InlineData("https://overlays.tangia.co/stream-overlay/fullscreen/")]
    [InlineData("https://overlays.tangia.co/")]
    public void TryParseEventKey_NoEvtSegment_ReturnsFalse(string url)
    {
        var result = TangiaService.TryParseEventKey(url, out var key);

        Assert.False(result);
        Assert.Equal(string.Empty, key);
    }


    [Fact]
    public void SimulateTangiaTokens_RaisesEventWithCorrectFields()
    {
        var ev = CaptureEvent(() => TangiaService.SimulateTangiaTokens(250));

        Assert.NotNull(ev);
        Assert.Equal(SubathonEventType.TangiaTokens, ev.EventType);
        Assert.Equal(SubathonEventSource.Simulated, ev.Source);
        Assert.Equal("SYSTEM", ev.User);
        Assert.Equal("250", ev.Value);
        Assert.Equal("tokens", ev.Currency);
    }


    [Fact]
    public async Task StartAsync_NoKey_BroadcastsFalse()
    {
        var service = MakeService();

        bool? status = null;
        void Handler(IntegrationConnection c) { if (c.Source == SubathonEventSource.Tangia) status = c.Status; }
        IntegrationEvents.ConnectionUpdated += Handler;
        try { await service.StartAsync(TestContext.Current.CancellationToken); }
        finally { IntegrationEvents.ConnectionUpdated -= Handler; }

        Assert.False(status);
    }

    [Fact]
    public async Task StartAsync_EmptyKey_BroadcastsFalse()
    {
        var service = MakeService(eventKey: "");

        bool? status = null;
        void Handler(IntegrationConnection c) { if (c.Source == SubathonEventSource.Tangia) status = c.Status; }
        IntegrationEvents.ConnectionUpdated += Handler;
        try { await service.StartAsync(TestContext.Current.CancellationToken); }
        finally { IntegrationEvents.ConnectionUpdated -= Handler; }

        Assert.False(status);
    }

    [Fact]
    public async Task StartAsync_ValidKey_BroadcastsTrue()
    {
        var (factory, _) = MakeHttpFactory(HttpStatusCode.NoContent);
        var service = MakeService("evt_testkey", factory);

        bool? status = null;
        void Handler(IntegrationConnection c) { if (c.Source == SubathonEventSource.Tangia) status = c.Status; }
        IntegrationEvents.ConnectionUpdated += Handler;
        try { await service.StartAsync(TestContext.Current.CancellationToken); }
        finally { IntegrationEvents.ConnectionUpdated -= Handler; }

        Assert.True(status);
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StopAsync_BroadcastsFalse()
    {
        var service = MakeService("evt_testkey");

        bool? status = null;
        void Handler(IntegrationConnection c) { if (c.Source == SubathonEventSource.Tangia) status = c.Status; }
        IntegrationEvents.ConnectionUpdated += Handler;
        try { await service.StopAsync(TestContext.Current.CancellationToken); }
        finally { IntegrationEvents.ConnectionUpdated -= Handler; }

        Assert.False(status);
    }


    [Fact]
    public async Task PollOnce_NoContent_NoEventRaised()
    {
        var (factory, _) = MakeHttpFactory(HttpStatusCode.NoContent);
        var service = MakeService("evt_testkey", factory);

        var ev = await CaptureEventFromPoll(service, "evt_testkey");

        Assert.Null(ev);
    }

    [Fact]
    public async Task PollOnce_ErrorStatus_NoEventRaised()
    {
        var (factory, _) = MakeHttpFactory(HttpStatusCode.InternalServerError, "error");
        var service = MakeService("evt_testkey", factory);

        var ev = await CaptureEventFromPoll(service, "evt_testkey");

        Assert.Null(ev);
    }

    [Fact]
    public async Task PollOnce_ValidEvent_RaisesEventWithCorrectFields()
    {
        const string json = """
            {
              "Events": [{
                "UnixMS": 1718600000000,
                "EventID": "evt_abc123",
                "Data": {
                  "type": "interaction",
                  "URL": "",
                  "OverlayParams": {
                    "TriggerData": { "price": 50 },
                    "BuyerInfo": { "name": "TestUser", "has-plus": false },
                    "name": "TestUser"
                  }
                }
              }]
            }
            """;
        var (factory, _) = MakeHttpFactory(HttpStatusCode.OK, json);
        var service = MakeService("evt_testkey", factory);

        var ev = await CaptureEventFromPoll(service, "evt_testkey");

        Assert.NotNull(ev);
        Assert.Equal(SubathonEventType.TangiaTokens, ev.EventType);
        Assert.Equal(SubathonEventSource.Tangia, ev.Source);
        Assert.Equal("TestUser", ev.User);
        Assert.Equal("50", ev.Value);
        Assert.Equal("tokens", ev.Currency);
    }

    [Theory]
    [InlineData("evt__test_abc")]
    [InlineData("evt_xyz_test_123")]
    public async Task PollOnce_TestEventId_Filtered(string eventId)
    {
        var json = $$"""
            {
              "Events": [{
                "UnixMS": 1718600000000,
                "EventID": "{{eventId}}",
                "Data": { "OverlayParams": { "TriggerData": { "price": 50 }, "BuyerInfo": { "name": "User" } } }
              }]
            }
            """;
        var (factory, _) = MakeHttpFactory(HttpStatusCode.OK, json);
        var service = MakeService("evt_testkey", factory);

        var ev = await CaptureEventFromPoll(service, "evt_testkey");

        Assert.Null(ev);
    }

    [Theory]
    [InlineData("evt__cp_abc")]
    [InlineData("evt_xyz_cp_123")]
    public async Task PollOnce_CpEventId_Filtered(string eventId)
    {
        var json = $$"""
            {
              "Events": [{
                "UnixMS": 1718600000000,
                "EventID": "{{eventId}}",
                "Data": { "OverlayParams": { "TriggerData": { "price": 50 }, "BuyerInfo": { "name": "User" } } }
              }]
            }
            """;
        var (factory, _) = MakeHttpFactory(HttpStatusCode.OK, json);
        var service = MakeService("evt_testkey", factory);

        var ev = await CaptureEventFromPoll(service, "evt_testkey");

        Assert.Null(ev);
    }

    [Fact]
    public async Task PollOnce_EmptyEventId_Filtered()
    {
        const string json = """
            {
              "Events": [{
                "UnixMS": 1718600000000,
                "EventID": "",
                "Data": { "OverlayParams": { "TriggerData": { "price": 50 }, "BuyerInfo": { "name": "User" } } }
              }]
            }
            """;
        var (factory, _) = MakeHttpFactory(HttpStatusCode.OK, json);
        var service = MakeService("evt_testkey", factory);

        var ev = await CaptureEventFromPoll(service, "evt_testkey");

        Assert.Null(ev);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public async Task PollOnce_ZeroOrNegativePrice_Filtered(int price)
    {
        var json = $$"""
            {
              "Events": [{
                "UnixMS": 1718600000000,
                "EventID": "evt_valid123",
                "Data": { "OverlayParams": { "TriggerData": { "price": {{price}} }, "BuyerInfo": { "name": "User" } } }
              }]
            }
            """;
        var (factory, _) = MakeHttpFactory(HttpStatusCode.OK, json);
        var service = MakeService("evt_testkey", factory);

        var ev = await CaptureEventFromPoll(service, "evt_testkey");

        Assert.Null(ev);
    }

    [Fact]
    public async Task PollOnce_BuyerInfoName_UsedAsUser()
    {
        const string json = """
            {
              "Events": [{
                "UnixMS": 1718600000000,
                "EventID": "evt_abc123",
                "Data": { "OverlayParams": { "TriggerData": { "price": 10 }, "BuyerInfo": { "name": "BuyerName" }, "name": "BuyerName" } }
              }]
            }
            """;
        var (factory, _) = MakeHttpFactory(HttpStatusCode.OK, json);
        var service = MakeService("evt_testkey", factory);

        var ev = await CaptureEventFromPoll(service, "evt_testkey");

        Assert.NotNull(ev);
        Assert.Equal("BuyerName", ev.User);
    }

    [Fact]
    public async Task PollOnce_NoBuyerInfo_FallsBackToOverlayParamsName()
    {
        const string json = """
            {
              "Events": [{
                "UnixMS": 1718600000000,
                "EventID": "evt_abc123",
                "Data": { "OverlayParams": { "TriggerData": { "price": 10 }, "name": "BuyerName" } }
              }]
            }
            """;
        var (factory, _) = MakeHttpFactory(HttpStatusCode.OK, json);
        var service = MakeService("evt_testkey", factory);

        var ev = await CaptureEventFromPoll(service, "evt_testkey");

        Assert.NotNull(ev);
        Assert.Equal("BuyerName", ev.User);
    }

    [Fact]
    public async Task PollOnce_NoNamesAtAll_FallsBackToDefault()
    {
        const string json = """
            {
              "Events": [{
                "UnixMS": 1718600000000,
                "EventID": "evt_abc123",
                "Data": { "OverlayParams": { "TriggerData": { "price": 10 } } }
              }]
            }
            """;
        var (factory, _) = MakeHttpFactory(HttpStatusCode.OK, json);
        var service = MakeService("evt_testkey", factory);

        var ev = await CaptureEventFromPoll(service, "evt_testkey");

        Assert.NotNull(ev);
        Assert.Equal("Tangia User", ev.User);
    }

    [Fact]
    public async Task PollOnce_EmptyEventsList_NoEventRaised()
    {
        const string json = """{ "Events": [] }""";
        var (factory, _) = MakeHttpFactory(HttpStatusCode.OK, json);
        var service = MakeService("evt_testkey", factory);

        var ev = await CaptureEventFromPoll(service, "evt_testkey");

        Assert.Null(ev);
    }

    [Fact]
    public async Task PollOnce_InvalidJson_DoesNotThrow()
    {
        var (factory, _) = MakeHttpFactory(HttpStatusCode.OK, "{{not valid json{{");
        var service = MakeService("evt_testkey", factory);

        var ex = await Record.ExceptionAsync(() => service.PollOnceAsync("evt_testkey", CancellationToken.None));

        Assert.Null(ex);
    }

    [Fact]
    public async Task PollOnce_Timeout_DoesNotThrow()
    {
        var (factory, _) = MakeHttpFactory(HttpStatusCode.OK, throwTimeout: true);
        var service = MakeService("evt_testkey", factory);

        var ex = await Record.ExceptionAsync(() => service.PollOnceAsync("evt_testkey", CancellationToken.None));

        Assert.Null(ex);
    }

    private class MockHttpHandler(HttpStatusCode statusCode, string? body = null, bool throwTimeout = false)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (throwTimeout)
                throw new TaskCanceledException("Simulated timeout");

            var response = new HttpResponseMessage(statusCode);
            if (body != null)
                response.Content = new StringContent(body, Encoding.UTF8, "application/json");
            return Task.FromResult(response);
        }
    }
}
