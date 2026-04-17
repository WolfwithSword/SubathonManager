using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Agash.Webhook.Abstractions;
using KoFi.Client.Events;
using KoFi.Client.Options;
using KoFi.Client.Webhooks;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;

namespace SubathonManager.Integration;

public class KoFiService(ILogger<KoFiService>? logger, IConfig config, IHttpClientFactory httpClientFactory, DevTunnelsService devTunnels)
    : IWebhookIntegration
{
    private readonly string _configSection = "KoFi";

    // Headers forwarded verbatim to any configured forward URLs.
    // Hop-by-hop and transport headers are excluded; they don't apply end-to-end.
    private static readonly HashSet<string> _skipForwardHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "host", "connection", "transfer-encoding", "keep-alive",
        "proxy-authenticate", "proxy-authorization", "te", "trailer", "upgrade"
    };

    public string WebhookPath => "/api/webhooks/kofi";

    private readonly KoFiWebhookHandler _handler = new();

    public async Task StartAsync(CancellationToken ct = default)
    {
        IntegrationEvents.ConnectionUpdated += OnTunnelUpdated;

        var token = config.GetFromEncoded(_configSection, "VerificationToken", string.Empty);
        bool enabled = !string.IsNullOrWhiteSpace(token);

        if (enabled)
        {
            logger?.LogInformation("[Ko-fi] Webhook listener ready at {Path}", WebhookPath);
            // Start tunnel on demand; no need to open a public endpoint if no token is set.
            // Fire-and-forget: OnTunnelUpdated will broadcast the composed URL once ready.
            _ = devTunnels.StartTunnelAsync(ct);
        }
        else
        {
            logger?.LogInformation("[Ko-fi] No verification token configured; Ko-fi integration is disabled");
        }

        // Seed status; include public URL if the tunnel is already running
        var tunnelConn = Utils.GetConnection(SubathonEventSource.DevTunnels, "Tunnel");
        BroadcastStatus(enabled, tunnelConn.Status ? tunnelConn.Name : null);

        await Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        IntegrationEvents.ConnectionUpdated -= OnTunnelUpdated;
        BroadcastStatus(false, null);
        return Task.CompletedTask;
    }

    // Re-broadcast our own status whenever the shared tunnel changes so consumers
    // (e.g. KoFiWebhookSettings) always see the composed public URL without having
    // to know about DevTunnels or manually concatenate paths.
    private void OnTunnelUpdated(IntegrationConnection connection)
    {
        if (connection is not { Source: SubathonEventSource.DevTunnels, Service: "Tunnel" }) return;
        var token = config.GetFromEncoded(_configSection, "VerificationToken", string.Empty);
        bool enabled = !string.IsNullOrWhiteSpace(token);
        BroadcastStatus(enabled, connection.Status ? connection.Name : null);
    }

    private void BroadcastStatus(bool enabled, string? tunnelBaseUrl)
    {
        // Compose the full public URL from the tunnel base + our own path.
        // WebhookPath is the single source of truth for the path segment.
        string? fullUrl = !string.IsNullOrWhiteSpace(tunnelBaseUrl) && tunnelBaseUrl != "(starting…)"
            ? tunnelBaseUrl.TrimEnd('/') + WebhookPath
            : null;

        IntegrationEvents.RaiseConnectionUpdate(new IntegrationConnection
        {
            Name = fullUrl ?? "",
            Status = enabled,
            Source = SubathonEventSource.KoFiWebhook,
            Service = nameof(SubathonEventSource.KoFiWebhook)
        });

        if (fullUrl != null)
            logger?.LogInformation("[Ko-fi] Public webhook URL: {Url}", fullUrl);
    }

    public async Task HandleWebhookAsync(byte[] rawBody, IReadOnlyDictionary<string, string> headers, CancellationToken ct = default)
    {
        // Forward the raw request to any configured URLs before verifying; the
        // forwarded service (e.g. StreamerBot) needs the same unmodified payload.
        await ForwardRequestAsync(rawBody, headers, ct);

        var token = config.GetFromEncoded(_configSection, "VerificationToken", string.Empty);
        if (string.IsNullOrWhiteSpace(token))
        {
            logger?.LogWarning("[Ko-fi] Received webhook but no verification token is configured");
            return;
        }

        // Ko-fi always sends application/x-www-form-urlencoded; use the inbound
        // Content-Type if present, fall back to the known Ko-fi value.
        var contentType = headers.TryGetValue("Content-Type", out var ct2) ? ct2
            : "application/x-www-form-urlencoded";

        var request = new WebhookRequest
        {
            Method = "POST",
            Path = WebhookPath,
            Body = rawBody,
            ContentType = contentType
        };

        var result = await _handler.HandleAsync(request, new KoFiWebhookOptions { VerificationToken = token }, ct);

        if (!result.IsAuthenticated)
        {
            logger?.LogWarning("[Ko-fi] Webhook authentication failed: {Reason}", result.FailureReason);
            return;
        }

        if (!result.IsKnownEvent || result.Event is null)
        {
            logger?.LogDebug("[Ko-fi] Received unknown/unsupported Ko-fi event type");
            return;
        }

        var ev = MapToSubathonEvent(result.Event);
        if (ev != null)
        {
            SubathonEvents.RaiseSubathonEventCreated(ev);
            logger?.LogDebug("[Ko-fi] Raised {EventType} from {User}", ev.EventType, ev.User);
        }
    }

    private SubathonEvent? MapToSubathonEvent(KoFiWebhookEvent koFiEvent)
    {
        try
        {
            return koFiEvent switch
            {
                KoFiDonationEvent d => new SubathonEvent
                {
                    Id = TryParseGuid(d.MessageId),
                    Source = SubathonEventSource.KoFiWebhook,
                    EventType = SubathonEventType.KoFiDonation,
                    User = d.FromName ?? "Ko-fi Supporter",
                    Value = d.Amount.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
                    Currency = d.Currency ?? "USD",
                    EventTimestamp = d.Timestamp.LocalDateTime,
                },
                KoFiSubscriptionStartedEvent s => new SubathonEvent
                {
                    Id = TryParseGuid(s.MessageId),
                    Source = SubathonEventSource.KoFiWebhook,
                    EventType = SubathonEventType.KoFiSub,
                    User = s.FromName ?? "Ko-fi Supporter",
                    Value = s.TierName ?? "1000",
                    Currency = "member",
                    EventTimestamp = s.Timestamp.LocalDateTime,
                },
                KoFiSubscriptionRenewedEvent r => new SubathonEvent
                {
                    Id = TryParseGuid(r.MessageId),
                    Source = SubathonEventSource.KoFiWebhook,
                    EventType = SubathonEventType.KoFiSub,
                    User = r.FromName ?? "Ko-fi Supporter",
                    Value = r.TierName ?? "1000",
                    Currency = "member",
                    EventTimestamp = r.Timestamp.LocalDateTime,
                },
                _ => null
            };
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "[Ko-fi] Failed to map Ko-fi event to SubathonEvent");
            return null;
        }
    }

    private async Task ForwardRequestAsync(byte[] rawBody, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        string? forwardUrlsRaw = config.Get(_configSection, "ForwardUrls", string.Empty);
        if (string.IsNullOrWhiteSpace(forwardUrlsRaw)) return;

        var urls = forwardUrlsRaw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var client = httpClientFactory.CreateClient(nameof(KoFiService));

        foreach (var url in urls)
        {
            try
            {
                using var content = new ByteArrayContent(rawBody);

                // Forward all inbound headers except hop-by-hop ones.
                // Content-Type is applied to the content itself; everything else goes on the request.
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
                logger?.LogDebug("[Ko-fi] Forwarded webhook to {Url}: {Status}", url, response.StatusCode);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "[Ko-fi] Failed to forward webhook to {Url}", url);
            }
        }
    }

    private static Guid TryParseGuid(string? value)
    {
        if (value != null && Guid.TryParse(value, out var g)) return g;
        return Utils.CreateGuidFromUniqueString(value ?? Guid.NewGuid().ToString());
    }
}
