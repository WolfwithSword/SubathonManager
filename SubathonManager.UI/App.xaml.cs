using System.IO;
using System.Windows;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SubathonManager.Server;
using SubathonManager.Core;
using SubathonManager.Core.Events;
using SubathonManager.Data;
using SubathonManager.Integration;
using SubathonManager.Services;

namespace SubathonManager.UI;

public partial class App
{    
    public static WebServer? AppWebServer { get; private set; }
    private FileSystemWatcher? _configWatcher;
    private static TimerService AppTimerService { get; } = new();
    public static EventService? AppEventService { get; private set; }
    
    public static TwitchService? AppTwitchService { get; } = new();
    public static YouTubeService? AppYouTubeService { get; } = new();
    public static StreamElementsService? AppStreamElementsService { get; } = new();
    public static StreamLabsService? AppStreamLabsService { get;} = new();
    private static DiscordWebhookService? AppDiscordWebhookService { get; set; }
    
    private ILogger? _logger;
    
    private IDbContextFactory<AppDbContext>? _factory;
    
    public static string AppVersion =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "dev";
    
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddProvider(new RotatingFileLoggerProvider("data/logs", 30));
            builder.AddSimpleConsole(options =>
            {
                options.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff ";
                options.SingleLine = true;
                options.IncludeScopes = false;
            });
            builder.AddFilter("StreamLabs.SocketClient", LogLevel.Warning);;
            builder.SetMinimumLevel(AppVersion.Contains("dev") ? LogLevel.Debug : LogLevel.Information); 
        });
        
        string folder = Path.GetFullPath(Path.Combine(string.Empty, 
            "data"));
        Directory.CreateDirectory(folder);
        
        Config.LoadOrCreateDefault();

        services.AddDbContextFactory<AppDbContext>();
        services.AddSingleton<EventService>();
        
        AppServices.Provider = services.BuildServiceProvider();
        _logger = AppServices.Provider.GetRequiredService<ILogger<App>>();
        _logger.LogInformation("======== Subathon Manager started ========");
        _logger.LogInformation($"== Data folder: {Config.DataFolder} ==");
        
        var factory = AppServices.Provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        _factory = factory;
        
        AppEventService = AppServices.Provider.GetRequiredService<EventService>();
        
        using var db =  _factory.CreateDbContext();
        db.Database.Migrate();
        AppDbContext.SeedDefaultValues(db);
        
        Task.Run(async () =>
            {
                await using var context1 = await _factory.CreateDbContextAsync();
                await AppDbContext.PauseAllTimers(context1);
                await using var context2 = await _factory.CreateDbContextAsync();
                await AppDbContext.ResetPowerHour(context2);
            }
        );

        AppWebServer = new WebServer(_factory, int.Parse(Config.Data["Server"]["Port"]));

        Task.Run(async () =>
        {
            if (AppTwitchService!.HasTokenFile())
            {
                var tokenValid = await AppTwitchService.ValidateTokenAsync();
                if (!tokenValid)
                {
                    AppTwitchService.RevokeTokenFile();
                    _logger.LogWarning("Twitch token expired - deleting token file");
                }
                else
                {
                    await AppTwitchService.InitializeAsync();
                }
            }
        });

        AppYouTubeService!.Start(null);
        
        Task.Run(() => AppWebServer.StartAsync());
        Task.Run(() => AppTimerService.StartAsync());
        TimerEvents.TimerTickEvent += UpdateSubathonTimers;
        Task.Run(() =>
        {
            Task.Delay(200);
            AppStreamElementsService!.InitClient();
            return Task.CompletedTask;
        });
        Task.Run(() =>
        {
            Task.Delay(200);
            return AppStreamLabsService!.InitClientAsync();
        });
        
        WatchConfig();

        AppDiscordWebhookService = new DiscordWebhookService();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _logger?.LogInformation("======== Subathon Manager exiting ========");
        Task.Run(() => AppServices.Provider?.GetRequiredService<EventService>().StopAsync());
        AppTimerService.Stop();
        AppWebServer?.Stop();
        AppStreamElementsService?.Disconnect();
        if (AppStreamLabsService!.Connected) Task.Run(() => { AppStreamLabsService?.DisconnectAsync(); });
        
        if (AppTwitchService != null)
        {
            try
            {
                var cts = new CancellationTokenSource(5000);
                Task.Run(async() => await AppTwitchService.StopAsync(cts.Token));
                _logger?.LogDebug("TwitchService stopped");
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"Error stopping TwitchService: {ex.Message}");
            }
        }
        if (AppDiscordWebhookService != null)
        {
            Task.Run(() =>
            {
                AppDiscordWebhookService?.StopAsync();
                AppDiscordWebhookService?.Dispose();
            });
        }
        
        AppYouTubeService?.Dispose();
        
        base.OnExit(e);
        _logger?.LogInformation("======== Subathon Manager exit ========");
    }
    
    public void WatchConfig()
    {
        string configFile = Path.GetFullPath(Path.Combine(string.Empty
            , "data/config.ini"));
        _configWatcher = new FileSystemWatcher(Path.GetDirectoryName(configFile)!)
        {
            Filter = Path.GetFileName(configFile),
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size
        };

        _configWatcher.Changed += ConfigChanged;
        _configWatcher.EnableRaisingEvents = true;
    }
    
    private void ConfigChanged(object sender, FileSystemEventArgs e)
    {
        try
        {
            int newPort = int.Parse(Config.Data["Server"]["Port"]);
            if (AppWebServer?.Port != newPort)
            {
                _logger?.LogDebug($"Config reloaded! New server port: {newPort}");
                AppWebServer?.Stop();
                AppWebServer = new WebServer(_factory!, newPort);
                Task.Run(async() => await AppWebServer.StartAsync());
            }
            AppDiscordWebhookService?.LoadFromConfig();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning($"Error reloading config: {ex}");
        }
    }
}