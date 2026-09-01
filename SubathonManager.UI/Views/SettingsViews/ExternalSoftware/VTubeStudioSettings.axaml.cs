using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;
using SubathonManager.Core.Security;
using SubathonManager.Core.Security.Interfaces;
using SubathonManager.Data;
using SubathonManager.Integration;
using SubathonManager.UI.Services;
using SubathonManager.UI.UiUtils;

namespace SubathonManager.UI.Views.SettingsViews.ExternalSoftware;

public partial class VTubeStudioSettings : SettingsControl {
    private const string ServiceName = "VTubeStudio";
    private readonly ILogger? _logger = AppServices.Provider.GetRequiredService<ILogger<VTubeStudioSettings>>();

    public VTubeStudioSettings() {
        InitializeComponent();
        Loaded += (_, _) => {
            LoadConfigIntoBoxes();

            IntegrationEvents.ConnectionUpdated -= UpdateStatus;
            IntegrationEvents.ConnectionUpdated += UpdateStatus;
            ServiceManager.VTubeStudio.ModelDataChanged -= OnModelDataChanged;
            ServiceManager.VTubeStudio.ModelDataChanged += OnModelDataChanged;

            UpdateVtsStatus(ServiceManager.VTubeStudio.Connected);
            PopulateModelData();
        };
        Unloaded += (_, _) => {
            IntegrationEvents.ConnectionUpdated -= UpdateStatus;
            ServiceManager.VTubeStudio.ModelDataChanged -= OnModelDataChanged;
        };
    }

    public override void Init(SettingsView host) {
        Host = host;
        LoadConfigIntoBoxes();
        UpdateVtsStatus(ServiceManager.VTubeStudio.Connected);
    }

    private void LoadConfigIntoBoxes() {
        (string host, string port, bool _) = ServiceManager.VTubeStudio.GetConfig();
        SuppressUnsavedChanges(() => {
            VtsHostBox.Text = host;
            VtsPortBox.Text = port;
        });
    }

    internal override void UpdateStatus(IntegrationConnection? connection) {
        if (connection is not { Source: SubathonEventSource.VTubeStudio, Service: ServiceName }) return;
        UpdateVtsStatus(connection.Status);
    }

    private void UpdateVtsStatus(bool connected) {
        Host.UpdateConnectionStatus(connected, VtsStatusText, null);
        Dispatcher.UIThread.Post(() => {
            VtsConnectBtn.Content = connected ? "Reconnect" : "Connect";
            VtsDisconnectBtn.IsVisible = connected;
            UpdateAuthText();
            if (!connected) ClearModelData();
        });
    }

    private void UpdateAuthText() {
        var secureStorage = AppServices.Provider.GetRequiredService<ISecureStorage>();
        bool hasToken = !string.IsNullOrWhiteSpace(
            secureStorage.GetOrDefault(StorageKeys.VTubeStudioAuthToken, string.Empty));

        if (hasToken) {
            VtsAuthText.Text = "Authorized";
            VtsAuthText.Foreground = Brushes.LimeGreen;
        }
        else {
            VtsAuthText.Text = "Not authorized (allow the popup in VTubeStudio)";
            VtsAuthText.Foreground = Brushes.Orange;
        }
    }

    private void SetHint(string text) {
        Dispatcher.UIThread.Post(() => VtsHintText.Text = text);
    }

    private async void VtsConnect_Click(object? sender, RoutedEventArgs e) {
        try {
            string host = (VtsHostBox.Text ?? "").Trim();
            string port = (VtsPortBox.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(host)) host = "localhost";
            if (string.IsNullOrWhiteSpace(port)) port = "8001";

            ServiceManager.VTubeStudio.SaveConfig(host, port, true, true);
            SetHint("Connecting. If VTube Studio shows an approval popup, please accept it");
            await ServiceManager.VTubeStudio.RestartAsync();
        }
        catch (Exception ex) {
            _logger?.LogError(ex, "Failed to connect VTubeStudio service");
        }
    }

    private async void VtsDisconnect_Click(object? sender, RoutedEventArgs e) {
        try {
            string host = (VtsHostBox.Text ?? "").Trim();
            string port = (VtsPortBox.Text ?? "").Trim();
            ServiceManager.VTubeStudio.SaveConfig(host, port, false, true);
            await ServiceManager.VTubeStudio.StopAsync();
        }
        catch (Exception ex) {
            _logger?.LogError(ex, "Failed to disconnect VTubeStudio service");
        }
    }

    private void VtsReauth_Click(object? sender, RoutedEventArgs e) {
        ServiceManager.VTubeStudio.ClearAuthToken();
        UpdateAuthText();
        SetHint("Stored token cleared. Reconnect and accept the popup in VTubeStudio");
    }

    private void OnModelDataChanged() {
        Dispatcher.UIThread.Post(PopulateModelData);
    }

    private async void VtsRefresh_Click(object? sender, RoutedEventArgs e) {
        if (!ServiceManager.VTubeStudio.Connected) {
            SetHint("Not connected to VTube Studio.");
            return;
        }

        await ServiceManager.VTubeStudio.RefreshAsync();
    }

    private void ClearModelData() {
        VtsModelText.Text = "No model loaded";
        VtsHotkeyBox.ItemsSource = null;
        VtsExpressionBox.ItemsSource = null;
        VtsParameterBox.ItemsSource = null;
    }

    private void PopulateModelData() {
        VTSService vts = ServiceManager.VTubeStudio;
        if (!vts.Connected) {
            ClearModelData();
            return;
        }

        VtsModelText.Text = string.IsNullOrWhiteSpace(vts.CurrentModelName)
            ? "No model loaded"
            : vts.CurrentModelName;

        SuppressUnsavedChanges(() => {
            VtsHotkeyBox.ItemsSource = vts.CachedHotkeys
                .Select(h => new PickerItem($"{h.Name}  ({h.Type})", h.Id))
                .ToList();
            VtsExpressionBox.ItemsSource = vts.CachedExpressions
                .Select(x => new PickerItem($"{x.Name}{(x.Active ? "  [on]" : "")}", x.File))
                .ToList();
            VtsParameterBox.ItemsSource = vts.CachedParameters
                .Select(p => new PickerItem(
                    $"{p.Name}  ({p.Value:0.##} of {p.Min:0.##}..{p.Max:0.##}){(p.IsCustom ? "  [custom]" : "")}",
                    p.Name, p.Value))
                .ToList();
        });

        SetHint($"{vts.CachedHotkeys.Count} hotkeys, {vts.CachedExpressions.Count} expressions, "
                + $"{vts.CachedParameters.Count} input parameters.");
    }

    private void VtsParameter_SelectionChanged(object? sender, SelectionChangedEventArgs e) {
        if (VtsParameterBox.SelectedItem is not PickerItem { Value: not null } item) return;
        SuppressUnsavedChanges(() => VtsParameterValueBox.Text = $"{item.Value:0.##}");
    }

    private async void CopyHotkeyId_Click(object? sender, RoutedEventArgs e) {
        await CopySelected(VtsHotkeyBox, "hotkey id");
    }

    private async void CopyExpressionFile_Click(object? sender, RoutedEventArgs e) {
        await CopySelected(VtsExpressionBox, "expression file");
    }

    private async void CopyParameterName_Click(object? sender, RoutedEventArgs e) {
        await CopySelected(VtsParameterBox, "parameter name");
    }

    private async Task CopySelected(ComboBox box, string label) {
        if (box.SelectedItem is not PickerItem item) {
            SetHint($"Select a {label} first");
            return;
        }

        await UiHelpers.TrySetClipboardTextAsync(item.Key);
        SetHint($"Copied {label}: {item.Key}");
    }

    private async void TriggerHotkey_Click(object? sender, RoutedEventArgs e) {
        if (VtsHotkeyBox.SelectedItem is not PickerItem item) {
            SetHint("Select a hotkey first");
            return;
        }

        bool ok = await ServiceManager.VTubeStudio.TriggerHotkeyAsync(item.Key);
        SetHint(ok ? $"Triggered hotkey {item.Key}" : "Hotkey trigger failed");
    }

    private async void ExpressionOn_Click(object? sender, RoutedEventArgs e) {
        await ApplyExpression(VtsToggleAction.On);
    }

    private async void ExpressionOff_Click(object? sender, RoutedEventArgs e) {
        await ApplyExpression(VtsToggleAction.Off);
    }

    private async void ExpressionToggle_Click(object? sender, RoutedEventArgs e) {
        await ApplyExpression(VtsToggleAction.Toggle);
    }

    private async Task ApplyExpression(VtsToggleAction action) {
        if (VtsExpressionBox.SelectedItem is not PickerItem item) {
            SetHint("Select an expression first");
            return;
        }

        bool ok = await ServiceManager.VTubeStudio.ApplyExpressionActionAsync(item.Key, action);
        SetHint(ok ? $"{action} applied to {item.Key}" : $"Failed to apply {action}");
        if (ok) await ServiceManager.VTubeStudio.RefreshAsync();
    }

    private async void SetParameter_Click(object? sender, RoutedEventArgs e) {
        if (VtsParameterBox.SelectedItem is not PickerItem item) {
            SetHint("Select a parameter first");
            return;
        }

        if (!double.TryParse((VtsParameterValueBox.Text ?? "").Trim(), out double value)) {
            SetHint("Enter a numeric value to set");
            return;
        }

        bool ok = await ServiceManager.VTubeStudio.SetParameterValueAsync(item.Key, value);
        SetHint(ok
            ? $"Holding {item.Key} at {value:0.##}. Press Release to hand it back to tracking"
            : "Failed to set the parameter");
    }

    private void ReleaseParameter_Click(object? sender, RoutedEventArgs e) {
        if (VtsParameterBox.SelectedItem is not PickerItem item) {
            SetHint("Select a parameter first");
            return;
        }

        bool released = ServiceManager.VTubeStudio.ReleaseParameter(item.Key);
        SetHint(released ? $"Released {item.Key}" : $"{item.Key} was not being held");
    }
    
    public override bool UpdateValueSettings(AppDbContext db) {
        return false;
    }

    public override void UpdateCurrencyBoxes(List<string> currencies, string selected) {
    }

    public override (string, string, TextBox?, TextBox?) GetValueBoxes(SubathonValue val) {
        return ("", "", null, null);
    }

    private sealed class PickerItem(string label, string key, double? value = null) {
        public string Key { get; } = key;
        public double? Value { get; } = value;

        public override string ToString() {
            return label;
        }
    }
}