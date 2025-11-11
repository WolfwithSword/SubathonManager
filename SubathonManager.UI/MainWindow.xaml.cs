using System.Windows;
using System.Collections.ObjectModel;
using Wpf.Ui.Appearance;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using SubathonManager.Core.Models;
using SubathonManager.Core.Events;
using SubathonManager.Data;
using SubathonManager.Core;

namespace SubathonManager.UI
{
    public partial class MainWindow
    {
        private readonly IDbContextFactory<AppDbContext> _factory;
        
        private DateTime? _lastUpdatedTimerAt;
        public ObservableCollection<Route> Overlays { get; set; } = new();
        public MainWindow()
        {
            _factory = AppServices.Provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
            InitializeComponent();
            ApplicationThemeManager.Apply(this);

            Loaded += MainWindow_Loaded;
            TitleBar.Title = $"Subathon Manager - {App.AppVersion}";

            SubathonEvents.SubathonDataUpdate += UpdateTimerValue;
            SubathonEvents.SubathonDataUpdate += UpdateMultiplierUi;
            Task.Run(App.InitSubathonTimer);
        }
        
        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            LoadRoutes();        
            Task.Run(() =>
            {
                Task.Delay(500);
                OverlayEvents.RaiseOverlayRefreshAllRequested();
            });
        }
    }
}
