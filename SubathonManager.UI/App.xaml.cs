using System.IO;
using System.Windows;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using SubathonManager.Server;
using SubathonManager.Core;
using SubathonManager.Core.Events;
using SubathonManager.Data;
using SubathonManager.Twitch;
using SubathonManager.Services;

namespace SubathonManager.UI;

public partial class App : Application
{    
    private WebServer _server;
    private FileSystemWatcher _configWatcher;
    private static TimerService _timerService { get; set; } = new();
    private static EventService _eventService { get; set; } = new();
    public static TwitchService? _twitchService { get; private set; } = new();
    
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
            if (_twitchService!.HasTokenFile())
            {
                var tokenValid = await _twitchService.ValidateTokenAsync();
                if (!tokenValid)
                {
                    _twitchService.RevokeTokenFile();
                    Console.WriteLine("Twitch token expired, deleted file.");
                }
                else
                {
                    await _twitchService.InitializeAsync();
                }
            }
        });
        
        Task.Run(() => _server.StartAsync());
        Task.Run(() => _timerService.StartAsync());
        TimerEvents.TimerTickEvent += UpdateSubathonTimers;
        Task.Run(() => _eventService.LoopAsync());
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
        
        // TODO push to websocket, and UI. Queue events, sort by time, consume
        if (subathon != null) SubathonEvents.RaiseSubathonDataUpdate(subathon, DateTime.Now);
        // if (subathon != null) Console.WriteLine($"Subathon Timer Updated: {subathon.MillisecondsCumulative} {subathon.MillisecondsElapsed} {subathon.PredictedEndTime()} {subathon.TimeRemaining()}");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Task.Run(() => _eventService.StopAsync());
        _timerService.Stop();
        _server?.Stop();
        if (_twitchService != null)
        {
            try
            {
                var cts = new CancellationTokenSource(5000);
                Task.Run(() => _twitchService.StopAsync(cts.Token));
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
        // not working. Want to restart webserver on port change
        string configFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.ini");
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
        // not being called
        try
        {
            // Attempt reload config
            Config.LoadOrCreateDefault();
            int newPort = int.Parse(Config.Data["Server"]["Port"]);
            Console.WriteLine($"Config reloaded! New server port: {newPort}");

            _server?.Stop();
            _server = new WebServer(newPort);
            Task.Run(() => _server.StartAsync());
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error reloading config: {ex}");
        }
    }
}