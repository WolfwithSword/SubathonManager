using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using GoAffPro.Client;
using GoAffPro.Client.Generated.Models;
using GoAffPro.Client.Generated.User.Sites;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;
using SubathonManager.Core.Security;
using SubathonManager.Core.Security.Interfaces;
using SubathonManager.Services;

// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.Integration;

public class GoAffProService(ILogger<GoAffProService>? logger, IConfig config, ISecureStorage secureStorage, TimerService? timerService = null) : IDisposable, IAppService
{
    private bool _disposed = false;

    private GoAffProClient? _client;
    private CancellationTokenSource? _detectorCts;
    private readonly string _configSection = "GoAffPro";
    
    internal Uri Endpoint = new Uri("https://api.goaffpro.com/v1/", UriKind.Absolute);
    internal int MaxRetries = 20;

    private string? Email => secureStorage.GetOrDefault(StorageKeys.GoAffProEmail, string.Empty);
    private string? Password => secureStorage.GetOrDefault(StorageKeys.GoAffProPassword, string.Empty);


    private HashSet<int> _siteIds = new();

    public async Task StartAsync(CancellationToken ct = default)
    {
        await StopAsync(ct);
        timerService?.Register("goaffpro-auth-check", TimeSpan.FromHours(48), ReconnectCheck);
        
        if (string.IsNullOrWhiteSpace(Email) || string.IsNullOrWhiteSpace(Password)) return;
        _siteIds.Clear();

        try
        {
            _client = await GoAffProClient.CreateLoggedInAsync(
                options: new GoAffProClientOptions()
                {
                    MaxRetries = MaxRetries,
                    Timeout = TimeSpan.FromSeconds(30),
                    BaseUrl = Endpoint
                },
                email: Email,
                password: Password, cancellationToken: ct);
            
            IntegrationConnection conn = new IntegrationConnection
            {
                Name = "",
                Status = true,
                Source = SubathonEventSource.GoAffPro,
                Service = nameof(SubathonEventSource.GoAffPro)
            };
            IntegrationEvents.RaiseConnectionUpdate(conn);
        }
        catch (Exception e)
        {
            _client = null;
            logger?.LogWarning(e, "[GoAffPro] Failed to login to GoAffPro");
            return;
        }
        
        UserSiteListResponse? sitesResponse;
        try
        {
            sitesResponse = await _client.Api.User.Sites.GetAsync(reqConfig =>
            {
                reqConfig.QueryParameters.FieldsAsGetFieldsQueryParameterType =
                [
                    GetFieldsQueryParameterType.Currency,
                    GetFieldsQueryParameterType.Id,
                    GetFieldsQueryParameterType.Name,
                    GetFieldsQueryParameterType.Status
                ];
                reqConfig.QueryParameters.Limit = 100;
                reqConfig.QueryParameters.Offset = 0;
            }, ct);
        }
        catch (Exception e)
        {
            logger?.LogWarning(e, "[GoAffPro] Failed to fetch connected sites");
            await StopAsync(ct);
            return;
        }
        
        if (sitesResponse?.Sites == null) return;

        foreach (var site in sitesResponse.Sites.Where(site => site is { Id: not null, Status: UserSite_status.Approved }))
        {
            if (!GoAffProSourceeHelper.TryGetSource(site.Id!.Value, out GoAffProSource source))
            {
                logger?.LogInformation("[GoAffPro] Site {Id} ({Name}) detected on account, but not integrated. Please create an integration request if you would like it supported", site.Id!.Value, site.Name);
                continue;
            }
            if (source == GoAffProSource.Unknown || source.IsDisabled()) continue;
            if (!_siteIds.Add(site.Id.Value)) continue;
            string currency = !string.IsNullOrWhiteSpace(site.Currency) ? site.Currency : "USD";
            
            IntegrationConnection conn = new IntegrationConnection
            {
                Name = currency,
                Status = true,
                Source = SubathonEventSource.GoAffPro,
                Service = source.ToString()
            };
            IntegrationEvents.RaiseConnectionUpdate(conn);
        }
        
        _detectorCts = new CancellationTokenSource();
        if (!int.TryParse(config.Get(_configSection, "DaysOffset", "0"), out var daysOffset)) daysOffset = 0;
        
        _client.OrderObserverStartTime = DateTimeOffset.UtcNow - TimeSpan.FromDays(daysOffset);
        logger?.LogInformation("[GoAffPro] Started GoAffPro service with {Count} connected sites", _siteIds.Count);
        
        _ = Task.Run(async() => {
            logger?.LogInformation("[GoAffPro] GoAffPro is now polling for orders...");
            await foreach (var order in _client.NewOrdersAsync(
                               pollingInterval: TimeSpan.FromSeconds(30),
                               pageSize: 100,
                               cancellationToken: _detectorCts.Token))
            {
                HandleOrder(order);
            }
            logger?.LogInformation("[GoAffPro] GoAffPro polling finished");
        }, _detectorCts.Token);
    }

    public void SimulateOrder(decimal total, int itemCount, decimal commissionTotal, GoAffProSource affilStore, string currency = "USD")
    {
        string id = Guid.NewGuid().ToString();
        // id is meant to be a long but w/e
        
        UserOrderFeedItem order = new UserOrderFeedItem();
        int idInt = affilStore.TryGetSiteId(out var idParse) ? idParse : int.MaxValue;
        order.SiteId = new UserOrderFeedItem.UserOrderFeedItem_site_id() { Integer = idInt };
        order.Id = new UserOrderFeedItem.UserOrderFeedItem_id() { String = id};
        order.Number = "SIMULATED";
        order.Total = total.ToString(CultureInfo.InvariantCulture);
        order.Subtotal = total.ToString(CultureInfo.InvariantCulture);
        order.Commission = commissionTotal.ToString(CultureInfo.InvariantCulture);
        order.Currency = currency;
        order.Status = "approved";
        order.CreatedAt = DateTimeOffset.UtcNow;
        order.LineItems = new List<UserOrderLineItem>()
        {
            new UserOrderLineItem()
            {
                Quantity = itemCount
            }
        };
        HandleOrder(order);
    }

    private void HandleOrder(UserOrderFeedItem order)
    {
        try
        {
            // If an order comes in as new and then approved, only one is added due to unique id's 

            if (order.Id == null || order.SiteId == null || order.LineItems == null ||
                string.IsNullOrWhiteSpace(order.Status) ||
                (!string.Equals(order.Status, "approved", StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(order.Status, "new", StringComparison.OrdinalIgnoreCase))) return;
            // new and approved can both come in, but same id will mean it doesn't add twice

            SubathonEvent ev = new SubathonEvent
            {
                Id = Utils.CreateGuidFromUniqueString(!string.IsNullOrWhiteSpace(order.Id.String)
                    ? order.Id.String
                    : order.Id!.Integer.ToString()),
                Source = SubathonEventSource.GoAffPro
            };
            if (order.CreatedAt.HasValue)
                ev.EventTimestamp = order.CreatedAt.Value.LocalDateTime;

            ev.User = "New Order!";
            if (!string.IsNullOrWhiteSpace(order.Number) && order.Number == "SIMULATED")
            {
                ev.User = "SIMULATED";
                ev.Source = SubathonEventSource.Simulated; // check based on eventType in event service
            }

            var site = order.SiteId!.Integer;
            if (site == null || !GoAffProSourceeHelper.TryGetSource((int)site, out GoAffProSource source)) return;
            if (source == GoAffProSource.Unknown || source.IsDisabled()) return;
            
            // we will listen for these sites regardless in orders, but will ignore if not enabled.
            var enabled = config.GetBool(_configSection, $"{source}.Enabled", true);
            if (!enabled) return;

            var mode = config.Get(_configSection, $"{source}.Mode", "Dollar");

            var sourceMode = Enum.TryParse(mode, out OrderTypeModes m) ? m : OrderTypeModes.Dollar;

            ev.Currency = sourceMode switch
            {
                OrderTypeModes.Item => "items",
                OrderTypeModes.Order => "order",
                _ => order.Currency
            };
            int itemCount = 0;
            foreach (var item in order.LineItems)
            {
                itemCount += item.Quantity ?? 0;
                itemCount -= item.RefundQuantity ?? 0;
            }
            ev.Amount = itemCount;
            switch (sourceMode)
            {
                case OrderTypeModes.Dollar:
                    ev.Value = $"{order.Subtotal}";
                    break;
                case OrderTypeModes.Order:
                    ev.Value = "New";
                    break;
                default:
                {
                    ev.Value = $"{itemCount}";
                    break;
                }
            }

            ev.SecondaryValue = $"{order.Commission}|{order.Currency}";
            ev.EventType = source.GetOrderEvent();
            if (ev.EventType == SubathonEventType.Unknown) return;

            ev.User = $"New {source}";
            if (ev.Source == SubathonEventSource.Simulated)
                ev.User = $"SYSTEM {source}";

            SubathonEvents.RaiseSubathonEventCreated(ev);
        }
        catch (Exception e)
        {
            logger?.LogWarning(e, "[GoAffPro] Failed to consume order. Data: {Serialize}", JsonSerializer.Serialize(order.AdditionalData));
        }
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        timerService?.Unregister("goaffpro-auth-check");

        foreach (var integ in Enum.GetNames<GoAffProSource>())
        {           
            IntegrationConnection conn = new IntegrationConnection
            {
                Name = "",
                Status = false,
                Source = SubathonEventSource.GoAffPro,
                Service = integ
            };
            IntegrationEvents.RaiseConnectionUpdate(conn);
        }        
        
        IntegrationConnection connection = new IntegrationConnection
        {
            Name = "",
            Status = false,
            Source = SubathonEventSource.GoAffPro,
            Service = nameof(SubathonEventSource.GoAffPro)
        };
        IntegrationEvents.RaiseConnectionUpdate(connection);
        
        if (_client != null && _detectorCts is { IsCancellationRequested: false })
            _detectorCts.Cancel();
        _client = null;
        return Task.CompletedTask;
    }

    public async Task ReconnectCheck(CancellationToken ct = default)
    {
        var conn = Utils.GetConnection(SubathonEventSource.GoAffPro, nameof(SubathonEventSource.GoAffPro));
        if (!conn.Status) return;

        await StopAsync(ct);
        await Task.Delay(50, ct);
        await StartAsync(ct);
    }

    [ExcludeFromCodeCoverage]
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
    
    [ExcludeFromCodeCoverage]
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (_client != null &&  _detectorCts is { IsCancellationRequested: true })
            _detectorCts.Cancel();
        _client?.Dispose();
        _disposed = true;
    }
}