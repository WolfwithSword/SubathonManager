using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Objects;

// ReSharper disable NullableWarningSuppressionIsUsed
// ReSharper disable InconsistentNaming

namespace SubathonManager.UI.Views.SettingsViews.External;

public abstract class DevTunnelSettingsControl : SettingsControl
{
    
    protected abstract Wpf.Ui.Controls.PasswordBox _WebhookUrlBox { get; }
    protected abstract Wpf.Ui.Controls.TextBlock _WebhookStatusText{ get; }
    protected abstract SubathonEventSource _EventSource { get; }
    protected abstract StackPanel _WebhookUrlRow { get; }
    protected abstract Wpf.Ui.Controls.TextBlock _TunnelPrereqStatusText{ get; }
    protected abstract Wpf.Ui.Controls.Button _TunnelPrereqHint { get; }
    protected abstract Wpf.Ui.Controls.TextBox _WebhookForwardUrlsBox { get; }
    protected abstract Popup _ForwardUrlsPopup { get; }
    protected abstract Wpf.Ui.Controls.TextBox _ForwardUrlsMultiBox { get; }
    protected abstract Wpf.Ui.Controls.Button? _ConnectBtn { get; }
    
    internal void GoToDevTunnels_Click(object sender, RoutedEventArgs e)
    {
        SettingsEvents.RaiseHotLinkToDevTunnelsRequest();
    }
    
    // Called on every Loaded (incl. re-visits after switching tabs) so the UI
    // always reflects the latest stored state without relying on in-memory service
    // properties or a live event that may have fired while this control was detached.
    internal void RefreshFromStoredState()
    {
        // KoFiService owns its own connection entry (Status + public URL in Name).
        UpdateStatus(Utils.GetConnection(_EventSource,
            $"{_EventSource}"));

        // DevTunnels prereq banner: read the last-known tunnel state from the shared dict.
        UpdateStatus(Utils.GetConnection(SubathonEventSource.DevTunnels, "Tunnel"));
    }
    
    internal async void CopyWebhookUrl_Click(object sender, RoutedEventArgs e)
    {
        var url = _WebhookUrlBox.Password;
        if (string.IsNullOrWhiteSpace(url)) return;
        var result = await UiUtils.UiUtils.TrySetClipboardTextAsync(url);
        if (!result) return;
        var btn = (sender as Wpf.Ui.Controls.Button)!;
        var original = btn.Content;
        btn.Content = "Copied!";
        await Task.Delay(1500);
        btn.Content = original;
    }
    
    internal override void UpdateStatus(IntegrationConnection? connection)
    {
        if (connection == null) return;

        Dispatcher.Invoke(() =>
        {
            // Services compose and own the full public URL; we just display Name.
            if (connection.Source == _EventSource)
            {
                Host.UpdateConnectionStatus(connection.Status, _WebhookStatusText, _ConnectBtn);
                ApplyWebhookUrl(connection.Name);
            }

            // Tunnel prereq banner, driven by DevTunnels tunnel events.
            if (connection is { Source: SubathonEventSource.DevTunnels, Service: "Tunnel" })
                ApplyTunnelBanner(connection.Status, connection.Name);
        });
    }
    
    private void ApplyWebhookUrl(string? url)
    {
        bool hasUrl = !string.IsNullOrWhiteSpace(url);
        _WebhookUrlRow.Visibility = hasUrl ? Visibility.Visible : Visibility.Collapsed;
        if (hasUrl) _WebhookUrlBox.Text = url!;
    }

    private void ApplyTunnelBanner(bool running, string? nameOrHint)
    {
        bool starting = nameOrHint == "(starting…)";
        _TunnelPrereqStatusText.Text = starting ? "Starting…" : (running ? "Running" : "Not running");
        _TunnelPrereqHint.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        // The URL row is driven by the sourceTunnel connection (Name field), not here.
    }
    
    
    internal void EditForwardUrls_Click(object sender, RoutedEventArgs e)
    {
        var urls = _WebhookForwardUrlsBox.Text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _ForwardUrlsMultiBox.Text = string.Join(Environment.NewLine, urls);

        _ForwardUrlsPopup.IsOpen = true;
        _ForwardUrlsMultiBox.Focus();

        var text = _ForwardUrlsMultiBox.Text;
        if (!string.IsNullOrWhiteSpace(text) && !text.EndsWith(Environment.NewLine))
            _ForwardUrlsMultiBox.Text = text + Environment.NewLine;

        _ForwardUrlsMultiBox.CaretIndex = _ForwardUrlsMultiBox.Text.Length;
    }

    internal void ForwardUrlsApply_Click(object sender, RoutedEventArgs e)
    {
        var urls = _ForwardUrlsMultiBox.Text
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(u => !string.IsNullOrWhiteSpace(u));
        _WebhookForwardUrlsBox.Text = string.Join(", ", urls);
        _ForwardUrlsPopup.IsOpen = false;
    }

    internal void ForwardUrlsCancel_Click(object sender, RoutedEventArgs e)
    {
        _ForwardUrlsPopup.IsOpen = false;
    }
}