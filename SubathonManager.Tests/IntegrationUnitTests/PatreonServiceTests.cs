using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DevTunnels.Client;
using Microsoft.Extensions.Logging;
using Moq;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Core.Security;
using SubathonManager.Integration;
using SubathonManager.Services;
using SubathonManager.Tests.Utility;

// ReSharper disable NullableWarningSuppressionIsUsed
namespace SubathonManager.Tests.IntegrationUnitTests;

[Collection("GlobalState")]
public class PatreonServiceTests {
    private const string Secret = "patreon-webhook-secret";

    public PatreonServiceTests() {
        typeof(IntegrationEvents)
            .GetField("ConnectionUpdated", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
    }

    private static PatreonService MakeService(bool withSecret = true, bool byAmount = false) {
        var storage = new InMemorySecureStorage(withSecret
            ? new Dictionary<string, string> { [StorageKeys.PatreonWebhookSecret] = Secret }
            : null);
        IConfig dtConfig = MockConfig.MakeMockConfig(new Dictionary<(string, string), string> {
            { ("Server", "Port"), "14040" }
        });
        var devTunnels = new DevTunnelsService(new Mock<ILogger<DevTunnelsService>>().Object, dtConfig,
            new Mock<IDevTunnelsClient>().Object);
        var oAuth = new OAuthService(null, new Mock<IHttpClientFactory>().Object, storage) { OpenBrowser = _ => { } };
        IConfig config = MockConfig.MakeMockConfig(new Dictionary<(string, string), string> {
            { ("Patreon", $"{SubathonEventType.PatreonPledge}.ValueByAmount"), byAmount ? "True" : "False" }
        });
        return new PatreonService(null, config, new Mock<IHttpClientFactory>().Object, devTunnels, storage, oAuth);
    }

    private static string Payload(int willPay = 500, int entitled = 500, int cadence = 1, bool freeTrial = false,
        string status = "active_patron", DateTimeOffset? lastCharge = null, string? chargeStatus = null,
        string currency = "USD", string? vanity = null) {
        return JsonSerializer.Serialize(new {
            data = new {
                id = "member-1", type = "member",
                attributes = new {
                    full_name = "Jane Doe", patron_status = status, is_free_trial = freeTrial,
                    pledge_cadence = cadence, will_pay_amount_cents = willPay,
                    currently_entitled_amount_cents = entitled,
                    pledge_relationship_start = "2026-09-01T10:00:00.000+00:00",
                    last_charge_date = lastCharge?.ToString("O"), last_charge_status = chargeStatus
                },
                relationships = new {
                    currently_entitled_tiers = new {
                        data = new[] { new { id = "t1", type = "tier" }, new { id = "t2", type = "tier" } }
                    }
                }
            },
            included = new object[] {
                new { id = "c1", type = "campaign", attributes = new { currency } },
                new { id = "u0", type = "user", attributes = new { full_name = "The Creator", vanity = "creator" } },
                new { id = "u1", type = "user", attributes = new { full_name = "Jane Doe", vanity } },
                new { id = "t1", type = "tier", attributes = new { title = "Bronze", amount_cents = 300 } },
                new { id = "t2", type = "tier", attributes = new { title = "Gold", amount_cents = 500 } }
            }
        });
    }

    private static async Task<SubathonEvent?> SendAsync(PatreonService service, string eventType, string body,
        string? signatureSecret = Secret) {
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        string signature =
            Convert.ToHexStringLower(HMACMD5.HashData(Encoding.UTF8.GetBytes(signatureSecret ?? ""), bytes));
        var headers = new Dictionary<string, string> {
            ["Content-Type"] = "application/json",
            ["X-Patreon-Event"] = eventType,
            ["X-Patreon-Signature"] = signature
        };
        return await EventUtil.SubathonEventCapture.CaptureAsync(() =>
            service.HandleWebhookAsync(bytes, headers, TestContext.Current.CancellationToken));
    }

    private static DateTimeOffset MinutesAgo(int minutes) {
        return DateTimeOffset.UtcNow.AddMinutes(-minutes);
    }

    [Fact]
    public async Task PledgeCreate_CountsTheWillPayAmountAtTheHighestTier() {
        SubathonEvent? ev = await SendAsync(MakeService(), PatreonService.PledgeCreated, Payload());

        Assert.NotNull(ev);
        Assert.Equal(SubathonEventSource.Patreon, ev.Source);
        Assert.Equal(SubathonEventType.PatreonPledge, ev.EventType);
        Assert.Equal("Jane", ev.User);
        Assert.Equal("Gold", ev.Value);
        Assert.Equal("Gold", ev.EventTypeMeta);
        Assert.Equal("member", ev.Currency);
        Assert.Equal(1, ev.Amount);
        Assert.Equal("5.00|USD", ev.SecondaryValue);
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero).LocalDateTime, ev.EventTimestamp);
    }

    [Theory]
    [InlineData("janedoe", "janedoe")]
    [InlineData(null, "Jane")]
    [InlineData("", "Jane")]
    public async Task User_IsThePatronsVanity_OrTheirName(string? vanity, string expected) {
        SubathonEvent? ev = await SendAsync(MakeService(), PatreonService.PledgeCreated, Payload(vanity: vanity));

        Assert.NotNull(ev);
        Assert.Equal(expected, ev.User);
    }

    [Fact]
    public async Task PayloadTier_OverridesAStaleCachedTier() {
        PatreonService service = MakeService();
        service.Tiers["t2"] = "Old Gold";

        SubathonEvent? ev = await SendAsync(service, PatreonService.PledgeCreated, Payload());

        Assert.NotNull(ev);
        Assert.Equal("Gold", ev.Value);
        Assert.Equal("Gold", service.Tiers["t2"]);
    }

    [Fact]
    public async Task PledgeCreate_UsesTheCampaignCurrency() {
        SubathonEvent? ev = await SendAsync(MakeService(byAmount: true), PatreonService.PledgeCreated,
            Payload(currency: "EUR"));

        Assert.NotNull(ev);
        Assert.Equal("5.00", ev.Value);
        Assert.Equal("EUR", ev.Currency);
        Assert.Equal("5.00|EUR", ev.SecondaryValue);
    }

    [Theory]
    [InlineData(false, "Gold", 12)]
    [InlineData(true, "54.00", 1)]
    public async Task AnnualPledge_IsTierTimesCadenceOrTheWillPayAmountOnce(bool byAmount, string value,
        int amount) {
        SubathonEvent? ev = await SendAsync(MakeService(byAmount: byAmount), PatreonService.PledgeCreated,
            Payload(5400, 500, 12));

        Assert.NotNull(ev);
        Assert.Equal(value, ev.Value);
        Assert.Equal(amount, ev.Amount);
        Assert.Equal("54.00|USD", ev.SecondaryValue);
    }

    [Fact]
    public async Task PledgeUpdate_PaidChargeAtTheSameAmount_CountsInFull() {
        SubathonEvent? ev = await SendAsync(MakeService(), PatreonService.PledgeUpdated,
            Payload(lastCharge: MinutesAgo(2), chargeStatus: "Paid"));

        Assert.NotNull(ev);
        Assert.Equal("Gold", ev.Value);
        Assert.Equal("5.00|USD", ev.SecondaryValue);
    }

    [Theory]
    [InlineData(false, "Gold", "member")]
    [InlineData(true, "5.00", "USD")]
    public async Task PledgeUpdate_Upgrade_CountsTheTierOrTheDifference(bool byAmount, string value,
        string currency) {
        SubathonEvent? ev = await SendAsync(MakeService(byAmount: byAmount), PatreonService.PledgeUpdated,
            Payload(1000, 500, lastCharge: MinutesAgo(1), chargeStatus: "Paid"));

        Assert.NotNull(ev);
        Assert.Equal(value, ev.Value);
        Assert.Equal(currency, ev.Currency);
        Assert.Equal(1, ev.Amount);
        Assert.Equal("5.00|USD", ev.SecondaryValue);
    }

    [Fact]
    public async Task PledgeUpdate_AnnualCharge_IsNotMistakenForAnUpgrade() {
        SubathonEvent? ev = await SendAsync(MakeService(), PatreonService.PledgeUpdated,
            Payload(5400, 500, 12, lastCharge: MinutesAgo(1), chargeStatus: "Paid"));

        Assert.NotNull(ev);
        Assert.Equal(12, ev.Amount);
        Assert.Equal("54.00|USD", ev.SecondaryValue);
    }

    [Theory]
    [InlineData(300, 500, 1, "Paid")]
    [InlineData(500, 500, null, null)]
    [InlineData(500, 500, 2, "Declined")]
    [InlineData(500, 500, 120, "Paid")]
    public async Task PledgeUpdate_DowngradeOrNoFreshPaidCharge_IsIgnored(int willPay, int entitled,
        int? chargedMinutesAgo, string? chargeStatus) {
        Assert.Null(await SendAsync(MakeService(), PatreonService.PledgeUpdated,
            Payload(willPay, entitled, lastCharge: chargedMinutesAgo is { } m ? MinutesAgo(m) : null,
                chargeStatus: chargeStatus)));
    }

    [Theory]
    [InlineData(PatreonService.PledgeCreated, "declined_patron", false, null)]
    [InlineData(PatreonService.PledgeCreated, "former_patron", false, null)]
    [InlineData(PatreonService.PledgeCreated, "active_patron", true, null)]
    [InlineData(PatreonService.PledgeCreated, "active_patron", false, "Declined")]
    [InlineData(PatreonService.PledgeUpdated, "former_patron", false, "Paid")]
    public async Task InactiveTrialOrDeclinedPledges_AreIgnored(string eventType, string status, bool freeTrial,
        string? chargeStatus) {
        Assert.Null(await SendAsync(MakeService(), eventType,
            Payload(freeTrial: freeTrial, status: status, lastCharge: MinutesAgo(1), chargeStatus: chargeStatus)));
    }

    [Fact]
    public async Task MemberUpdate_NewPaidCharge_IsARenewal() {
        SubathonEvent? ev = await SendAsync(MakeService(), PatreonService.MemberUpdated,
            Payload(lastCharge: MinutesAgo(2), chargeStatus: "Paid"));

        Assert.NotNull(ev);
        Assert.Equal("Gold", ev.Value);
        Assert.Equal(1, ev.Amount);
        Assert.Equal("5.00|USD", ev.SecondaryValue);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(2, "Declined")]
    [InlineData(120, "Paid")]
    public async Task MemberUpdate_WithoutAFreshPaidCharge_IsIgnored(int? chargedMinutesAgo, string? chargeStatus) {
        Assert.Null(await SendAsync(MakeService(), PatreonService.MemberUpdated,
            Payload(lastCharge: chargedMinutesAgo is { } m ? MinutesAgo(m) : null, chargeStatus: chargeStatus)));
    }

    [Theory]
    [InlineData(PatreonService.PledgeCreated)]
    [InlineData(PatreonService.PledgeUpdated)]
    public async Task SameChargeOnMemberUpdate_HasTheSameId(string firstEvent) {
        PatreonService service = MakeService();
        DateTimeOffset charged = MinutesAgo(1);

        SubathonEvent? first = await SendAsync(service, firstEvent, Payload(1000, 500, lastCharge: charged,
            chargeStatus: "Paid"));
        SubathonEvent? same = await SendAsync(service, PatreonService.MemberUpdated, Payload(1000, 1000,
            lastCharge: charged, chargeStatus: "Paid"));
        SubathonEvent? renewal = await SendAsync(service, PatreonService.MemberUpdated, Payload(1000, 1000,
            lastCharge: charged.AddSeconds(30), chargeStatus: "Paid"));

        Assert.NotNull(first);
        Assert.NotNull(renewal);
        Assert.Equal(first.Id, same?.Id);
        Assert.NotEqual(first.Id, renewal.Id);
    }

    [Fact]
    public async Task RedeliveredWebhook_HasTheSameId() {
        PatreonService service = MakeService();
        string body = Payload();

        SubathonEvent? first = await SendAsync(service, PatreonService.PledgeCreated, body);
        SubathonEvent? again = await SendAsync(service, PatreonService.PledgeCreated, body);

        Assert.NotNull(first);
        Assert.Equal(first.Id, again?.Id);
    }

    [Theory]
    [InlineData("members:create", Secret)]
    [InlineData("members:pledge:delete", Secret)]
    [InlineData(PatreonService.PledgeCreated, "wrong-secret")]
    public async Task IgnoredOrUnverifiedWebhooks_RaiseNothing(string eventType, string signedWith) {
        Assert.Null(await SendAsync(MakeService(), eventType, Payload(), signedWith));
    }

    [Fact]
    public async Task NoStoredSecret_RaisesNothing() {
        Assert.Null(await SendAsync(MakeService(false), PatreonService.PledgeCreated, Payload()));
    }

    [Theory]
    [InlineData("True", true)]
    [InlineData("False", false)]
    public void PledgeCountsAsDonation_FromMode(string setting, bool expected) {
        IConfig config = MockConfig.MakeMockConfig(new Dictionary<(string, string), string> {
            { ("Patreon", $"{SubathonEventType.PatreonPledge}.CommissionAsDonation"), setting }
        });
        var ev = new SubathonEvent { EventType = SubathonEventType.PatreonPledge, SecondaryValue = "5.00|USD" };
        Assert.Equal(expected, Utils.IsCommissionAsDonation(config, ev));
    }

    [Fact]
    public void SimulateMembership_RaisesSimulatedEventForChosenTier() {
        PatreonService service = MakeService();

        SubathonEvent? ev = EventUtil.SubathonEventCapture.Capture(() => service.SimulateMembership("Silver", true));

        Assert.NotNull(ev);
        Assert.Equal(SubathonEventSource.Simulated, ev.Source);
        Assert.Equal("Silver", ev.Value);
        Assert.Equal(12, ev.Amount);
    }
}
