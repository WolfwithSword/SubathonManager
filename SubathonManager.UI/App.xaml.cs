using System.IO;
using System.Windows;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui.Appearance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SubathonManager.Server;
using SubathonManager.Core;
using SubathonManager.Core.Events;
using SubathonManager.Data;
using SubathonManager.Integration;
using SubathonManager.Services;
using Wpf.Ui.Markup;

namespace SubathonManager.UI;

public partial class App
{    
    public static WebServer? AppWebServer { get; private set; }
    private FileSystemWatcher? _configWatcher;
    private static TimerService AppTimerService { get; } = new();
    public static EventService? AppEventService { get; private set; }
    
    public static TwitchService? AppTwitchService { get; set; }
    public static YouTubeService? AppYouTubeService { get; set; }
    public static StreamElementsService? AppStreamElementsService { get; set; }
    public static StreamLabsService? AppStreamLabsService { get; set; }
    
    public static IConfig? AppConfig;
    private static DiscordWebhookService? AppDiscordWebhookService { get; set; }
    private ResourceDictionary? _themeDictionary;
    private ILogger? _logger;
    
    private IDbContextFactory<AppDbContext>? _factory;
    
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
    
    protected override void OnStartup(StartupEventArgs e)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        
        AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
        {
            File.WriteAllText("error_load.log", $"{ex.ExceptionObject}");
        };
        
        var services = new ServiceCollection();
        
        string folder = Path.GetFullPath(Path.Combine(string.Empty, 
            "data"));
        Directory.CreateDirectory(folder);
        
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddProvider(new RotatingFileLoggerProvider("data/logs"));
            builder.AddSimpleConsole(options =>
            {
                options.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff ";
                options.SingleLine = true;
                options.IncludeScopes = false;
            });
            builder.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Error);
            builder.AddFilter("StreamLabs.SocketClient", LogLevel.Warning);
            builder.AddFilter("Microsoft.Extensions.Http", LogLevel.Warning);
             builder.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
            builder.SetMinimumLevel(AppVersion.Contains("dev") ? LogLevel.Debug : LogLevel.Information); 
        });

        try
        {
            services.AddSingleton<IConfig, Config>();
            services.AddDbContextFactory<AppDbContext>(options =>
            {
                var dbPath = Config.DatabasePath;
                Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
                options.UseSqlite($"Data Source={dbPath}");
            });
            
            services.AddHttpClient<CurrencyService>()
                .SetHandlerLifetime(Timeout.InfiniteTimeSpan); 
            services.AddSingleton<CurrencyService>(sp =>
            {
                var httpClient = sp.GetRequiredService<System.Net.Http.HttpClient>();
                var logger = sp.GetRequiredService<ILogger<CurrencyService>>();
                var config = sp.GetRequiredService<IConfig>();
                return new CurrencyService(logger, config, httpClient);
            });
            services.AddSingleton<TwitchService>();
            services.AddSingleton<YouTubeService>();
            services.AddSingleton<StreamElementsService>();
            services.AddSingleton<StreamLabsService>();
            services.AddSingleton<EventService>();

            AppServices.Provider = services.BuildServiceProvider();

            AppConfig = AppServices.Provider.GetRequiredService<IConfig>();
            AppConfig.LoadOrCreateDefault();
            AppConfig.MigrateConfig();

            string theme = (AppConfig.Get("App", "Theme", "Dark"))!.Trim();
            _themeDictionary = new ResourceDictionary
            {
                Source = new Uri($"Themes/{theme.ToUpper()}.xaml", UriKind.Relative)
            };

            var oldCustom = Resources.MergedDictionaries
                .FirstOrDefault(d =>
                    d.Source != null && d.Source.OriginalString.StartsWith("Themes/") &&
                    !d.Source.OriginalString.Contains("Fluent"));
            if (oldCustom != null)
                Resources.MergedDictionaries.Remove(oldCustom);
            Resources.MergedDictionaries.Add(_themeDictionary);
            if (Resources.MergedDictionaries.FirstOrDefault(d => d is ThemesDictionary) is ThemesDictionary fluentDict)
                fluentDict.Theme = theme.Equals("Dark", StringComparison.OrdinalIgnoreCase)
                    ? ApplicationTheme.Dark
                    : ApplicationTheme.Light;

            _logger = AppServices.Provider.GetRequiredService<ILogger<App>>();
            _logger.LogInformation("======== Subathon Manager started ========");
            _logger.LogInformation($"== Data folder: {Config.DataFolder} ==");

            var factory = AppServices.Provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
            _factory = factory;      
            using (var db = _factory.CreateDbContext()) {
                db.Database.Migrate();
                AppDbContext.SeedDefaultValues(db);
            }

            base.OnStartup(e);

            AppEventService = AppServices.Provider.GetRequiredService<EventService>();
            AppTwitchService = AppServices.Provider.GetRequiredService<TwitchService>();
            AppYouTubeService = AppServices.Provider.GetRequiredService<YouTubeService>();
            AppStreamElementsService = AppServices.Provider.GetRequiredService<StreamElementsService>();
            AppStreamLabsService = AppServices.Provider.GetRequiredService<StreamLabsService>();
            

            Task.Run(async () =>
                {
                    await using var context1 = await _factory.CreateDbContextAsync();
                    await AppDbContext.PauseAllTimers(context1);
                    await using var context2 = await _factory.CreateDbContextAsync();
                    await AppDbContext.ResetPowerHour(context2);
                    await using var context3 = await _factory.CreateDbContextAsync();
                    await SetupSubathonCurrencyData(context3);
                }
            );

            AppWebServer = new WebServer(_factory, int.Parse(AppConfig.Get("Server", "Port", "14040")!));

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

            AppDiscordWebhookService = new DiscordWebhookService(null, AppConfig);
        }
        catch (Exception ex)
        {
            File.WriteAllText("error_startup.log", $"{ex}\r\n{ex.StackTrace}");
            _logger?.LogError(ex, "Error occurred when starting Subathon Manager");
            Current.Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _logger?.LogInformation("======== Subathon Manager exiting ========");
        Task.Run(() => AppServices.Provider.GetRequiredService<EventService>().StopAsync());
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
    
    private void WatchConfig()
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
            int newPort = int.Parse(AppConfig!.Get("Server", "Port", "14040")!);
            if (AppWebServer?.Port != newPort)
            {
                _logger?.LogDebug($"Config reloaded! New server port: {newPort}");
                AppWebServer?.Stop();
                AppWebServer = new WebServer(_factory!, newPort);
                Task.Run(async() => await AppWebServer.StartAsync());
            }
            AppDiscordWebhookService?.LoadFromConfig();
            SetThemeFromConfig();
            Task.Run(async () =>
            {
                await using var db = await _factory!.CreateDbContextAsync();
                await SetupSubathonCurrencyData(db);
            });
        }
        catch (Exception ex)
        {
            _logger?.LogWarning($"Error reloading config: {ex}");
        }
    }

    private void SetThemeFromConfig()
    {
        Current.Dispatcher.InvokeAsync(() =>
        {
            string theme = AppConfig!.Get("App", "Theme", "Dark")!;
            theme = theme.Trim();

            if (_themeDictionary == null)
            {
                _themeDictionary = new ResourceDictionary
                {
                    Source = new Uri($"Themes/{theme.ToUpper()}.xaml", UriKind.Relative)
                };
                Application.Current.Resources.MergedDictionaries.Add(_themeDictionary);
            }
            else
            {
                _themeDictionary.Source = new Uri($"Themes/{theme.ToUpper()}.xaml", UriKind.Relative);
            }

            if (Application.Current.Resources.MergedDictionaries.FirstOrDefault(d => d is ThemesDictionary) is ThemesDictionary fluentDict)
            {
                fluentDict.Theme = theme.Equals("Dark", StringComparison.OrdinalIgnoreCase)
                    ? ApplicationTheme.Dark
                    : ApplicationTheme.Light;
            }
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private async Task SetupSubathonCurrencyData(AppDbContext db)
    {
        // Setup first time or convert currency
        string currency = AppConfig!.Get("Currency", "Primary", "USD")!;
        var subathon = await db.SubathonDatas.AsNoTracking().FirstOrDefaultAsync(s => s.IsActive);
        if (subathon == null || currency.Equals(subathon.Currency, StringComparison.OrdinalIgnoreCase)) return;

        string? oldCurrency = subathon.Currency;
        await AppDbContext.UpdateSubathonCurrency(db, currency);
        
        var currencyService = AppServices.Provider.GetRequiredService<CurrencyService>();
        if (subathon.MoneySum != null && !subathon.MoneySum.Equals((double)0) && !string.IsNullOrWhiteSpace(oldCurrency))
        {
            var amt = await currencyService.ConvertAsync((double)subathon.MoneySum, oldCurrency, currency);
            await AppDbContext.UpdateSubathonMoney(db, amt, subathon.Id);
            return;
        }
        
        var events = await AppDbContext.GetSubathonCurrencyEvents(db);
        
        double sum = 0;
        foreach (var e in events)
        {
            if (string.IsNullOrWhiteSpace(e.Currency)) continue;
            var amt = await currencyService.ConvertAsync(double.Parse(e.Value), e.Currency, currency.ToUpper());
            sum += amt;
        }
        await AppDbContext.UpdateSubathonMoney(db, sum, subathon.Id);
    }

}