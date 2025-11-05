using System.IO;
using System.Windows;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
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

    public static IServiceProvider? AppServices { get; private set; }
    
    private IDbContextFactory<AppDbContext>? _factory;
    
    public static string AppVersion =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "dev";
    
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var services = new ServiceCollection();
        
        string folder = Path.GetFullPath(Path.Combine(string.Empty, 
            "data"));
        Directory.CreateDirectory(folder);
        
        Config.LoadOrCreateDefault();

        services.AddDbContextFactory<AppDbContext>();
        services.AddSingleton<EventService>();
        
        AppServices = services.BuildServiceProvider();
        
        var factory = AppServices.GetRequiredService<IDbContextFactory<AppDbContext>>();
        _factory = factory;
        
        AppEventService = AppServices.GetRequiredService<EventService>();
        
        using var db =  _factory.CreateDbContext();
        db.Database.Migrate();
        AppDbContext.SeedDefaultValues(db);
        
        Task.Run(() =>
            {
                using var context1 = _factory.CreateDbContext();
                AppDbContext.PauseAllTimers(context1);
                using var context2 = _factory.CreateDbContext();
                AppDbContext.ResetPowerHour(context2);
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
                    Console.WriteLine("Twitch token expired, deleted file.");
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
        Task.Run(() => AppServices?.GetRequiredService<EventService>().StopAsync());
        AppTimerService.Stop();
        AppWebServer?.Stop();
        AppStreamElementsService?.Disconnect();
        if (AppStreamLabsService!.Connected) Task.Run(() => { AppStreamLabsService?.DisconnectAsync(); });
        
        if (AppTwitchService != null)
        {
            try
            {
                var cts = new CancellationTokenSource(5000);
                Task.Run(() => AppTwitchService.StopAsync(cts.Token));
                Console.WriteLine("TwitchService stopped cleanly.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error stopping TwitchService: {ex.Message}");
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
                Console.WriteLine($"Config reloaded! New server port: {newPort}");
                AppWebServer?.Stop();
                AppWebServer = new WebServer(_factory!, newPort);
                Task.Run(() => AppWebServer.StartAsync());
            }
            AppDiscordWebhookService?.LoadFromConfig();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error reloading config: {ex}");
        }
    }
}