using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Objects;
using SubathonManager.Core.Models;
using SubathonManager.Core.Security;
using SubathonManager.Core.Security.Interfaces;

namespace SubathonManager.Integration;

public class TangiaService(ILogger<TangiaService>? logger, IHttpClientFactory httpClientFactory, ISecureStorage secureStorage) : IAppService
{
    private const string PollBase = "https://api.tangia.co/ack-hook/poll";
    private const string PingBase = "https://api.tangia.co/ack-hook/ping";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(10);

    private string? EventKey => secureStorage.GetOrDefault(StorageKeys.TangiaEventKey, string.Empty);

    private CancellationTokenSource? _cts;
    private int _instanceId;

    public Task StartAsync(CancellationToken ct = default)
    {
        StopPolling();

        var key = EventKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            logger?.LogInformation("[Tangia] Not configured. Integration disabled.");
            BroadcastStatus(false);
            return Task.CompletedTask;
        }

        _instanceId = Random.Shared.Next(100_000_000, 999_999_999);
        BroadcastStatus(true);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;
        _ = Task.Run(() => PollLoopAsync(key, token), token);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        StopPolling();
        BroadcastStatus(false);
        return Task.CompletedTask;
    }

    private void StopPolling()
    {
        if (_cts is { IsCancellationRequested: false })
            _cts.Cancel();
        _cts = null;
    }

    [ExcludeFromCodeCoverage]
    private async Task PollLoopAsync(string key, CancellationToken ct)
    {
        logger?.LogInformation("[Tangia] Polling started");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(key, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "[Tangia] Unexpected error during poll");
            }

            try { await Task.Delay(PollInterval, ct); }
            catch (OperationCanceledException) { break; }
        }

        logger?.LogInformation("[Tangia] Polling stopped");
    }

    internal async Task PollOnceAsync(string key, CancellationToken ct)
    {
        double cacheBust = Random.Shared.NextDouble();
        string url = $"{PollBase}?key={Uri.EscapeDataString(key)}&cacheBust={cacheBust:F16}&instance={_instanceId}";

        using var client = httpClientFactory.CreateClient(nameof(TangiaService));
        client.Timeout = HttpTimeout;

        HttpResponseMessage response;
        try
        {
            response = await client.GetAsync(url, ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            logger?.LogDebug("[Tangia] Poll request timed out");
            return;
        }

        // wasnt sure if this is needed, keeping in case it is later
        //_ = Task.Run(() => PingAsync(key, ct), ct);

        if (response.StatusCode == HttpStatusCode.NoContent)
            return;

        if (!response.IsSuccessStatusCode)
        {
            logger?.LogWarning("[Tangia] Unexpected poll response: {Status}", response.StatusCode);
            return;
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(json))
            return;

        TangiaResponse? payload;
        try
        {
            payload = JsonSerializer.Deserialize<TangiaResponse>(json);
        }
        catch (JsonException ex)
        {
            logger?.LogWarning(ex, "[Tangia] Failed to deserialize poll response");
            return;
        }

        if (payload?.Events == null || payload.Events.Count == 0)
            return;

        foreach (var ev in payload.Events)
        {
            logger?.LogDebug("[Tangia] Event received: {Event}", JsonSerializer.Serialize(ev));
            if (string.IsNullOrWhiteSpace(ev.EventId)) continue;
            if (ev.EventId.Contains("_cp_")) continue;
            if (ev.Data?.OverlayParams?.TriggerData?.Price <= 0) continue;
            if (ev.EventId.Contains("_test_")) continue;
            
            // var time = TimeSpan.FromMilliseconds(ev.UnixMs);
            SubathonEvent sev = new SubathonEvent()
            {
                User = ev.Data?.OverlayParams?.BuyerInfo?.Name ?? ev.Data?.OverlayParams?.Name ?? "Tangia User",
                Id = Utils.CreateGuidFromUniqueString(ev.EventId),
                Value = $"{ev.Data?.OverlayParams?.TriggerData?.Price}",
                Currency = "tokens",
                Source = SubathonEventSource.Tangia,
                EventType = SubathonEventType.TangiaTokens,
                // EventTimestamp = new DateTime(time.Ticks).ToLocalTime(), // so apparently this isnt real unix, it's like, UTC+2 or something
            };
            SubathonEvents.RaiseSubathonEventCreated(sev);
        }
    }

    [ExcludeFromCodeCoverage]
    [Obsolete("Unused and may not be required for functionality")]
    private async Task PingAsync(string key, CancellationToken ct)
    {
        try
        {
            string url = $"{PingBase}?key={Uri.EscapeDataString(key)}&instance={_instanceId}&currentEvent=undefined";
            using var client = httpClientFactory.CreateClient(nameof(TangiaService));
            await client.PostAsync(url, content: null, ct);
        }
        catch { /**/ }
    }

    public static void SimulateTangiaTokens(long amount)
    {
        var ev = new SubathonEvent
        {
            Source = SubathonEventSource.Simulated,
            EventType = SubathonEventType.TangiaTokens,
            User = "SYSTEM",
            Value = amount.ToString(),
            Currency = "tokens",
            EventTimestamp = DateTime.Now.ToLocalTime()
        };
        SubathonEvents.RaiseSubathonEventCreated(ev);
    }

    public static bool TryParseEventKey(string overlayUrl, out string eventKey)
    {
        eventKey = string.Empty;
        if (string.IsNullOrWhiteSpace(overlayUrl)) return false;

        try
        {
            var uri = new Uri(overlayUrl);
            var path = uri.AbsolutePath;
            var lastSegment = path.Split('/').LastOrDefault(s => !string.IsNullOrWhiteSpace(s));
            if (string.IsNullOrWhiteSpace(lastSegment) || !lastSegment.StartsWith("evt_"))
                return false;

            eventKey = lastSegment;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void BroadcastStatus(bool connected)
    {
        IntegrationEvents.RaiseConnectionUpdate(new IntegrationConnection
        {
            Source = SubathonEventSource.Tangia,
            Service = nameof(SubathonEventSource.Tangia),
            Name = "",
            Status = connected,
            Configured = !string.IsNullOrWhiteSpace(EventKey)
        });
    }

    private class TangiaResponse
    {
        [JsonPropertyName("Events")]
        public List<TangiaEvent>? Events { get; set; }
    }

    internal class TangiaEvent
    {
        [JsonPropertyName("UnixMS")]
        public long UnixMs { get; set; }

        [JsonPropertyName("Data")]
        public TangiaEventData? Data { get; set; }

        [JsonPropertyName("EventID")]
        public string? EventId { get; set; }
    }

    internal class TangiaEventData
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("URL")]
        public string? Url { get; set; }

        [JsonPropertyName("OverlayParams")]
        public TangiaOverlayParams? OverlayParams { get; set; }
    }

    internal class TangiaOverlayParams
    {
        
        [JsonPropertyName("InteractionTitle")]
        public string? InteractionTitle { get; set; }
        [JsonPropertyName("TriggerData")]
        public TangiaTriggerData? TriggerData { get; set; }
        [JsonPropertyName("BuyerInfo")]
        public TangiaBuyerInfo? BuyerInfo { get; set; }
        [JsonPropertyName("duration")]
        public long? Duration { get; set; }
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    internal class TangiaTriggerData
    {
        [JsonPropertyName("price")]
        public int? Price { get; set; }
    }
    internal class TangiaBuyerInfo
    {
        [JsonPropertyName("has-plus")]
        public bool? HasPlus { get; set; }
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }
}
