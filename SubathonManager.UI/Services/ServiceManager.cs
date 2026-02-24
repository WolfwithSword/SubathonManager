using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubathonManager.Core.Interfaces;
using SubathonManager.Integration;
using SubathonManager.Server;
using SubathonManager.Services;

namespace SubathonManager.UI.Services;

public class ServiceManager(ILogger<ServiceManager> logger)
{
    private readonly HashSet<Type> _running = new();
    private readonly ConcurrentDictionary<Type, SemaphoreSlim> _locks = new();
    
    public static string AppVersion
    {
        get
        {
            var ver = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion ?? "dev";

            var plusIndex = ver.IndexOf('+');
            return plusIndex > 0 && ver.StartsWith('v') ? ver[..plusIndex] : ver;
        }
    }
    private static IServiceProvider Provider
    {
        get => Core.AppServices.Provider;
        set => Core.AppServices.Provider = value;
    }

    public async Task StartIntegrationsAsync()
    {
        await StartAsync<TwitchService>();
        await StartAsync<YouTubeService>();
        await StartAsync<PicartoService>();
        await StartAsync<StreamElementsService>();
        await StartAsync<StreamLabsService>();
        await StartAsync<GoAffProService>();
        await StartAsync<DiscordWebhookService>();
    }

    public async Task StopIntegrationsAsync()
    {
        await StopAsync<TwitchService>();
        await StopAsync<YouTubeService>();
        await StopAsync<PicartoService>();
        await StopAsync<StreamElementsService>();
        await StopAsync<StreamLabsService>();
        await StopAsync<GoAffProService>();
        await StopAsync<DiscordWebhookService>();
    }

    public async Task StopCoreServicesAsync()
    {
        await StopAsync<TimerService>();
        await StopAsync<EventService>();
        await StopAsync<WebServer>();
    }
    
    private SemaphoreSlim GetLock(Type t) => 
        _locks.GetOrAdd(t, _ => new SemaphoreSlim(1, 1));
    
    public async Task StartAsync<T>(CancellationToken ct = default, bool fireAndForget = false) where T : IAppService
    {
        var lk = GetLock(typeof(T));
        await lk.WaitAsync(ct);
        try
        {
            if (_running.Contains(typeof(T))) return;
            
            var service = Provider.GetRequiredService<T>();

            if (fireAndForget)
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                Task.Run(async() => await service.StartAsync(ct), ct); // run sync
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
            else 
                await service.StartAsync(ct);
            _running.Add(typeof(T));
            logger?.LogDebug("{Service} started", typeof(T).Name);
        }
        finally { lk.Release(); }
    }
    
    public async Task StopAsync<T>(CancellationToken ct = default) where T : IAppService
    {
        var lk = GetLock(typeof(T));
        await lk.WaitAsync(ct);
        try
        {
            if (!_running.Contains(typeof(T))) return;
            
            var service = Provider.GetService<T>();
            if (service != null)
                await service.StopAsync(ct);
            _running.Remove(typeof(T));
            logger?.LogDebug("{Service} stopped", typeof(T).Name);
        }
        finally { lk.Release(); }
    }
    
    public bool IsRunning<T>() => _running.Contains(typeof(T));
    
    // Core Services //
    // CurrencyService
    // EventService
    // WebServer
    // TimerService
    
    // Integration Services //
    // Twitch, YouTube, Picarto, StreamElements, StreamLabs, GoAffPro
    
    // Other Services //
    // DiscordWebhookService
    
    // Other Services (Static) //
    // ExternalEventService
    // BlerpChatService
    // CommandService (Dependency for many)
    
    public static EventService Events => Provider.GetRequiredService<EventService>(); 
    public static DiscordWebhookService DiscordWebhooks => Provider.GetRequiredService<DiscordWebhookService>();
    public static GoAffProService GoAffPro => Provider.GetRequiredService<GoAffProService>();
    public static TwitchService Twitch => Provider.GetRequiredService<TwitchService>(); 
    public static YouTubeService YouTube => Provider.GetRequiredService<YouTubeService>();
    public static PicartoService Picarto => Provider.GetRequiredService<PicartoService>();
    public static StreamElementsService StreamElements => Provider.GetRequiredService<StreamElementsService>();
    public static StreamLabsService StreamLabs => Provider.GetRequiredService<StreamLabsService>();
    
    public static WebServer Server => Provider.GetRequiredService<WebServer>();
    public static TimerService Timer => Provider.GetRequiredService<TimerService>();
    
    public static EventService? EventsOrNull => Provider.GetService<EventService>();
    public static DiscordWebhookService? DiscordWebHooksOrNull => Provider.GetService<DiscordWebhookService>();
    public static StreamElementsService? StreamElementsOrNull => Provider.GetService<StreamElementsService>();
    public static StreamLabsService? StreamLabsOrNull => Provider.GetService<StreamLabsService>();
    
}