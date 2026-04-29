using System.Windows;
using System.Windows.Input;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Objects;
using SubathonManager.UI.Services;

namespace SubathonManager.UI.Views;

public partial class SettingsView
{
    internal void InitOBSSettings()
    {
        var (host, port, pw) = ServiceManager.OBS.GetConfig();
        SuppressUnsavedChanges(() =>
        {
            ObsHostBox.Text = host;
            ObsPortBox.Text = port;
            ObsPasswordBox.Password = pw;
        });

        IntegrationEvents.ConnectionUpdated += OnObsConnectionUpdated;
        UpdateObsStatus(ServiceManager.OBS.Connected);
    }

    internal void UnloadOBSSettings()
    {
        IntegrationEvents.ConnectionUpdated -= OnObsConnectionUpdated;
    }

    private void OnObsConnectionUpdated(IntegrationConnection? connection)
    {
        if (connection is not { Source: SubathonEventSource.OBS }) return;
        UpdateObsStatus(connection.Status);
    }

    private void UpdateObsStatus(bool connected)
    {
        Dispatcher.Invoke(() =>
        {
            ObsStatusText.Text = connected ? "Connected" : "Disconnected";
            ObsConnectBtn.Content = connected ? "Reconnect" : "Connect";
        });
    }

    private void ObsConnect_Click(object sender, RoutedEventArgs e)
    {
        var host = ObsHostBox.Text.Trim();
        var port = ObsPortBox.Text.Trim();
        var password = ObsPasswordBox.Password.Trim();

        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(port)) return;

        ServiceManager.OBS.SaveConfig(host, port, password, true);

        if (ServiceManager.OBS.Connected)
            ServiceManager.OBS.StopAsync();

        ServiceManager.OBS.TryConnect();
    }
    
    private void NumberOnly_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !int.TryParse(e.Text, out _);
    }

}