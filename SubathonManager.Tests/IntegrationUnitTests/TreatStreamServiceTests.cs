using System.Globalization;
using Microsoft.Extensions.Logging;
using Moq;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Core.Security;
using SubathonManager.Core.Security.Interfaces;
using SubathonManager.Integration;
using SubathonManager.Tests.Utility;

namespace SubathonManager.Tests.IntegrationUnitTests;

[Collection("GlobalState")]
public class TreatStreamServiceTests {
    private const string TreatJson = """
                                     {
                                       "message": "enjoy!",
                                       "sender": "GenerousViewer",
                                       "receiver": "Streamer",
                                       "title": "Large Pizza",
                                       "sender_type": "user",
                                       "receiver_type": "streamer",
                                       "date_created": "2026-07-08 12:34:56"
                                     }
                                     """;

    private static SubathonEvent? CaptureEvent(Action trigger) {
        return EventUtil.SubathonEventCapture.CaptureRequired(trigger);
    }

    private static (TreatStreamService Service, ISecureStorage Storage) MakeService(
        string? accessToken = null, string? refreshToken = null) {
        var logger = new Mock<ILogger<TreatStreamService>>();
        var seed = new Dictionary<string, string>();
        if (accessToken != null) seed[StorageKeys.TreatStreamAccessToken] = accessToken;
        if (refreshToken != null) seed[StorageKeys.TreatStreamRefreshToken] = refreshToken;
        var storage = new InMemorySecureStorage(seed.Count > 0 ? seed : null);
        IHttpClientFactory httpFactory = new Mock<IHttpClientFactory>().Object;
        ITimerService timer = new Mock<ITimerService>().Object;
        return (new TreatStreamService(logger.Object, httpFactory, storage, timer), storage);
    }

    [Fact]
    public void ProcessTreatJson_ValidTreat_RaisesOrderEvent() {
        (TreatStreamService service, _) = MakeService();
        var result = false;
        SubathonEvent? ev = CaptureEvent(() => result = service.ProcessTreatJson(TreatJson));

        Assert.True(result);
        Assert.NotNull(ev);
        Assert.Equal(SubathonEventSource.TreatStream, ev!.Source);
        Assert.Equal(SubathonEventType.TreatStreamOrder, ev.EventType);
        Assert.Equal("GenerousViewer", ev.User);
        Assert.Equal("item", ev.Currency);
        Assert.Equal(1, ev.Amount);
        Assert.Equal("Large Pizza", ev.Value);
        Assert.Equal("Large Pizza", ev.TertiaryValue);
    }

    [Fact]
    public void ProcessTreatJson_SameTreat_HasDeterministicId() {
        (TreatStreamService service, _) = MakeService();
        SubathonEvent? ev1 = CaptureEvent(() => service.ProcessTreatJson(TreatJson));
        SubathonEvent? ev2 = CaptureEvent(() => service.ProcessTreatJson(TreatJson));

        Assert.NotNull(ev1);
        Assert.NotNull(ev2);
        Assert.NotEqual(Guid.Empty, ev1!.Id);
        Assert.Equal(ev1.Id, ev2!.Id);
    }

    [Fact]
    public void ProcessTreatJson_DifferentDate_HasDifferentId() {
        (TreatStreamService service, _) = MakeService();
        SubathonEvent? ev1 = CaptureEvent(() => service.ProcessTreatJson(TreatJson));
        SubathonEvent? ev2 = CaptureEvent(() => service.ProcessTreatJson(
            TreatJson.Replace("12:34:56", "12:34:57")));

        Assert.NotEqual(ev1!.Id, ev2!.Id);
    }

    [Fact]
    public void ProcessTreatJson_SystemSender_IsSimulated() {
        (TreatStreamService service, _) = MakeService();
        SubathonEvent? ev = CaptureEvent(() => service.ProcessTreatJson(
            TreatJson.Replace("GenerousViewer", "SYSTEM")));

        Assert.NotNull(ev);
        Assert.Equal(SubathonEventSource.Simulated, ev!.Source);
        Assert.Equal("SYSTEM", ev.User);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"sender\":\"a\",\"title\":\"\"}")]
    [InlineData("[1,2,3]")]
    public void ProcessTreatJson_Invalid_ReturnsFalse(string json) {
        (TreatStreamService service, _) = MakeService();
        Assert.False(service.ProcessTreatJson(json));
    }

    [Fact]
    public void SimulateTreat_RaisesSimulatedEvent() {
        SubathonEvent? ev = CaptureEvent(() => TreatStreamService.SimulateTreat("Cupcake"));

        Assert.NotNull(ev);
        Assert.Equal(SubathonEventSource.Simulated, ev!.Source);
        Assert.Equal(SubathonEventType.TreatStreamOrder, ev.EventType);
        Assert.Equal("SYSTEM", ev.User);
        Assert.Equal("item", ev.Currency);
        Assert.Equal(1, ev.Amount);
        Assert.Equal("Cupcake", ev.Value);
    }

    [Fact]
    public void StoreExpiry_UsesExpiresIn() {
        (TreatStreamService service, ISecureStorage storage) = MakeService("token", "refresh");
        service.StoreExpiry("3600");

        string? raw = storage.GetOrDefault(StorageKeys.TreatStreamTokenExpiry, "");
        Assert.True(DateTime.TryParse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out DateTime expiry));
        Assert.InRange(expiry, DateTime.UtcNow.AddMinutes(55), DateTime.UtcNow.AddMinutes(65));
    }

    [Fact]
    public void StoreExpiry_MissingExpiresIn_DefaultsTo15Days() {
        // api says 30, we do half for safety
        (TreatStreamService service, ISecureStorage storage) = MakeService("token", "refresh");
        service.StoreExpiry(null);

        string? raw = storage.GetOrDefault(StorageKeys.TreatStreamTokenExpiry, "");
        Assert.True(DateTime.TryParse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out DateTime expiry));
        Assert.InRange(expiry, DateTime.UtcNow.AddDays(14), DateTime.UtcNow.AddDays(16));
    }

    [Fact]
    public async Task StartAsync_NoTokens_StaysDisconnected() {
        (TreatStreamService service, _) = MakeService();
        await service.StartAsync(TestContext.Current.CancellationToken);
        Assert.False(service.HasTokens());
        await service.StopAsync(TestContext.Current.CancellationToken);
    }
}