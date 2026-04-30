using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Agash.Webhook.Abstractions;
using Fourthwall.Client.Events;
using Fourthwall.Client.Authentication;
using Fourthwall.Client.Generated;
using Fourthwall.Client.Generated.Models.App.Openapi.Endpoint.OpenApiWebhookConfigurationEndpoint;
using Fourthwall.Client.Generated.Models.Openapi.Model;
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
using SubathonManager.Core.Security.Interfaces;
// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.Integration;

public class FourthWallService(ILogger<FourthWallService>? logger, IConfig config, IHttpClientFactory httpClientFactory, DevTunnelsService devTunnels, ISecureStorage secureStorage)
    : IWebhookIntegration
{
    private readonly string _configSection = "FourthWall";

    private static readonly HashSet<string> _skipForwardHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "host", "connection", "transfer-encoding", "keep-alive",
        "proxy-authenticate", "proxy-authorization", "te", "trailer", "upgrade"
    };

    public string WebhookPath => "/api/webhooks/fourthwall";
    internal readonly string _oAuthURl = "https://oauth.subathonmanager.app/auth/fourthwall/login";
    internal readonly string _refreshURl = "https://oauth.subathonmanager.app/auth/fourthwall/refresh";
    
    internal Action<string> OpenBrowser = url => Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });

    private string? AccessToken => secureStorage.GetOrDefault(StorageKeys.FourthWallAccessToken, string.Empty);
    private string? RefreshToken => secureStorage.GetOrDefault(StorageKeys.FourthWallRefreshToken, string.Empty);
    private string? ShopName { get; set; }
    
    public readonly Dictionary<string, string> MembershipNames = new();

    private readonly FourthwallWebhookHandler _handler = new(signatureVerifier: new FourthwallWebhookSignatureVerifier());
    
    [ExcludeFromCodeCoverage]
    private bool CheckExpiry()
    {
        if (string.IsNullOrWhiteSpace(AccessToken)) return false;
        
        DateTime? expires = Utils.GetAccessTokenExpiry(AccessToken);
        if (expires == null) return false;
        bool isExpired =  DateTime.UtcNow >= expires.Value.AddSeconds(-60);
        return isExpired;
    }

    [ExcludeFromCodeCoverage]
    private async Task<bool> CheckForTokenAsync(CancellationToken ct = default)
    {
        if (!HasTokenFile())
        {
            await StartOAuthFlowAsync();
            
            if (string.IsNullOrWhiteSpace(AccessToken) || string.IsNullOrWhiteSpace(RefreshToken))
            {
                return false;
            }
        }
        else if (CheckExpiry())
        {
            var success = await StartOAuthRefreshAsync(ct);
            if (!success)
            {
                RevokeTokenFile();
                await StartOAuthFlowAsync();
            }
            if (string.IsNullOrWhiteSpace(AccessToken)) return false;
        }

        return !string.IsNullOrWhiteSpace(AccessToken) && !CheckExpiry();
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        IntegrationEvents.ConnectionUpdated += OnTunnelUpdated;
        
        Utils.PendingOAuthCallback = null;
        bool enabled = HasTokenFile();
        
        if (enabled)
        {
            logger?.LogInformation("[FourthWall] Webhook listener ready at {Path}", WebhookPath);
            _ = devTunnels.StartTunnelAsync(ct);
        }
        else
        {
            logger?.LogInformation("[FourthWall] Has not been setup before. Integration is disabled");
            BroadcastStatus(false, null);
        }

        await Task.CompletedTask;
    }
    
    
    [ExcludeFromCodeCoverage]
    public async Task Initialize(CancellationToken ct = default)
    { 
        var tunnelConn = Utils.GetConnection(SubathonEventSource.DevTunnels, "Tunnel");
        if (!tunnelConn.Status) { 
            return;
        }
        
        var canConnect = await CheckForTokenAsync(ct);
        if (!canConnect || string.IsNullOrWhiteSpace(AccessToken))
        {
            RevokeTokenFile();
            BroadcastStatus(false, null);
            return;
        }
        
        var enabled = HasTokenFile();
        var client = new FourthwallApiClient(new HttpClientRequestAdapter(new FourthwallBearerAuthenticationProvider(accessToken: AccessToken)));
        
        var shop = await client.OpenApi.V10.Shops.Current.GetAsync(cancellationToken: ct);
        logger?.LogInformation("[FourthWall] connected to {shopName}", shop?.Name ?? "none");
        if (shop == null)
        {
            BroadcastStatus(false, null);
            return;
        };
        
        ShopName = shop.Name;
        var webhooks = await client.OpenApi.V10.Webhooks.GetAsync(cancellationToken: ct);

        bool hasWh = false;
        if (webhooks is { Results: not null })
        {
            foreach (var webhookConfigurationV1 in webhooks.Results)
            {
                if (!string.IsNullOrWhiteSpace(webhookConfigurationV1.Url) &&
                    webhookConfigurationV1.Url.Contains(tunnelConn.Name.Replace("https://", ""), StringComparison.CurrentCultureIgnoreCase))
                {
                    // TODO what if we add more future scopes, we'd need to compare allowed_types.
                    hasWh = true;
                    logger?.LogDebug("Webhook found, no need to make a new one for fourthwall");
                    break;
                }
            }
        }

        if (!hasWh)
        {
            string? fullUrl = !string.IsNullOrWhiteSpace(tunnelConn.Name) && tunnelConn.Name != "(starting…)"
                ? tunnelConn.Name.TrimEnd('/') + WebhookPath
                : null;
            WebhookConfigurationCreateRequest req = new WebhookConfigurationCreateRequest
            {
                Url = fullUrl,
                AllowedTypes =
                [
                    WebhookConfigurationCreateRequest_allowedTypes.ORDER_PLACED,
                    WebhookConfigurationCreateRequest_allowedTypes.DONATION,
                    WebhookConfigurationCreateRequest_allowedTypes.SUBSCRIPTION_PURCHASED,
                    WebhookConfigurationCreateRequest_allowedTypes.SUBSCRIPTION_CHANGED,
                    WebhookConfigurationCreateRequest_allowedTypes.GIFT_PURCHASE
                ]
            };
            var resp = await client.OpenApi.V10.Webhooks.PostAsync(req, cancellationToken: ct);
            if (resp == null || string.IsNullOrWhiteSpace(resp.Url))
            {
                logger?.LogWarning("[FourthWall] Unable to create Webhook configuration");
                BroadcastStatus(false, null);
                return;
            }
            hasWh = true;
            logger?.LogInformation("[FourthWall] Webhook successfully made: {WHId}", resp.Id );
        }

        var memberships = await client.OpenApi.V10.Memberships.Tiers.GetAsync(cancellationToken: ct);
        if (memberships is { Count: > 0 })
        {
            memberships.ForEach(x =>
            {
                if (string.IsNullOrWhiteSpace(x.Id) || string.IsNullOrWhiteSpace(x.Name)) return;
                
                MembershipNames[x.Id] = x.Name;
            });
        }
        IntegrationEvents.RaiseFourthWallMembershipsSynced(MembershipNames);
  
        // Seed status; include public URL if the tunnel is already running
        tunnelConn = Utils.GetConnection(SubathonEventSource.DevTunnels, "Tunnel");
        BroadcastStatus(enabled, tunnelConn.Status ? tunnelConn.Name : null);
        await Task.CompletedTask;
    }


    public Task StopAsync(CancellationToken ct = default)
    {
        IntegrationEvents.ConnectionUpdated -= OnTunnelUpdated;
        BroadcastStatus(false, null);
        
        return Task.CompletedTask;
    }

    [ExcludeFromCodeCoverage]
    private async Task<bool> StartOAuthRefreshAsync(CancellationToken ct = default)
    {
        logger?.LogDebug("Refreshing FourthWall tokens...");

        if (!HasTokenFile()) return false;

        if (string.IsNullOrWhiteSpace(RefreshToken)) return false;

        using var client = httpClientFactory.CreateClient(nameof(FourthWallService));
        using var body = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("refresh_token", RefreshToken),
        });

        var response = await client.PostAsync(_refreshURl, body, ct);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            logger?.LogWarning("[FourthWall] Token refresh failed ({Status}): {Error}", response.StatusCode, error);
            return false;
        }

        var responseJson = await response.Content.ReadAsStringAsync(ct);
        var tokens = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(responseJson);

        var newAccess  = tokens?.GetValueOrDefault("access_token").GetString();
        var newRefresh = tokens?.GetValueOrDefault("refresh_token").GetString() ?? RefreshToken;

        if (string.IsNullOrWhiteSpace(newAccess))
        {
            logger?.LogWarning("[FourthWall] Token refresh returned no access_token");
            return false;
        }

        secureStorage.Set(StorageKeys.FourthWallAccessToken, newAccess);
        secureStorage.Set(StorageKeys.FourthWallRefreshToken, newRefresh);

        logger?.LogDebug("[FourthWall] Tokens refreshed successfully");
        return true;
    }
    
    private async Task StartOAuthFlowAsync()
    {
        RevokeTokenFile();
        Utils.PendingOAuthCallback = null;
        logger?.LogDebug("Opening FourthWall OAuth...");
        OpenBrowser(_oAuthURl);
        var (newAccess, newRefresh) = await WaitForProtocolCallbackAsync();   
        if (!string.IsNullOrEmpty(AccessToken) || string.IsNullOrEmpty(RefreshToken))
        {
            secureStorage.Set(StorageKeys.FourthWallAccessToken, newAccess!);
            secureStorage.Set(StorageKeys.FourthWallRefreshToken, newRefresh!);
        }
    }
    private async Task<(string?, string?)> WaitForProtocolCallbackAsync(CancellationToken ct = default)
    {
        var timeout = DateTime.Now.AddMinutes(15);
        while (DateTime.Now < timeout && !ct.IsCancellationRequested)
        {
            var cb = Utils.PendingOAuthCallback;
            if (cb?.Provider == "fourthwall" && (!string.IsNullOrEmpty(cb.AccessToken) || !string.IsNullOrEmpty(cb.RefreshToken)))
            {
                Utils.PendingOAuthCallback = null;
                return (cb.AccessToken, cb.RefreshToken);
            }
            await Task.Delay(250, ct);
        }
        return (null, null);
    }

    private bool HasTokenFile()
    {
        return secureStorage.Exists(StorageKeys.FourthWallAccessToken) &&
               secureStorage.Exists(StorageKeys.FourthWallRefreshToken)
               && !string.IsNullOrWhiteSpace(AccessToken) && !string.IsNullOrWhiteSpace(RefreshToken);
    }

    [ExcludeFromCodeCoverage]
    public void RevokeTokenFile()
    {
        secureStorage.Delete(StorageKeys.FourthWallAccessToken);
        secureStorage.Delete(StorageKeys.FourthWallRefreshToken);
    }

    [ExcludeFromCodeCoverage]
    private void OnTunnelUpdated(IntegrationConnection connection)
    {
        if (connection is not { Source: SubathonEventSource.DevTunnels, Service: "Tunnel" }) return;

        bool enabled = HasTokenFile();
        if (enabled && connection.Status)
        {
            Task.Run(() => Initialize());
            return;
        }
        BroadcastStatus(enabled, connection.Status ? connection.Name : null);
    }

    private void BroadcastStatus(bool enabled, string? tunnelBaseUrl)
    {
        string? fullUrl = !string.IsNullOrWhiteSpace(tunnelBaseUrl) && tunnelBaseUrl != "(starting…)"
            ? tunnelBaseUrl.TrimEnd('/') + WebhookPath
            : null;
        
        IntegrationEvents.RaiseConnectionUpdate(new IntegrationConnection
        {
            Name = fullUrl ?? "",
            Status = enabled && fullUrl != null,
            Source = SubathonEventSource.FourthWall,
            Service = nameof(SubathonEventSource.FourthWall)
        });

        if (fullUrl != null)
            logger?.LogInformation("[FourthWall] Public webhook URL: {Url}", fullUrl);
    }

    public async Task HandleWebhookAsync(byte[] rawBody, IReadOnlyDictionary<string, string> headers, CancellationToken ct = default)
    {
        await ForwardRequestAsync(rawBody, headers, ct);

        var contentType = headers.TryGetValue("Content-Type", out var ct2) ? ct2
            : "application/x-www-form-urlencoded";
        
        var newHeaders = headers.ToDictionary(
            kvp => kvp.Key,
            kvp => new[] { kvp.Value }
        );
        IReadOnlyDictionary<string, string[]> readOnlyHeaders = newHeaders;
        
        var request = new WebhookRequest
        {
            Method = "POST",
            Path = WebhookPath,
            Body = rawBody,
            ContentType = contentType,
            Headers = readOnlyHeaders
        };
        
        var result = await _handler.HandleAsync(request, new FourthwallWebhookOptions
        {
             SigningSecret = "",
             SignatureMode = FourthwallWebhookSignatureMode.PlatformAppWebhook
        }, ct);
        
        if (!result.IsKnownEvent || result.Event is null)
        {
            logger?.LogDebug("[FourthWall] Received unknown/unsupported FourthWall event type");
            return;
        }
        var ev = MapToSubathonEvent(result.Event);
        if (ev != null)
        {
            SubathonEvents.RaiseSubathonEventCreated(ev);
            logger?.LogDebug("[FourthWall] Raised {EventType} from {User}", ev.EventType, ev.User);
        }
    }

    public SubathonEvent? MapToSubathonEvent(FourthwallWebhookEvent fwEvent)
    {
        try
        {
            var mode = config.Get(_configSection, $"{SubathonEventType.FourthWallOrder}.Mode", "Dollar");
            var mode2 = config.Get(_configSection, $"{SubathonEventType.FourthWallGiftOrder}.Mode", "Dollar");
            var sourceMode = Enum.TryParse(mode, out OrderTypeModes m) ? m : OrderTypeModes.Dollar;
            var sourceMode2 = Enum.TryParse(mode2, out OrderTypeModes m2) ? m2 : OrderTypeModes.Dollar;
            var defaultCurrency = "USD";
            string username = "FourthWall Customer";
            if (fwEvent.TestMode) username = "FourthWall Test";//"SYSTEM";
            SubathonEvent? ev = null;
            if (fwEvent is FourthwallDonationWebhookEvent donationEvent)
            {
                var d = donationEvent.Data;
                if (!fwEvent.TestMode && !string.IsNullOrWhiteSpace(d.Username))
                    username = d.Username.Split(' ').First();
                ev = new SubathonEvent
                {
                    Id = TryParseGuid(d.Id),
                    Source =  string.Equals(username,"SYSTEM") ? SubathonEventSource.Simulated : SubathonEventSource.FourthWall,
                    EventType = SubathonEventType.FourthWallDonation,
                    User = username,
                    Value =
                        d.Amounts?.Total?.Value?.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) ??
                        "0.00",
                    Currency = !string.IsNullOrWhiteSpace(d.Amounts?.Total?.Currency)
                        ? d.Amounts?.Total?.Currency
                        : defaultCurrency,
                    EventTimestamp = d.CreatedAt?.LocalDateTime ?? DateTime.Now.ToLocalTime(),
                };
            }
            else if (fwEvent is FourthwallOrderPlacedWebhookEvent orderPlacedEvent)
            {
                var order =  orderPlacedEvent.Data;
                if (!string.Equals("ORDER", order.Source?.Order?.Type ?? "", StringComparison.CurrentCultureIgnoreCase)
                    && !string.Equals("SAMPLES_ORDER", order.Source?.Order?.Type ?? "", StringComparison.CurrentCultureIgnoreCase))
                {
                    return null;
                }
                
                //if (order.Status != OrderV1_status.CONFIRMED) return null;
                if (!fwEvent.TestMode && !string.IsNullOrWhiteSpace(order.Username))
                    username = order.Username.Split(' ').First();
                if (string.Equals("SAMPLES_ORDER", order.Source?.Order?.Type ?? "",
                        StringComparison.CurrentCultureIgnoreCase))
                {
                    username = "Internal Samples";
                }
                var itemCount = 0;
                double totalValue = 0;
                double totalDirect = 0;
                string currency = order.Amounts?.Subtotal?.Currency ?? defaultCurrency;
                
                double costs = 0;
                double prices = 0;
                order.Offers?.ForEach(x =>
                {
                    itemCount += (x.Variant?.Quantity ?? 1);
                    costs += (x.Variant?.Cost?.Value ?? 0);
                    prices += (x.Variant?.Price?.Value ?? 0);
                });
                
                totalValue += (order.Amounts?.Subtotal?.Value ?? 0);
                totalDirect += (order.Amounts?.Donation?.Value ?? 0);

                var profit = Math.Max(prices - costs, 0);
                if (string.Equals("SAMPLES_ORDER", order.Source?.Order?.Type ?? "",
                        StringComparison.CurrentCultureIgnoreCase))
                {
                    logger?.LogInformation("[FourthWall] Samples order placed. Setting profit from {Profit} to 0.", profit);
                    profit = 0;
                }
                totalDirect += profit;
                ev = new SubathonEvent
                {
                    Id = TryParseGuid(order.Id),
                    Source =  string.Equals(username,"SYSTEM") ? SubathonEventSource.Simulated : SubathonEventSource.FourthWall,
                    EventType = SubathonEventType.FourthWallOrder,
                    User = username,
                    Value = sourceMode switch
                    {
                        OrderTypeModes.Item => $"{itemCount}",
                        OrderTypeModes.Order => "New",
                        _ => totalValue.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
                    },
                    Currency = sourceMode switch
                    {
                        OrderTypeModes.Item => "items",
                        OrderTypeModes.Order => "order",
                        _ => string.IsNullOrWhiteSpace(currency) ? currency : defaultCurrency
                    },
                    Amount = Math.Max(itemCount, 1),
                    SecondaryValue = $"{totalDirect.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}|{
                        (!string.IsNullOrWhiteSpace(currency) ? currency : defaultCurrency)}",
                    EventTimestamp = order.CreatedAt?.LocalDateTime ?? DateTime.Now.ToLocalTime()
                };

            }
            else if (fwEvent is FourthwallGiftPurchaseWebhookEvent giftPurchaseWebhookEvent)
            {
                var order =  giftPurchaseWebhookEvent.Data;
                if (!fwEvent.TestMode && !string.IsNullOrWhiteSpace(order.Username))
                    username = order.Username.Split(' ').First();
                var itemCount = 1;
                if (order.Quantity != null) itemCount = order.Quantity.Value;
                
                double totalValue = 0;
                double totalDirect = 0;
                string currency = order.Amounts?.Subtotal?.Currency ?? defaultCurrency;
                
                totalValue += order.Amounts?.Subtotal?.Value ?? 0;
                totalDirect += order.Amounts?.Profit?.Value ?? 0;

                ev = new SubathonEvent
                {
                    Id = TryParseGuid(order.Id),
                    Source =  string.Equals(username,"SYSTEM") ? SubathonEventSource.Simulated : SubathonEventSource.FourthWall,
                    EventType = SubathonEventType.FourthWallGiftOrder,
                    User = username,
                    Value = sourceMode2 switch
                    {
                        OrderTypeModes.Item => $"{itemCount}",
                        OrderTypeModes.Order => "New Gift",
                        _ => totalValue.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
                    },
                    Currency = sourceMode2 switch
                    {
                        OrderTypeModes.Item => "items",
                        OrderTypeModes.Order => "order",
                        _ => string.IsNullOrWhiteSpace(currency) ? currency : defaultCurrency
                    },
                    Amount = Math.Max(itemCount, 1),
                    SecondaryValue = $"{totalDirect.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}|{
                        (!string.IsNullOrWhiteSpace(currency) ? currency : defaultCurrency)}",
                    EventTimestamp = order.CreatedAt?.LocalDateTime ?? DateTime.Now.ToLocalTime()
                };

            }
            else if (fwEvent is FourthwallSubscriptionPurchasedWebhookEvent subscriptionPurchasedWebhookEvent)
            {
                var data = subscriptionPurchasedWebhookEvent.Data;
                if (!fwEvent.TestMode && !string.IsNullOrWhiteSpace(data.Nickname))
                    username = data.Nickname.Split(' ').First();
                var subscription = data.Subscription?.Active;
                if (subscription == null) return null;
                var tierName = "DEFAULT";
                MembershipNames.TryGetValue(subscription.Variant?.TierId ?? "DEFAULT", out tierName);
                if (string.IsNullOrWhiteSpace(tierName)) tierName = "DEFAULT";

                ev = new SubathonEvent
                {
                    Id = fwEvent.TestMode ? Guid.NewGuid() : TryParseGuid(data.Id),
                    Source =  string.Equals(username,"SYSTEM") ? SubathonEventSource.Simulated : SubathonEventSource.FourthWall,
                    EventType = SubathonEventType.FourthWallMembership,
                    User = username,
                    Value = tierName,
                    Currency = "member",
                    Amount = subscription.Variant?.Interval == MembershipTierVariantV1_interval.ANNUAL ? 12 : 1,
                    SecondaryValue = $"{(subscription.Variant?.Amount?.Value ?? 0).ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}|{
                        (!string.IsNullOrWhiteSpace(subscription.Variant?.Amount?.Currency) ? subscription.Variant?.Amount?.Currency : defaultCurrency)}",
                    EventTimestamp = subscriptionPurchasedWebhookEvent.CreatedAt.LocalDateTime
                };
            }
            else if (fwEvent is FourthwallSubscriptionChangedWebhookEvent  subscriptionChangedWebhookEvent)
            {
                var data = subscriptionChangedWebhookEvent.Data;
                if (!fwEvent.TestMode && !string.IsNullOrWhiteSpace(data.Nickname))
                    username = data.Nickname.Split(' ').First();
                var subscription = data.Subscription?.Active;
                if (subscription == null) return null;
                var tierName = "DEFAULT";
                MembershipNames.TryGetValue(subscription.Variant?.TierId ?? "DEFAULT", out tierName);
                if (string.IsNullOrWhiteSpace(tierName)) tierName = "DEFAULT";

                ev = new SubathonEvent
                {
                    Id = fwEvent.TestMode ? Guid.NewGuid() : TryParseGuid(data.Id),
                    Source =  string.Equals(username,"SYSTEM") ? SubathonEventSource.Simulated : SubathonEventSource.FourthWall,
                    EventType = SubathonEventType.FourthWallMembership,
                    User = username,
                    Value = tierName,
                    Currency = "member",
                    Amount = subscription.Variant?.Interval == MembershipTierVariantV1_interval.ANNUAL ? 12 : 1,
                    SecondaryValue = $"{(subscription.Variant?.Amount?.Value ?? 0).ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}|{
                        (!string.IsNullOrWhiteSpace(subscription.Variant?.Amount?.Currency) ? subscription.Variant?.Amount?.Currency : defaultCurrency)}",
                    EventTimestamp = subscriptionChangedWebhookEvent.CreatedAt.LocalDateTime
                };
            }
            return ev;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "[FourthWall] Failed to map FourthWall event to SubathonEvent");
            return null;
        }
    }

    private async Task ForwardRequestAsync(byte[] rawBody, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        string? forwardUrlsRaw = config.Get(_configSection, "ForwardUrls", string.Empty);
        if (string.IsNullOrWhiteSpace(forwardUrlsRaw)) return;

        var urls = forwardUrlsRaw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var client = httpClientFactory.CreateClient(nameof(FourthWallService));

        foreach (var url in urls)
        {
            try
            {
                using var content = new ByteArrayContent(rawBody);

                foreach (var (key, value) in headers)
                {
                    if (_skipForwardHeaders.Contains(key)) continue;

                    if (key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                    {
                        if (MediaTypeHeaderValue.TryParse(value, out var mt))
                            content.Headers.ContentType = mt;
                    }
                    else if (key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    {
                        // HttpClient sets this automatically from ByteArrayContent
                    }
                    else
                    {
                        content.Headers.TryAddWithoutValidation(key, value);
                    }
                }

                // Ensure Content-Type is set even if the inbound request omitted it
                content.Headers.ContentType ??= new MediaTypeHeaderValue("application/x-www-form-urlencoded")
                {
                    CharSet = Encoding.UTF8.WebName
                };

                var response = await client.PostAsync(url, content, ct);
                logger?.LogDebug("[FourthWall] Forwarded webhook to {Url}: {Status}", url, response.StatusCode);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "[FourthWall] Failed to forward webhook to {Url}", url);
            }
        }
    }

    private static Guid TryParseGuid(string? value)
    {
        if (value != null && Guid.TryParse(value, out var g)) return g;
        return Utils.CreateGuidFromUniqueString(value ?? Guid.NewGuid().ToString());
    }
}
