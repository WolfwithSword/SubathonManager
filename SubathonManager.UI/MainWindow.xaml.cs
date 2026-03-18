using System.Windows;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Interop;
using Wpf.Ui.Appearance;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging; 
using Microsoft.EntityFrameworkCore;
using SubathonManager.Core.Models;
using SubathonManager.Core.Events;
using SubathonManager.Data;
using SubathonManager.Core;
using SubathonManager.Core.Interfaces;
using SubathonManager.UI.Services;
using SubathonManager.UI.Views;

namespace SubathonManager.UI
{
    public partial class MainWindow
    {
        private readonly IDbContextFactory<AppDbContext> _factory;
        
        private DateTime? _lastUpdatedTimerAt;
        private ObservableCollection<Route> Overlays { get; set; } = new();
        private readonly ILogger? _logger = AppServices.Provider.GetRequiredService<ILogger<MainWindow>>();

        public MainWindow()
        {
            _factory = AppServices.Provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
            InitializeComponent();
            ApplicationThemeManager.Apply(this);

            Loaded += MainWindow_Loaded;
            TitleBar.Title = $"Subathon Manager - {ServiceManager.AppVersion}";

            SubathonEvents.SubathonDataUpdate += UpdateTimerValue;
            SubathonEvents.SubathonDataUpdate += UpdateMultiplierUi;
            Task.Run(App.InitSubathonTimer);
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            var hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        }

        private IntPtr WndProc(
            IntPtr hwnd,
            int msg,
            IntPtr wParam,
            IntPtr lParam,
            ref bool handled)
        {
            if (msg == Utils.SingleInstanceHelper.WM_SHOWAPP)
            {
                Show();
                WindowState = WindowState.Normal;
                Activate();
                Topmost = true;
                Topmost = false;
                Focus();

                handled = true;
            }

            return IntPtr.Zero;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            LoadRoutes();
            Task.Run(() =>
            {
                Task.Delay(500);
                OverlayEvents.RaiseOverlayRefreshAllRequested();
            });
            var config = AppServices.Provider.GetRequiredService<IConfig>();
            var currencies = ServiceManager.Events.ValidEventCurrencies().OrderBy(x => x).ToList();
            AdjustCurrencyBox.ItemsSource = currencies;
            AdjustCurrencyBox.SelectedItem = config.Get("Currency", "Primary", "USD")?
                .Trim().ToUpperInvariant() ?? "USD";

        }
        
        private void ExportRoute_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not Route route) return;

            var dialog = new ExportOverlayDialog(route)
            {
                Owner = Application.Current.Windows
                    .OfType<Window>()
                    .FirstOrDefault(w => w.IsActive),
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            dialog.ShowDialog();
        }
        
        private async void ImportRoute_Click(object sender, RoutedEventArgs e)
        {
            var openDialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Import Overlay",
                Filter = "Subathon Manager Overlay (*.smo)|*.smo",
                DefaultExt = "smo"
            };
 
            if (openDialog.ShowDialog() != true) return;
 
            string importsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "imports");
 
            try
            {
                var result = await OverlayPorter.ImportRouteAsync(
                    openDialog.FileName, importsDir, _factory);
 
                if (result.Failed)
                {
                    _logger?.LogError("Import failed: {Reason}", result.FailReason);
                    return;
                }
 
                if (!result.HasAnythingNew)
                {
                    _logger?.LogInformation("Import: everything already exists, nothing to add");
                    return;
                }
 
                await using var db = await _factory.CreateDbContextAsync();
 
                if (result is { RouteIsNew: true, Route: not null })
                    db.Routes.Add(result.Route);
 
                if (result.NewWidgets.Count > 0)
                    db.Widgets.AddRange(result.NewWidgets);
 
                if (result.NewCssVariables.Count > 0)
                    db.CssVariables.AddRange(result.NewCssVariables);
 
                if (result.NewJsVariables.Count > 0)
                    db.JsVariables.AddRange(result.NewJsVariables);
 
                await db.SaveChangesAsync();
                await Dispatcher.InvokeAsync(LoadRoutes);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Import of overlay failed");
            }
        }
    }
}
