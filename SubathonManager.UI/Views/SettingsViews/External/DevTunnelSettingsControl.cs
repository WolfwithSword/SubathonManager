using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Objects;
using SubathonManager.UI.UiUtils;
// ReSharper disable InconsistentNaming

namespace SubathonManager.UI.Views.SettingsViews.External;

public abstract class DevTunnelSettingsControl : SettingsControl
{
    protected abstract TextBox _WebhookUrlBox { get; }
    protected abstract TextBlock _WebhookStatusText { get; }
    protected abstract SubathonEventSource _EventSource { get; }
    protected abstract StackPanel _WebhookUrlRow { get; }
    protected abstract TextBlock _TunnelPrereqStatusText { get; }
    protected abstract Button _TunnelPrereqHint { get; }
    protected abstract TextBox? _WebhookForwardUrlsBox { get; }
    protected abstract Popup? _ForwardUrlsPopup { get; }
    protected abstract TextBox? _ForwardUrlsMultiBox { get; }
    protected abstract Button? _ConnectBtn { get; }

    internal void GoToDevTunnels_Click(object? sender, RoutedEventArgs e)
    {
        SettingsEvents.RaiseHotLinkToDevTunnelsRequest();
    }

    internal void RefreshFromStoredState()
    {
        UpdateStatus(Utils.GetConnection(SubathonEventSource.DevTunnels, "Tunnel"));
        UpdateStatus(Utils.GetConnection(_EventSource, $"{_EventSource}"));
    }

    internal async void CopyWebhookUrl_Click(object? sender, RoutedEventArgs e)
    {
        var url = _WebhookUrlBox.Text;
        if (string.IsNullOrWhiteSpace(url)) return;
        var result = await UiHelpers.TrySetClipboardTextAsync(url);
        if (!result) return;
        if (sender is not Button btn) return;
        var original = btn.Content;
        btn.Content = "Copied!";
        await Task.Delay(1500);
        btn.Content = original;
    }

    internal override void UpdateStatus(IntegrationConnection? connection)
    {
        if (connection == null) return;

        Dispatcher.UIThread.Post(() =>
        {
            if (connection.Source == _EventSource)
            {
                Host.UpdateConnectionStatus(connection.Status, _WebhookStatusText, _ConnectBtn);
                ApplyWebhookUrl(connection.Name);
            }

            if (connection is { Source: SubathonEventSource.DevTunnels, Service: "Tunnel" })
                ApplyTunnelBanner(connection.Status, connection.Name);
        });
    }

    private void ApplyWebhookUrl(string? url)
    {
        bool hasUrl = !string.IsNullOrWhiteSpace(url);
        _WebhookUrlRow.IsVisible = hasUrl;
        if (hasUrl) _WebhookUrlBox.Text = url!;
    }

    private void ApplyTunnelBanner(bool running, string? nameOrHint)
    {
        bool starting = nameOrHint == "(starting...)";
        _TunnelPrereqStatusText.Text = starting ? "Starting..." : (running ? "Running" : "Not running");
        _TunnelPrereqHint.IsVisible = !running;
    }

    internal void EditForwardUrls_Click(object? sender, RoutedEventArgs e)
    {
        if (_WebhookForwardUrlsBox == null || _ForwardUrlsPopup == null || _ForwardUrlsMultiBox == null) return;
        var urls = (_WebhookForwardUrlsBox.Text ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _ForwardUrlsMultiBox.Text = string.Join(Environment.NewLine, urls);

        _ForwardUrlsPopup.IsOpen = true;
        _ForwardUrlsMultiBox.Focus();

        var text = _ForwardUrlsMultiBox.Text;
        if (!string.IsNullOrWhiteSpace(text) && !text.EndsWith(Environment.NewLine))
            _ForwardUrlsMultiBox.Text = text + Environment.NewLine;

        _ForwardUrlsMultiBox.CaretIndex = (_ForwardUrlsMultiBox.Text ?? "").Length;
    }

    internal void ForwardUrlsApply_Click(object? sender, RoutedEventArgs e)
    {
        if (_WebhookForwardUrlsBox == null || _ForwardUrlsPopup == null || _ForwardUrlsMultiBox == null) return;
        var urls = (_ForwardUrlsMultiBox.Text ?? "")
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(u => !string.IsNullOrWhiteSpace(u));
        _WebhookForwardUrlsBox.Text = string.Join(", ", urls);
        _ForwardUrlsPopup.IsOpen = false;
    }

    internal void ForwardUrlsCancel_Click(object? sender, RoutedEventArgs e)
    {
        if (_WebhookForwardUrlsBox == null || _ForwardUrlsPopup == null || _ForwardUrlsMultiBox == null) return;
        _ForwardUrlsPopup.IsOpen = false;
    }
}
