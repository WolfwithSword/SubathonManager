using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;
using SubathonManager.Data;
using SubathonManager.UI.Services;
using SubathonManager.UI.UiUtils;

namespace SubathonManager.UI.Views.SettingsViews.External;

public partial class PatreonSettings : DevTunnelSettingsControl {
    private readonly ILogger? _logger = AppServices.Provider.GetService<ILogger<PatreonSettings>>();

    public PatreonSettings() {
        InitializeComponent();
        UiHelpers.AttachMoneyPointRateHint(PerUnitBox2, PerUnitRateHint);
        Loaded += (_, _) => {
            IntegrationEvents.ConnectionUpdated += UpdateStatus;
            IntegrationEvents.MembershipTiersSynced += OnTiersSynced;
            RegisterUnsavedChangeHandlers();
            RefreshFromStoredState();
        };
        Unloaded += (_, _) => {
            IntegrationEvents.ConnectionUpdated -= UpdateStatus;
            IntegrationEvents.MembershipTiersSynced -= OnTiersSynced;
        };
    }

    protected override StackPanel? _MembershipsPanel => MembershipsPanel;
    protected override TextBox? _DefaultMembershipSecondsBox => SubDTextBox;
    protected override TextBox? _DefaultMembershipPointsBox => SubDTextBox2;
    protected override ComboBox? _MembershipTierCombo => SimTierSelection;
    protected override SubathonEventType? _membershipEventType => SubathonEventType.PatreonPledge;
    protected override bool allowMembershipDelete => false;
    protected override string? _PerUnitMembershipMeta => Utils.PerUnitMeta;

    protected override TextBox _WebhookUrlBox => WebhookUrlBox;
    protected override TextBlock _WebhookStatusText => WebhookStatusText;
    protected override SubathonEventSource _EventSource => SubathonEventSource.Patreon;
    protected override StackPanel _WebhookUrlRow => WebhookUrlRow;
    protected override TextBlock _TunnelPrereqStatusText => TunnelPrereqStatusText;
    protected override Button _TunnelPrereqHint => TunnelPrereqHint;
    protected override TextBox? _WebhookForwardUrlsBox => null;
    protected override Popup? _ForwardUrlsPopup => null;
    protected override TextBox? _ForwardUrlsMultiBox => null;
    protected override Button? _ConnectBtn => ConnectBtn;

    public override void Init(SettingsView host) {
        Host = host;
        RegisterUnsavedChangeHandlers();
    }

    internal override void UpdateStatus(IntegrationConnection? conn) {
        if (conn == null) return;
        base.UpdateStatus(conn);
        if (conn.Source != SubathonEventSource.Patreon) return;
        Dispatcher.UIThread.Post(() => {
            DisconnBtn.IsVisible = conn.Configured;
            RecreateWebhookBtn.IsVisible = conn.Configured;
        });
    }

    protected internal override void LoadValues(AppDbContext db) {
        SuppressUnsavedChanges(() => {
            var config = AppServices.Provider.GetRequiredService<IConfig>();
            AsDonationBox.IsChecked = config.GetBool(nameof(SubathonEventSource.Patreon),
                $"{SubathonEventType.PatreonPledge}.CommissionAsDonation");
            ByAmountBox.IsChecked = config.GetBool(nameof(SubathonEventSource.Patreon), 
                $"{SubathonEventType.PatreonPledge}.ValueByAmount");
            LoadMembershipValues(db);
        });
    }

    public override bool UpdateValueSettings(AppDbContext db) {
        bool hasUpdated = SaveMembershipValues(db);
        SubathonValue? perUnit = db.SubathonValues.FirstOrDefault(v =>
            v.EventType == SubathonEventType.PatreonPledge && v.Meta == Utils.PerUnitMeta);
        if (perUnit != null && double.TryParse(PerUnitBox.Text, out double seconds) &&
            !seconds.Equals(perUnit.Seconds)) {
            perUnit.Seconds = seconds;
            hasUpdated = true;
        }

        if (perUnit != null && double.TryParse(PerUnitBox2.Text, out double points) &&
            !points.Equals(perUnit.Points)) {
            perUnit.Points = points;
            hasUpdated = true;
        }

        return hasUpdated;
    }

    protected internal override bool UpdateConfigValueSettings() {
        var config = AppServices.Provider.GetRequiredService<IConfig>();
        var hasUpdated = false;
        hasUpdated |= config.SetBool(nameof(SubathonEventSource.Patreon), 
            $"{SubathonEventType.PatreonPledge}.CommissionAsDonation",
            AsDonationBox.IsChecked == true);
        hasUpdated |= config.SetBool(nameof(SubathonEventSource.Patreon), 
            $"{SubathonEventType.PatreonPledge}.ValueByAmount",
            ByAmountBox.IsChecked == true);
        return hasUpdated;
    }

    protected override void OnMembershipRowsLoaded() {
        ApplyValueMode();
    }

    private void ByAmount_Changed(object? sender, RoutedEventArgs e) {
        ApplyValueMode();
    }

    private void ApplyValueMode() {
        bool byAmount = ByAmountBox.IsChecked == true;
        PerUnitBox.IsEnabled = byAmount;
        PerUnitBox2.IsEnabled = byAmount;
        SubDTextBox.IsEnabled = !byAmount;
        SubDTextBox2.IsEnabled = !byAmount;
        foreach (DynamicSubRow row in _dynamicSubRows) {
            row.TimeBox.IsEnabled = !byAmount;
            row.PointsBox.IsEnabled = !byAmount;
        }
    }

    public override void UpdateCurrencyBoxes(List<string> currencies, string selected) {
    }

    public override (string seconds, string points, TextBox? timeBox, TextBox? pointsBox) GetValueBoxes(
        SubathonValue val) {
        return val.Meta == Utils.PerUnitMeta
            ? ($"{val.Seconds}", $"{val.Points}", PerUnitBox, PerUnitBox2)
            : ($"{val.Seconds}", $"{val.Points}", null, null);
    }

    internal override void AddMembership_Click(object? sender, RoutedEventArgs e) {
    }

    private void OnTiersSynced(SubathonEventSource source, IReadOnlyCollection<string> tierNames) {
        if (source == SubathonEventSource.Patreon) SyncMembershipTiers(tierNames);
    }

    private async void Connect_Click(object? sender, RoutedEventArgs e) {
        try {
            await ServiceManager.Patreon.ConnectAsync();
        }
        catch (Exception ex) {
            _logger?.LogError(ex, "Failed to connect Patreon");
        }
    }

    private async void Disconnect_Click(object? sender, RoutedEventArgs e) {
        try {
            await ServiceManager.Patreon.DisconnectAsync();
        }
        catch (Exception ex) {
            _logger?.LogError(ex, "Failed to disconnect Patreon");
        }
    }

    private async void RecreateWebhook_Click(object? sender, RoutedEventArgs e) {
        try {
            await ServiceManager.Patreon.RecreateWebhookAsync();
        }
        catch (Exception ex) {
            _logger?.LogError(ex, "Failed to recreate the Patreon webhook");
        }
    }

    private void ManageWebhooks_Click(object? sender, RoutedEventArgs e) {
        try {
            Process.Start(new ProcessStartInfo {
                FileName = "https://www.patreon.com/portal/registration/register-webhooks",
                UseShellExecute = true
            });
        }
        catch {
            /**/
        }
    }

    private void TestSub_Click(object? sender, RoutedEventArgs e) {
        string tier = (SimTierSelection.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "DEFAULT";
        bool annual = string.Equals((SimDuration.SelectedItem as ComboBoxItem)?.Content?.ToString(), "Annual",
            StringComparison.OrdinalIgnoreCase);
        ServiceManager.Patreon.SimulateMembership(tier, annual);
    }
}
