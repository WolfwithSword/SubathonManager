using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;
using SubathonManager.Data;
using SubathonManager.UI.Services;

namespace SubathonManager.UI.Views.SettingsViews.ExternalSoftware;

public partial class ObsSettings : SettingsControl
{
    public ObsSettings()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            var (host, port, pw) = ServiceManager.OBS.GetConfig();
            SuppressUnsavedChanges(() =>
            {
                ObsHostBox.Text = host;
                ObsPortBox.Text = port;
                ObsPasswordBox.Password = pw;
            });

            IntegrationEvents.ConnectionUpdated -= UpdateStatus;
            IntegrationEvents.ConnectionUpdated += UpdateStatus;
            ServiceManager.OBS.HelperScriptStatusChanged -= OnHelperScriptStatusChanged;
            ServiceManager.OBS.HelperScriptStatusChanged += OnHelperScriptStatusChanged;
            UpdateObsStatus(ServiceManager.OBS.Connected);
            UpdateScriptStatus(ServiceManager.OBS.HelperScriptActive);
            ServiceManager.OBS.RecheckHelperScript();
        };
        Unloaded += (_, _) =>
        {
            IntegrationEvents.ConnectionUpdated -= UpdateStatus;
            ServiceManager.OBS.HelperScriptStatusChanged -= OnHelperScriptStatusChanged;
        };
    }

    private void OnHelperScriptStatusChanged(bool active)
    {
        Dispatcher.InvokeAsync(() => UpdateScriptStatus(active));
    }

    private void UpdateScriptStatus(bool active)
    {
        if (active)
        {
            ScriptStatusText.Text = "Active";
            ScriptStatusText.Foreground = System.Windows.Media.Brushes.LimeGreen;
        }
        else if (ServiceManager.OBS.Connected)
        {
            ScriptStatusText.Text = "Not Loaded (add via OBS Tools -> Scripts)";
            ScriptStatusText.Foreground = System.Windows.Media.Brushes.Orange;
        }
        else
        {
            ScriptStatusText.Text = "Unknown (not connected)";
            ScriptStatusText.Foreground = System.Windows.Media.Brushes.Gray;
        }
    }

    private async void CopyScriptPath_Click(object sender, RoutedEventArgs e)
    {
        await UiUtils.UiUtils.TrySetClipboardTextAsync(Integration.OBSService.ScriptPath);
    }

    private void RecheckScript_Click(object sender, RoutedEventArgs e)
    {
        ServiceManager.OBS.RecheckHelperScript();
    }

    internal override void UpdateStatus(IntegrationConnection? connection)
    {
        if (connection is not { Source: SubathonEventSource.OBS }) return;
        UpdateObsStatus(connection.Status);
    }

    private void UpdateObsStatus(bool connected)
    {
        Host.UpdateConnectionStatus(connected, ObsStatusText, ObsConnectBtn);
        UpdateScriptStatus(ServiceManager.OBS.HelperScriptActive);
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

    public override bool UpdateValueSettings(AppDbContext db) => false;
    public override void UpdateCurrencyBoxes(List<string> currencies, string selected) { }
    public override (string, string, TextBox?, TextBox?) GetValueBoxes(SubathonValue val) => ("", "", null, null);
}
