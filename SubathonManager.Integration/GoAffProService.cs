using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using GoAffPro.Client;
using GoAffPro.Client.Events;
using GoAffPro.Client.Generated.User;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Interfaces;

namespace SubathonManager.Integration;

// TODO
public class GoAffProService : IDisposable, IAppService
{
    private bool _disposed = false;
    
    private readonly Utils.ServiceReconnectState _reconnectState = 
        new(TimeSpan.FromSeconds(5), maxRetries: 50, maxBackoff: TimeSpan.FromMinutes(5));
    
    private readonly ILogger? _logger;
    private readonly IConfig _config;

    private DateTime lastCheckedTime = DateTime.UtcNow;
    private GoAffProEventDetector detector;
    private GoAffProClient _client;
    private CancellationTokenSource? _detectorCts;

    private readonly Dictionary<int, GoAffProSource> _siteMapping = new() // supported sites
    {
        { 165328, GoAffProSource.GamerSupps }
    };
    
    // gamersupps site id = 165328

    public GoAffProService(ILogger<GoAffProService>? logger, IConfig config)
    {
        _config = config;
        _logger = logger;

        //Task.Run(ConnectAsync);
    }
    
    public async Task StartAsync(CancellationToken ct = default)
    {
        // in dev
        
        // var username = "";
        // var password = "";
        // _client = await GoAffProClient.CreateLoggedInAsync(
        //     email: username,
        //     password: password);
        //
        // var sitesFields = new List<Anonymous>
        // {
        //     Anonymous.Id, Anonymous.Name, Anonymous.Ref_code, Anonymous.Referral_link, Anonymous.Website, Anonymous.Affiliate_portal, Anonymous.Currency, Anonymous.Coupon
        // };
        // var userSites = await _client.User.UserSitesAsync(10, 0, Status.Approved, sitesFields);
        //
        // var options = new JsonSerializerOptions
        // {
        //     WriteIndented = true
        // };
        //
        // Console.WriteLine(JsonSerializer.Serialize(userSites, options));
        // detector = new GoAffProEventDetector(_client, pollingInterval: TimeSpan.FromSeconds(30));
        // detector.OrderStartTime = DateTimeOffset.UtcNow;
        // detector.OrderDetected += (_, args) => Console.WriteLine($"Order: {args.Order.Id} {JsonSerializer.Serialize(args.Order.RawPayload, options)}");
        // _detectorCts = new CancellationTokenSource();
        // await detector.StartAsync(_detectorCts.Token);
        await Task.Delay(10);
        /////////////////////
        // var orders = await _client.User.UserFeedOrdersAsync(limit: 10, fields: (IEnumerable<Anonymous3>) Array.Empty<Anonymous3>());
        //var orders = await _client.GetOrdersAsync(limit: 50);
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        Dispose();
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
            _client.Dispose();
            if (disposing)
            {
                _reconnectState.Dispose();
            }
            _disposed = true;
        }
    }
}