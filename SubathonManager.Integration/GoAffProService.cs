using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using GoAffPro.Client;
using GoAffPro.Client.Generated.Models;
using GoAffPro.Client.Generated.User.Sites;
using Microsoft.Extensions.Logging;
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

public class GoAffProService(
    ILogger<GoAffProService>? logger,
    IConfig config,
    ISecureStorage secureStorage,
    ITimerService? timerService = null) : IDisposable, IAppService {
    private readonly string _configSection = "GoAffPro";

    private GoAffProClient? _client;
    private CancellationTokenSource? _detectorCts;
    private bool _disposed;


    private readonly HashSet<int> _siteIds = new();

    internal Uri Endpoint = new("https://api.goaffpro.com/v1/", UriKind.Absolute);
    internal int MaxRetries = 20;

    private string? Email => secureStorage.GetOrDefault(StorageKeys.GoAffProEmail, string.Empty);
    private string? Password => secureStorage.GetOrDefault(StorageKeys.GoAffProPassword, string.Empty);

    public async Task StartAsync(CancellationToken ct = default) {
        await StopAsync(ct);
        timerService?.Register("goaffpro-auth-check", TimeSpan.FromHours(48), ReconnectCheck);

        if (string.IsNullOrWhiteSpace(Email) || string.IsNullOrWhiteSpace(Password)) return;
        _siteIds.Clear();

        try {
            _client = await GoAffProClient.CreateLoggedInAsync(
                options: new GoAffProClientOptions {
                    MaxRetries = MaxRetries,
                    Timeout = TimeSpan.FromSeconds(30),
                    BaseUrl = Endpoint
                },
                email: Email,
                password: Password, cancellationToken: ct);

            var conn = new IntegrationConnection {
                Name = "",
                Status = true,
                Source = SubathonEventSource.GoAffPro,
                Service = nameof(SubathonEventSource.GoAffPro)
            };
            IntegrationEvents.RaiseConnectionUpdate(conn);
        }
        catch (Exception e) {
            _client = null;
            logger?.LogWarning(e, "[GoAffPro] Failed to login to GoAffPro");
            return;
        }

        UserSiteListResponse? sitesResponse;
        try {
            sitesResponse = await _client.Api.User.Sites.GetAsync(reqConfig => {
                reqConfig.QueryParameters.FieldsAsGetFieldsQueryParameterType = [
                    GetFieldsQueryParameterType.Currency,
                    GetFieldsQueryParameterType.Id,
                    GetFieldsQueryParameterType.Name,
                    GetFieldsQueryParameterType.Status
                ];
                reqConfig.QueryParameters.Limit = 100;
                reqConfig.QueryParameters.Offset = 0;
            }, ct);
        }
        catch (Exception e) {
            logger?.LogWarning(e, "[GoAffPro] Failed to fetch connected sites");
            await StopAsync(ct);
            return;
        }

        if (sitesResponse?.Sites == null) return;

        foreach (UserSite site in sitesResponse.Sites.Where(site => site is
                     { Id: not null, Status: UserSite_status.Approved })) {
            // dynamically provision any store on the account we haven't seen before
            GoAffProStore store = GoAffProStoreRegistry.GetOrProvision(site.Id!.Value, site.Name ?? "");
            if (!store.Enabled) continue;
            if (!_siteIds.Add(site.Id.Value)) continue;
            GoAffProStoreRegistry.MarkActiveOnAccount(site.Id.Value);
            string currency = !string.IsNullOrWhiteSpace(site.Currency) ? site.Currency : "USD";

            var conn = new IntegrationConnection {
                Name = currency,
                Status = true,
                Source = SubathonEventSource.GoAffPro,
                Service = store.InternalName
            };
            IntegrationEvents.RaiseConnectionUpdate(conn);
        }

        _detectorCts = new CancellationTokenSource();
        if (!int.TryParse(config.Get(_configSection, "DaysOffset", "0"), out int daysOffset)) daysOffset = 0;

        _client.OrderObserverStartTime = DateTimeOffset.UtcNow - TimeSpan.FromDays(daysOffset);
        logger?.LogInformation("[GoAffPro] Started GoAffPro service with {Count} connected sites", _siteIds.Count);

        _ = Task.Run(async () => {
            logger?.LogInformation("[GoAffPro] GoAffPro is now polling for orders...");
            await foreach (UserOrderFeedItem order in _client.NewOrdersAsync(
                               TimeSpan.FromSeconds(30),
                               100,
                               _detectorCts.Token))
                HandleOrder(order);
            logger?.LogInformation("[GoAffPro] GoAffPro polling finished");
        }, _detectorCts.Token);
    }

    public Task StopAsync(CancellationToken ct = default) {
        timerService?.Unregister("goaffpro-auth-check");

        foreach (GoAffProStore store in GoAffProStoreRegistry.All()) {
            GoAffProStoreRegistry.MarkActiveOnAccount(store.SiteId, false);
            var conn = new IntegrationConnection {
                Name = "",
                Status = false,
                Source = SubathonEventSource.GoAffPro,
                Service = store.InternalName,
                Configured = false
            };
            IntegrationEvents.RaiseConnectionUpdate(conn);
        }

        var connection = new IntegrationConnection {
            Name = "",
            Status = false,
            Source = SubathonEventSource.GoAffPro,
            Service = nameof(SubathonEventSource.GoAffPro),
            Configured = !string.IsNullOrWhiteSpace(Email) && !string.IsNullOrWhiteSpace(Password)
        };
        IntegrationEvents.RaiseConnectionUpdate(connection);

        if (_client != null && _detectorCts is { IsCancellationRequested: false })
            _detectorCts.Cancel();
        _client = null;
        return Task.CompletedTask;
    }

    [ExcludeFromCodeCoverage]
    public void Dispose() {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    public void SimulateOrder(decimal total, int itemCount, decimal commissionTotal, GoAffProStore? affilStore,
        string currency = "USD") {
        var id = Guid.NewGuid().ToString();
        // id is meant to be a long but w/e

        var order = new UserOrderFeedItem();
        int idInt = affilStore?.SiteId > 0 ? affilStore.SiteId : int.MaxValue;
        order.SiteId = new UserOrderFeedItem.UserOrderFeedItem_site_id { Integer = idInt };
        order.Id = new UserOrderFeedItem.UserOrderFeedItem_id { String = id };
        order.Number = "SIMULATED";
        order.Total = total.ToString(CultureInfo.InvariantCulture);
        order.Subtotal = total.ToString(CultureInfo.InvariantCulture);
        order.Commission = commissionTotal.ToString(CultureInfo.InvariantCulture);
        order.Currency = currency;
        order.Status = "approved";
        order.CreatedAt = DateTimeOffset.UtcNow;
        order.LineItems = new List<UserOrderLineItem> {
            new() {
                Quantity = itemCount
            }
        };
        HandleOrder(order);
    }

    private void HandleOrder(UserOrderFeedItem order) {
        try {
            // If an order comes in as new and then approved, only one is added due to unique id's 

            if (order.Id == null || order.SiteId == null || order.LineItems == null ||
                string.IsNullOrWhiteSpace(order.Status) ||
                (!string.Equals(order.Status, "approved", StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(order.Status, "new", StringComparison.OrdinalIgnoreCase))) return;
            // new and approved can both come in, but same id will mean it doesn't add twice

            var ev = new SubathonEvent {
                Id = Utils.CreateGuidFromUniqueString(!string.IsNullOrWhiteSpace(order.Id.String)
                    ? order.Id.String
                    : order.Id!.Integer.ToString()),
                Source = SubathonEventSource.GoAffPro
            };
            if (order.CreatedAt.HasValue)
                ev.EventTimestamp = order.CreatedAt.Value.LocalDateTime;

            ev.User = "New Order!";
            if (!string.IsNullOrWhiteSpace(order.Number) && order.Number == "SIMULATED") {
                ev.User = "SIMULATED";
                ev.Source = SubathonEventSource.Simulated; // check based on eventType in event service
            }

            int? site = order.SiteId!.Integer;
            if (site == null || !GoAffProStoreRegistry.TryGetBySiteId((int)site, out GoAffProStore? store)) return;
            if (!store.Enabled) return;

            // we will listen for these sites regardless in orders, but will ignore if not enabled.
            bool enabled = config.GetBool(_configSection, $"{store.InternalName}.Enabled", true);
            if (!enabled) return;

            OrderTypeModes sourceMode = config.GetOrderTypeMode(_configSection,
                store.InternalName, OrderTypeModes.Dollar);

            ev.Currency = sourceMode switch {
                OrderTypeModes.Item => "items",
                OrderTypeModes.Order => "order",
                _ => order.Currency
            };
            var itemCount = 0;
            foreach (UserOrderLineItem item in order.LineItems) {
                itemCount += item.Quantity ?? 0;
                itemCount -= item.RefundQuantity ?? 0;
            }

            ev.Amount = itemCount;
            switch (sourceMode) {
                case OrderTypeModes.Dollar:
                    ev.Value = $"{order.Subtotal}";
                    break;
                case OrderTypeModes.Order:
                    ev.Value = "New";
                    break;
                default: {
                    ev.Value = $"{itemCount}";
                    break;
                }
            }

            ev.SecondaryValue = $"{order.Commission}|{order.Currency}";
            ev.EventType = SubathonEventType.GoAffProOrder;
            ev.EventTypeMeta = store.SiteId.ToString();

            ev.User = $"New {store.InternalName}";
            if (ev.Source == SubathonEventSource.Simulated)
                ev.User = $"SYSTEM {store.InternalName}";

            SubathonEvents.RaiseSubathonEventCreated(ev);
        }
        catch (Exception e) {
            logger?.LogWarning(e, "[GoAffPro] Failed to consume order. Data: {Serialize}",
                JsonSerializer.Serialize(order.AdditionalData));
        }
    }

    public async Task ReconnectCheck(CancellationToken ct = default) {
        IntegrationConnection conn =
            Utils.GetConnection(SubathonEventSource.GoAffPro, nameof(SubathonEventSource.GoAffPro));
        if (!conn.Status) return;

        await StopAsync(ct);
        await Task.Delay(50, ct);
        await StartAsync(ct);
    }

    [ExcludeFromCodeCoverage]
    protected virtual void Dispose(bool disposing) {
        if (_disposed) return;
        if (_client != null && _detectorCts is { IsCancellationRequested: true })
            _detectorCts.Cancel();
        _client?.Dispose();
        _disposed = true;
    }
}