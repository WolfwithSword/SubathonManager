using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Extensions.DependencyInjection;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Core.Security;
using SubathonManager.Core.Security.Interfaces;
using SubathonManager.Data;
using SubathonManager.Integration;
using Button = Wpf.Ui.Controls.Button;
using PasswordBox = Wpf.Ui.Controls.PasswordBox;
using TextBlock = Wpf.Ui.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;

namespace SubathonManager.UI.Views.SettingsViews.External.KoFi;

public partial class KoFiWebhookSettings: DevTunnelSettingsControl
{
    private const string ConfigSection = "KoFi";
    protected override PasswordBox _WebhookUrlBox => WebhookUrlBox;
    protected override SubathonEventSource _EventSource => SubathonEventSource.KoFiTunnel;
    protected override TextBlock _WebhookStatusText => KoFiWebhookStatusText;
    protected override StackPanel _WebhookUrlRow => WebhookUrlRow;
    protected override TextBlock _TunnelPrereqStatusText => TunnelPrereqStatusText;
    protected override Button _TunnelPrereqHint => TunnelPrereqHint;
    protected override Wpf.Ui.Controls.TextBox _WebhookForwardUrlsBox => KoFiWebhookForwardUrlsBox;
    protected override Wpf.Ui.Controls.TextBox _ForwardUrlsMultiBox => ForwardUrlsMultiBox;
    protected override Popup _ForwardUrlsPopup => ForwardUrlsPopup;
    protected override Button? _ConnectBtn => null;

    public KoFiWebhookSettings()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            IntegrationEvents.ConnectionUpdated += UpdateStatus;
            RegisterUnsavedChangeHandlers();
            SuppressUnsavedChanges(() =>
            {
                WireControl(KoFiWebhookTokenBox);
            });
            RefreshFromStoredState();
        };

        Unloaded += (_, _) =>
        {
            IntegrationEvents.ConnectionUpdated -= UpdateStatus;
        };
    }

    protected internal override void LoadValues(AppDbContext db)
    {
        SuppressUnsavedChanges(() =>
        {
            var config = AppServices.Provider.GetRequiredService<IConfig>();
            var secureStorage = AppServices.Provider.GetRequiredService<ISecureStorage>();
            KoFiWebhookTokenBox.Text = secureStorage.GetOrDefault(StorageKeys.KoFiVerificationToken, string.Empty) ?? string.Empty;
            KoFiWebhookForwardUrlsBox.Text = config.Get(ConfigSection, "ForwardUrls", string.Empty) ?? string.Empty;
        });
    }

    protected internal override bool UpdateConfigValueSettings()
    {
        var config = AppServices.Provider.GetRequiredService<IConfig>();
        bool hasUpdated = false;
        
        var secureStorage = AppServices.Provider.GetRequiredService<ISecureStorage>();
        hasUpdated |= secureStorage.Set(StorageKeys.KoFiVerificationToken, KoFiWebhookTokenBox.Password.Trim());
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

    private void OpenKoFiTokenLink_Click(object sender, RoutedEventArgs e)
    {    
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://ko-fi.com/manage/webhooks",
                UseShellExecute = true
            });
        }
        catch { /**/ }
        
    }
}
