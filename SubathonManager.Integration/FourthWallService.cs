using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Agash.Webhook.Abstractions;
using Fourthwall.Client.Authentication;
using Fourthwall.Client.Events;
using Fourthwall.Client.Generated;
using Fourthwall.Client.Generated.Models.App.Openapi.Endpoint.OpenApiWebhookConfigurationEndpoint;
using Fourthwall.Client.Generated.Models.Openapi.Model;
using Fourthwall.Client.Generated.Models.Openapi.Model.DonationV1;
using Fourthwall.Client.Generated.Models.Openapi.Model.GiftPurchaseV1;
using Fourthwall.Client.Generated.Models.Openapi.Model.MembershipSupporterV1;
using Fourthwall.Client.Generated.Models.Openapi.Model.MembershipSupporterV1.Subscription;
using Fourthwall.Client.Generated.Models.Openapi.Model.OrderV1;
using Fourthwall.Client.Options;
using Fourthwall.Client.Webhooks;
using Microsoft.Extensions.Logging;
using Microsoft.Kiota.Http.HttpClientLibrary;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;
using SubathonManager.Core.Security;
using SubathonManager.Services;
using WebhookConfigurationV1 =
    Fourthwall.Client.Generated.Models.Openapi.Model.OpenApiPageResponseCom.Fourthwall.Openapi.Model.
    WebhookConfigurationV1;

// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.Integration;

public class FourthWallService(
    ILogger<FourthWallService>? logger,
    IConfig config,
    DevTunnelsService devTunnels,
    OAuthService oAuth)
    : IWebhookIntegration, IMissedEventSource {
    private readonly string _configSection = "FourthWall";

    private readonly FourthwallWebhookHandler _handler = new(new FourthwallWebhookSignatureVerifier());
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private static readonly OAuthProvider OAuthKeys = new("fourthwall", StorageKeys.FourthWallAccessToken,
        StorageKeys.FourthWallRefreshToken);

    public readonly Dictionary<string, string> MembershipNames = new();
    internal int MaxMissedPages = 50;
    internal int MissedPageSize = 100;

    private string? AccessToken => oAuth.GetAccessToken(OAuthKeys);
    private string? ShopName { get; set; }
    public string WebhookPath => "/api/webhooks/fourthwall";
    public const string AutoDeleteCancelledKey = "AutoDeleteCancelledOrders";

    public async Task StartAsync(CancellationToken ct = default) {
        IntegrationEvents.ConnectionUpdated += OnTunnelUpdated;

        bool enabled = HasTokenFile();

        if (enabled) {
            logger?.LogInformation("[FourthWall] Webhook listener ready at {Path}", WebhookPath);
            _ = devTunnels.StartTunnelAsync(ct);
        }
        else {
            logger?.LogInformation("[FourthWall] Has not been setup before. Integration is disabled");
            BroadcastStatus(false, null);
        }

        await Task.CompletedTask;
    }


    public Task StopAsync(CancellationToken ct = default) {
        IntegrationEvents.ConnectionUpdated -= OnTunnelUpdated;
        BroadcastStatus(false, null);

        return Task.CompletedTask;
    }

    public async Task HandleWebhookAsync(byte[] rawBody, IReadOnlyDictionary<string, string> headers,
        CancellationToken ct = default) {
        string contentType = headers.TryGetValue("Content-Type", out string? ct2)
            ? ct2
            : "application/x-www-form-urlencoded";

        Dictionary<string, string[]> newHeaders = headers.ToDictionary(
            kvp => kvp.Key,
            kvp => new[] { kvp.Value }
        );
        IReadOnlyDictionary<string, string[]> readOnlyHeaders = newHeaders;

        var request = new WebhookRequest {
            Method = "POST",
            Path = WebhookPath,
            Body = rawBody,
            ContentType = contentType,
            Headers = readOnlyHeaders
        };

        WebhookHandleResult<FourthwallWebhookEvent> result = await _handler.HandleAsync(request,
            new FourthwallWebhookOptions {
                SigningSecret = "",
                SignatureMode = FourthwallWebhookSignatureMode.PlatformAppWebhook
            }, ct);

        if (!result.IsKnownEvent || result.Event is null) {
            logger?.LogDebug("[FourthWall] Received unknown/unsupported FourthWall event type");
            return;
        }

        if (result.Event is FourthwallOrderUpdatedWebhookEvent orderUpdated) {
            HandleOrderUpdated(orderUpdated);
            return;
        }

        SubathonEvent? ev = MapToSubathonEvent(result.Event);
        if (ev != null) {
            SubathonEvents.RaiseSubathonEventCreated(ev);
            logger?.LogDebug("[FourthWall] Raised {EventType} from {User}", ev.EventType, ev.User);
        }
    }

    internal bool HandleOrderUpdated(FourthwallOrderUpdatedWebhookEvent orderUpdated) {
        OrderV1? order = orderUpdated.Data?.Order;
        if (order?.Status != OrderV1_status.CANCELLED || string.IsNullOrWhiteSpace(order.Id)) return false;

        string reference = $"#{order.FriendlyId ?? order.Id}";
        if (!config.GetBool(_configSection, AutoDeleteCancelledKey, false)) {
            logger?.LogInformation("[FourthWall] Order {Order} was cancelled, auto delete is off so its event is kept",
                reference);
            return false;
        }

        SubathonEvents.RaiseSubathonEventCancelled(Utils.TryParseGuid(order.Id), SubathonEventType.FourthWallOrder,
            reference);
        return true;
    }

    [ExcludeFromCodeCoverage]
    private async Task<bool> CheckForTokenAsync(CancellationToken ct = default) {
        if (!HasTokenFile())
            await oAuth.AuthorizeAsync(OAuthKeys, ct);
        else if (oAuth.NeedsRefresh(OAuthKeys) && !await oAuth.RefreshAsync(OAuthKeys, ct))
            await oAuth.AuthorizeAsync(OAuthKeys, ct);

        return HasTokenFile() && !oAuth.NeedsRefresh(OAuthKeys);
    }

    [ExcludeFromCodeCoverage]
    public async Task Initialize(CancellationToken ct = default) {
        if (!await _initLock.WaitAsync(0, ct)) return;
        try {
            await InitializeCoreAsync(ct);
        }
        finally {
            _initLock.Release();
        }
    }

    [ExcludeFromCodeCoverage]
    private async Task InitializeCoreAsync(CancellationToken ct) {
        if (await devTunnels.RequireTunnelAsync(SubathonEventSource.FourthWall, ct) is not { } tunnelConn) {
            BroadcastStatus(HasTokenFile(), null);
            return;
        }

        bool canConnect = await CheckForTokenAsync(ct);
        if (!canConnect || string.IsNullOrWhiteSpace(AccessToken)) {
            RevokeTokenFile();
            BroadcastStatus(false, null);
            return;
        }

        bool enabled = HasTokenFile();
        var client =
            new FourthwallApiClient(
                new HttpClientRequestAdapter(new FourthwallBearerAuthenticationProvider(AccessToken)));

        ShopV1? shop = await client.OpenApi.V10.Shops.Current.GetAsync(cancellationToken: ct);
        logger?.LogInformation("[FourthWall] connected to {shopName}", shop?.Name ?? "none");
        if (shop == null) {
            BroadcastStatus(false, null);
            return;
        }

        ;

        ShopName = shop.Name;
        WebhookConfigurationV1? webhooks = await client.OpenApi.V10.Webhooks.GetAsync(cancellationToken: ct);

        var hasWh = false;
        if (webhooks is { Results: not null })
            foreach (Fourthwall.Client.Generated.Models.Openapi.Model.WebhookConfigurationV1 webhookConfigurationV1 in
                     webhooks.Results)
                if (!string.IsNullOrWhiteSpace(webhookConfigurationV1.Url) &&
                    webhookConfigurationV1.Url.Contains(tunnelConn.Name.Replace("https://", ""),
                        StringComparison.CurrentCultureIgnoreCase)) {
                    hasWh = true;
                    logger?.LogDebug("Webhook found, no need to make a new one for fourthwall");
                    if (webhookConfigurationV1.AllowedTypes?.Contains(WebhookConfigurationV1_allowedTypes.ORDER_UPDATED) != true &&
                        !string.IsNullOrWhiteSpace(webhookConfigurationV1.Id))
                        try {
                            List<WebhookConfigurationUpdateRequest_allowedTypes?> types =
                                (webhookConfigurationV1.AllowedTypes ?? [])
                                .Select(t => Enum.TryParse($"{t}", out WebhookConfigurationUpdateRequest_allowedTypes u)
                                    ? u
                                    : (WebhookConfigurationUpdateRequest_allowedTypes?)null)
                                .Where(t => t != null)
                                .Append(WebhookConfigurationUpdateRequest_allowedTypes.ORDER_UPDATED)
                                .ToList();
                            await client.OpenApi.V10.Webhooks[webhookConfigurationV1.Id].PutAsync(
                                new WebhookConfigurationUpdateRequest {
                                    Url = webhookConfigurationV1.Url, AllowedTypes = types
                                }, cancellationToken: ct);
                            logger?.LogInformation("[FourthWall] Added order updates to existing webhook");
                        }
                        catch (Exception ex) {
                            logger?.LogWarning(ex, "[FourthWall] Couldn't add order updates to existing webhook");
                        }

                    break;
                }

        if (!hasWh) {
            string? fullUrl = !string.IsNullOrWhiteSpace(tunnelConn.Name) && tunnelConn.Name != "(starting...)"
                ? tunnelConn.Name.TrimEnd('/') + WebhookPath
                : null;
            var req = new WebhookConfigurationCreateRequest {
                Url = fullUrl,
                AllowedTypes = [
                    WebhookConfigurationCreateRequest_allowedTypes.ORDER_PLACED,
                    WebhookConfigurationCreateRequest_allowedTypes.ORDER_UPDATED,
                    WebhookConfigurationCreateRequest_allowedTypes.DONATION,
                    WebhookConfigurationCreateRequest_allowedTypes.SUBSCRIPTION_PURCHASED,
                    WebhookConfigurationCreateRequest_allowedTypes.SUBSCRIPTION_CHANGED,
                    WebhookConfigurationCreateRequest_allowedTypes.GIFT_PURCHASE
                ]
            };
            Fourthwall.Client.Generated.Models.Openapi.Model.WebhookConfigurationV1? resp =
                await client.OpenApi.V10.Webhooks.PostAsync(req, cancellationToken: ct);
            if (resp == null || string.IsNullOrWhiteSpace(resp.Url)) {
                logger?.LogWarning("[FourthWall] Unable to create Webhook configuration");
                BroadcastStatus(false, null);
                return;
            }

            hasWh = true;
            logger?.LogInformation("[FourthWall] Webhook successfully made: {WHId}", resp.Id);
        }

        List<MembershipTierV1>? memberships =
            await client.OpenApi.V10.Memberships.Tiers.GetAsync(cancellationToken: ct);
        if (memberships is { Count: > 0 })
            memberships.ForEach(x => {
                if (string.IsNullOrWhiteSpace(x.Id) || string.IsNullOrWhiteSpace(x.Name)) return;

                MembershipNames[x.Id] = x.Name;
            });
        IntegrationEvents.RaiseMembershipTiersSynced(SubathonEventSource.FourthWall, MembershipNames.Values.ToList());

        // Seed status; include public URL if the tunnel is already running
        tunnelConn = Utils.GetConnection(SubathonEventSource.DevTunnels, "Tunnel");
        BroadcastStatus(enabled, tunnelConn.Status ? tunnelConn.Name : null);
        await Task.CompletedTask;
    }

    private bool HasTokenFile() {
        return oAuth.HasTokens(OAuthKeys);
    }

    [ExcludeFromCodeCoverage]
    public void RevokeTokenFile() {
        oAuth.RevokeTokens(OAuthKeys);
    }

    [ExcludeFromCodeCoverage]
    private void OnTunnelUpdated(IntegrationConnection connection) {
        if (connection is not { Source: SubathonEventSource.DevTunnels, Service: "Tunnel" }) return;

        bool enabled = HasTokenFile();
        if (enabled && connection.Status) {
            Task.Run(() => Initialize());
            return;
        }

        BroadcastStatus(enabled, connection.Status ? connection.Name : null);
    }

    private void BroadcastStatus(bool enabled, string? tunnelBaseUrl) {
        string? fullUrl = !string.IsNullOrWhiteSpace(tunnelBaseUrl) && tunnelBaseUrl != "(starting...)"
            ? tunnelBaseUrl.TrimEnd('/') + WebhookPath
            : null;

        IntegrationEvents.RaiseConnectionUpdate(new IntegrationConnection {
            Name = fullUrl ?? "",
            Status = enabled && fullUrl != null,
            Source = SubathonEventSource.FourthWall,
            Service = nameof(SubathonEventSource.FourthWall),
            Configured = enabled
        });

        if (fullUrl != null)
            logger?.LogInformation("[FourthWall] Public webhook URL: {Url}", fullUrl);
    }

    public SubathonEvent? MapToSubathonEvent(FourthwallWebhookEvent fwEvent) {
        try {
            OrderTypeModes sourceMode2 = config.GetOrderTypeMode(_configSection,
                $"{SubathonEventType.FourthWallGiftOrder}", OrderTypeModes.Dollar);
            var defaultCurrency = "USD";
            var username = "FourthWall Customer";
            if (fwEvent.TestMode) username = "FourthWall Test"; //"SYSTEM";
            SubathonEvent? ev = null;
            if (fwEvent is FourthwallDonationWebhookEvent donationEvent) {
                ev = MapDonation(donationEvent.Data, fwEvent.TestMode);
            }
            else if (fwEvent is FourthwallOrderPlacedWebhookEvent orderPlacedEvent) {
                ev = MapOrder(orderPlacedEvent.Data, fwEvent.TestMode);
            }
            else if (fwEvent is FourthwallGiftPurchaseWebhookEvent giftPurchaseWebhookEvent) {
                GiftPurchaseV1 order = giftPurchaseWebhookEvent.Data;
                if (!fwEvent.TestMode && !string.IsNullOrWhiteSpace(order.Username))
                    username = order.Username.Split(' ').First();
                var itemCount = 1;
                if (order.Quantity != null) itemCount = order.Quantity.Value;

                double totalValue = 0;
                double totalDirect = 0;
                string currency = !string.IsNullOrWhiteSpace(order.Amounts?.Subtotal?.Currency)
                    ? order.Amounts.Subtotal.Currency
                    : defaultCurrency;

                totalValue += order.Amounts?.Subtotal?.Value ?? 0;
                totalDirect += order.Amounts?.Profit?.Value ?? 0;

                ev = new SubathonEvent {
                    Id = Utils.TryParseGuid(order.Id),
                    Source = string.Equals(username, "SYSTEM")
                        ? SubathonEventSource.Simulated
                        : SubathonEventSource.FourthWall,
                    EventType = SubathonEventType.FourthWallGiftOrder,
                    User = username,
                    Value = sourceMode2 switch {
                        OrderTypeModes.Item => $"{itemCount}",
                        OrderTypeModes.Order => "New Gift",
                        _ => totalValue.ToString("F2", CultureInfo.InvariantCulture)
                    },
                    Currency = sourceMode2 switch {
                        OrderTypeModes.Item => "items",
                        OrderTypeModes.Order => "order",
                        _ => !string.IsNullOrWhiteSpace(currency) ? currency : defaultCurrency
                    },
                    Amount = Math.Max(itemCount, 1),
                    SecondaryValue = $"{totalDirect.ToString("F2", CultureInfo.InvariantCulture)}|{
                        (!string.IsNullOrWhiteSpace(currency) ? currency : defaultCurrency)}",
                    EventTimestamp = order.CreatedAt?.LocalDateTime ?? DateTime.Now.ToLocalTime()
                };
            }
            else if (fwEvent is FourthwallSubscriptionPurchasedWebhookEvent subscriptionPurchasedWebhookEvent) {
                MembershipSupporterV1 data = subscriptionPurchasedWebhookEvent.Data;
                if (!fwEvent.TestMode && !string.IsNullOrWhiteSpace(data.Nickname))
                    username = data.Nickname.Split(' ').First();
                Active? subscription = data.Subscription?.Active;
                if (subscription == null) return null;
                var tierName = "DEFAULT";
                MembershipNames.TryGetValue(subscription.Variant?.TierId ?? "DEFAULT", out tierName);
                if (string.IsNullOrWhiteSpace(tierName)) tierName = "DEFAULT";

                ev = new SubathonEvent {
                    Id = fwEvent.TestMode ? Guid.NewGuid() : Utils.TryParseGuid(data.Id),
                    Source = string.Equals(username, "SYSTEM")
                        ? SubathonEventSource.Simulated
                        : SubathonEventSource.FourthWall,
                    EventType = SubathonEventType.FourthWallMembership,
                    User = username,
                    Value = tierName,
                    EventTypeMeta = tierName,
                    Currency = "member",
                    Amount = subscription.Variant?.Interval == MembershipTierVariantV1_interval.ANNUAL ? 12 : 1,
                    SecondaryValue =
                        $"{(subscription.Variant?.Amount?.Value ?? 0).ToString("F2", CultureInfo.InvariantCulture)}|{
                            (!string.IsNullOrWhiteSpace(subscription.Variant?.Amount?.Currency) ? subscription.Variant?.Amount?.Currency : defaultCurrency)}",
                    EventTimestamp = subscriptionPurchasedWebhookEvent.CreatedAt.LocalDateTime
                };
            }
            else if (fwEvent is FourthwallSubscriptionChangedWebhookEvent subscriptionChangedWebhookEvent) {
                MembershipSupporterV1 data = subscriptionChangedWebhookEvent.Data;
                if (!fwEvent.TestMode && !string.IsNullOrWhiteSpace(data.Nickname))
                    username = data.Nickname.Split(' ').First();
                Active? subscription = data.Subscription?.Active;
                if (subscription == null) return null;
                var tierName = "DEFAULT";
                MembershipNames.TryGetValue(subscription.Variant?.TierId ?? "DEFAULT", out tierName);
                if (string.IsNullOrWhiteSpace(tierName)) tierName = "DEFAULT";

                ev = new SubathonEvent {
                    Id = fwEvent.TestMode ? Guid.NewGuid() : Utils.TryParseGuid(data.Id),
                    Source = string.Equals(username, "SYSTEM")
                        ? SubathonEventSource.Simulated
                        : SubathonEventSource.FourthWall,
                    EventType = SubathonEventType.FourthWallMembership,
                    User = username,
                    Value = tierName,
                    EventTypeMeta = tierName,
                    Currency = "member",
                    Amount = subscription.Variant?.Interval == MembershipTierVariantV1_interval.ANNUAL ? 12 : 1,
                    SecondaryValue =
                        $"{(subscription.Variant?.Amount?.Value ?? 0).ToString("F2", CultureInfo.InvariantCulture)}|{
                            (!string.IsNullOrWhiteSpace(subscription.Variant?.Amount?.Currency) ? subscription.Variant?.Amount?.Currency : defaultCurrency)}",
                    EventTimestamp = subscriptionChangedWebhookEvent.CreatedAt.LocalDateTime
                };
            }

            return ev;
        }
        catch (Exception ex) {
            logger?.LogWarning(ex, "[FourthWall] Failed to map FourthWall event to SubathonEvent");
            return null;
        }
    }

    private static SubathonEvent MapDonation(DonationV1 d, bool testMode) {
        var username = testMode ? "FourthWall Test" : "FourthWall Customer";
        if (!testMode && !string.IsNullOrWhiteSpace(d.Username))
            username = d.Username.Split(' ').First();
        return new SubathonEvent {
            Id = Utils.TryParseGuid(d.Id),
            Source = string.Equals(username, "SYSTEM")
                ? SubathonEventSource.Simulated
                : SubathonEventSource.FourthWall,
            EventType = SubathonEventType.FourthWallDonation,
            User = username,
            Value = d.Amounts?.Total?.Value?.ToString("F2", CultureInfo.InvariantCulture) ?? "0.00",
            Currency = !string.IsNullOrWhiteSpace(d.Amounts?.Total?.Currency) ? d.Amounts?.Total?.Currency : "USD",
            EventTimestamp = d.CreatedAt?.LocalDateTime ?? DateTime.Now.ToLocalTime()
        };
    }

    private static string GetOrderType(OrderV1 order) {
        OrderV1.OrderV1_source? source = order.Source;
        return source?.Order?.Type ?? source?.SamplesOrder?.Type ?? source?.TwitchGiftRedemption?.Type ??
            source?.GiveawayLinks?.Type ?? "";
    }

    private SubathonEvent? MapOrder(OrderV1 order, bool testMode) {
        string orderType = GetOrderType(order);
        bool isSamples = string.Equals("SAMPLES_ORDER", orderType, StringComparison.CurrentCultureIgnoreCase);
        if (!isSamples && !string.Equals("ORDER", orderType, StringComparison.CurrentCultureIgnoreCase))
            return null;

        OrderTypeModes sourceMode = config.GetOrderTypeMode(_configSection,
            $"{SubathonEventType.FourthWallOrder}", OrderTypeModes.Dollar);
        const string defaultCurrency = "USD";
        var username = testMode ? "FourthWall Test" : "FourthWall Customer";
        if (!testMode && !string.IsNullOrWhiteSpace(order.Username))
            username = order.Username.Split(' ').First();
        if (isSamples) username = "Internal Samples";

        var itemCount = 0;
        double costs = 0;
        double prices = 0;
        order.Offers?.ForEach(x => {
            itemCount += x.Variant?.Quantity ?? 1;
            costs += x.Variant?.Cost?.Value ?? 0;
            prices += x.Variant?.Price?.Value ?? 0;
        });

        double totalValue = order.Amounts?.Subtotal?.Value ?? 0;
        double totalDirect = order.Amounts?.Donation?.Value ?? 0;
        string currency = !string.IsNullOrWhiteSpace(order.Amounts?.Subtotal?.Currency)
            ? order.Amounts.Subtotal.Currency
            : defaultCurrency;

        double profit = Math.Max(prices - costs, 0);
        if (isSamples) {
            if (logger?.IsEnabled(LogLevel.Information) ?? false)
                logger?.LogInformation("[FourthWall] Samples order placed. Setting profit from {Profit} to 0.", profit);
            profit = 0;
        }

        totalDirect += profit;
        return new SubathonEvent {
            Id = Utils.TryParseGuid(order.Id),
            Source = string.Equals(username, "SYSTEM")
                ? SubathonEventSource.Simulated
                : SubathonEventSource.FourthWall,
            EventType = SubathonEventType.FourthWallOrder,
            User = username,
            Value = sourceMode switch {
                OrderTypeModes.Item => $"{itemCount}",
                OrderTypeModes.Order => "New",
                _ => totalValue.ToString("F2", CultureInfo.InvariantCulture)
            },
            Currency = sourceMode switch {
                OrderTypeModes.Item => "items",
                OrderTypeModes.Order => "order",
                _ => currency
            },
            Amount = Math.Max(itemCount, 1),
            SecondaryValue = $"{totalDirect.ToString("F2", CultureInfo.InvariantCulture)}|{currency}",
            EventTimestamp = order.CreatedAt?.LocalDateTime ?? DateTime.Now.ToLocalTime()
        };
    }

    [ExcludeFromCodeCoverage]
    public async Task<List<SubathonEvent>> FetchMissedEventsAsync(DateTime from, DateTime to,
        CancellationToken ct = default) {
        if (!HasTokenFile()) throw new InvalidOperationException("FourthWall is not connected");
        if (oAuth.NeedsRefresh(OAuthKeys) && !await oAuth.RefreshAsync(OAuthKeys, ct))
            throw new InvalidOperationException("FourthWall login has expired, reconnect it first");

        var client = new FourthwallApiClient(
            new HttpClientRequestAdapter(new FourthwallBearerAuthenticationProvider(AccessToken!)));
        DateTimeOffset start = from.ToUniversalTime();
        DateTimeOffset end = to.ToUniversalTime();
        List<SubathonEvent> events = [];

        List<OrderV1> orders = await FetchAllPagesAsync<OrderV1>(async page =>
            (await client.OpenApi.V10.Order.GetAsync(r => {
                r.QueryParameters.CreatedAtgt = start;
                r.QueryParameters.CreatedAtlt = end;
                r.QueryParameters.Page = page;
                r.QueryParameters.Size = MissedPageSize;
            }, ct))?.Results, o => o.Id);
        List<OrderV1> inRange = orders.Where(o => o.CreatedAt >= start && o.CreatedAt <= end).ToList();
        int cancelled = inRange.Count(o => o.Status == OrderV1_status.CANCELLED);
        events.AddRange(inRange.Where(o => o.Status != OrderV1_status.CANCELLED)
            .Select(o => MapOrder(o, false)).OfType<SubathonEvent>());
        int mappedOrders = events.Count;
        string skippedTypes = string.Join(", ", inRange.Where(o => o.Status != OrderV1_status.CANCELLED)
            .Select(GetOrderType).Where(t => t is not "ORDER" and not "SAMPLES_ORDER")
            .GroupBy(t => t == "" ? "(no source)" : t).Select(g => $"{g.Key} x{g.Count()}"));

        List<DonationV1> donations = await FetchAllPagesAsync<DonationV1>(async page =>
            (await client.OpenApi.V10.Donations.GetAsync(r => {
                r.QueryParameters.Page = page;
                r.QueryParameters.Size = MissedPageSize;
            }, ct))?.Results, d => d.Id);
        List<DonationV1> donationsInRange = donations.Where(d => d.CreatedAt >= start && d.CreatedAt <= end).ToList();
        events.AddRange(donationsInRange.Where(d => d.Status is null or DonationV1_status.COMPLETED)
            .Select(d => MapDonation(d, false)));

        if (logger?.IsEnabled(LogLevel.Information) ?? false)
            logger?.LogInformation(
                "[FourthWall] Missed event lookup {Start:u} to {End:u}: {Orders} order(s) returned, {InRange} in range, " +
                "{Cancelled} cancelled, {Mapped} usable, skipped types [{Skipped}]; {Donations} donation(s) checked, " +
                "{DonationsInRange} in range",
                start, end, orders.Count, inRange.Count, cancelled, mappedOrders, skippedTypes, donations.Count,
                donationsInRange.Count);
        return events;
    }

    private async Task<List<T>> FetchAllPagesAsync<T>(Func<int, Task<List<T>?>> getPage, Func<T, string?> getId) {
        List<T> all = [];
        HashSet<string> seen = [];
        for (var page = 0; page < MaxMissedPages; page++) {
            List<T> items = await getPage(page) ?? [];
            if (items.Count == 0) {
                if (page == 0) continue;
                break;
            }

            List<T> fresh = items.Where(i => seen.Add(getId(i) ?? Guid.NewGuid().ToString())).ToList();
            if (fresh.Count == 0) break;
            all.AddRange(fresh);
        }

        return all;
    }
}
