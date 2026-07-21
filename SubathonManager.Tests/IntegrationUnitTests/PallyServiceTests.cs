using Microsoft.Extensions.Logging;
using Moq;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Core.Security;
using SubathonManager.Integration;
using SubathonManager.Tests.Utility;

namespace SubathonManager.Tests.IntegrationUnitTests;

[Collection("SharedEventBusTests")]
public class PallyServiceTests
{
    private static SubathonEvent? CaptureEvent(Action trigger) =>
        EventUtil.SubathonEventCapture.CaptureRequired(trigger);

    private static PallyService MakeService(string? apiKey = null, string room = "", bool enabled = true)
    {
        var logger = new Mock<ILogger<PallyService>>();
        var storage = new InMemorySecureStorage(apiKey != null
            ? new Dictionary<string, string> { [StorageKeys.PallyApiKey] = apiKey }
            : null);
        IConfig config = MockConfig.MakeMockConfig(new Dictionary<(string, string), string>
        {
            { ("PallyGG", "Room"), room },
            { ("PallyGG", "Enabled"), enabled.ToString() }
        });
        return new PallyService(logger.Object, config, storage);
    }

    private const string TipMessage = """
        {
          "type": "campaigntip.notify",
          "payload": {
            "campaignTip": {
              "createdAt": "2024-03-13T18:02:33.743Z",
              "displayName": "Someone",
              "grossAmountInCents": 500,
              "id": "b1w2pjwjtb9fx0v1se9ex4n2",
              "message": "",
              "netAmountInCents": 500,
              "processingFeeInCents": 0,
              "updatedAt": "2024-03-13T18:02:33.743Z"
            },
            "page": {
              "id": "1627451579049x550722173620715500",
              "slug": "pally",
              "title": "PallyGG.gg's Team Page",
              "url": "https://pally.gg/p/pally"
            }
          }
        }
        """;

    [Fact]
    public void ProcessMessage_ValidTip_RaisesEvent()
    {
        var service = MakeService("key");
        bool result = false;
        var ev = CaptureEvent(() => result = service.ProcessMessage(TipMessage));

        Assert.True(result);
        Assert.NotNull(ev);
        Assert.Equal(SubathonEventSource.PallyGG, ev!.Source);
        Assert.Equal(SubathonEventType.PallyGGDonation, ev.EventType);
        Assert.Equal("USD", ev.Currency);
        Assert.Equal("5.00", ev.Value);
        Assert.Equal("Someone", ev.User);
        Assert.Equal("pally", ev.EventTypeMeta);
    }

    [Fact]
    public void ProcessMessage_SameTip_HasDeterministicId()
    {
        var service = MakeService("key");
        var ev1 = CaptureEvent(() => service.ProcessMessage(TipMessage));
        var ev2 = CaptureEvent(() => service.ProcessMessage(TipMessage));

        Assert.NotNull(ev1);
        Assert.NotNull(ev2);
        Assert.NotEqual(Guid.Empty, ev1!.Id);
        Assert.Equal(ev1.Id, ev2!.Id);
    }

    [Fact]
    public void ProcessMessage_MissingDisplayName_UsesAnonymous()
    {
        var service = MakeService("key");
        var message = TipMessage.Replace("\"displayName\": \"Someone\",", "\"displayName\": \"\",");
        var ev = CaptureEvent(() => service.ProcessMessage(message));

        Assert.NotNull(ev);
        Assert.Equal("Anonymous", ev!.User);
    }

    [Fact]
    public void ProcessMessage_Simulated_UsesSystemAndSimulatedSource()
    {
        var service = MakeService("key");
        var ev = CaptureEvent(() => service.ProcessMessage(TipMessage, simulated: true));

        Assert.NotNull(ev);
        Assert.Equal("SYSTEM", ev!.User);
        Assert.Equal(SubathonEventSource.Simulated, ev.Source);
    }

    [Theory]
    [InlineData("pong")]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{\"type\":\"something.else\",\"payload\":{}}")]
    [InlineData("{\"type\":\"campaigntip.notify\",\"payload\":{}}")]
    public void ProcessMessage_InvalidOrIrrelevant_ReturnsFalse(string message)
    {
        var service = MakeService("key");
        Assert.False(service.ProcessMessage(message));
    }

    [Fact]
    public void ProcessMessage_ZeroAmount_ReturnsFalse()
    {
        var service = MakeService("key");
        var message = TipMessage.Replace("\"grossAmountInCents\": 500,", "\"grossAmountInCents\": 0,");
        Assert.False(service.ProcessMessage(message));
    }

    [Fact]
    public void BuildUri_NoRoom_UsesFirehose()
    {
        var service = MakeService("my-key", room: "");
        var uri = service.BuildUri();

        Assert.StartsWith("wss://events.pally.gg", uri.ToString());
        Assert.Contains("auth=my-key", uri.Query);
        Assert.Contains("channel=firehose", uri.Query);
        Assert.DoesNotContain("room=", uri.Query);
    }

    [Fact]
    public void BuildUri_WithRoom_UsesActivityFeed()
    {
        var service = MakeService("my-key", room: "my-page");
        var uri = service.BuildUri();

        Assert.Contains("channel=activity-feed", uri.Query);
        Assert.Contains("room=my-page", uri.Query);
    }

    [Fact]
    public void SimulateTip_RaisesSimulatedUsdEvent()
    {
        var ev = CaptureEvent(() => PallyService.SimulateTip("12.34"));

        Assert.NotNull(ev);
        Assert.Equal("SYSTEM", ev!.User);
        Assert.Equal(SubathonEventSource.Simulated, ev.Source);
        Assert.Equal(SubathonEventType.PallyGGDonation, ev.EventType);
        Assert.Equal("USD", ev.Currency);
        Assert.Equal("12.34", ev.Value);
    }

    [Fact]
    public async Task StartAsync_NoApiKey_StaysDisconnected()
    {
        var service = MakeService(apiKey: null);
        await service.StartAsync(TestContext.Current.CancellationToken);
        Assert.False(service.Connected);
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StartAsync_Disabled_StaysDisconnected()
    {
        var service = MakeService(apiKey: "key", enabled: false);
        await service.StartAsync(TestContext.Current.CancellationToken);
        Assert.False(service.Connected);
        await service.StopAsync(TestContext.Current.CancellationToken);
    }
}
