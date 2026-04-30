using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;
using SubathonManager.UI.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Interfaces;
using SubathonManager.UI.Views;

namespace SubathonManager.UI;

public partial class MainWindow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool _obsConnected;
    public bool ObsConnected
    {
        get => _obsConnected;
        set { _obsConnected = value; OnPropertyChanged(); }
    }

    private void InitObsIntegration()
    {
        IntegrationEvents.ConnectionUpdated += OnObsConnectionUpdated;
        ObsConnected = ServiceManager.OBS.Connected;
    }

    private void OnObsConnectionUpdated(IntegrationConnection? connection)
    {
        if (connection is not { Source: SubathonEventSource.OBS }) return;
        Dispatcher.Invoke(() => ObsConnected = connection.Status);
    }

    private void AddToObs_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Wpf.Ui.Controls.Button { DataContext: Route route }) return;

        try
        {
            var scenes = ServiceManager.OBS.GetScenes(); // returns List<string>
            string currentScene = ServiceManager.OBS.GetCurrentScene();
            var config = AppServices.Provider.GetRequiredService<IConfig>();
            string url = route.GetRouteUrl(config);

            var dialog = new ObsAddSourceDialog(route, url, scenes, currentScene)
            {
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[OBS] Failed to open add source dialog for route {Name}", route.Name);
        }
    }
}