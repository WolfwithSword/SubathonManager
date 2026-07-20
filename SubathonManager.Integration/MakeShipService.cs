using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;

namespace SubathonManager.Integration;

public partial class MakeShipService(ILogger<MakeShipService>? logger, IHttpClientFactory httpClientFactory,
    ITimerService? timerService) : IAppService
{
    // pledges and campaign sales must always end up as checked for by item count / quantity
    // because we poll and wrap up delta between (ignoring bootup or initial number)
    private const string PreProductApiBase = "https://api.preproduct.io/api/preproducts/";
    private const string PreProductShopQuery = "?shop=makeship-store.myshopify.com";
    private const string StorefrontBase = "https://storefront.makeship.com";

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(20);

    [GeneratedRegex("""<[^>]*id="preproduct-pledge"[^>]*data-id="(\d+)""", RegexOptions.IgnoreCase)]
    private static partial Regex PetitionPledgeIdRegex();

    [GeneratedRegex("""<[^>]*data-id="(\d+)"[^>]*id="preproduct-pledge""", RegexOptions.IgnoreCase)]
    private static partial Regex PetitionPledgeIdAltRegex();

    [GeneratedRegex(@"gid://shopify/Product/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex CampaignProductIdRegex();

    private const string TimerKey = "makeship-poll";
    private static readonly TimeSpan StaggerDelay = TimeSpan.FromSeconds(2);
    private int _pollSweepRunning;
    private readonly HashSet<Guid> _syncedSinceStart = new();
    private readonly Dictionary<Guid, int> _contentFailures = new();
    private const int InvalidAfterConsecutiveFailures = 3;
    private readonly Lock _syncLock = new();
    private bool _running;
    private bool _lastBroadcastStatus;

    public bool Connected { get; private set; }

    public Task StartAsync(CancellationToken ct = default)
    {
        UnregisterTimers();
        lock (_syncLock)
        {
            _syncedSinceStart.Clear();
            _contentFailures.Clear();
        }

        var trackings = MakeShipTrackingRegistry.All();
        foreach (var tracking in trackings)
        {
            var classified = MakeShipTrackingRegistry.ClassifyUrl(tracking.Url);
            if (classified == MakeShipProductType.Invalid) continue;
            if (!string.IsNullOrEmpty(tracking.ShopifyProductId))
                tracking.ProductType = classified;
            else if (tracking.ProductType == MakeShipProductType.Invalid)
                tracking.ProductType = MakeShipProductType.Unknown;
        }
        if (trackings.Count == 0)
        {
            logger?.LogInformation("[MakeShip] No urls tracked. Integration inactive.");
            _running = false;
            BroadcastStatus(force: true);
            return Task.CompletedTask;
        }

        _running = true;

        timerService?.Register(TimerKey, PollInterval, PollTimerTick);
        logger?.LogInformation("[MakeShip] Tracking started ({Count} urls)", trackings.Count);

        _ = PollTimerTick(ct);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        UnregisterTimers();
        _running = false;
        Connected = false;
        BroadcastStatus(force: true);
        return Task.CompletedTask;
    }

    public async Task RestartAsync()
    {
        await StopAsync();
        await StartAsync();
    }

    private void UnregisterTimers() => timerService?.Unregister(TimerKey);

    [ExcludeFromCodeCoverage]
    private Task PollTimerTick(CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _pollSweepRunning, 1, 0) != 0) return Task.CompletedTask;
        _ = Task.Run(async () =>
        {
            try { await PollAllAsync(ct); }
            finally { Interlocked.Exchange(ref _pollSweepRunning, 0); }
        }, ct);
        return Task.CompletedTask;
    }

    [ExcludeFromCodeCoverage]
    private async Task PollAllAsync(CancellationToken ct)
    {
        bool first = true;
        foreach (var tracking in MakeShipTrackingRegistry.All())
        {
            if (!_running || ct.IsCancellationRequested) return;
            if (!first)
            {
                try { await Task.Delay(StaggerDelay, ct); }
                catch (OperationCanceledException) { return; }
            }
            first = false;

            try
            {
                await PollTrackingAsync(tracking, ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "[MakeShip] Failed to poll {Url}", tracking.Url);
            }
        }

        var trackings = MakeShipTrackingRegistry.All();
        Connected = trackings.Count > 0 && trackings.Any(IsTracked);
        BroadcastStatus();
    }

    private static bool IsTracked(MakeShipTracking t) =>
        t.ProductType is MakeShipProductType.Petition or MakeShipProductType.Campaign
        && !string.IsNullOrEmpty(t.ShopifyProductId);

    internal async Task PollTrackingAsync(MakeShipTracking tracking, CancellationToken ct)
    {
        if (tracking.ProductType == MakeShipProductType.Invalid) return;
        if (string.IsNullOrEmpty(tracking.ShopifyProductId) && !await ResolveTrackingAsync(tracking, ct))
        {
            MakeShipTrackingRegistry.RaiseTrackingUpdated(tracking);
            return;
        }

        int previousPledges = tracking.Orders;
        int previousSales = tracking.Sales;
        bool refreshed = tracking.ProductType switch
        {
            MakeShipProductType.Petition => await RefreshPetitionAsync(tracking, ct),
            MakeShipProductType.Campaign => await RefreshCampaignAsync(tracking, ct),
            _ => false
        };
        tracking.PollFailing = !refreshed;

        if (!refreshed)
        {
            logger?.LogWarning("[MakeShip] Poll failed for {Type} '{Name}' ({Url})",
                tracking.ProductType, tracking.Name, tracking.Url);
            MakeShipTrackingRegistry.RaiseTrackingUpdated(tracking);
            return;
        }

        bool firstSyncSinceStart;
        lock (_syncLock) { firstSyncSinceStart = _syncedSinceStart.Add(tracking.Id); }

        if (!firstSyncSinceStart)
        {
            (int previous, int current) = tracking.ProductType == MakeShipProductType.Petition
                ? (previousPledges, tracking.Orders)
                : (previousSales, tracking.Sales);
            if (current > previous)
                RaiseNewOrderEvent(tracking, previous, current);
        }

        MakeShipTrackingRegistry.RaiseTrackingUpdated(tracking);
    }

    private bool RegisterContentFailure(MakeShipTracking tracking)
    {
        int failures;
        lock (_syncLock)
        {
            failures = _contentFailures.GetValueOrDefault(tracking.Id) + 1;
            _contentFailures[tracking.Id] = failures;
        }
        if (failures < InvalidAfterConsecutiveFailures)
        {
            logger?.LogInformation("[MakeShip] Content failure {Count}/{Max} for {Url}; retrying next poll",
                failures, InvalidAfterConsecutiveFailures, tracking.Url);
            return false;
        }
        tracking.ProductType = MakeShipProductType.Invalid;
        return true;
    }

    private void ClearContentFailures(Guid trackingId)
    {
        lock (_syncLock) { _contentFailures.Remove(trackingId); }
    }

    private async Task<bool> ResolveTrackingAsync(MakeShipTracking tracking, CancellationToken ct)
    {
        var type = MakeShipTrackingRegistry.ClassifyUrl(tracking.Url);
        if (type == MakeShipProductType.Invalid)
        {
            tracking.ProductType = MakeShipProductType.Invalid;
            logger?.LogWarning("[MakeShip] Url is not a petition or product campaign: {Url}", tracking.Url);
            return false;
        }

        string? html = await GetStringAsync(tracking.Url, ct);
        if (html == null) return false;

        string? productId = type == MakeShipProductType.Petition
            ? MatchFirst(html, PetitionPledgeIdRegex(), PetitionPledgeIdAltRegex())
            : MatchFirst(html, CampaignProductIdRegex());

        if (string.IsNullOrEmpty(productId))
        {
            if (RegisterContentFailure(tracking))
                logger?.LogWarning("[MakeShip] Could not find a product id on page (not a trackable {Type}?): {Url}",
                    type, tracking.Url);
            return false;
        }

        ClearContentFailures(tracking.Id);
        tracking.ProductType = type;
        tracking.ShopifyProductId = productId;
        if (string.IsNullOrWhiteSpace(tracking.Name)
            || tracking.Name == MakeShipTrackingRegistry.GetSlug(tracking.Url))
            tracking.Name = MakeShipTrackingRegistry.GetDisplayNameFromSlug(tracking.Url);
        logger?.LogInformation("[MakeShip] Resolved {Type} '{Name}' -> product {Id}", type, tracking.Name, productId);
        return true;
    }

    private async Task<bool> RefreshPetitionAsync(MakeShipTracking tracking, CancellationToken ct)
    {
        string? json = await GetStringAsync(
            $"{PreProductApiBase}{tracking.ShopifyProductId}.json{PreProductShopQuery}", ct);
        if (json == null) return false;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (FindProperty(doc.RootElement, "name") is { ValueKind: JsonValueKind.String } nameEl)
            {
                var name = nameEl.GetString();
                if (!string.IsNullOrWhiteSpace(name) && !tracking.Name.Equals(name)) tracking.Name = name;
            }
            if (FindProperty(doc.RootElement, "sales_actual") is { } salesEl && TryGetInt(salesEl, out int sales))
                tracking.Sales = sales;
        }
        catch (JsonException ex)
        {
            logger?.LogWarning(ex, "[MakeShip] Bad preproduct json for {Name}", tracking.Name);
            return false;
        }

        return await RefreshPledgeCountAsync(tracking,
            $"{StorefrontBase}/orders/petitions/{tracking.ShopifyProductId}/pledges/count", ct);
    }

    private async Task<bool> RefreshCampaignAsync(MakeShipTracking tracking, CancellationToken ct)
    {
        string? json = await GetStringAsync(
            $"{StorefrontBase}/products/{tracking.ShopifyProductId}/sales-quantity", ct);
        if (json == null)
        {
            logger?.LogWarning("[MakeShip] No sales-quantity for product {Id} ({Url})",
                tracking.ShopifyProductId, tracking.Url);
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (FindProperty(doc.RootElement, "quantity") is { } qtyEl && TryGetInt(qtyEl, out int quantity))
                tracking.Sales = quantity;
        }
        catch (JsonException ex)
        {
            logger?.LogWarning(ex, "[MakeShip] Bad sales-quantity json for {Name}", tracking.Name);
            return false;
        }

        return await RefreshPledgeCountAsync(tracking,
            $"{StorefrontBase}/orders/petitions/{tracking.ShopifyProductId}/pledges/count", ct);
    }

    private async Task<bool> RefreshPledgeCountAsync(MakeShipTracking tracking, string url, CancellationToken ct)
    {
        string? body = await GetStringAsync(url, ct);
        if (body == null)
        {
            logger?.LogWarning("[MakeShip] Pledge count request failed for '{Name}' ({Url})", tracking.Name, url);
            return false;
        }

        string trimmed = body.Trim().Trim('"');
        if (!int.TryParse(trimmed, out int count))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (FindProperty(doc.RootElement, "count") is { } el && TryGetInt(el, out count)) { }
                else if (FindProperty(doc.RootElement, "quantity") is { } el2 && TryGetInt(el2, out count)) { }
                else
                {
                    logger?.LogWarning("[MakeShip] Unrecognized pledge count response for '{Name}': {Body}",
                        tracking.Name, Truncate(body, 200));
                    return false;
                }
            }
            catch (JsonException)
            {
                logger?.LogWarning("[MakeShip] Unrecognized pledge count response for '{Name}': {Body}",
                    tracking.Name, Truncate(body, 200));
                return false;
            }
        }

        tracking.Orders = count;
        return true;
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "...";

    private void RaiseNewOrderEvent(MakeShipTracking tracking, int fromCount, int toCount)
    {
        var eventType = tracking.ProductType == MakeShipProductType.Petition
            ? SubathonEventType.MakeShipPledge
            : SubathonEventType.MakeShipSale;
        
        string unit = eventType == SubathonEventType.MakeShipPledge ? "pledges" : "sales";
        int delta = toCount - fromCount;

        SubathonEvents.RaiseSubathonEventCreated(new SubathonEvent
        {
            Id = Utils.CreateGuidFromUniqueString($"makeship-{tracking.Id}-{fromCount}-{toCount}"),
            Source = SubathonEventSource.MakeShip,
            EventType = eventType,
            EventTypeMeta = tracking.Name,
            User = $"New MakeShip {(delta > 1 ? unit : unit.TrimEnd('s'))}!",
            Currency = unit,
            Value = $"{delta}",
            Amount = delta,
            TertiaryValue = tracking.Name,
            EventTimestamp = DateTime.Now
        });
        logger?.LogDebug("[MakeShip] {Count} new {Unit} for '{Name}' ({From} -> {To})",
            delta, unit, tracking.Name, fromCount, toCount);
    }

    public static void Simulate(string name, bool isPetition, int count)
    {
        if (string.IsNullOrWhiteSpace(name)) name = "Test Plush";
        if (count <= 0) count = 1;
        var eventType = isPetition ? SubathonEventType.MakeShipPledge : SubathonEventType.MakeShipSale;
        string unit = isPetition ? "pledges" : "sales";

        SubathonEvents.RaiseSubathonEventCreated(new SubathonEvent
        {
            Source = SubathonEventSource.Simulated,
            EventType = eventType,
            EventTypeMeta = name.Trim(),
            User = $"New MakeShip {(count > 1 ? unit : unit.TrimEnd('s'))}!",
            Currency = unit,
            Value = $"{count}",
            Amount = count,
            TertiaryValue = name.Trim(),
            EventTimestamp = DateTime.Now
        });
    }

    private void BroadcastStatus(bool force = false)
    {
        var trackings = MakeShipTrackingRegistry.All();
        int tracked = trackings.Count(IsTracked);
        bool status = _running && tracked > 0;
        if (!force && status == _lastBroadcastStatus) return;
        _lastBroadcastStatus = status;
        IntegrationEvents.RaiseConnectionUpdate(new IntegrationConnection
        {
            Source = SubathonEventSource.MakeShip,
            Service = nameof(SubathonEventSource.MakeShip),
            Name = trackings.Count == 0 ? "" : $"{tracked}/{trackings.Count} tracked",
            Status = status,
            Configured = trackings.Count > 0
        });
    }

    private async Task<string?> GetStringAsync(string url, CancellationToken ct)
    {
        using var client = httpClientFactory.CreateClient(nameof(MakeShipService));
        client.Timeout = HttpTimeout;
        if (!client.DefaultRequestHeaders.UserAgent.Any())
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) SubathonManager");
        try
        {
            var response = await client.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger?.LogDebug("[MakeShip] {Status} from {Url}", response.StatusCode, url);
                return null;
            }
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger?.LogDebug("[MakeShip] Timeout fetching {Url}", url);
            return null;
        }
        catch (HttpRequestException ex)
        {
            logger?.LogDebug(ex, "[MakeShip] Request failed for {Url}", url);
            return null;
        }
    }

    private static string? MatchFirst(string input, params Regex[] regexes)
    {
        foreach (var regex in regexes)
        {
            var match = regex.Match(input);
            if (match.Success) return match.Groups[1].Value;
        }
        return null;
    }

    private static JsonElement? FindProperty(JsonElement element, string name, int depth = 3)
    {
        if (element.ValueKind != JsonValueKind.Object || depth < 0) return null;
        if (element.TryGetProperty(name, out var direct)) return direct;
        foreach (var child in element.EnumerateObject())
        {
            if (child.Value.ValueKind != JsonValueKind.Object) continue;
            if (FindProperty(child.Value, name, depth - 1) is { } found) return found;
        }
        return null;
    }

    private static bool TryGetInt(JsonElement element, out int value)
    {
        value = 0;
        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetInt32(out value),
            JsonValueKind.String => int.TryParse(element.GetString(), out value),
            _ => false
        };
    }
}
