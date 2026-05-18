using System.Diagnostics.CodeAnalysis;
using System.Text;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;
using SubathonManager.Core.Security;

// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.Integration;

public class ThroneService(ILogger<ThroneService>? logger, IConfig config, DevTunnelsService devTunnels)
    : IWebhookIntegration
{
    private bool Enabled => config.GetBool("Throne", "Enabled", false);
    public string WebhookPath => "/api/webhooks/throne";

    private const string ThronePublicKeyPem = """
                                                      -----BEGIN PUBLIC KEY-----
                                                      MCowBQYDK2VwAyEAPXbUfxh7XL4SYUVcfhmYMIbxvtR9E9LDd8gPJ1PwSD8=
                                                      -----END PUBLIC KEY-----
                                              """;
    private static readonly TimeSpan TimestampTolerance = TimeSpan.FromMinutes(10);

    
    public async Task StartAsync(CancellationToken ct = default)
    {      
        IntegrationEvents.ConnectionUpdated += OnTunnelUpdated;

        if (Enabled)
        {
            logger?.LogInformation("[Throne] Webhook listener ready at {Path}", WebhookPath);
            _ = devTunnels.StartTunnelAsync(ct);
        }
        else
        {
            logger?.LogInformation("[Throne] Has not been enabled. Shutting down...");
            BroadcastStatus(false, null);
        }

        await Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        IntegrationEvents.ConnectionUpdated -= OnTunnelUpdated;
        BroadcastStatus(false, null);
        
        return Task.CompletedTask;
    }
    
    
    [ExcludeFromCodeCoverage]
    public async Task Initialize(CancellationToken ct = default)
    { 
        var tunnelConn = Utils.GetConnection(SubathonEventSource.DevTunnels, "Tunnel");
        if (!tunnelConn.Status) { 
            return;
        }
        
        if (!Enabled)
        {
            BroadcastStatus(false, null);
            return;
        }
        
        tunnelConn = Utils.GetConnection(SubathonEventSource.DevTunnels, "Tunnel");
        BroadcastStatus(Enabled, tunnelConn.Status ? tunnelConn.Name : null);
        await Task.CompletedTask;
    }
    
    [ExcludeFromCodeCoverage]
    private void OnTunnelUpdated(IntegrationConnection connection)
    {
        if (connection is not { Source: SubathonEventSource.DevTunnels, Service: "Tunnel" }) return;

        if (Enabled && connection.Status)
        {
            Task.Run(() => Initialize());
            return;
        }
        BroadcastStatus(Enabled, connection.Status ? connection.Name : null);
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
            Source = SubathonEventSource.Throne,
            Service = nameof(SubathonEventSource.Throne)
        });

        if (fullUrl != null)
            logger?.LogInformation("[Throne] Public webhook URL: {Url}", fullUrl);
    }
    public Task HandleWebhookAsync(byte[] rawBody, IReadOnlyDictionary<string, string> headers, CancellationToken ct = default)
    {
        if (!Enabled) return Task.CompletedTask;
        if (!headers.TryGetValue("X-Signature-Timestamp", out var timestamp)
            || string.IsNullOrWhiteSpace(timestamp)
            || !long.TryParse(timestamp, out var timestampUnix))
        {
            logger?.LogWarning("[Throne] Rejected - missing or non-numeric X-Signature-Timestamp");
            return Task.CompletedTask;
        }
 
        if (!headers.TryGetValue("X-Signature-Ed25519", out var signatureHex)
            || string.IsNullOrWhiteSpace(signatureHex))
        {
            logger?.LogWarning("[Throne] Rejected - missing X-Signature-Ed25519");
            return Task.CompletedTask;
        }
 
        var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(timestampUnix);
        if (age.Duration() > TimestampTolerance)
        {
            logger?.LogWarning("[Throne] Rejected - timestamp age {Age:F0}s exceeds tolerance", age.TotalSeconds);
            return Task.CompletedTask;
        }
 
        var stringData = Encoding.UTF8.GetString(rawBody);
        if (!SignatureVerification.VerifyEd25519Signature( stringData, timestamp, signatureHex, ThronePublicKeyPem))
        {
            logger?.LogWarning("[Throne] Rejected - invalid signature");
            return Task.CompletedTask;
        }
 
        logger?.LogDebug("[Throne] Signature verified OK");
        
        ProcessData(stringData);

        return Task.CompletedTask;
    }

    public void ProcessData(string stringData, bool isSim = false)
    {
        var throneEvent = JsonConvert.DeserializeObject<Dictionary<string, object>>(stringData);
        if (throneEvent == null) return;
        throneEvent.TryGetValue("data", out var value);

        if (value == null || string.IsNullOrWhiteSpace(value.ToString())) return;

        var data = JsonConvert.DeserializeObject<Dictionary<string, object>>(value.ToString() ?? string.Empty)!;

        throneEvent.TryGetValue("event_id", out var uuid);
        data.TryGetValue("item_name", out var itemName);
        data.TryGetValue("gifter_username", out var gifterName);
        data.TryGetValue("currency", out var currency);
        // data.TryGetValue("creator_username", out var creatorUsername);
        // bool.TryParse(isSurpriseRaw?.ToString(), out var isSurprise);
        // var username = creatorUsername?.ToString();
        
        data.TryGetValue("is_surprise_gift", out var isSurpriseRaw);
        if ((bool)(isSurpriseRaw ?? false))
            itemName = "Surprise Gift";
        
        SubathonEvent subathonEvent = new SubathonEvent
        {
            Id = Utils.TryParseGuid(uuid?.ToString()),
            SecondaryValue = itemName?.ToString() ?? "Item",
            User = gifterName?.ToString() ?? "Crowdfunding Complete!",
            Source = isSim ? SubathonEventSource.Simulated : SubathonEventSource.Throne
        };

        throneEvent.TryGetValue("event_type", out var eventType);
        subathonEvent.TertiaryValue = itemName?.ToString() ?? "New Gift";
        switch (eventType?.ToString())
        {
            case "gift_purchased":
                var mode = config.GetOrderTypeMode("Throne", $"{SubathonEventType.ThroneGiftPurchase}",
                    OrderTypeModes.Dollar);
                
                data.TryGetValue("price", out var price);
                double.TryParse(price?.ToString() ?? "0.00", out var priceInt);
                subathonEvent.EventType = SubathonEventType.ThroneGiftPurchase;
                subathonEvent.Currency = mode == OrderTypeModes.Dollar && !string.IsNullOrWhiteSpace(currency!.ToString()) ? currency.ToString() : "item";
                subathonEvent.Amount = 1;
                subathonEvent.Value = mode != OrderTypeModes.Dollar ? itemName?.ToString() ?? "1" :   
                    (priceInt / 100).ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                break;
            case "contribution_purchased":
                subathonEvent.EventType = SubathonEventType.ThroneGiftContribution;
                subathonEvent.Currency = currency?.ToString() ?? "";
                data.TryGetValue("amount", out var amount);
                subathonEvent.TertiaryValue = itemName?.ToString() ?? "New Contribution";
                double.TryParse(amount?.ToString(), out var amountInt);
                subathonEvent.Value =
                    (amountInt / 100).ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                break;
            case "gift_crowdfunded":
                data.TryGetValue("price", out var price2);
                double.TryParse(price2?.ToString() ?? "0.00", out var priceInt2);
                subathonEvent.EventType = SubathonEventType.ThroneCrowdGiftComplete;
                subathonEvent.User = itemName?.ToString() ?? "Crowdfunding Complete!";
                subathonEvent.Currency = currency?.ToString() ?? "item";
                subathonEvent.Value =
                    (priceInt2 / 100).ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

                break;
            default:
                return;
        }
        
        SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
    }
}