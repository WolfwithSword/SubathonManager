using System.IO;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Security;
using SubathonManager.Core.Security.Interfaces;
using SubathonManager.Data;
using SubathonManager.Server;
using Avalonia.Platform.Storage;
using SubathonManager.Services;
using SubathonManager.UI.Platform;
using SubathonManager.UI.Services;
// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.UI;

public partial class App : Application
{
    private FileSystemWatcher? _configWatcher;
    private ILogger? _logger;
    private IDbContextFactory<AppDbContext>? _factory;
    private string _currencyVal = string.Empty;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }
        
        try { Program.Platform.RegisterFileAssociations(); } catch { /**/ }
        Program.Platform.ActivationReceived += OnActivationReceived;

        // macOs app events
        if (this.TryGetFeature<IActivatableLifetime>() is { } activatable)
            activatable.Activated += (_, e) => OnAppActivated(e);

        if (desktop.Args is { Length: > 0 })
            ProtocolParser.Parse(desktop.Args);

        var services = new ServiceCollection();

        string folder = Path.GetFullPath(Path.Combine(string.Empty, "data"));
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
            MigrateSecureStore(config);

            bool bitsAsDonationCheck = config.GetBool("Currency", "BitsLikeAsDonation", false);
            Utils.DonationSettings["BitsLikeAsDonation"] = bitsAsDonationCheck;
            foreach (var orderSource in Enum.GetValues<SubathonEventType>().Where(et => 
                         et.GetSource() is not (SubathonEventSource.Throne or SubathonEventSource.GoAffPro)
                         && !et.IsDisabled() && ((SubathonEventType?)et).IsOrder()))
            {
                Utils.DonationSettings[$"{orderSource.ToString()?.Split("Order")[0]}"] =
                    config.GetBool($"{orderSource.GetSource()}",
                        $"{orderSource.ToString()?.Split("Order")[0]}.CommissionAsDonation", true);
            }

            _currencyVal = config.Get("Currency", "Primary", "USD")!;

            SetThemeVariant(config);

            if (PlatformSettings is { } ps)
                ps.ColorValuesChanged += (_, _) =>
                {
                    var cfg = AppServices.Provider.GetRequiredService<IConfig>();
                    var t = cfg.Get("App", "Theme", "System")!.Trim();
                    if (!t.Equals("Light", StringComparison.OrdinalIgnoreCase)
                        && !t.Equals("Dark", StringComparison.OrdinalIgnoreCase))
                        Dispatcher.UIThread.Post(() => SetThemeVariant(cfg));
                };

            _logger = AppServices.Provider.GetRequiredService<ILogger<App>>();
            _logger.LogInformation("======== Subathon Manager started ========");
            _logger.LogDebug("== Avalonia Build ==");
            _logger.LogInformation("== Data folder: {DataFolder} ==", Config.DataFolder);

            var factory = AppServices.Provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
            _factory = factory;
            using (var db = _factory.CreateDbContext())
            {
                db.Database.Migrate();
                AppDbContext.SeedDefaultValues(db);

                var stores = db.GoAffProStores.ToList();
                GoAffProStoreRegistry.Initialize(stores);

                MakeShipTrackingRegistry.Initialize(db.MakeShipTrackings.AsNoTracking().ToList());

                JuniperStoreRegistry.Initialize(db.JuniperStores
                    .Include(s => s.Products).AsNoTracking().ToList());
            }

            GoAffProConfigMigration.Run(config);
            foreach (var store in GoAffProStoreRegistry.All())
            {
                Utils.DonationSettings[store.InternalName] =
                    config.GetBool("GoAffPro", $"{store.InternalName}.CommissionAsDonation", false);
            }

            WireRegistryPersistence();

            var window = new MainWindow();
            desktop.MainWindow = window;
            window.Closing += (_, _) => window.CloseEditor(); 
            
            desktop.ShutdownMode = global::Avalonia.Controls.ShutdownMode.OnMainWindowClose;

            if (Utils.PendingOAuthCallback != null)
            {
                desktop.Shutdown();
                return;
            }

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

                await sm.StartAsync<WebServer>(fireAndForget: true);
                await sm.StartAsync<TimerService>(fireAndForget: true);
                await sm.StartAsync<PromptOrchestratorService>();
                await sm.StartAsync<WheelSpinTriggerService>();
                await Task.Delay(100);
                TimerEvents.TimerTickEvent += UpdateSubathonTimers;
                InitSubathonTimer();

                await sm.StartIntegrationsAsync();

                if (config.GetBool("Telemetry", "Enabled", false))
                    await sm.StartAsync<TelemetryService>();
            });

            WatchConfig();

            desktop.ShutdownRequested += OnShutdownRequested;
        }
        catch (Exception ex)
        {
            File.WriteAllText("error_startup.log", $"{ex}\r\n{ex.StackTrace}");
            _logger?.LogError(ex, "Error occurred when starting Subathon Manager");
            desktop.Shutdown();
        }

        base.OnFrameworkInitializationCompleted();
    }
    
    private void OnActivationReceived(ActivationRequest request)
    {
        Dispatcher.UIThread.Post(() =>
        {
            switch (request.Kind)
            {
                case Platform.ActivationKind.SmoFile:
                    Utils.PendingOverlayImportPath = request.Payload;
                    break;
                case Platform.ActivationKind.OAuth:
                    ProtocolParser.Parse([request.Payload]);
                    break;
            }
            DispatchToMainWindow(request.Kind);
        });
    }

    private void OnAppActivated(object? activationArgs)
    {
        ActivationRequest request = activationArgs switch
        {
            FileActivatedEventArgs f when f.Files.FirstOrDefault()?.TryGetLocalPath() is { } path
                => ProtocolParser.Parse([path]),
            ProtocolActivatedEventArgs p
                => ProtocolParser.Parse([p.Uri.ToString()]),
            _ => new ActivationRequest(Platform.ActivationKind.Unknown, string.Empty)
        };

        if (request.Kind == Platform.ActivationKind.Unknown) return;
        Dispatcher.UIThread.Post(() => DispatchToMainWindow(request.Kind));
    }

    private void DispatchToMainWindow(Platform.ActivationKind kind)
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        if (desktop.MainWindow is not MainWindow mw) return;

        mw.Show();
        mw.WindowState = global::Avalonia.Controls.WindowState.Normal;
        mw.Activate();
        mw.HandlePendingActivation(kind);
    }

    private void SetThemeVariant(IConfig config)
    {
        string theme = config.Get("App", "Theme", "System")!.Trim();
        RequestedThemeVariant = theme switch
        {
            _ when theme.Equals("Light", StringComparison.OrdinalIgnoreCase) => ThemeVariant.Light,
            _ when theme.Equals("Dark", StringComparison.OrdinalIgnoreCase) => ThemeVariant.Dark,
            _ => DetectSystemThemeVariant()
        };
    }

    private ThemeVariant DetectSystemThemeVariant()
    {
        var sys = PlatformSettings?.GetColorValues().ThemeVariant ?? PlatformThemeVariant.Dark;
        return sys == PlatformThemeVariant.Light ? ThemeVariant.Light : ThemeVariant.Dark;
    }

    private void SetThemeFromConfig()
    {
        Dispatcher.UIThread.Post(() =>
        {
            var config = AppServices.Provider.GetRequiredService<IConfig>();
            SetThemeVariant(config);
        }, DispatcherPriority.Background);
    }

    private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        _logger?.LogInformation("======== Subathon Manager exiting ========");
        try
        {
            var sm = AppServices.Provider.GetRequiredService<ServiceManager>();
            Task.Run(async () =>
            {
                await sm.StopCoreServicesAsync();
                await sm.StopIntegrationsAsync();
            }).Wait(TimeSpan.FromSeconds(8));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("Error during shutdown: {Exception}", ex);
        }

        _configWatcher?.Dispose();
        (AppServices.Provider as IDisposable)?.Dispose();
        _logger?.LogInformation("======== Subathon Manager exit ========");
        //
    }

    private void MigrateSecureStore(IConfig config)
    {
        bool hasUpdated = false;
        var secureStorage = AppServices.Provider.GetRequiredService<ISecureStorage>();

        var value = config.Get("StreamElements", "JWT", string.Empty);
        if (!string.IsNullOrWhiteSpace(value))
        {
            secureStorage.Set(StorageKeys.StreamElementsJwt, value);
            hasUpdated |= config.Set("StreamElements", "JWT", string.Empty);
        }
        value = config.Get("StreamLabs", "SocketToken", string.Empty);
        if (!string.IsNullOrWhiteSpace(value))
        {
            secureStorage.Set(StorageKeys.StreamLabsSocketToken, value);
            hasUpdated |= config.Set("StreamLabs", "SocketToken", string.Empty);
        }
        value = config.GetFromEncoded("KoFi", "VerificationToken", string.Empty);
        if (!string.IsNullOrWhiteSpace(value))
        {
            secureStorage.Set(StorageKeys.KoFiVerificationToken, value);
            hasUpdated |= config.Set("KoFi", "VerificationToken", string.Empty);
        }
        value = config.GetFromEncoded("OBS", "Password", string.Empty);
        if (!string.IsNullOrWhiteSpace(value))
        {
            secureStorage.Set(StorageKeys.OBSWebSocketPassword, value);
            hasUpdated |= config.Set("OBS", "Password", string.Empty);
        }
        value = config.GetFromEncoded("GoAffPro", "Email", string.Empty);
        if (!string.IsNullOrWhiteSpace(value))
        {
            secureStorage.Set(StorageKeys.GoAffProEmail, value);
            hasUpdated |= config.Set("GoAffPro", "Email", string.Empty);
        }
        value = config.GetFromEncoded("GoAffPro", "Password", string.Empty);
        if (!string.IsNullOrWhiteSpace(value))
        {
            secureStorage.Set(StorageKeys.GoAffProPassword, value);
            hasUpdated |= config.Set("GoAffPro", "Password", string.Empty);
        }

        if (hasUpdated) config.Save();
    }

    private void WatchConfig()
    {
        string configFile = Path.GetFullPath(Path.Combine(string.Empty, "data/config.ini"));
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
                Task.Run(async () => await sm.StartAsync<TelemetryService>());
            else if (!config.GetBool("Telemetry", "Enabled", false) && sm.IsRunning<TelemetryService>())
                Task.Run(async () => await sm.StopAsync<TelemetryService>());

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
            if (currencyChanged) _currencyVal = currency;

            if (Utils.DonationSettings.TryGetValue("BitsLikeAsDonation", out bool asDonoBits) && asDonoBits != bitsAsDonationCheck)
            {
                optionToggled = true;
                Utils.DonationSettings["BitsLikeAsDonation"] = bitsAsDonationCheck;
            }

            foreach (var store in GoAffProStoreRegistry.All())
            {
                bool asDonation = config.GetBool("GoAffPro", $"{store.InternalName}.CommissionAsDonation", false);
                if (Utils.DonationSettings.TryGetValue(store.InternalName, out bool hasVal) && hasVal == asDonation) continue;
                optionToggled = true;
                Utils.DonationSettings[store.InternalName] = asDonation;
            }

            foreach (var orderSource in Enum.GetValues<SubathonEventType>().Where(et => 
                         et.GetSource() is not (SubathonEventSource.Throne or SubathonEventSource.GoAffPro)
                         && !et.IsDisabled() && ((SubathonEventType?)et).IsOrder()))
            {
                bool asDonation = config.GetBool($"{orderSource.GetSource()}", $"{orderSource.ToString()?.Split("Order")[0]}.CommissionAsDonation", true);
                if (Utils.DonationSettings.TryGetValue($"{orderSource.ToString()?.Split("Order")[0]}", out bool hasVal) && hasVal == asDonation) continue;
                optionToggled = true;
                Utils.DonationSettings[$"{orderSource.ToString()?.Split("Order")[0]}"] = asDonation;
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

    private async Task SetupSubathonCurrencyData(AppDbContext db, bool? optionToggled)
    {
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

        var currencyService = AppServices.Provider.GetRequiredService<CurrencyService>();
        if (subathon.MoneySum != null &&
            !subathon.MoneySum.Equals((double)0) && !string.IsNullOrWhiteSpace(oldCurrency))
        {
            var amt = await currencyService.ConvertAsync((double)subathon.MoneySum, oldCurrency, currency);
            await db.UpdateSubathonMoney(amt, subathon.Id);
            if (optionToggled != null && !(bool)optionToggled)
            {
                var subathonTotals = await EventService.GetSubathonTotalsAsync(db);
                if (subathonTotals != null)
                    SubathonEvents.RaiseSubathonTotalsUpdated(subathonTotals);
                return;
            }
        }

        var events = await AppDbContext.GetSubathonCurrencyEvents(db);

        double sum = 0;
        double bits = 0;
        foreach (var ev in events)
        {
            (bool isBitsLike, double modifier) = Utils.GetAltCurrencyUseAsDonation(config, ev.EventType);
            if (ev.EventType.IsToken() && isBitsLike)
            {
                bits += int.Parse(ev.Value) * modifier;
                continue;
            }
            if (string.IsNullOrWhiteSpace(ev.Currency)) continue;
            var value = ev.Value;
            var curr = ev.Currency;
            if (ev.EventType.IsOrder())
            {
                value = ev.SecondaryValue.Split('|')[0];
                curr = ev.SecondaryValue.Split('|')[1];
            }
            var amt = await currencyService.ConvertAsync(double.Parse(value), curr, currency.ToUpper());
            sum += amt;
        }

        if (Utils.DonationSettings.TryGetValue("BitsLikeAsDonation", out var bitslike) && bitslike)
        {
            double val = await currencyService.ConvertAsync(bits / 100, "USD", subathon.Currency);
            sum += val;
        }
        await db.UpdateSubathonMoney(sum, subathon.Id);
        var totals = await EventService.GetSubathonTotalsAsync(db);
        if (totals != null)
            SubathonEvents.RaiseSubathonTotalsUpdated(totals);
    }
}
