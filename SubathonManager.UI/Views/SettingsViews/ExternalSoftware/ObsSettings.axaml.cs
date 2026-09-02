using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;
using SubathonManager.Data;
using SubathonManager.Integration;
using SubathonManager.UI.Services;
using SubathonManager.UI.UiUtils;

namespace SubathonManager.UI.Views.SettingsViews.ExternalSoftware;

public partial class ObsSettings : SettingsControl {
    public ObsSettings() {
        InitializeComponent();
        Loaded += (_, _) => {
            (string host, string port, string pw) = ServiceManager.OBS.GetConfig();
            SuppressUnsavedChanges(() => {
                ObsHostBox.Text = host;
                ObsPortBox.Text = port;
                ObsPasswordBox.Text = pw;
            });

            IntegrationEvents.ConnectionUpdated -= UpdateStatus;
            IntegrationEvents.ConnectionUpdated += UpdateStatus;
            ServiceManager.OBS.HelperScriptStatusChanged -= OnHelperScriptStatusChanged;
            ServiceManager.OBS.HelperScriptStatusChanged += OnHelperScriptStatusChanged;
            UpdateObsStatus(ServiceManager.OBS.Connected);
            UpdateScriptStatus(ServiceManager.OBS.HelperScriptActive);
            ServiceManager.OBS.RecheckHelperScript();
        };
        Unloaded += (_, _) => {
            IntegrationEvents.ConnectionUpdated -= UpdateStatus;
            ServiceManager.OBS.HelperScriptStatusChanged -= OnHelperScriptStatusChanged;
        };
    }

    private void OnHelperScriptStatusChanged(bool active) {
        Dispatcher.UIThread.Post(() => UpdateScriptStatus(active));
    }

    private void UpdateScriptStatus(bool active) {
        if (active) {
            string? loaded = ServiceManager.OBS.HelperScriptVersion;
            string? expected = OBSService.ExpectedHelperScriptVersion;
            if (ServiceManager.OBS.HelperScriptOutdated) {
                ScriptStatusText.Text = loaded != null
                    ? $"Outdated (v{loaded} loaded, v{expected} available) - reload in OBS Tools -> Scripts"
                    : $"Outdated (v{expected} available) - reload in OBS Tools -> Scripts";
                ScriptStatusText.Foreground = Brushes.Orange;
            }
            else {
                ScriptStatusText.Text = loaded != null ? $"Active (v{loaded})" : "Active";
                ScriptStatusText.Foreground = Brushes.LimeGreen;
            }
        }
        else if (ServiceManager.OBS.Connected) {
            ScriptStatusText.Text = "Not Loaded (add via OBS Tools -> Scripts)";
            ScriptStatusText.Foreground = Brushes.Orange;
        }
        else {
            ScriptStatusText.Text = "Unknown (not connected)";
            ScriptStatusText.Foreground = Brushes.Gray;
        }
    }

    private async void CopyScriptPath_Click(object? sender, RoutedEventArgs e) {
        await UiHelpers.TrySetClipboardTextAsync(OBSService.ScriptPath);
    }

    private void RecheckScript_Click(object? sender, RoutedEventArgs e) {
        ServiceManager.OBS.RecheckHelperScript();
    }

    internal override void UpdateStatus(IntegrationConnection? connection) {
        if (connection is not { Source: SubathonEventSource.OBS, Service: "OBS" }) return;
        UpdateObsStatus(connection.Status);
    }

    private void UpdateObsStatus(bool connected) {
        Host.UpdateConnectionStatus(connected, ObsStatusText, ObsConnectBtn);
        UpdateScriptStatus(ServiceManager.OBS.HelperScriptActive);
    }

    private void ObsConnect_Click(object? sender, RoutedEventArgs e) {
        string host = (ObsHostBox.Text ?? "").Trim();
        string port = (ObsPortBox.Text ?? "").Trim();
        string password = (ObsPasswordBox.Text ?? "").Trim();

        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(port)) return;

        ServiceManager.OBS.SaveConfig(host, port, password, true);

        if (ServiceManager.OBS.Connected)
            ServiceManager.OBS.StopAsync();

        ServiceManager.OBS.TryConnect();
    }

    public override bool UpdateValueSettings(AppDbContext db) {
        return false;
    }

    public override void UpdateCurrencyBoxes(List<string> currencies, string selected) {
    }

    public override (string, string, TextBox?, TextBox?) GetValueBoxes(SubathonValue val) {
        return ("", "", null, null);
    }
}