using System.IO;
using System.Windows;
using System.Reflection;
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
    private WebServer? _server;
    private FileSystemWatcher? _configWatcher;
    private static TimerService AppTimerService { get; set; } = new();
    private static EventService AppEventService { get; set; } = new();
    
    public static TwitchService? AppTwitchService { get; private set; } = new();
    public static StreamElementsService? AppStreamElementsService { get; private set; } = new();
    private static DiscordWebhookService? AppDiscordWebhookService { get; set; }
    
    public static string AppVersion =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "dev";
    
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string folder = Path.GetFullPath(Path.Combine(string.Empty, 
            "data"));
        Directory.CreateDirectory(folder);
        
        Config.LoadOrCreateDefault();

        using var db = new AppDbContext();
        db.Database.Migrate();
        AppDbContext.SeedDefaultValues(db);
        Task.Run(() =>
            {
                AppDbContext.PauseAllTimers(new AppDbContext());
                AppDbContext.ResetPowerHour(new AppDbContext());
            }
        );

        _server = new WebServer(int.Parse(Config.Data["Server"]["Port"]));

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
        
        Task.Run(() => _server.StartAsync());
        Task.Run(() => AppTimerService.StartAsync());
        TimerEvents.TimerTickEvent += UpdateSubathonTimers;
        Task.Run(() =>
        {
            Task.Delay(200);
            AppStreamElementsService!.InitClient();
            return Task.CompletedTask;
        });
        WatchConfig();

        AppDiscordWebhookService = new DiscordWebhookService();
        
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Task.Run(() => AppEventService.StopAsync());
        AppTimerService.Stop();
        _server?.Stop();
        AppStreamElementsService?.Disconnect();
        
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
            if (_server?.Port != newPort)
            {
                Console.WriteLine($"Config reloaded! New server port: {newPort}");
                _server?.Stop();
                _server = new WebServer(newPort);
                Task.Run(() => _server.StartAsync());
            }
            AppDiscordWebhookService?.LoadFromConfig();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error reloading config: {ex}");
        }
    }
}