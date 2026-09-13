using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.Extensions.Logging;
using SubathonManager.UI.UiUtils;

// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.UI.Views;

public partial class SubathonSummaryWindow {
    private bool _amountsLoaded;
    private bool _amountsPinChoice;
    private bool _lbPinChoice;

    private string ApiBaseUrl() {
        int port = int.TryParse(_config.Get("Server", "Port", "14040"), out int parsed) ? parsed : 14040;
        return $"http://localhost:{port}";
    }
    
    private bool PinSubathonId(CheckBox? box) {
        if (Selected == null) return false;
        return box?.IsChecked == true || !Selected.IsActive;
    }

    private string AmountsUrl() {
        string url = $"{ApiBaseUrl()}/api/data/amounts";
        return PinSubathonId(AmountsIncludeSubathonBox) ? $"{url}?subathon={Selected!.Id}" : url;
    }

    private void AmountsIncludeSubathon_Changed(object? sender, RoutedEventArgs e) {
        if (AmountsIncludeSubathonBox?.IsEnabled == true)
            _amountsPinChoice = AmountsIncludeSubathonBox.IsChecked == true;
        if (AmountsUrlBox != null) AmountsUrlBox.Text = AmountsUrl();
    }

    private void LbIncludeSubathon_Changed(object? sender, RoutedEventArgs e) {
        if (LbIncludeSubathonBox?.IsEnabled == true)
            _lbPinChoice = LbIncludeSubathonBox.IsChecked == true;
        if (LbUrlBox == null || LbUrlBox.Text?.Length is null or 0) return;
        string? rebuilt = BuildLeaderboardUrl();
        if (rebuilt != null) SetLeaderboardUrl(rebuilt);
    }

    private void UpdateSubathonPinBoxes() {
        var past = Selected is { IsActive: false };

        if (AmountsIncludeSubathonBox != null) {
            AmountsIncludeSubathonBox.IsEnabled = !past;
            AmountsIncludeSubathonBox.IsChecked = past || _amountsPinChoice;
        }

        if (LbIncludeSubathonBox == null) return;
        LbIncludeSubathonBox.IsEnabled = !past;
        LbIncludeSubathonBox.IsChecked = past || _lbPinChoice;
    }

    private async void InvalidateAmounts() {
        _amountsLoaded = false;
        AmountsJsonBox.Text = string.Empty;
        AmountsUrlBox.Text = AmountsUrl();
        AmountsStatus.Text = "Not loaded yet.";

        LbIncludeSubathon_Changed(null, new RoutedEventArgs());

        if (AmountsTab == null || MainTabs.SelectedItem != AmountsTab) return;
        _amountsLoaded = true;
        await LoadAmountsAsync();
    }

    private async void MainTabs_SelectionChanged(object? sender, SelectionChangedEventArgs e) {
        if (AmountsTab == null || MainTabs.SelectedItem != AmountsTab || _amountsLoaded) return;
        _amountsLoaded = true;
        await LoadAmountsAsync();
    }

    private async Task LoadAmountsAsync() {
        string url = AmountsUrl();
        AmountsUrlBox.Text = url;
        AmountsStatus.Text = "Loading...";

        try {
            HttpResponseMessage response = await LeaderboardClient.GetAsync(url);
            string body = await response.Content.ReadAsStringAsync();
            AmountsJsonBox.Text = body;
            AmountsStatus.Text = response.IsSuccessStatusCode
                ? $"Loaded at {DateTime.Now:HH:mm:ss}"
                : $"The server returned {(int)response.StatusCode}.";
        }
        catch (Exception ex) {
            _logger?.LogWarning(ex, "[Summary] Amounts request failed");
            AmountsJsonBox.Text = string.Empty;
            AmountsStatus.Text = "Could not reach the web server. Is it running? (Settings -> Port)";
        }
    }

    private async void AmountsRun_Click(object? sender, RoutedEventArgs e) {
        await LoadAmountsAsync();
    }

    private async void AmountsCopyUrl_Click(object? sender, RoutedEventArgs e) {
        bool copied = await UiHelpers.TrySetClipboardTextAsync(AmountsUrl());
        AmountsStatus.Text = copied ? "URL copied to clipboard" : "Could not access the clipboard";
    }

    private async void AmountsCopyJson_Click(object? sender, RoutedEventArgs e) {
        string json = AmountsJsonBox.Text ?? string.Empty;
        if (json.Length == 0) {
            AmountsStatus.Text = "Nothing to copy, load it first";
            return;
        }

        bool copied = await UiHelpers.TrySetClipboardTextAsync(json);
        AmountsStatus.Text = copied ? "JSON copied to clipboard" : "Could not access the clipboard";
    }

    private void AmountsOpen_Click(object? sender, RoutedEventArgs e) {
        try {
            Process.Start(new ProcessStartInfo { FileName = AmountsUrl(), UseShellExecute = true });
        }
        catch (Exception ex) {
            _logger?.LogWarning(ex, "[Summary] Could not open amounts URL");
            AmountsStatus.Text = "Could not open a browser";
        }
    }
}
