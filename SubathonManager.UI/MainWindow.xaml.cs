using System.Windows;
using System.Collections.ObjectModel;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using SubathonManager.Core.Models;
using SubathonManager.Core;
using SubathonManager.Core.Events;

// TODO: split up xaml?

namespace SubathonManager.UI
{
    public partial class MainWindow : FluentWindow
    {
        private DateTime? _lastUpdatedTimerAt = null;
        public ObservableCollection<Route> Overlays { get; set; } = new();
        
        public MainWindow()
        {
            TwitchEvents.TwitchConnected += UpdateTwitchStatus;
            StreamElementsEvents.StreamElementsConnectionChanged += UpdateSEStatus;
            InitializeComponent();
            ApplicationThemeManager.Apply(this);

            Loaded += MainWindow_Loaded;
            SaveSettingsButton.Click += SaveSettingsButton_Click;
            TitleBar.Title = $"Subathon Manager - {App.AppVersion}";
            DataFolderText.Text = $"Data Folder: {Config.DataFolder}";
            SEJWTTokenBox.Text = Config.Data["StreamElements"]["JWT"];

            SubathonEvents.SubathonDataUpdate += UpdateTimerValue;
            Task.Run(() => App.InitSubathonTimer());
        }
        
        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            LoadRoutes();
            ServerPortTextBox.Text = Config.Data["Server"]["Port"];
            LoadValues();
        }
    }
}
