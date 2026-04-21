using System.Diagnostics;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;
using SubathonManager.Data;
using SubathonManager.Integration;
using TextBox = System.Windows.Controls.TextBox;

namespace SubathonManager.UI.Views.SettingsViews.External.KoFi;

public partial class KoFiWebhookSettings : SettingsControl
{
    private const string ConfigSection = "KoFi";

    public KoFiWebhookSettings()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            IntegrationEvents.ConnectionUpdated += UpdateStatus;
            RegisterUnsavedChangeHandlers();
            RefreshFromStoredState();
        };

        Unloaded += (_, _) =>
        {
            IntegrationEvents.ConnectionUpdated -= UpdateStatus;
        };
    }

    // Called on every Loaded (incl. re-visits after switching tabs) so the UI
    // always reflects the latest stored state without relying on in-memory service
    // properties or a live event that may have fired while this control was detached.
    private void RefreshFromStoredState()
    {
        // KoFiService owns its own connection entry (Status + public URL in Name).
        UpdateStatus(Utils.GetConnection(SubathonEventSource.KoFiTunnel,
            nameof(SubathonEventSource.KoFiTunnel)));

        // DevTunnels prereq banner: read the last-known tunnel state from the shared dict.
        UpdateStatus(Utils.GetConnection(SubathonEventSource.DevTunnels, "Tunnel"));
    }

    // ISettingsControl

    internal override void UpdateStatus(IntegrationConnection? connection)
    {
        if (connection == null) return;

        Dispatcher.Invoke(() =>
        {
            // KoFiService composes and owns the full public URL; we just display Name.
            if (connection.Source == SubathonEventSource.KoFiTunnel)
            {
                Host.UpdateConnectionStatus(connection.Status, KoFiWebhookStatusText, null);
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
        WebhookUrlRow.Visibility = hasUrl ? Visibility.Visible : Visibility.Collapsed;
        if (hasUrl) WebhookUrlBox.Text = url!;
    }

    private void ApplyTunnelBanner(bool running, string? nameOrHint)
    {
        bool starting = nameOrHint == "(starting…)";
        TunnelPrereqStatusText.Text = starting ? "Starting…" : (running ? "Running" : "Not running");
        TunnelPrereqHint.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        // The URL row is driven by the KoFiTunnel connection (Name field), not here.
    }

    // Config persistence

    protected internal override void LoadValues(AppDbContext db)
    {
        SuppressUnsavedChanges(() =>
        {
            var config = AppServices.Provider.GetRequiredService<IConfig>();
            KoFiWebhookTokenBox.Text = config.GetFromEncoded(ConfigSection, "VerificationToken", string.Empty) ?? string.Empty;
            KoFiWebhookForwardUrlsBox.Text = config.Get(ConfigSection, "ForwardUrls", string.Empty) ?? string.Empty;
        });
    }

    protected internal override bool UpdateConfigValueSettings()
    {
        var config = AppServices.Provider.GetRequiredService<IConfig>();
        bool hasUpdated = false;
        hasUpdated |= config.SetEncoded(ConfigSection, "VerificationToken", KoFiWebhookTokenBox.Password.Trim());
        hasUpdated |= config.Set(ConfigSection, "ForwardUrls", KoFiWebhookForwardUrlsBox.Text.Trim());

        if (hasUpdated)
        {
            // Restart KoFiService so it picks up the new token and (if enabled) triggers
            // tunnel startup on demand. Fire-and-forget is fine here; the service
            // broadcasts its own status events when it finishes starting.
            _ = RestartKoFiAsync();
        }

        return hasUpdated;
    }

    private static async Task RestartKoFiAsync()
    {
        var sm = AppServices.Provider.GetRequiredService<Services.ServiceManager>();
        await sm.StopAsync<KoFiService>();
        await sm.StartAsync<KoFiService>();
    }

    public override bool UpdateValueSettings(AppDbContext db) => false;
    public override void UpdateCurrencyBoxes(List<string> currencies, string selected) { }
    public override (string, string, TextBox?, TextBox?) GetValueBoxes(SubathonValue val) => ("", "", null, null);

    // Clipboard

    private async void CopyWebhookUrl_Click(object sender, RoutedEventArgs e)
    {
        var url = WebhookUrlBox.Password;
        if (string.IsNullOrWhiteSpace(url)) return;
        var result = await UiUtils.UiUtils.TrySetClipboardTextAsync(url);
        if (!result) return;
        var btn = (sender as Wpf.Ui.Controls.Button)!;
        var original = btn.Content;
        btn.Content = "Copied!";
        await Task.Delay(1500);
        btn.Content = original;
    }

    private void GoToDevTunnels_Click(object sender, RoutedEventArgs e)
    {
        SettingsEvents.RaiseHotLinkToDevTunnelsRequest();
    }

    private void OpenKoFiTokenLink_Click(object sender, RoutedEventArgs e)
    {        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://ko-fi.com/manage/webhooks",
                UseShellExecute = true
            });
        }
        catch { /**/ }
        
    }
    
    private void EditForwardUrls_Click(object sender, RoutedEventArgs e)
    {
        var urls = KoFiWebhookForwardUrlsBox.Text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        ForwardUrlsMultiBox.Text = string.Join(Environment.NewLine, urls);

        ForwardUrlsPopup.IsOpen = true;
        ForwardUrlsMultiBox.Focus();

        var text = ForwardUrlsMultiBox.Text;
        if (!string.IsNullOrWhiteSpace(text) && !text.EndsWith(Environment.NewLine))
            ForwardUrlsMultiBox.Text = text + Environment.NewLine;

        ForwardUrlsMultiBox.CaretIndex = ForwardUrlsMultiBox.Text.Length;
    }

    private void ForwardUrlsApply_Click(object sender, RoutedEventArgs e)
    {
        var urls = ForwardUrlsMultiBox.Text
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(u => !string.IsNullOrWhiteSpace(u));
        KoFiWebhookForwardUrlsBox.Text = string.Join(", ", urls);
        ForwardUrlsPopup.IsOpen = false;
    }

    private void ForwardUrlsCancel_Click(object sender, RoutedEventArgs e)
    {
        ForwardUrlsPopup.IsOpen = false;
    }
}
