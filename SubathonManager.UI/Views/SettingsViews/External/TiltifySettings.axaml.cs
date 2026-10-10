using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;
using SubathonManager.Data;
using SubathonManager.Integration;
using SubathonManager.UI.Services;
using SubathonManager.UI.UiUtils;

namespace SubathonManager.UI.Views.SettingsViews.External;

public partial class TiltifySettings : SettingsControl {
    private readonly ILogger? _logger = AppServices.Provider.GetRequiredService<ILogger<TiltifySettings>>();
    private readonly List<(TiltifyService.CampaignOption Option, CheckBox Box)> _campaignBoxes = new();
    private readonly List<TiltifyService.CampaignOption?> _testCampaigns = new();
    private bool _campaignsLoaded;
    private bool _wasConnected;

    public TiltifySettings() {
        InitializeComponent();
        SetTestCampaigns(ServiceManager.Tiltify.KnownCampaigns);
        UiHelpers.AttachMoneyPointRateHint(DonoBox2, DonoRateHint);
        Loaded += (_, _) => {
            IntegrationEvents.ConnectionUpdated += UpdateStatus;
            RegisterUnsavedChangeHandlers();
            UpdateStatus(Utils.GetConnection(SubathonEventSource.Tiltify, nameof(SubathonEventSource.Tiltify)));
        };
        Unloaded += (_, _) => { IntegrationEvents.ConnectionUpdated -= UpdateStatus; };
    }

    public override void Init(SettingsView host) {
        Host = host;
        UpdateStatus(Utils.GetConnection(SubathonEventSource.Tiltify, nameof(SubathonEventSource.Tiltify)));
    }

    internal override void UpdateStatus(IntegrationConnection? connection) {
        if (connection is not { Source: SubathonEventSource.Tiltify }) return;
        Dispatcher.UIThread.Post(() => {
            string username = connection.Status && !string.IsNullOrEmpty(connection.Name)
                ? connection.Name
                : "Disconnected";
            if (TiltifyStatusText.Text != username) TiltifyStatusText.Text = username;

            string connectLabel = connection.Status ? "Reconnect" : "Connect";
            if (ConnectBtn.Content?.ToString() != connectLabel) ConnectBtn.Content = connectLabel;

            DisconnBtn.IsVisible = connection.Status;
            ImportMissedBtn.IsVisible = connection.Status;
            RefreshCampaignsBtn.IsEnabled = connection.Status;

            if (connection.Status && !_wasConnected) _ = LoadCampaignsAsync();
            _wasConnected = connection.Status;
        });
    }

    private async Task LoadCampaignsAsync() {
        CampaignsHint.Text = "Loading...";
        List<TiltifyService.CampaignOption> options;
        try {
            options = await ServiceManager.Tiltify.GetAvailableCampaignsAsync();
        }
        catch (Exception ex) {
            _logger?.LogError(ex, "Failed to load Tiltify campaigns");
            CampaignsHint.Text = "Failed to load campaigns";
            return;
        }

        List<(Guid Id, bool IsTeam)> selected = ServiceManager.Tiltify.GetSelectedCampaigns();
        options.AddRange(selected.Where(s => options.All(o => o.Id != s.Id))
            .Select(s => new TiltifyService.CampaignOption(s.Id, $"Unavailable campaign ({s.Id})", s.IsTeam)));

        await Dispatcher.UIThread.InvokeAsync(() => SuppressUnsavedChanges(() => {
            CampaignsPanel.Children.Clear();
            _campaignBoxes.Clear();
            foreach (TiltifyService.CampaignOption option in options) {
                var box = new CheckBox {
                    Content = option.IsTeam ? $"{option.Name} (team)" : option.Name,
                    IsChecked = selected.Any(s => s.Id == option.Id && s.IsTeam == option.IsTeam)
                };
                CampaignsPanel.Children.Add(box);
                _campaignBoxes.Add((option, box));
                WireControl(box);
            }

            _campaignsLoaded = true;
            CampaignsHint.Text = options.Count == 0
                ? "No campaigns found on your Tiltify account"
                : "Donations are only tracked for checked campaigns";
            SetTestCampaigns(options);
        }));
    }

    private void SetTestCampaigns(IEnumerable<TiltifyService.CampaignOption> campaigns) {
        _testCampaigns.Clear();
        _testCampaigns.Add(null);
        _testCampaigns.AddRange(campaigns);
        TestCampaignBox.ItemsSource = _testCampaigns
            .Select(c => c == null ? "DEFAULT" : c.IsTeam ? $"{c.Name} (team)" : c.Name)
            .ToList();
        TestCampaignBox.SelectedIndex = 0;
    }

    protected internal override void LoadValues(AppDbContext db) {
    }

    public override bool UpdateValueSettings(AppDbContext db) {
        var hasUpdated = false;
        SubathonValue? donoValue = db.SubathonValues.FirstOrDefault(sv =>
            sv.EventType == SubathonEventType.TiltifyDonation && sv.Meta == "");
        if (donoValue != null) {
            if (double.TryParse(DonoBox.Text, out double s) && !s.Equals(donoValue.Seconds)) {
                donoValue.Seconds = s;
                hasUpdated = true;
            }

            if (double.TryParse(DonoBox2.Text, out double p) && !p.Equals(donoValue.Points)) {
                donoValue.Points = p;
                hasUpdated = true;
            }
        }

        return hasUpdated;
    }

    protected internal override bool UpdateConfigValueSettings() {
        if (!_campaignsLoaded) return false;
        return ServiceManager.Tiltify.SetSelectedCampaigns(
            _campaignBoxes.Where(c => c.Box.IsChecked == true).Select(c => c.Option));
    }

    public override void UpdateCurrencyBoxes(List<string> currencies, string selected) {
        CurrencyBox.ItemsSource = currencies;
        CurrencyBox.SelectedItem = selected;
    }

    public override (string, string, TextBox?, TextBox?) GetValueBoxes(SubathonValue val) {
        var v = $"{val.Seconds}";
        var p = $"{val.Points}";
        return val.EventType switch {
            SubathonEventType.TiltifyDonation => (v, p, DonoBox, DonoBox2),
            _ => (v, p, null, null)
        };
    }

    private async void Connect_Click(object? sender, RoutedEventArgs e) {
        try {
            await ServiceManager.Tiltify.ConnectAsync();
        }
        catch (Exception ex) {
            _logger?.LogError(ex, "Failed to connect Tiltify");
        }
    }

    private async void Disconnect_Click(object? sender, RoutedEventArgs e) {
        await ServiceManager.Tiltify.StopAsync();
        ServiceManager.Tiltify.RevokeTokens();
        _campaignsLoaded = false;
        _campaignBoxes.Clear();
        CampaignsPanel.Children.Clear();
        CampaignsHint.Text = "Connect to load your campaigns";
        SetTestCampaigns([]);
    }

    private void ImportMissed_Click(object? sender, RoutedEventArgs e) {
        ImportMissedWindow.Open(this, SubathonEventSource.Tiltify, ServiceManager.Tiltify);
    }

    private async void RefreshCampaigns_Click(object? sender, RoutedEventArgs e) {
        await LoadCampaignsAsync();
    }

    private void TestDonation_Click(object? sender, RoutedEventArgs e) {
        int index = TestCampaignBox.SelectedIndex;
        TiltifyService.CampaignOption? campaign =
            index > 0 && index < _testCampaigns.Count ? _testCampaigns[index] : null;
        TiltifyService.SimulateDonation(SimulateAmountBox.Text ?? "", CurrencyBox.Text ?? "", campaign);
    }
}
