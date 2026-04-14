using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui.Appearance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using SubathonManager.Server;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Data;
using SubathonManager.Services;
using SubathonManager.UI.Services;
using Wpf.Ui.Markup;
// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.UI;

public partial class App
{    
    private FileSystemWatcher? _configWatcher;
    private static Mutex? _mutex;
    
    private ResourceDictionary? _themeDictionary;
    private ILogger? _logger;
    
    private IDbContextFactory<AppDbContext>? _factory;
    private string _currencyVal = string.Empty;
    
    protected override void OnStartup(StartupEventArgs e)
    {
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        
        AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
        {
            File.WriteAllText("error_load.log", $"{ex.ExceptionObject}");
        };
        
        const string mutexName = @"Global\SubathonManager_SingleInstanceMutex";
        _mutex = new Mutex(true, mutexName, out var createdNew);
        
        if (!createdNew)
        {
            ParseImportFile(e);
            string? filePath = Utils.PendingOverlayImportPath;
            FocusRunningInstance(filePath);
            Shutdown();
            return;
        }

        SetFileAssociations();
        
        var services = new ServiceCollection();
        
        string folder = Path.GetFullPath(Path.Combine(string.Empty, 
            "data"));
        Directory.CreateDirectory(folder);

        try
        {
            services.SetupInfrastructure();
            services.SetupCoreServices();
            services.AddIntegrations();

            AppServices.Provider = services.BuildServiceProvider();

            var config = AppServices.Provider.GetRequiredService<IConfig>();
            config.LoadOrCreateDefault();
            config.MigrateConfig();

            bool bitsAsDonationCheck = config.GetBool("Currency", "BitsLikeAsDonation", false);
            Utils.DonationSettings["BitsLikeAsDonation"] = bitsAsDonationCheck;
            foreach (var goAffProSource in Enum.GetValues<GoAffProSource>().Where(ga => ga != GoAffProSource.Unknown && !ga.IsDisabled()))
            {
                Utils.DonationSettings[$"{goAffProSource}"] =
                    config.GetBool("GoAffPro", $"{goAffProSource}.CommissionAsDonation", false);
            }
            
            _currencyVal = config.Get("Currency", "Primary", "USD")!;

            SetStartupTheme(config);

            _logger = AppServices.Provider.GetRequiredService<ILogger<App>>();
            _logger.LogInformation("======== Subathon Manager started ========");
            _logger.LogInformation("== Data folder: {DataFolder} ==", Config.DataFolder);

            var factory = AppServices.Provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
            _factory = factory;      
            using (var db = _factory.CreateDbContext()) {
                db.Database.Migrate();
                AppDbContext.SeedDefaultValues(db);
            }

            base.OnStartup(e);
            
            var window = new MainWindow();
            Current.MainWindow = window;
            window.Show();
            
            ParseImportFile(e);

            SubathonEvents.SubathonDataUpdate += UpdateTickStateCache;
            
            var sm = AppServices.Provider.GetRequiredService<ServiceManager>();
            
            Task.Run(async () =>
            {
                await sm.StartAsync<EventService>();
                
                await using var context1 = await _factory.CreateDbContextAsync();
                await AppDbContext.PauseAllTimers(context1);
                await using var context2 = await _factory.CreateDbContextAsync();
                await AppDbContext.ResetPowerHour(context2);
                await using var context3 = await _factory.CreateDbContextAsync();
                await SetupSubathonCurrencyData(context3, false);
                
                // fire-forget
                await sm.StartAsync<WebServer>(fireAndForget: true);
                await sm.StartAsync<TimerService>(fireAndForget: true);
                await Task.Delay(100);
                TimerEvents.TimerTickEvent += UpdateSubathonTimers;
                
                await sm.StartIntegrationsAsync();

                if (config.GetBool("Telemetry", "Enabled", false))
                {
                    await sm.StartAsync<TelemetryService>();
                }
            });
            
            WatchConfig();
        }
        catch (Exception ex)
        {
            File.WriteAllText("error_startup.log", $"{ex}\r\n{ex.StackTrace}");
            _logger?.LogError(ex, "Error occurred when starting Subathon Manager");
            Current.Shutdown();
        }
    }

    private static void ParseImportFile(StartupEventArgs e)
    {
        if (e.Args.Length > 0 && File.Exists(e.Args[0]) && 
            e.Args[0].EndsWith(".smo", StringComparison.OrdinalIgnoreCase))
        {
            Utils.PendingOverlayImportPath = e.Args[0];
        }
        else if (e.Args.Length > 0)
        {
            var arg = e.Args[0];

            if (arg.StartsWith("subathonmanager://"))
            {
                var uri = new Uri(arg);

                var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                var url = query["url"];

                if (!string.IsNullOrEmpty(url))
                {
                    Utils.PendingOverlayImportPath = url;
                }
            }
        }
    }

    private void SetStartupTheme(IConfig config)
    {
        string theme = config.Get("App", "Theme", "Dark")!.Trim();
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
    }
    
    private void FocusRunningInstance(string? filePath = null)
    {
        var current = Process.GetCurrentProcess();
        foreach (var proc in Process.GetProcessesByName(current.ProcessName))
        {
            if (proc.Id == current.Id)
                continue;
            
            if (proc.MainWindowHandle != IntPtr.Zero)
            {
                if (!string.IsNullOrEmpty(filePath))
                {
                    Utils.SingleInstanceHelper.SendStringMessage(proc.MainWindowHandle, filePath);
                }

                Utils.SingleInstanceHelper.PostMessage(
                    proc.MainWindowHandle,
                    Utils.SingleInstanceHelper.WM_SHOWAPP,
                    IntPtr.Zero,
                    IntPtr.Zero);
            }
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        _logger?.LogInformation("======== Subathon Manager exiting ========");
        var sm =  AppServices.Provider.GetRequiredService<ServiceManager>();
        await sm.StopCoreServicesAsync();
        await sm.StopIntegrationsAsync();
        
        _configWatcher?.Dispose();
        (AppServices.Provider as IDisposable)?.Dispose();
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
            var config = AppServices.Provider.GetRequiredService<IConfig>();
            var sm = AppServices.Provider.GetRequiredService<ServiceManager>();
            int newPort = int.Parse(config.Get("Server", "Port", "14040")!);
            int currentPort = ServiceManager.Server?.Port ?? newPort;

            if (config.GetBool("Telemetry", "Enabled", false) && !sm.IsRunning<TelemetryService>())
            {
                Task.Run(async () =>
                {
                    await sm.StartAsync<TelemetryService>();
                });
            }
            else if (!config.GetBool("Telemetry", "Enabled", false) && sm.IsRunning<TelemetryService>())
            {
                Task.Run(async () =>
                {
                    await sm.StopAsync<TelemetryService>();
                });
            }
            
            if (currentPort != newPort)
            {
                _logger?.LogDebug("Config reloaded! New server port: {NewPort}", newPort);
                if (ServiceManager.Server != null) ServiceManager.Server.Port = newPort;
                Task.Run(async () =>
                {
                    try
                    {
                        await sm.StopAsync<WebServer>();
                        await Task.Delay(100);
                        await sm.StartAsync<WebServer>(fireAndForget: true);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Error occurred when starting Subathon Manager");
                    }
                });
                
            }
            ServiceManager.DiscordWebHooksOrNull?.LoadFromConfig();
            SetThemeFromConfig();

            bool bitsAsDonationCheck = config.GetBool("Currency", "BitsLikeAsDonation", false);
            string currency = config.Get("Currency", "Primary", "USD")!;

            bool optionToggled = false;
            bool currencyChanged = _currencyVal != currency;
            
            if (currencyChanged)
                _currencyVal = currency;
    
            
            if (Utils.DonationSettings.TryGetValue("BitsLikeAsDonation", out bool asDonoBits) && asDonoBits != bitsAsDonationCheck)
            {
                optionToggled = true;
                Utils.DonationSettings["BitsLikeAsDonation"] = bitsAsDonationCheck;
            }
            
            foreach (var goAffProSource in Enum.GetValues<GoAffProSource>().Where(ga => ga != GoAffProSource.Unknown && !ga.IsDisabled()))
            {
                bool asDonation = config.GetBool("GoAffPro", $"{goAffProSource}.CommissionAsDonation", false);
                if (Utils.DonationSettings.TryGetValue($"{goAffProSource}", out bool hasVal) && hasVal == asDonation) continue;
                optionToggled = true;
                Utils.DonationSettings[$"{goAffProSource}"] = asDonation;
            }

            if (currencyChanged || optionToggled)
            {
                Task.Run(async () =>
                {
                    await using var db = await _factory!.CreateDbContextAsync();
                    await SetupSubathonCurrencyData(db, optionToggled);
                });
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("Error reloading config: {Exception}", ex);
        }
    }

    private void SetThemeFromConfig()
    {
        Current.Dispatcher.InvokeAsync(() =>
        {
            var config = AppServices.Provider.GetRequiredService<IConfig>();
            string theme = config.Get("App", "Theme", "Dark")!;
            theme = theme.Trim();

            if (_themeDictionary == null)
            {
                _themeDictionary = new ResourceDictionary
                {
                    Source = new Uri($"Themes/{theme.ToUpper()}.xaml", UriKind.Relative)
                };
                Current.Resources.MergedDictionaries.Add(_themeDictionary);
            }
            else
            {
                _themeDictionary.Source = new Uri($"Themes/{theme.ToUpper()}.xaml", UriKind.Relative);
            }

            if (Current.Resources.MergedDictionaries.FirstOrDefault(d => d is ThemesDictionary) is ThemesDictionary fluentDict)
            {
                fluentDict.Theme = theme.Equals("Dark", StringComparison.OrdinalIgnoreCase)
                    ? ApplicationTheme.Dark
                    : ApplicationTheme.Light;
            }
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private async Task SetupSubathonCurrencyData(AppDbContext db, bool? optionToggled)
    {
        // Setup first time or convert currency
        var config = AppServices.Provider.GetRequiredService<IConfig>();
        string currency = config.Get("Currency", "Primary", "USD")!;
        
        var subathon = await db.SubathonDatas.AsNoTracking().FirstOrDefaultAsync(s => s.IsActive);
        _currencyVal = currency; 
        if (subathon == null) return;

        string? oldCurrency = subathon.Currency;
        if (oldCurrency != currency)
        {
            await AppDbContext.UpdateSubathonCurrency(db, currency);
            await db.Entry(subathon).ReloadAsync();
            db.Entry(subathon).State = EntityState.Detached;
        }

        // validate this logic, it always runs? 
        var currencyService = AppServices.Provider.GetRequiredService<CurrencyService>();
        if (subathon.MoneySum != null && 
            !subathon.MoneySum.Equals((double)0) && !string.IsNullOrWhiteSpace(oldCurrency))
        {
            var amt = await currencyService.ConvertAsync((double)subathon.MoneySum, oldCurrency, currency);
            await db.UpdateSubathonMoney(amt, subathon.Id);
            if (optionToggled != null && !(bool)optionToggled) {
                var subathonTotals = await EventService.GetSubathonTotalsAsync(db);
                if (subathonTotals != null)
                    SubathonEvents.RaiseSubathonTotalsUpdated(subathonTotals);
                return;
            }
        }

        // reconvert everything, rarely called, unless toggling bits as donations
        // alternate was to add bits always when sending out, but that got messy code-wise
        // this way has downside of recalculating conversions historically, but, only if toggling often.
        // and this is estimated to be low usage to change currency or bit dono toggle.
        
        var events = await AppDbContext.GetSubathonCurrencyEvents(db);

        // warning: there can be drift upward if constantly swapping between multiple currencies.
        // A cent usually, but when >1k$ could be a dollar or so. Sometimes when toggling some things, it can self-fix.
        
        double sum = 0;
        double bits = 0;
        foreach (var e in events)
        {
            (bool isBitsLike, double modifier) = Utils.GetAltCurrencyUseAsDonation(config, e.EventType);
            if (e.EventType.IsToken() && isBitsLike)
            {
                bits += (int.Parse(e.Value) * modifier);
                continue;
            }
            if (string.IsNullOrWhiteSpace(e.Currency)) continue;
            var value = e.Value;
            var curr = e.Currency;
            if (e.EventType.IsOrder())
            {
                value = e.SecondaryValue.Split('|')[0];
                curr = e.SecondaryValue.Split('|')[1];
            }
            var amt = await currencyService.ConvertAsync(double.Parse(value), curr, currency.ToUpper());
            sum += amt;
        }

        if (Utils.DonationSettings.TryGetValue("BitsLikeAsDonation", out var bitslike) && bitslike)
        {
            double val = await currencyService.ConvertAsync((bits) / 100, "USD", subathon.Currency);
            sum += val;
        }
        await db.UpdateSubathonMoney(sum, subathon.Id);
        var totals = await EventService.GetSubathonTotalsAsync(db);
        if (totals != null)
            SubathonEvents.RaiseSubathonTotalsUpdated(totals);
    }

    private void SetFileAssociations()
    {
        var exePath = Environment.ProcessPath!;

        EnsureRegistryValue(@"HKEY_CURRENT_USER\Software\Classes\.smo", "", "SubathonManager.Overlay");

        EnsureRegistryValue(@"HKEY_CURRENT_USER\Software\Classes\SubathonManager.Overlay", "", "Subathon Manager Overlay");

        EnsureRegistryValue(
            @"HKEY_CURRENT_USER\Software\Classes\SubathonManager.Overlay\DefaultIcon",
            "",
            $"{exePath},0"
        );

        EnsureRegistryValue(
            @"HKEY_CURRENT_USER\Software\Classes\SubathonManager.Overlay\shell\open\command",
            "",
            $"\"{exePath}\" \"%1\""
        );
        
        EnsureRegistryValue(
            @"HKEY_CURRENT_USER\Software\Classes\subathonmanager",
            "",
            "URL:Subathon Manager Protocol");

        EnsureRegistryValue(
            @"HKEY_CURRENT_USER\Software\Classes\subathonmanager",
            "URL Protocol",
            "");

        EnsureRegistryValue(
            @"HKEY_CURRENT_USER\Software\Classes\subathonmanager\shell\open\command",
            "",
            $"\"{exePath}\" \"%1\"");
        
        EnsureRegistryValue(
            @"HKEY_CURRENT_USER\Software\Classes\SubathonManager.Overlay\shell\import",
            "",
            "Import into Subathon Manager"
        );
        
        EnsureRegistryValue(
            @"HKEY_CURRENT_USER\Software\Classes\SubathonManager.Overlay\shell\import",
            "Icon",
            $"{exePath},0"
        );
        
        EnsureRegistryValue(
            @"HKEY_CURRENT_USER\Software\Classes\Applications\SubathonManager.exe",
            "FriendlyAppName",
            "Subathon Manager"
        );
        
        EnsureRegistryValue(
            @"HKEY_CURRENT_USER\Software\Classes\.smo\OpenWithProgids",
            "SubathonManager.Overlay",
            ""
        );
    }
    
    private void EnsureRegistryValue(string keyPath, string name, string expectedValue)
    {
        var currentValue = Registry.GetValue(keyPath, name, null) as string;

        if (currentValue == expectedValue)
            return;

        Registry.SetValue(keyPath, name, expectedValue);
    }

}