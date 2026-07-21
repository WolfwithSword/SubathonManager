using System.Numerics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Objects;
using SubathonManager.Core.Models;

namespace SubathonManager.Integration;

public class JuniperService(ILogger<JuniperService>? logger, IHttpClientFactory httpClientFactory,
    ITimerService? timerService = null) : IDisposable, IAppService
{
    internal const string OrdersSumBase = "https://prod.orders.junipercreates.com/orders/sum";
    internal const string FixedEndTime = "2099-11-20T07:59:00.000Z";
    private const int MaxIdsPerRequest = 10;
    private const string TimerKey = "juniper-orders";

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(20);

    private DateTime _lastFetchTime = DateTime.UtcNow;
    private bool _running;
    private bool _errored;
    private bool _lastBroadcastStatus;

    public bool Connected => _running && !_errored;

    internal static List<string> MakeQueryUrls(IReadOnlyList<BigInteger> productIds, DateTime startTime)
    {
        var urls = new List<string>();
        string start = startTime.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
        for (int i = 0; i < productIds.Count; i += MaxIdsPerRequest)
        {
            string joined = string.Join(",", productIds.Skip(i).Take(MaxIdsPerRequest));
            urls.Add($"{OrdersSumBase}?productIds={joined}&startTime={start}&endTime={FixedEndTime}");
        }
        return urls;
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        timerService?.Unregister(TimerKey);
        _errored = false;

        var productIds = JuniperStoreRegistry.AllValidProductIds();
        if (productIds.Count == 0)
        {
            logger?.LogInformation("[Juniper] No products tracked. Integration inactive.");
            _running = false;
            BroadcastStatus(force: true);
            return Task.CompletedTask;
        }

        _running = true;
        _lastFetchTime = DateTime.UtcNow;
        timerService?.Register(TimerKey, Interval, PollAsync);
        logger?.LogInformation("[Juniper] Tracking started ({Count} products)", productIds.Count);
        BroadcastStatus(force: true);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        timerService?.Unregister(TimerKey);
        _running = false;
        BroadcastStatus(force: true);
        return Task.CompletedTask;
    }

    public async Task RestartAsync()
    {
        await StopAsync();
        await StartAsync();
    }

    public void Dispose() => timerService?.Unregister(TimerKey);

    internal async Task PollAsync(CancellationToken ct)
    {
        if (!_running) return;

        var startTime = _lastFetchTime;
        _lastFetchTime = DateTime.UtcNow;

        var productIds = JuniperStoreRegistry.AllValidProductIds();
        if (productIds.Count == 0) return;

        bool anyError = false;
        foreach (var url in MakeQueryUrls(productIds, startTime))
        {
            string? json = await GetStringAsync(url, ct);
            if (json == null)
            {
                anyError = true;
                continue;
            }
            if (!HandleOrdersResponse(json, startTime))
                anyError = true;
        }

        _errored = anyError;
        BroadcastStatus();
    }

    internal bool HandleOrdersResponse(string json, DateTime windowStart)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            logger?.LogDebug("[Juniper] orders/sum response ({Kind}): {Body}",
                root.ValueKind, json);

            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("products_details", out var products) ||
                products.ValueKind != JsonValueKind.Object)
            {
                logger?.LogWarning("[Juniper] orders/sum response missing products_details: {Body}", json);
                return false;
            }

            foreach (JsonProperty product in products.EnumerateObject())
            {
                string productId = product.Name;

                if (!product.Value.TryGetProperty("units_sold", out var unitsEl) ||
                    !unitsEl.TryGetInt32(out int unitsSold))
                    continue;

                if (unitsSold <= 0) continue;

                if (!JuniperStoreRegistry.TryGetProduct(productId, out var juniperProduct))
                {
                    continue;
                }
                
                SubathonEvent ev = new SubathonEvent
                {
                    Amount = unitsSold,
                    EventTypeMeta = productId,
                    Currency = "sales",
                    EventType = SubathonEventType.JuniperMerchSale,
                    Source = SubathonEventSource.JuniperCreates,
                    Value = $"{unitsSold}",
                    TertiaryValue = juniperProduct.ProductName,
                    User = JuniperStoreRegistry.GetStoreName(juniperProduct.StoreId)
                };
                SubathonEvents.RaiseSubathonEventCreated(ev);
            }
            
            return true;
        }
        catch (JsonException ex)
        {
            logger?.LogWarning(ex, "[Juniper] Bad orders/sum response: {Body}", json);
            return false;
        }
    }

    public static void Simulate(string meta, int count)
    {
        if (count <= 0) count = 1;
        meta = meta?.Trim() ?? "";
        string name = JuniperOrderHelper.TryGetProduct(meta, out var product)
            ? product.ProductName
            : "Test Product";

        SubathonEvents.RaiseSubathonEventCreated(new SubathonEvent
        {
            Source = SubathonEventSource.Simulated,
            EventType = SubathonEventType.JuniperMerchSale,
            EventTypeMeta = meta,
            User = product == null ? "JuniperCreates Store" : JuniperStoreRegistry.GetStoreName(product.StoreId),
            Currency = "sales",
            Value = $"{count}",
            Amount = count,
            TertiaryValue = name
        });
    }

    private void BroadcastStatus(bool force = false)
    {
        var stores = JuniperStoreRegistry.AllStores();
        bool status = Connected;
        if (!force && status == _lastBroadcastStatus) return;
        _lastBroadcastStatus = status;

        IntegrationEvents.RaiseConnectionUpdate(new IntegrationConnection
        {
            Source = SubathonEventSource.JuniperCreates,
            Service = nameof(SubathonEventSource.JuniperCreates),
            Name = stores.Count == 0 ? "" : $"{stores.Count} store(s)",
            Status = status,
            Configured = stores.Count > 0
        });

        foreach (var store in stores)
        {
            IntegrationEvents.RaiseConnectionUpdate(new IntegrationConnection
            {
                Source = SubathonEventSource.JuniperCreates,
                Service = store.StoreName,
                Name = $"{store.Products.Count} tracked",
                Status = status && store.Enabled,
                Configured = true
            });
        }
    }

    private async Task<string?> GetStringAsync(string url, CancellationToken ct)
    {
        using var client = httpClientFactory.CreateClient(nameof(JuniperService));
        client.Timeout = HttpTimeout;
        if (!client.DefaultRequestHeaders.UserAgent.Any())
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) SubathonManager");
        try
        {
            var response = await client.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger?.LogWarning("[Juniper] {Status} from {Url}", response.StatusCode, url);
                return null;
            }
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger?.LogWarning("[Juniper] Timeout fetching {Url}", url);
            return null;
        }
        catch (HttpRequestException ex)
        {
            logger?.LogWarning(ex, "[Juniper] Request failed for {Url}", url);
            return null;
        }
    }
}
