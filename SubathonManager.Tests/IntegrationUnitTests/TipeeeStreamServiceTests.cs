using System.Reflection;
using Microsoft.Extensions.Logging;
using Moq;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;
using SubathonManager.Core.Security;
using SubathonManager.Integration;
using SubathonManager.Tests.Utility;

namespace SubathonManager.Tests.IntegrationUnitTests;

[Collection("SharedEventBusTests")]
public class TipeeeStreamServiceTests
{
    public TipeeeStreamServiceTests()
    {
        typeof(IntegrationEvents)
            .GetField("ConnectionUpdated", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
    }

    private static SubathonEvent? CaptureEvent(Action trigger) =>
        EventUtil.SubathonEventCapture.CaptureRequired(trigger);

    private static TipeeeStreamService MakeService(
        Dictionary<string, string>? storageData = null,
        Dictionary<(string, string), string>? configValues = null)
    {
        var logger = new Mock<ILogger<TipeeeStreamService>>();
        var config = MockConfig.MakeMockConfig(configValues);
        var httpFactory = new Mock<IHttpClientFactory>();
        var storage = new InMemorySecureStorage(storageData);
        var timerService = new Mock<ITimerService>();
        timerService
            .Setup(t => t.Register(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<Func<CancellationToken, Task>>()))
            .Returns(Mock.Of<IDisposable>());
        return new TipeeeStreamService(logger.Object, httpFactory.Object, storage, timerService.Object);
    }

    private static SubathonEvent? InvokeProcessEventJson(TipeeeStreamService service, string json)
    {
        var method = typeof(TipeeeStreamService)
            .GetMethod("ProcessEventJson", BindingFlags.NonPublic | BindingFlags.Instance);
        return CaptureEvent(() => method?.Invoke(service, [json]));
    }

    [Fact]
    public async Task StartAsync_NoTokens_BroadcastsStatusFalse()
    {
        var service = MakeService();

        bool? status = null;
        void Handler(IntegrationConnection conn)
        {
            if (conn.Source == SubathonEventSource.TipeeeStream)
                status = conn.Status;
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
    }

    [Fact]
    public async Task StartAsync_EmptyTokenValues_BroadcastsStatusFalse()
    {
        var service = MakeService(new Dictionary<string, string>
        {
            [StorageKeys.TipeeeStreamAccessToken] = "",
            [StorageKeys.TipeeeStreamRefreshToken] = "",
        });

        bool? status = null;
        void Handler(IntegrationConnection conn)
        {
            if (conn.Source == SubathonEventSource.TipeeeStream)
                status = conn.Status;
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
    }

    [Fact]
    public void SimulateDonation_ValidAmount_RaisesDonationEvent()
    {
        var ev = CaptureEvent(() => TipeeeStreamService.SimulateDonation("10.50", "USD"));

        Assert.NotNull(ev);
        Assert.Equal(SubathonEventType.TipeeeStreamDonation, ev.EventType);
        Assert.Equal(SubathonEventSource.Simulated, ev.Source);
        Assert.Equal("SYSTEM", ev.User);
        Assert.Equal("10.50", ev.Value);
        Assert.Equal("USD", ev.Currency);
    }

    [Fact]
    public void SimulateDonation_EmptyCurrency_DefaultsToEur()
    {
        var ev = CaptureEvent(() => TipeeeStreamService.SimulateDonation("5.00", ""));

        Assert.NotNull(ev);
        Assert.Equal("EUR", ev.Currency);
    }

    [Fact]
    public void SimulateDonation_InvalidAmount_DoesNotRaiseEvent()
    {
        var ev = CaptureEvent(() => TipeeeStreamService.SimulateDonation("not-a-number", "USD"));

        Assert.Null(ev);
    }

    [Fact]
    public void ProcessEventJson_DonationType_RaisesDonationEvent()
    {
        var service = MakeService();
        const string json = """[{"event":{"type":"donation","ref":"TIPEEESTREAM_abc","parameters":{"username":"Donor","amount":15.0,"currency":"USD"}}}]""";

        var ev = InvokeProcessEventJson(service, json);

        Assert.NotNull(ev);
        Assert.Equal(SubathonEventType.TipeeeStreamDonation, ev.EventType);
        Assert.Equal(SubathonEventSource.TipeeeStream, ev.Source);
        Assert.Equal("Donor", ev.User);
        Assert.Equal("15.00", ev.Value);
        Assert.Equal("USD", ev.Currency);
    }

    [Fact]
    public void ProcessEventJson_TipeeeType_RaisesDonationEvent()
    {
        var service = MakeService();
        const string json = """[{"event":{"type":"tipeee","ref":"TIPEEESTREAM_abc","parameters":{"username":"SubGuy","amount":5.0,"currency":"EUR"}}}]""";

        var ev = InvokeProcessEventJson(service, json);

        Assert.NotNull(ev);
        Assert.Equal(SubathonEventType.TipeeeStreamDonation, ev.EventType);
        Assert.Equal("SubGuy", ev.User);
    }

    [Fact]
    public void ProcessEventJson_UnknownType_DoesNotRaise()
    {
        var service = MakeService();
        const string json = """[{"event":{"type":"follow","ref":"abc","parameters":{"username":"Follower","amount":0,"currency":""}}}]""";

        var ev = InvokeProcessEventJson(service, json);

        Assert.Null(ev);
    }

    [Fact]
    public void ProcessEventJson_TwitchRef_DoesNotRaise()
    {
        var service = MakeService();
        const string json = """[{"event":{"type":"donation","ref":"TWITCH_SUB_abc","parameters":{"username":"TwitchUser","amount":5.0,"currency":"USD"}}}]""";

        var ev = InvokeProcessEventJson(service, json);

        Assert.Null(ev);
    }

    [Fact]
    public void ProcessEventJson_YoutubeRef_DoesNotRaise()
    {
        var service = MakeService();
        const string json = """[{"event":{"type":"donation","ref":"YOUTUBE_MEMBERSHIP_abc","parameters":{"username":"YtUser","amount":5.0,"currency":"USD"}}}]""";

        var ev = InvokeProcessEventJson(service, json);

        Assert.Null(ev);
    }

    [Theory]
    [InlineData("TWITCH_sub")]
    [InlineData("twitch_sub")]
    [InlineData("Twitch_Sub")]
    public void ProcessEventJson_TwitchRef_CaseInsensitive_DoesNotRaise(string refValue)
    {
        var service = MakeService();
        string json = """[{"event":{"type":"donation","ref":"REF","parameters":{"username":"User","amount":5.0,"currency":"USD"}}}]"""
            .Replace("REF", refValue);

        var ev = InvokeProcessEventJson(service, json);

        Assert.Null(ev);
    }

    [Theory]
    [InlineData("YOUTUBE_member")]
    [InlineData("youtube_member")]
    [InlineData("YouTube_Member")]
    public void ProcessEventJson_YoutubeRef_CaseInsensitive_DoesNotRaise(string refValue)
    {
        var service = MakeService();
        string json = """[{"event":{"type":"donation","ref":"REF","parameters":{"username":"User","amount":5.0,"currency":"USD"}}}]"""
            .Replace("REF", refValue);

        var ev = InvokeProcessEventJson(service, json);

        Assert.Null(ev);
    }

    [Fact]
    public void ProcessEventJson_SimulationRef_SetsSystemUser()
    {
        var service = MakeService();
        const string json = """[{"event":{"type":"donation","ref":"simulation_abc","parameters":{"username":"RealDonor","amount":10.0,"currency":"USD"}}}]""";

        var ev = InvokeProcessEventJson(service, json);

        Assert.NotNull(ev);
        Assert.Equal("SYSTEM", ev.User);
    }

    [Theory]
    [InlineData("simulation_test")]
    [InlineData("SIMULATION_TEST")]
    [InlineData("Simulation123")]
    public void ProcessEventJson_SimulationRef_CaseInsensitive(string refValue)
    {
        var service = MakeService();
        string json = """[{"event":{"type":"donation","ref":"REF","parameters":{"username":"Donor","amount":10.0,"currency":"USD"}}}]"""
            .Replace("REF", refValue);

        var ev = InvokeProcessEventJson(service, json);

        Assert.NotNull(ev);
        Assert.Equal("SYSTEM", ev.User);
    }

    [Fact]
    public void ProcessEventJson_ParametersUsernameUsedOverProvider()
    {
        var service = MakeService();
        const string json = """[{"event":{"type":"donation","ref":"ref","parameters":{"username":"DonorName","amount":5.0,"currency":"EUR"},"user":{"providers":[{"code":"twitch","username":"ProviderUser"}]}}}]""";

        var ev = InvokeProcessEventJson(service, json);

        Assert.NotNull(ev);
        Assert.Equal("DonorName", ev.User);
    }

    [Fact]
    public void ProcessEventJson_FallsBackToProviderUsername()
    {
        var service = MakeService();
        const string json = """[{"event":{"type":"donation","ref":"ref","parameters":{"username":"","amount":5.0,"currency":"EUR"},"user":{"providers":[{"code":"twitch","username":"ProviderUser"}]}}}]""";

        var ev = InvokeProcessEventJson(service, json);

        Assert.NotNull(ev);
        Assert.Equal("ProviderUser", ev.User);
    }

    [Fact]
    public void ProcessEventJson_FallsBackToTipeeeStream()
    {
        var service = MakeService();
        const string json = """[{"event":{"type":"donation","ref":"ref","parameters":{"username":"","amount":5.0,"currency":"EUR"}}}]""";

        var ev = InvokeProcessEventJson(service, json);

        Assert.NotNull(ev);
        Assert.Equal("TipeeeStream", ev.User);
    }

    [Fact]
    public void ProcessEventJson_EmptyCurrency_DefaultsToEur()
    {
        var service = MakeService();
        const string json = """[{"event":{"type":"donation","ref":"ref","parameters":{"username":"Donor","amount":10.0,"currency":""}}}]""";

        var ev = InvokeProcessEventJson(service, json);

        Assert.NotNull(ev);
        Assert.Equal("EUR", ev.Currency);
    }

    [Fact]
    public void ProcessEventJson_AmountFormattedToTwoDecimals()
    {
        var service = MakeService();
        const string json = """[{"event":{"type":"donation","ref":"ref","parameters":{"username":"Donor","amount":7.5,"currency":"USD"}}}]""";

        var ev = InvokeProcessEventJson(service, json);

        Assert.NotNull(ev);
        Assert.Equal("7.50", ev.Value);
    }

    [Fact]
    public void ProcessEventJson_NullParameters_DoesNotRaise()
    {
        var service = MakeService();
        const string json = """[{"event":{"type":"donation","ref":"ref"}}]""";

        var ev = InvokeProcessEventJson(service, json);

        Assert.Null(ev);
    }

    [Fact]
    public void ProcessEventJson_CreatedAt_Iso8601_UsedAsTimestamp()
    {
        var service = MakeService();
        const string json = """[{"event":{"type":"donation","ref":"ref","created_at":"2026-06-06T15:13:02.103Z","parameters":{"username":"Donor","amount":5.0,"currency":"USD"}}}]""";

        var ev = InvokeProcessEventJson(service, json);

        Assert.NotNull(ev);
        var expected = DateTimeOffset.Parse("2026-06-06T15:13:02.103Z").LocalDateTime;
        Assert.Equal(expected, ev.EventTimestamp);
    }

    [Fact]
    public void ProcessEventJson_CreatedAt_UnparseableFormat_FallsBackToNow()
    {
        var service = MakeService();
        const string json = """[{"event":{"type":"donation","ref":"ref","created_at":"not-a-date","parameters":{"username":"Donor","amount":5.0,"currency":"USD"}}}]""";
        var before = DateTime.Now.ToLocalTime();

        var ev = InvokeProcessEventJson(service, json);

        Assert.NotNull(ev);
        Assert.True(ev.EventTimestamp >= before);
    }

    [Fact]
    public void ProcessEventJson_StringAmount_ParsesSuccessfully()
    {
        var service = MakeService();
        const string json = """[{"event":{"type":"donation","ref":442715.728856922,"parameters":{"username":"Anonymous21","amount":"2.77","currency":"USD"}}}]""";

        var ev = InvokeProcessEventJson(service, json);

        Assert.NotNull(ev);
        Assert.Equal("2.77", ev.Value);
        Assert.Equal("USD", ev.Currency);
    }

    [Fact]
    public void ProcessEventJson_NumericRef_ParsesSuccessfully()
    {
        var service = MakeService();
        const string json = """[{"event":{"type":"donation","ref":704638.4114700294,"parameters":{"username":"Anonymous19","amount":25.0,"currency":"USD"}}}]""";

        var ev = InvokeProcessEventJson(service, json);

        Assert.NotNull(ev);
        Assert.Equal("Anonymous19", ev.User);
        Assert.Equal("25.00", ev.Value);
    }

    [Fact]
    public void ProcessEventJson_InvalidJson_DoesNotThrow()
    {
        var service = MakeService();

        var ex = Record.Exception(() => InvokeProcessEventJson(service, "not-valid-json{{{"));

        Assert.Null(ex);
    }

    [Fact]
    public void RevokeTokens_DeletesAllStorageKeys()
    {
        var storage = new InMemorySecureStorage(new Dictionary<string, string>
        {
            [StorageKeys.TipeeeStreamAccessToken] = "access",
            [StorageKeys.TipeeeStreamRefreshToken] = "refresh",
            [StorageKeys.TipeeeStreamApiKey] = "apikey",
        });
        var service = new TipeeeStreamService(
            new Mock<ILogger<TipeeeStreamService>>().Object,
            new Mock<IHttpClientFactory>().Object,
            storage,
            new Mock<ITimerService>().Object);

        service.RevokeTokens();

        Assert.False(storage.Exists(StorageKeys.TipeeeStreamAccessToken));
        Assert.False(storage.Exists(StorageKeys.TipeeeStreamRefreshToken));
        Assert.False(storage.Exists(StorageKeys.TipeeeStreamApiKey));
        Assert.Equal(3, storage.DeleteCount);
    }
}
