using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Interfaces;
using SubathonManager.Integration;
using SubathonManager.Server;
using SubathonManager.Services;

namespace SubathonManager.UI.Services;

public class ServiceManager(ILogger<ServiceManager> logger) {
    private readonly ConcurrentDictionary<Type, SemaphoreSlim> _locks = new();
    private readonly ConcurrentDictionary<Type, byte> _running = new();

    public static string AppVersion {
        get {
            string ver = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion ?? "dev";

            int plusIndex = ver.IndexOf('+');
            return plusIndex > 0 && ver.StartsWith('v') ? ver[..plusIndex] : ver;
        }
    }

    private static IServiceProvider Provider {
        get => AppServices.Provider;
        set => AppServices.Provider = value;
    }

    // Core Services //
    // TelemetryService
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
    public static MakeShipService MakeShip => Provider.GetRequiredService<MakeShipService>();
    public static JuniperService Juniper => Provider.GetRequiredService<JuniperService>();
    public static TangiaService Tangia => Provider.GetRequiredService<TangiaService>();
    public static FourthWallService FourthWall => Provider.GetRequiredService<FourthWallService>();
    public static ThroneService Throne => Provider.GetRequiredService<ThroneService>();
    public static PallyService Pally => Provider.GetRequiredService<PallyService>();
    public static TiltifyService Tiltify => Provider.GetRequiredService<TiltifyService>();
    public static PatreonService Patreon => Provider.GetRequiredService<PatreonService>();
    public static DevTunnelsService DevTunnels => Provider.GetRequiredService<DevTunnelsService>();
    public static KoFiService KoFi => Provider.GetRequiredService<KoFiService>();
    public static TwitchService Twitch => Provider.GetRequiredService<TwitchService>();
    public static YouTubeService YouTube => Provider.GetRequiredService<YouTubeService>();
    public static PicartoService Picarto => Provider.GetRequiredService<PicartoService>();
    public static StreamElementsService StreamElements => Provider.GetRequiredService<StreamElementsService>();
    public static StreamLabsService StreamLabs => Provider.GetRequiredService<StreamLabsService>();
    public static TipeeeStreamService TipeeeStream => Provider.GetRequiredService<TipeeeStreamService>();
    public static TreatStreamService TreatStream => Provider.GetRequiredService<TreatStreamService>();
    public static OBSService OBS => Provider.GetRequiredService<OBSService>();
    public static VTSService VTubeStudio => Provider.GetRequiredService<VTSService>();
    public static MixItUpService MixItUp => Provider.GetRequiredService<MixItUpService>();
    public static StreamerBotService StreamerBot => Provider.GetRequiredService<StreamerBotService>();
    public static ActionService Actions => Provider.GetRequiredService<ActionService>();

    public static WebServer Server => Provider.GetRequiredService<WebServer>();

    public static EventService? EventsOrNull => Provider.GetService<EventService>();
    public static DiscordWebhookService? DiscordWebHooksOrNull => Provider.GetService<DiscordWebhookService>();

    public async Task StartIntegrationsAsync() {
        // Background Services & Integrations
        await TryStartAsync<OBSService>();
        if (FeatureFlags.VTubeStudioEnabled) await TryStartAsync<VTSService>();
        await TryStartAsync<MixItUpService>();
        await TryStartAsync<StreamerBotService>();
        await TryStartAsync<ActionService>();
        await TryStartAsync<DevTunnelsService>(); // shared tunnel infrastructure; must start before webhook integrations

        // Independent Services & Integrations
        // non blocking for any
        await Task.WhenAll(
            TryStartAsync<TwitchService>(),
            TryStartAsync<YouTubeService>(),
            TryStartAsync<PicartoService>(),
            TryStartAsync<StreamElementsService>(),
            TryStartAsync<StreamLabsService>(),
            TryStartAsync<TipeeeStreamService>(),
            TryStartAsync<TreatStreamService>(),
            TryStartAsync<GoAffProService>(),
            TryStartAsync<MakeShipService>(),
            TryStartAsync<JuniperService>(),
            TryStartAsync<TangiaService>(),
            TryStartAsync<KoFiService>(),
            TryStartAsync<FourthWallService>(),
            TryStartAsync<PatreonService>(),
            TryStartAsync<ThroneService>(),
            TryStartAsync<PallyService>(),
            TryStartAsync<TiltifyService>(),
            TryStartAsync<DiscordWebhookService>());
    }

    private async Task TryStartAsync<T>() where T : IAppService {
        try {
            await StartAsync<T>();
        }
        catch (Exception ex) {
            logger?.LogError(ex, "{Service} failed to start", typeof(T).Name);
        }
    }

    public async Task StopIntegrationsAsync() {
        await StopAsync<OBSService>();
        if (FeatureFlags.VTubeStudioEnabled) await StopAsync<VTSService>();
        await StopAsync<TwitchService>();
        await StopAsync<YouTubeService>();
        await StopAsync<PicartoService>();
        await StopAsync<StreamElementsService>();
        await StopAsync<StreamLabsService>();
        await StopAsync<TipeeeStreamService>();
        await StopAsync<TreatStreamService>();
        await StopAsync<GoAffProService>();
        await StopAsync<MakeShipService>();
        await StopAsync<JuniperService>();
        await StopAsync<TangiaService>();
        await StopAsync<KoFiService>();
        await StopAsync<ThroneService>();
        await StopAsync<ActionService>();
        await StopAsync<MixItUpService>();
        await StopAsync<StreamerBotService>();
        await StopAsync<PallyService>();
        await StopAsync<TiltifyService>();
        await StopAsync<FourthWallService>();
        await StopAsync<PatreonService>();
        await StopAsync<DevTunnelsService>();
        await StopAsync<DiscordWebhookService>();
    }

    public async Task StopCoreServicesAsync() {
        await StopAsync<TimerService>();
        await StopAsync<EventService>();
        await StopAsync<WebServer>();
        await StopAsync<TelemetryService>();
        await StopAsync<PromptOrchestratorService>();
        await StopAsync<WheelSpinTriggerService>();
        await StopAsync<SubathonTimerService>();
    }

    private SemaphoreSlim GetLock(Type t) {
        return _locks.GetOrAdd(t, _ => new SemaphoreSlim(1, 1));
    }

    public async Task StartAsync<T>(CancellationToken ct = default, bool fireAndForget = false) where T : IAppService {
        SemaphoreSlim lk = GetLock(typeof(T));
        await lk.WaitAsync(ct);
        try {
            if (_running.ContainsKey(typeof(T))) return;

            var service = Provider.GetRequiredService<T>();

            if (fireAndForget)
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                Task.Run(async () => await service.StartAsync(ct), ct); // run sync
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
            else
                await service.StartAsync(ct);
            _running.TryAdd(typeof(T), 0);
            
            if (logger?.IsEnabled(LogLevel.Debug) ?? false)
                logger?.LogDebug("{Service} started", typeof(T).Name);
        }
        finally {
            lk.Release();
        }
    }

    public async Task StopAsync<T>(CancellationToken ct = default) where T : IAppService {
        SemaphoreSlim lk = GetLock(typeof(T));
        await lk.WaitAsync(ct);
        try {
            if (!_running.ContainsKey(typeof(T))) return;

            var service = Provider.GetService<T>();
            if (service != null)
                await service.StopAsync(ct);
            _running.TryRemove(typeof(T), out _);
            if (logger?.IsEnabled(LogLevel.Debug) ?? false)
                logger?.LogDebug("{Service} stopped", typeof(T).Name);
        }
        finally {
            lk.Release();
        }
    }

    public bool IsRunning<T>() {
        return _running.ContainsKey(typeof(T));
    }
}