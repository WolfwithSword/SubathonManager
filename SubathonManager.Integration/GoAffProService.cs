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

namespace SubathonManager.Integration;

public class GoAffProService(ILogger<GoAffProService>? logger, IConfig config) : IDisposable, IAppService
{
    private bool _disposed = false;

    private GoAffProClient? _client;
    private CancellationTokenSource? _detectorCts;
    private readonly string _configSection = "GoAffPro";
    
    internal Uri Endpoint = new Uri("https://api.goaffpro.com/v1/", UriKind.Absolute);
    internal int MaxRetries = 20;
    
    private static readonly Dictionary<int, GoAffProSource> SiteMapping = new() // supported sites
    {
        { 165328, GoAffProSource.GamerSupps },
        { 132230, GoAffProSource.UwUMarket }
    };
    private static readonly Dictionary<GoAffProSource, int> ReverseSiteMapping = 
        SiteMapping.ToDictionary(x => x.Value, x => x.Key);
    
    private HashSet<int> _siteIds = new();

    public static readonly Dictionary<GoAffProSource, SubathonEventType> OrderMapping = new()
    {
        { GoAffProSource.GamerSupps, SubathonEventType.GamerSuppsOrder},
        { GoAffProSource.UwUMarket, SubathonEventType.UwUMarketOrder}
    };
    

    public async Task StartAsync(CancellationToken ct = default)
    {
        await StopAsync(ct);

        var email = config.GetFromEncoded(_configSection, "Email",string.Empty);
        var password = config.GetFromEncoded(_configSection, "Password", string.Empty);

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password)) return;
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
                email: email,
                password: password, cancellationToken: ct);
            
            IntegrationEvents.RaiseConnectionUpdate(true, SubathonEventSource.GoAffPro,
                "", nameof(SubathonEventSource.GoAffPro));
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

        var options = new JsonSerializerOptions///////////////////////////////////////////////// todo remove
        {//////////
            WriteIndented = true//////////
        };///////////
        
        
        if (sitesResponse?.Sites == null) return;

        foreach (var site in sitesResponse.Sites.Where(site => site is { Id: not null, Status: UserSite_status.Approved }))
        {
            if (!SiteMapping.ContainsKey(site.Id!.Value) || !_siteIds.Add(site!.Id.Value)) continue;
            string currency = !string.IsNullOrWhiteSpace(site.Currency) ? site.Currency : "USD";

            IntegrationEvents.RaiseConnectionUpdate(true, SubathonEventSource.GoAffPro,
                currency, SiteMapping[(int)site.Id].ToString());

            Console.WriteLine($"{SiteMapping[(int)site.Id].ToString()} connected"); /////////////// todo remove
        }

        Console.WriteLine(JsonSerializer.Serialize(sitesResponse.AdditionalData, options));////////////////// todo remove
        
        _detectorCts = new CancellationTokenSource();
        _client.OrderObserverStartTime = DateTimeOffset.UtcNow;
        //_client.OrderDetected += OnOrderDetected;
        
        _ = Task.Run(async() => {
            await foreach (var order in _client.NewOrdersAsync(
                               pollingInterval: TimeSpan.FromSeconds(30),
                               pageSize: 100,
                               cancellationToken: _detectorCts.Token))
            {
                HandleOrder(order);
            }
        }, _detectorCts.Token);
    }

    public void SimulateOrder(decimal total, int itemCount, decimal commissionTotal, GoAffProSource affilStore, string currency = "USD")
    {
        string id = Guid.NewGuid().ToString();
        // id is meant to be a long but w/e
        
        UserOrderFeedItem order = new UserOrderFeedItem();
        int idInt = ReverseSiteMapping.TryGetValue(affilStore, out var idParse) ? idParse : int.MaxValue;
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
        // If an order comes in as new and then approved, only one is added due to unique id's 
        
        // todo remove
        var options = new JsonSerializerOptions////////////////////////
        {////////////////////////
            WriteIndented = true/////////////////
        };/////////////////////////
        Console.WriteLine(JsonSerializer.Serialize(order.AdditionalData, options));//////////////////

        if (order.Id == null || order.SiteId == null || order.LineItems == null || string.IsNullOrWhiteSpace(order.Status) ||
            (!string.Equals(order.Status, "approved", StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(order.Status, "new", StringComparison.OrdinalIgnoreCase))) return;
        // new and approved can both come in, but same id will mean it doesn't add twice
        
        SubathonEvent ev = new SubathonEvent
        {
            Id = Utils.CreateGuidFromUniqueString(!string.IsNullOrWhiteSpace(order.Id.String) ? order.Id.String : order.Id!.Integer.ToString()),
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

        var site = (int) order.SiteId!.Integer;
        if (!SiteMapping.TryGetValue(site, out GoAffProSource source)) return;
        
        // we will listen for these sites regardless in orders, but will ignore if not enabled.
        var enabled = config.GetBool(_configSection, $"{source}.Enabled", true);
        if (!enabled) return;
        
        var mode = config.Get(_configSection, $"{source}.Mode", "Dollar");

        var sourceMode = Enum.TryParse(mode, out GoAffProModes m) ? m : GoAffProModes.Dollar;
        
        ev.Currency = sourceMode switch
        {
            GoAffProModes.Item => "items",
            GoAffProModes.Order => "order",
            _ => order.Currency
        };
        
        switch (sourceMode)
        {
            case GoAffProModes.Dollar:
                ev.Value = $"{order.Subtotal}";
                break;
            case GoAffProModes.Order:
                ev.Value = "New";
                break;
            default:
            {
                int itemCount = 0;
                foreach (var item in order.LineItems)
                {
                    itemCount += item.Quantity ?? 0;
                    itemCount -= item.RefundQuantity ?? 0;
                }
                ev.Value = $"{itemCount}";
                break;
            }
        }
        
        ev.SecondaryValue = $"{order.Commission}|{order.Currency}";
        ev.EventType = OrderMapping.GetValueOrDefault(source, SubathonEventType.Unknown);
        if (ev.EventType == SubathonEventType.Unknown) return;
        
        ev.User = $"New {source}";
        if (ev.Source == SubathonEventSource.Simulated)
            ev.User = $"SYSTEM {source}";
        
        SubathonEvents.RaiseSubathonEventCreated(ev);
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        foreach (var integ in Enum.GetNames<GoAffProSource>())
        {
            IntegrationEvents.RaiseConnectionUpdate(false, SubathonEventSource.GoAffPro,
                "", integ);
        }
        IntegrationEvents.RaiseConnectionUpdate(false, SubathonEventSource.GoAffPro,
            "", nameof(SubathonEventSource.GoAffPro));
        
        if (_client != null && _detectorCts is { IsCancellationRequested: false })
            _detectorCts.Cancel();
        _client = null;
        return Task.CompletedTask;
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
        if (!_disposed)
        {
            if (_client != null &&  _detectorCts is { IsCancellationRequested: true })
                _detectorCts.Cancel();
            _client?.Dispose();
            _disposed = true;
        }
    }
}