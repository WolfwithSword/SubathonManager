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

public partial class App : Application
{    
    private WebServer? _server;
    private FileSystemWatcher? _configWatcher;
    private static TimerService AppTimerService { get; set; } = new();
    private static EventService AppEventService { get; set; } = new();
    public static TwitchService? AppTwitchService { get; private set; } = new();
    public static StreamElementsService? AppStreamElementsService { get; private set; } = new();
    
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
        Task.Run(() => AppDbContext.PauseAllTimers(new AppDbContext()));

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

    }

    public static async void InitSubathonTimer()
    {
        using var db = new AppDbContext();
        var subathon = await db.SubathonDatas.SingleOrDefaultAsync(x => x.IsActive);
        if (subathon != null) SubathonEvents.RaiseSubathonDataUpdate(subathon, DateTime.Now);
    }
    
    private async void UpdateSubathonTimers(TimeSpan time)
    {
        using var db = new AppDbContext();
        await db.Database.ExecuteSqlRawAsync("UPDATE SubathonDatas SET MillisecondsElapsed = MillisecondsElapsed + {0}" +
                                             " WHERE IsActive = 1 AND IsPaused = 0 " +
                                             "AND MillisecondsCumulative - MillisecondsElapsed > 0", 
            time.TotalMilliseconds);
        
        var subathon = await db.SubathonDatas.SingleOrDefaultAsync(x => x.IsActive && !x.IsPaused);

        if (subathon != null)
        {
            SubathonEvents.RaiseSubathonDataUpdate(subathon, DateTime.Now);
            if (subathon.TimeRemainingRounded().TotalSeconds <= 0 && !subathon.IsPaused)
            {
                
                await db.Database.ExecuteSqlRawAsync("UPDATE SubathonDatas SET IsLocked = 1" +
                                                     " WHERE IsActive = 1 AND IsPaused = 0 " +
                                                     "AND Id = {0}", 
                    subathon.Id);
            }

            await db.Entry(subathon).ReloadAsync();
            SubathonEvents.RaiseSubathonDataUpdate(subathon, DateTime.Now);
        }
        // if (subathon != null) Console.WriteLine($"Subathon Timer Updated: {subathon.MillisecondsCumulative} {subathon.MillisecondsElapsed} {subathon.PredictedEndTime()} {subathon.TimeRemaining()}");
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
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error reloading config: {ex}");
        }
    }
}