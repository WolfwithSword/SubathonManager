using System.Windows;
using System.Collections.ObjectModel;
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
    }
}
