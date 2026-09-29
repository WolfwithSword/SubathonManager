using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Kiota.Abstractions.Serialization;
using Microsoft.Kiota.Serialization.Json;
using Moq;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;
using SubathonManager.Core.Security;
using SubathonManager.Core.Security.Interfaces;
using SubathonManager.Integration;
using SubathonManager.Services;
using SubathonManager.Tests.Utility;
using Tiltify.Client.Generated.Models;

namespace SubathonManager.Tests.IntegrationUnitTests;

[Collection("GlobalState")]
public class TiltifyServiceTests {
    private static readonly Guid CampaignId = Guid.Parse("de43da9d-2e81-4a2e-a781-4323e036b9d4");
    private static readonly Guid TeamCampaignId = Guid.Parse("5bd6d7b6-43c2-41bd-9a95-ee175f4d8a24");

    private const string DonationsJson = """
                                         {
                                           "data": [
                                             {
                                               "amount": { "currency": "USD", "value": "182.32" },
                                               "campaign_id": "de43da9d-2e81-4a2e-a781-4323e036b9d4",
                                               "completed_at": "2026-09-02T16:57:16.710392Z",
                                               "donation_matches": [
                                                 {
                                                   "active": true,
                                                   "amount": { "currency": "USD", "value": "182.32" },
                                                   "completed_at": "2026-09-02T16:57:16.726761Z",
                                                   "donation_id": "11a633e9-adb7-49b2-9e74-7b4a82479489",
                                                   "id": "361ce7a3-2846-44a0-9098-cc1f868778b5",
                                                   "matched_by": "Big Donor 1",
                                                   "pledged_amount": { "currency": "USD", "value": "182.32" },
                                                   "total_amount_raised": { "currency": "USD", "value": "182.32" }
                                                 }
                                               ],
                                               "donor_comment": "Keep up the great work!",
                                               "donor_name": "Test Name",
                                               "id": "f567aa73-9604-4e4d-a942-05d7705683d7",
                                               "legacy_id": 992616935,
                                               "sustained": false
                                             }
                                           ],
                                           "metadata": { "after": "bGlnaHQgwd==", "before": null, "limit": 10 }
                                         }
                                         """;

    public TiltifyServiceTests() {
        typeof(IntegrationEvents)
            .GetField("ConnectionUpdated", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
    }

    private static List<SubathonEvent> CaptureAll(Action trigger) {
        typeof(SubathonEvents)
            .GetField("SubathonEventCreated", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);

        List<SubathonEvent> events = [];

        void Handler(SubathonEvent e) {
            events.Add(e);
        }

        SubathonEvents.SubathonEventCreated += Handler;
        try {
            trigger();
        }
        finally {
            SubathonEvents.SubathonEventCreated -= Handler;
        }

        return events;
    }

    private static (TiltifyService Service, ISecureStorage Storage, Dictionary<(string, string), string> Config)
        MakeService(Dictionary<string, string>? storageData = null,
            Dictionary<(string, string), string>? configValues = null) {
        var logger = new Mock<ILogger<TiltifyService>>();
        Dictionary<(string, string), string> configStore = configValues ?? new Dictionary<(string, string), string>();
        var config = new Mock<IConfig>();
        
        config.Setup(c => c.Get(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string s, string k, string d) => configStore.TryGetValue((s, k), out string? v) ? v : d);
        config.Setup(c => c.Set(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string s, string k, string v) => {
                if (configStore.TryGetValue((s, k), out string? old) && old == v) return false;
                configStore[(s, k)] = v;
                return true;
            });

        var storage = new InMemorySecureStorage(storageData);
        var timerService = new Mock<ITimerService>();
        timerService
            .Setup(t => t.Register(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<Func<CancellationToken, Task>>()))
            .Returns(Mock.Of<IDisposable>());

        var service = new TiltifyService(logger.Object, config.Object, timerService.Object,
            new OAuthService(null, new Mock<IHttpClientFactory>().Object, storage) { OpenBrowser = _ => { } });
        return (service, storage, configStore);
    }

    private static void SetKnownCampaigns(TiltifyService service, params TiltifyService.CampaignOption[] campaigns) {
        typeof(TiltifyService).GetProperty(nameof(TiltifyService.KnownCampaigns))!
            .SetValue(service, campaigns.ToList());
    }

    private static Donation MakeDonation(string value = "25.5", string? currency = "CAD", string? donor = "Donor",
        Guid? campaignId = null, List<DonationMatch>? matches = null) {
        return new Donation {
            Id = Guid.NewGuid(),
            Amount = new Money { Value = value, Currency = currency },
            DonorName = donor,
            CampaignId = campaignId,
            CompletedAt = DateTimeOffset.UtcNow.AddSeconds(-5),
            DonationMatches = matches
        };
    }

    private static async Task<List<Donation>> ParseDonationsAsync(string json) {
        ParseNodeFactoryRegistry.DefaultInstance.ContentTypeAssociatedFactories
            .TryAdd("application/json", new JsonParseNodeFactory());
        using JsonDocument doc = JsonDocument.Parse(json);
        IEnumerable<Donation> donations = await KiotaJsonSerializer.DeserializeCollectionAsync(
            doc.RootElement.GetProperty("data").GetRawText(), Donation.CreateFromDiscriminatorValue,
            TestContext.Current.CancellationToken);
        return donations.ToList();
    }

    [Fact]
    public async Task HandleDonation_SamplePayload_RaisesDonationAndMatch() {
        (TiltifyService service, _, _) = MakeService();
        SetKnownCampaigns(service, new TiltifyService.CampaignOption(CampaignId, "My Campaign", false));
        string completedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
            .ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ", CultureInfo.InvariantCulture);
        string matchCompletedAt = DateTimeOffset.UtcNow.AddSeconds(-30)
            .ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ", CultureInfo.InvariantCulture);
        Donation donation = Assert.Single(await ParseDonationsAsync(DonationsJson
            .Replace("2026-09-02T16:57:16.710392Z", completedAt)
            .Replace("2026-09-02T16:57:16.726761Z", matchCompletedAt)));

        List<SubathonEvent> events = CaptureAll(() => service.HandleDonation(donation, CampaignId));

        Assert.Equal(2, events.Count);
        SubathonEvent dono = events[0];
        Assert.Equal(Guid.Parse("f567aa73-9604-4e4d-a942-05d7705683d7"), dono.Id);
        Assert.Equal(SubathonEventSource.Tiltify, dono.Source);
        Assert.Equal(SubathonEventType.TiltifyDonation, dono.EventType);
        Assert.Equal("Test Name", dono.User);
        Assert.Equal("182.32", dono.Value);
        Assert.Equal("USD", dono.Currency);
        Assert.Equal(CampaignId.ToString(), dono.EventTypeMeta);
        Assert.Equal("My Campaign", dono.TertiaryValue);
        Assert.InRange(dono.EventTimestamp, DateTime.Now.AddMinutes(-5), DateTime.Now);

        SubathonEvent match = events[1];
        Assert.Equal(Guid.Parse("361ce7a3-2846-44a0-9098-cc1f868778b5"), match.Id);
        Assert.Equal(SubathonEventType.TiltifyDonation, match.EventType);
        Assert.Equal("Big Donor 1", match.User);
        Assert.Equal("182.32", match.Value);
        Assert.Equal("USD", match.Currency);
        Assert.Equal(dono.EventTypeMeta, match.EventTypeMeta);
        Assert.Equal(dono.TertiaryValue, match.TertiaryValue);
        Assert.InRange(match.EventTimestamp, DateTime.Now.AddMinutes(-5), DateTime.Now);
        Assert.True(match.EventTimestamp > dono.EventTimestamp);
    }

    [Fact]
    public void HandleDonation_MissingFields_UsesFallbacks() {
        (TiltifyService service, _, _) = MakeService();
        SetKnownCampaigns(service, new TiltifyService.CampaignOption(TeamCampaignId, "Team: Big Event", true));
        Donation donation = MakeDonation(currency: null, donor: " ");

        SubathonEvent ev = Assert.Single(CaptureAll(() => service.HandleDonation(donation, TeamCampaignId)));

        Assert.Equal(donation.Id, ev.Id);
        Assert.Equal("Anonymous", ev.User);
        Assert.Equal("25.50", ev.Value);
        Assert.Equal("USD", ev.Currency);
        Assert.Equal(TeamCampaignId.ToString(), ev.EventTypeMeta);
        Assert.Equal("Team: Big Event", ev.TertiaryValue);
    }

    [Fact]
    public void HandleDonation_MemberCampaign_KeepsItsIdButFallsBackToPolledName() {
        (TiltifyService service, _, _) = MakeService();
        SetKnownCampaigns(service, new TiltifyService.CampaignOption(TeamCampaignId, "Team: Big Event", true));
        var memberCampaign = Guid.NewGuid();

        SubathonEvent ev = Assert.Single(CaptureAll(() =>
            service.HandleDonation(MakeDonation(campaignId: memberCampaign), TeamCampaignId)));

        Assert.Equal(memberCampaign.ToString(), ev.EventTypeMeta);
        Assert.Equal("Team: Big Event", ev.TertiaryValue);
    }

    [Fact]
    public void HandleDonation_UnknownCampaign_LeavesTertiaryEmpty() {
        (TiltifyService service, _, _) = MakeService();

        SubathonEvent ev = Assert.Single(CaptureAll(() =>
            service.HandleDonation(MakeDonation(campaignId: CampaignId))));

        Assert.Equal(CampaignId.ToString(), ev.EventTypeMeta);
        Assert.Equal("", ev.TertiaryValue);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    public void HandleDonation_InvalidAmount_RaisesNothing(string value) {
        (TiltifyService service, _, _) = MakeService();
        Assert.Empty(CaptureAll(() => service.HandleDonation(MakeDonation(value))));
    }

    [Fact]
    public void HandleDonation_Matches_OnlyActiveWithIdAndAmountAreRaised() {
        (TiltifyService service, _, _) = MakeService();
        var validId = Guid.NewGuid();
        Donation donation = MakeDonation(matches: [
            new DonationMatch { Id = validId, Active = true, Amount = new Money { Value = "10", Currency = null } },
            new DonationMatch { Id = Guid.NewGuid(), Active = false, Amount = new Money { Value = "10" } },
            new DonationMatch { Id = null, Active = true, Amount = new Money { Value = "10" } },
            new DonationMatch { Id = Guid.NewGuid(), Active = true, Amount = new Money { Value = "0" } },
            new DonationMatch { Id = Guid.NewGuid(), Active = true, Amount = null }
        ]);

        List<SubathonEvent> events = CaptureAll(() => service.HandleDonation(donation));

        Assert.Equal(2, events.Count);
        SubathonEvent match = events[1];
        Assert.Equal(validId, match.Id);
        Assert.Equal("Donation Match", match.User);
        Assert.Equal("10.00", match.Value);
        Assert.Equal("CAD", match.Currency);
        Assert.Equal(events[0].EventTimestamp, match.EventTimestamp);
    }

    [Fact]
    public void SimulateDonation_Default_HasNoCampaign() {
        SubathonEvent ev = Assert.Single(CaptureAll(() => TiltifyService.SimulateDonation("10.5", "")));

        Assert.Equal(SubathonEventSource.Simulated, ev.Source);
        Assert.Equal(SubathonEventType.TiltifyDonation, ev.EventType);
        Assert.Equal("SYSTEM", ev.User);
        Assert.Equal("10.50", ev.Value);
        Assert.Equal("USD", ev.Currency);
        Assert.Null(ev.EventTypeMeta);
        Assert.Equal("", ev.TertiaryValue);
    }

    [Fact]
    public void SimulateDonation_WithCampaign_TagsMetaAndName() {
        var campaign = new TiltifyService.CampaignOption(CampaignId, "My Campaign", false);
        SubathonEvent ev = Assert.Single(CaptureAll(() => TiltifyService.SimulateDonation("5", "EUR", campaign)));

        Assert.Equal("EUR", ev.Currency);
        Assert.Equal(CampaignId.ToString(), ev.EventTypeMeta);
        Assert.Equal("My Campaign", ev.TertiaryValue);
    }

    [Fact]
    public void SimulateDonation_InvalidAmount_RaisesNothing() {
        Assert.Empty(CaptureAll(() => TiltifyService.SimulateDonation("nope", "USD")));
    }

    [Fact]
    public void SelectedCampaigns_RoundTripThroughConfig() {
        (TiltifyService service, _, Dictionary<(string, string), string> config) = MakeService(
            configValues: new Dictionary<(string, string), string> {
                [("Tiltify", TiltifyService.CampaignIdsKey)] = $"{CampaignId}, not-a-guid,{CampaignId}",
                [("Tiltify", TiltifyService.TeamCampaignIdsKey)] = $"{TeamCampaignId}"
            });

        Assert.Equal(new List<(Guid, bool)> { (CampaignId, false), (TeamCampaignId, true) },
            service.GetSelectedCampaigns());

        var other = Guid.NewGuid();
        Assert.True(service.SetSelectedCampaigns([
            new TiltifyService.CampaignOption(other, "Other", false),
            new TiltifyService.CampaignOption(TeamCampaignId, "Team", true)
        ]));
        Assert.Equal($"{other}", config[("Tiltify", TiltifyService.CampaignIdsKey)]);
        Assert.Equal($"{TeamCampaignId}", config[("Tiltify", TiltifyService.TeamCampaignIdsKey)]);
        Assert.Equal(new List<(Guid, bool)> { (other, false), (TeamCampaignId, true) },
            service.GetSelectedCampaigns());

        Assert.False(service.SetSelectedCampaigns([
            new TiltifyService.CampaignOption(other, "Other", false),
            new TiltifyService.CampaignOption(TeamCampaignId, "Team", true)
        ]));
    }

    [Fact]
    public void RevokeTokens_ClearsAllStoredTokens() {
        (TiltifyService service, ISecureStorage storage, _) = MakeService(new Dictionary<string, string> {
            [StorageKeys.TiltifyAccessToken] = "access",
            [StorageKeys.TiltifyRefreshToken] = "refresh",
            [StorageKeys.TiltifyTokenExpiry] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        });
        Assert.True(service.HasTokens());

        service.RevokeTokens();

        Assert.False(service.HasTokens());
        Assert.False(storage.Exists(StorageKeys.TiltifyTokenExpiry));
    }

    [Fact]
    public async Task StartAsync_NoTokens_BroadcastsStatusFalse() {
        (TiltifyService service, _, _) = MakeService();
        IntegrationConnection? status = null;

        void Handler(IntegrationConnection conn) {
            if (conn.Source == SubathonEventSource.Tiltify) status = conn;
        }

        IntegrationEvents.ConnectionUpdated += Handler;
        try {
            await service.StartAsync(TestContext.Current.CancellationToken);
        }
        finally {
            IntegrationEvents.ConnectionUpdated -= Handler;
        }

        Assert.NotNull(status);
        Assert.False(status.Status);
        Assert.False(status.Configured);
        await service.StopAsync(TestContext.Current.CancellationToken);
    }
}
