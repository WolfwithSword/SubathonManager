using System.Windows;
using System.Collections.ObjectModel;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using SubathonManager.Core.Models;
using SubathonManager.Core.Events;

namespace SubathonManager.UI
{
    public partial class MainWindow : FluentWindow
    {
        private DateTime? _lastUpdatedTimerAt = null;
        public ObservableCollection<Route> Overlays { get; set; } = new();
        public MainWindow()
        {
            
            InitializeComponent();
            ApplicationThemeManager.Apply(this);

            Loaded += MainWindow_Loaded;
            TitleBar.Title = $"Subathon Manager - {App.AppVersion}";

            SubathonEvents.SubathonDataUpdate += UpdateTimerValue;
            Task.Run(() => App.InitSubathonTimer());
        }
        
        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            LoadRoutes();
        }
    }
}
