using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Agash.Webhook.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Patreon.Client;
using Patreon.Client.Events;
using Patreon.Client.JsonApi;
using Patreon.Client.Models;
using Patreon.Client.Webhooks;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;
using SubathonManager.Core.Security;
using SubathonManager.Core.Security.Interfaces;
using SubathonManager.Services;

namespace SubathonManager.Integration;

public class PatreonService(
    ILogger<PatreonService>? logger,
    IConfig config,
    IHttpClientFactory httpClientFactory,
    DevTunnelsService devTunnels,
    ISecureStorage secureStorage,
    OAuthService oAuth) : IWebhookIntegration {
    private const string ApiBase = "https://www.patreon.com/api/oauth2/v2/";
    internal const string PledgeCreated = "members:pledge:create";
    internal const string PledgeUpdated = "members:pledge:update";
    internal const string MemberUpdated = "members:update";

    // patreon tokens last ~30d, set to under to be safe
    private static readonly OAuthProvider OAuthKeys = new("patreon", StorageKeys.PatreonAccessToken,
        StorageKeys.PatreonRefreshToken, StorageKeys.PatreonTokenExpiry,
        DefaultLifetime: TimeSpan.FromDays(30), RefreshMargin: TimeSpan.FromDays(4));

    internal static readonly IReadOnlyList<string> Triggers = [PledgeCreated, PledgeUpdated, MemberUpdated];
    internal static readonly TimeSpan LiveWindow = TimeSpan.FromMinutes(30);

    private readonly PatreonWebhookHandler _handler = new(new PatreonWebhookSignatureVerifier());
    private readonly SemaphoreSlim _initLock = new(1, 1);

    // tier id -> (title, cents), refreshed from webhook payloads so renames follow the id
    // cents stored is for simulating/testing
    internal readonly ConcurrentDictionary<string, (string Title, int Cents)> Tiers = new();
    private string? _campaignId;
    private string _currency = "USD";

    private bool ByAmount => config.GetBool(nameof(SubathonEventSource.Patreon),
        $"{SubathonEventType.PatreonPledge}.ValueByAmount");

    public string WebhookPath => "/api/webhooks/patreon";

    public async Task StartAsync(CancellationToken ct = default) {
        IntegrationEvents.ConnectionUpdated += OnTunnelUpdated;
        if (HasTokens()) {
            logger?.LogInformation("[Patreon] Webhook listener ready at {Path}", WebhookPath);
            _ = devTunnels.StartTunnelAsync(ct);
        }
        else {
            logger?.LogInformation("[Patreon] Has not been setup before. Integration is disabled");
            BroadcastStatus(null);
        }

        await Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default) {
        IntegrationEvents.ConnectionUpdated -= OnTunnelUpdated;
        BroadcastStatus(null);
        return Task.CompletedTask;
    }

    public async Task HandleWebhookAsync(byte[] rawBody, IReadOnlyDictionary<string, string> headers,
        CancellationToken ct = default) {
        string? secret = secureStorage.GetOrDefault(StorageKeys.PatreonWebhookSecret, string.Empty);
        if (string.IsNullOrWhiteSpace(secret)) {
            logger?.LogWarning("[Patreon] Received a webhook but no signing secret is stored. Recreate the webhook");
            return;
        }

        Dictionary<string, string[]> requestHeaders = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> header in headers) requestHeaders[header.Key] = [header.Value];

        var request = new WebhookRequest {
            Method = "POST",
            Path = WebhookPath,
            Body = rawBody,
            ContentType = requestHeaders.TryGetValue("Content-Type", out string[]? type) ? type[0] : "application/json",
            Headers = requestHeaders
        };

        WebhookHandleResult<PatreonWebhookEvent> result =
            await _handler.HandleAsync(request, new PatreonWebhookOptions { WebhookSecret = secret }, ct);
        if (!result.IsAuthenticated) {
            logger?.LogWarning("[Patreon] Rejected webhook: {Reason}", result.FailureReason);
            return;
        }

        // string body = Encoding.UTF8.GetString(rawBody);
        // logger?.LogInformation("[Patreon] Received {Type} webhook: {Body}", result.Event?.EventType, body);

        (MemberAttributes? member, IReadOnlyList<string> tierIds, IReadOnlyList<JsonElement>? included) =
            result.Event switch {
                PatreonPledgeWebhookEvent { EventType: PledgeCreated or PledgeUpdated } p =>
                    (p.Attributes, p.EntitledTierIds, p.Document?.Included),
                PatreonMemberWebhookEvent { EventType: MemberUpdated } m =>
                    (m.Attributes, m.EntitledTierIds, m.Document?.Included),
                _ => (null, [], null)
            };
        if (member == null || string.IsNullOrWhiteSpace(result.Event?.ResourceId)) {
            if (logger?.IsEnabled(LogLevel.Debug) ?? false)
                logger?.LogDebug("[Patreon] Ignoring {Type} webhook", result.Event?.EventType);
            return;
        }

        var index = new JsonApiIncludedIndex(included);
        SubathonEvent? ev = MapPledge(result.Event.EventType, result.Event.ResourceId, member, tierIds, index,
            PatronVanity(member.FullName, index) ?? member.FullName?.Split(" ").First() ?? "Patreon Member",
            DateTimeOffset.UtcNow);
        if (ev == null) return;

        SubathonEvents.RaiseSubathonEventCreated(ev);
        if (logger?.IsEnabled(LogLevel.Debug) ?? false)
            logger?.LogDebug("[Patreon] Raised pledge {Id} from {User} ({Value})", ev.Id, ev.User, ev.Value);
    }

    public bool HasTokens() {
        return oAuth.HasTokens(OAuthKeys);
    }

    [ExcludeFromCodeCoverage]
    public async Task ConnectAsync(CancellationToken ct = default) {
        if (!await oAuth.AuthorizeAsync(OAuthKeys, ct)) {
            BroadcastStatus(null);
            return;
        }

        IntegrationEvents.ConnectionUpdated -= OnTunnelUpdated;
        IntegrationEvents.ConnectionUpdated += OnTunnelUpdated;
        await InitializeAsync(ct);
    }

    [ExcludeFromCodeCoverage]
    public async Task DisconnectAsync(CancellationToken ct = default) {
        string? webhookId = secureStorage.GetOrDefault(StorageKeys.PatreonWebhookId, string.Empty);
        if (!string.IsNullOrWhiteSpace(webhookId) && HasTokens())
            try {
                await CreateApiClient().DeleteWebhookAsync(webhookId, ct);
                logger?.LogInformation("[Patreon] Deleted webhook {Id}", webhookId);
            }
            catch (Exception ex) {
                logger?.LogWarning(ex, "[Patreon] Couldn't delete webhook {Id}, it may need removing on Patreon",
                    webhookId);
            }

        await StopAsync(ct);
        oAuth.RevokeTokens(OAuthKeys);
        secureStorage.Delete(StorageKeys.PatreonWebhookId);
        secureStorage.Delete(StorageKeys.PatreonWebhookSecret);
        BroadcastStatus(null);
    }

    [ExcludeFromCodeCoverage]
    public async Task RecreateWebhookAsync(CancellationToken ct = default) {
        secureStorage.Delete(StorageKeys.PatreonWebhookId);
        secureStorage.Delete(StorageKeys.PatreonWebhookSecret);
        await InitializeAsync(ct);
    }

    [ExcludeFromCodeCoverage]
    public async Task InitializeAsync(CancellationToken ct = default) {
        if (!await _initLock.WaitAsync(0, ct)) return;
        try {
            await InitializeCoreAsync(ct);
        }
        catch (Exception ex) {
            logger?.LogError(ex, "[Patreon] Failed to initialize");
            ErrorMessageEvents.RaiseErrorEvent("ERROR", nameof(SubathonEventSource.Patreon),
                $"Patreon failed to connect: {ex.Message}", DateTime.Now);
            BroadcastStatus(null);
        }
        finally {
            _initLock.Release();
        }
    }

    private HttpClient CreateHttpClient() {
        HttpClient http = httpClientFactory.CreateClient(nameof(PatreonService));
        http.BaseAddress = new Uri(ApiBase);
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", oAuth.GetAccessToken(OAuthKeys));
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SubathonManager");
        return http;
    }

    private PatreonApiClient CreateApiClient() {
        return new PatreonApiClient(CreateHttpClient(), NullLogger<PatreonApiClient>.Instance);
    }

    [ExcludeFromCodeCoverage]
    private async Task<(string? Id, string? Secret)> CreateWebhookAsync(string url, CancellationToken ct) {
        var payload = new {
            data = new {
                type = "webhook",
                attributes = new { triggers = Triggers, uri = url },
                relationships = new { campaign = new { data = new { type = "campaign", id = _campaignId } } }
            }
        };

        using HttpClient http = CreateHttpClient();
        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await http.PostAsync("webhooks", content, ct);
        string body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) return (null, null);

        using JsonDocument doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("data", out JsonElement data)) {
            logger?.LogWarning("[Patreon] Webhook create response had no data: {Body}", body);
            return (null, null);
        }

        string? id = data.TryGetProperty("id", out JsonElement idEl) ? idEl.GetString() : null;
        string? secret = data.TryGetProperty("attributes", out JsonElement attrs) &&
                         attrs.TryGetProperty("secret", out JsonElement secretEl)
            ? secretEl.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(secret))
            logger?.LogWarning("[Patreon] Webhook create response was missing {Missing}: {Body}",
                string.IsNullOrWhiteSpace(id) ? "the id" : "the secret", body);
        return (id, secret);
    }

    [ExcludeFromCodeCoverage]
    private async Task InitializeCoreAsync(CancellationToken ct) {
        if (await devTunnels.RequireTunnelAsync(SubathonEventSource.Patreon, ct) is not { } tunnelConn) {
            BroadcastStatus(null);
            return;
        }

        if (!HasTokens() || (oAuth.NeedsRefresh(OAuthKeys) && !await oAuth.RefreshAsync(OAuthKeys, ct))) {
            logger?.LogWarning("[Patreon] Login is missing or expired");
            ErrorMessageEvents.RaiseErrorEvent("WARN", nameof(SubathonEventSource.Patreon),
                "Patreon login has expired. Reconnect it in the Patreon settings.", DateTime.Now);
            BroadcastStatus(null);
            return;
        }

        PatreonApiClient client = CreateApiClient();
        JsonApiCollectionDocument<CampaignAttributes>? campaigns =
            await client.GetCampaignsAsync(["name", "currency"], cancellationToken: ct);
        JsonApiResource<CampaignAttributes>? campaign = campaigns?.Data?.Count > 0 ? campaigns?.Data?[0] : null;

        if (campaign == null) {
            logger?.LogWarning("[Patreon] The connected account has no campaign");
            ErrorMessageEvents.RaiseErrorEvent("WARN", nameof(SubathonEventSource.Patreon),
                "The connected Patreon account doesn't have a creator campaign.", DateTime.Now);
            BroadcastStatus(null);
            return;
        }

        _campaignId = campaign.Id;
        if (!string.IsNullOrWhiteSpace(campaign.Attributes?.Currency)) _currency = campaign.Attributes.Currency;
        logger?.LogInformation("[Patreon] Connected to campaign {Name}", campaign.Attributes?.Name);

        IReadOnlyList<JsonApiResource<TierAttributes>>? tiers =
            await client.GetCampaignTiersAsync(campaign.Id, ["title", "amount_cents", "published"], ct);
        Tiers.Clear();

        foreach (JsonApiResource<TierAttributes> tier in tiers ?? [])
            if (tier.Attributes is { AmountCents: > 0 } attrs && !string.IsNullOrWhiteSpace(attrs.Title))
                Tiers[tier.Id] = (attrs.Title, attrs.AmountCents);
        IntegrationEvents.RaiseMembershipTiersSynced(SubathonEventSource.Patreon,
            Tiers.Values.Select(t => t.Title).ToList());

        string url = tunnelConn.Name.TrimEnd('/') + WebhookPath;
        if (!await EnsureWebhookAsync(client, url, ct)) {
            BroadcastStatus(null);
            return;
        }

        BroadcastStatus(tunnelConn.Name);
    }

    [ExcludeFromCodeCoverage]
    private async Task<bool> EnsureWebhookAsync(PatreonApiClient client, string url, CancellationToken ct) {
        string? storedId = secureStorage.GetOrDefault(StorageKeys.PatreonWebhookId, string.Empty);
        string? secret = secureStorage.GetOrDefault(StorageKeys.PatreonWebhookSecret, string.Empty);

        IReadOnlyList<JsonApiResource<WebhookAttributes>> hooks = (await client.GetWebhooksAsync(ct))?.Data ?? [];

        JsonApiResource<WebhookAttributes>? ours = string.IsNullOrWhiteSpace(storedId)
            ? null
            : hooks.FirstOrDefault(h => h.Id == storedId);

        if (ours != null && !string.IsNullOrWhiteSpace(secret)) {
            WebhookAttributes? attrs = ours.Attributes;
            bool triggersMatch = attrs?.Triggers != null && Triggers.All(attrs.Triggers.Contains);
            if (attrs?.Uri == url && !attrs.Paused && triggersMatch) return true;

            JsonApiDocument<WebhookAttributes>? updated = null;
            try {
                updated = await client.UpdateWebhookAsync(ours.Id, false, url, Triggers, ct);
            }
            catch (Exception ex) {
                logger?.LogWarning(ex, "[Patreon] Updating webhook {Id} threw", ours.Id);
            }

            if (updated?.Data != null) {
                logger?.LogInformation("[Patreon] Updated webhook {Id} (url changed, paused, or triggers changed)",
                    ours.Id);
                return true;
            }

            logger?.LogWarning("[Patreon] Couldn't update webhook {Id}, recreating it", ours.Id);
        }

        foreach (JsonApiResource<WebhookAttributes> stale in hooks.Where(h =>
                     h.Attributes?.Uri?.EndsWith(WebhookPath, StringComparison.OrdinalIgnoreCase) == true))
            try {
                await client.DeleteWebhookAsync(stale.Id, ct);
                logger?.LogInformation("[Patreon] Removed stale webhook {Id} ({Uri})", stale.Id, stale.Attributes?.Uri);
            }
            catch (Exception ex) {
                logger?.LogWarning(ex, "[Patreon] Couldn't remove stale webhook {Id}", stale.Id);
            }

        (string? newId, string? newSecret) = await CreateWebhookAsync(url, ct);
        if (string.IsNullOrWhiteSpace(newId) || string.IsNullOrWhiteSpace(newSecret)) {
            logger?.LogWarning("[Patreon] Couldn't create a webhook for {Url} on campaign {Campaign}", url,
                _campaignId);
            ErrorMessageEvents.RaiseErrorEvent("ERROR", nameof(SubathonEventSource.Patreon),
                "Couldn't create the Patreon webhook, check the log for Patreon's reply. " +
                "Try Recreate Webhook in the Patreon settings.", DateTime.Now);
            return false;
        }

        secureStorage.Set(StorageKeys.PatreonWebhookId, newId);
        secureStorage.Set(StorageKeys.PatreonWebhookSecret, newSecret);
        logger?.LogInformation("[Patreon] Created webhook {Id}", newId);
        return true;
    }

    internal SubathonEvent? MapPledge(string eventType, string memberId, MemberAttributes? member,
        IReadOnlyList<string> tierIds, JsonApiIncludedIndex included, string? name, DateTimeOffset now) {
        if (member is not { PatronStatus: "active_patron", IsFreeTrial: false }) return null;

        int willPay = member.WillPayAmountCents;
        int entitled = member.CurrentlyEntitledAmountCents;
        // possibly a downgrade if weillPay is 0
        if (willPay <= 0 || entitled <= 0) return null;

        // get highest if multiple come for some reason
        string tier = tierIds
            .Select(id => ResolveTier(included, id))
            .OfType<(string Title, int Cents)>()
            .OrderByDescending(t => t.Cents)
            .Select(t => t.Title)
            .FirstOrDefault() ?? "DEFAULT";
        string currency = CampaignCurrency(included);

        // annual or other term cadence: will_pay is the whole cadence while entitled is per month
        // if tier mode we multiply the config for tier by cadence amount
        // if amount/mode mode, we ignore cadence as amount is amount charged/paid at time of
        int cadence = member.PledgeCadence ?? 1;

        DateTimeOffset? charged = Utils.ParseDateFromString(member.LastChargeDate);
        var chargeKey = $"patreon|{memberId}|charge|{member.LastChargeDate}";

        if (eventType == PledgeCreated) {
            if (!string.IsNullOrEmpty(member.LastChargeStatus) && !IsPaid(member.LastChargeStatus)) return null;
            return BuildPledgeEvent(
                charged != null ? chargeKey : $"patreon|{memberId}|start|{member.PledgeRelationshipStart}",
                name, tier, willPay, currency, cadence, Utils.ParseDateFromString(member.PledgeRelationshipStart) ?? now);
        }

        // charge date needed for members:update to be within window
        // created and update for pledge can be null just bc it's surely a pledge, but also, their test webhooks have it null...
        if (charged != null && (!IsPaid(member.LastChargeStatus) || charged < now - LiveWindow)) return null;
        if (eventType == MemberUpdated)
            return charged is { } paidAt
                ? BuildPledgeEvent(chargeKey, name, tier, willPay, currency, cadence, paidAt)
                : null;

        // assumes an upgrade shows will_pay below entitled so only the difference counts
        // unless its upgrade to annual, then will pay is above likely. Either way we don't care much
        // we get the amount charged anyways
        return BuildPledgeEvent(chargeKey, name, tier, willPay, currency,
            cadence, charged ?? now);
    }

    private static string? PatronVanity(string? fullName, JsonApiIncludedIndex included) {
        foreach (JsonElement user in included.GetAllOfType("user")) {
            JsonElement attrs = user.TryGetProperty("attributes", out JsonElement nested) ? nested : user;
            string? tryName = Utils.GetJsonString(attrs, "full_name");
            if (!string.IsNullOrWhiteSpace(tryName) &&
                tryName.Equals(fullName, StringComparison.InvariantCultureIgnoreCase) &&
                Utils.GetJsonString(attrs, "vanity") is { Length: > 0 } vanity)
                return vanity;
        }

        return null;
    }

    private static bool IsPaid(string? status) {
        return string.Equals(status, "Paid", StringComparison.OrdinalIgnoreCase);
    }

    private string CampaignCurrency(JsonApiIncludedIndex included) {
        foreach (JsonElement campaign in included.GetAllOfType("campaign")) {
            JsonElement attrs = campaign.TryGetProperty("attributes", out JsonElement nested) ? nested : campaign;
            if (Utils.GetJsonString(attrs, "currency") is { Length: > 0 } currency) return currency;
        }

        return _currency;
    }

    private (string Title, int Cents)? ResolveTier(JsonApiIncludedIndex included, string id) {
        JsonElement attrs = default;
        if (included.TryGet("tier", id, out JsonElement tier))
            attrs = tier.TryGetProperty("attributes", out JsonElement nested) ? nested : tier;
        if (attrs.ValueKind != JsonValueKind.Object || Utils.GetJsonString(attrs, "title") is not { Length: > 0 } title)
            return Tiers.TryGetValue(id, out (string Title, int Cents) known) ? known : null;

        int cents = attrs.TryGetProperty("amount_cents", out JsonElement amount) && amount.TryGetInt32(out int c)
            ? c
            : Tiers.TryGetValue(id, out (string Title, int Cents) cached) ? cached.Cents : 0;
        bool newTitle = Tiers.Values.All(t => t.Title != title);
        Tiers[id] = (title, cents);
        if (newTitle)
            IntegrationEvents.RaiseMembershipTiersSynced(SubathonEventSource.Patreon,
                Tiers.Values.Select(t => t.Title).ToList());
        return (title, cents);
    }

    private SubathonEvent BuildPledgeEvent(string idKey, string? name, string tierName, int cents,
        string? currency, int months, DateTimeOffset timestamp) {
        string user = string.IsNullOrWhiteSpace(name) || name.Equals("Patreon Member") ? "Patreon Member" : name;
        string chargedIn = string.IsNullOrWhiteSpace(currency) ? _currency : currency;
        var amount = (cents / 100.0).ToString("F2", CultureInfo.InvariantCulture);
        bool byAmount = ByAmount;

        return new SubathonEvent {
            Id = Utils.CreateGuidFromUniqueString(idKey),
            Source = user == "SYSTEM" ? SubathonEventSource.Simulated : SubathonEventSource.Patreon,
            EventType = SubathonEventType.PatreonPledge,
            User = user,
            Value = byAmount ? amount : tierName,
            EventTypeMeta = byAmount ? Utils.PerUnitMeta : tierName,
            Currency = byAmount ? chargedIn : "member",
            Amount = byAmount ? 1 : Math.Max(months, 1),
            SecondaryValue = $"{amount}|{chargedIn}",
            TertiaryValue = tierName,
            EventTimestamp = timestamp.LocalDateTime
        };
    }

    public void SimulateMembership(string tierName, bool annual) {
        int cents = Tiers.Values.FirstOrDefault(t => t.Title == tierName).Cents is > 0 and var known ? known : 500;
        SubathonEvent ev = BuildPledgeEvent($"patreon|simulated|{Guid.NewGuid()}", "SYSTEM", tierName,
            annual ? cents * 12 : cents, _currency, annual ? 12 : 1, DateTimeOffset.Now);
        SubathonEvents.RaiseSubathonEventCreated(ev);
    }

    [ExcludeFromCodeCoverage]
    private void OnTunnelUpdated(IntegrationConnection connection) {
        if (connection is not { Source: SubathonEventSource.DevTunnels, Service: "Tunnel" }) return;
        if (HasTokens() && connection.Status) {
            Task.Run(() => InitializeAsync());
            return;
        }

        BroadcastStatus(null);
    }

    private void BroadcastStatus(string? tunnelBaseUrl) {
        string? fullUrl = !string.IsNullOrWhiteSpace(tunnelBaseUrl) && tunnelBaseUrl != "(starting...)"
            ? tunnelBaseUrl.TrimEnd('/') + WebhookPath
            : null;

        IntegrationEvents.RaiseConnectionUpdate(new IntegrationConnection {
            Name = fullUrl ?? "",
            Status = HasTokens() && fullUrl != null,
            Source = SubathonEventSource.Patreon,
            Service = nameof(SubathonEventSource.Patreon),
            Configured = HasTokens()
        });
    }

    public sealed class ErrorLoggingHandler(ILogger? logger) : DelegatingHandler {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) {
            HttpResponseMessage response = await base.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode) return response;

            await response.Content.LoadIntoBufferAsync(cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            logger?.LogWarning("[Patreon] {Method} {Url} returned HTTP {Status}: {Body}", request.Method,
                request.RequestUri, (int)response.StatusCode, body.Length > 2000 ? body[..2000] : body);
            return response;
        }
    }
}