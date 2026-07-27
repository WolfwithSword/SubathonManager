using Avalonia;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Objects;
using SubathonManager.Core.Models;
using SubathonManager.UI.Views;
using SubathonManager.UI.Services;

namespace SubathonManager.UI;

public partial class MainWindow
{
    public static readonly StyledProperty<bool> ObsConnectedProperty =
        AvaloniaProperty.Register<MainWindow, bool>(nameof(ObsConnected));

    public bool ObsConnected
    {
        get => GetValue(ObsConnectedProperty);
        set => SetValue(ObsConnectedProperty, value);
    }

    private void InitObsIntegration()
    {
        IntegrationEvents.ConnectionUpdated += OnObsConnectionUpdated;
        Closed += (_, _) => IntegrationEvents.ConnectionUpdated -= OnObsConnectionUpdated;
        ObsConnected = ServiceManager.OBS.Connected;
    }

    private void OnObsConnectionUpdated(IntegrationConnection? connection)
    {
        if (connection is not { Source: SubathonEventSource.OBS, Service: "OBS" }) return;
        Dispatcher.UIThread.Post(() => ObsConnected = connection.Status);
    }

    private async void AddToObs_Click(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not global::Avalonia.Controls.Control { DataContext: Route route }) return;

        try
        {
            var scenes = ServiceManager.OBS.GetScenes();
            string currentScene = ServiceManager.OBS.GetCurrentScene();
            var config = AppServices.Provider.GetRequiredService<IConfig>();
            string url = route.GetRouteUrl(config);

            var dialog = new ObsAddSourceDialog(route, url, scenes, currentScene);
            await dialog.ShowDialog(this);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[OBS] Failed to open add source dialog for route {Name}", route.Name);
        }
    }
}
