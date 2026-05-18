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
using SubathonManager.Core.Objects;
using SubathonManager.Data;
using SubathonManager.Integration;
using SubathonManager.UI.Services;
using Button = Wpf.Ui.Controls.Button;
using PasswordBox = Wpf.Ui.Controls.PasswordBox;
using TextBlock = Wpf.Ui.Controls.TextBlock;

namespace SubathonManager.UI.Views.SettingsViews.External;

public partial class ThroneSettings: DevTunnelSettingsControl
{
    // TODO
    protected override StackPanel? _MembershipsPanel => null;
    protected override PasswordBox _WebhookUrlBox => WebhookUrlBox;
    protected override TextBlock _WebhookStatusText => ThroneWebhookStatusText;
    protected override SubathonEventSource _EventSource => SubathonEventSource.Throne;
    protected override StackPanel _WebhookUrlRow => WebhookUrlRow;
    protected override TextBlock _TunnelPrereqStatusText => TunnelPrereqStatusText;
    protected override Button _TunnelPrereqHint => TunnelPrereqHint;
    protected override Wpf.Ui.Controls.TextBox? _WebhookForwardUrlsBox => null;
    protected override Popup? _ForwardUrlsPopup => null;
    protected override Wpf.Ui.Controls.TextBox? _ForwardUrlsMultiBox => null;
    protected override SubathonEventType? _membershipEventType => null;
    protected override Button? _ConnectBtn => ConnectBtn;
    protected override bool allowMembershipDelete => false;

    private readonly string configSection = "Throne";
    
    public ThroneSettings()
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
    
    public override void Init(SettingsView host)
    {
        Host = host;
        RegisterUnsavedChangeHandlers();
    }
    
    internal override void UpdateStatus(IntegrationConnection? conn)
    {
        if (conn == null) return;
        base.UpdateStatus(conn);
        if (conn.Source != SubathonEventSource.Throne) return;
        Dispatcher.Invoke(() =>
            {
                DisconnBtn.Visibility = conn.Status ? Visibility.Visible : Visibility.Collapsed; 
                ConnectBtn.Visibility = conn.Status ? Visibility.Collapsed :  Visibility.Visible;
            }
        );
    }
    

    public override bool UpdateValueSettings(AppDbContext db)
    {
        bool hasUpdated = false;
        
        var contribVal = db.SubathonValues.FirstOrDefault(x => x.EventType == SubathonEventType.ThroneGiftContribution && x.Meta == "");
        if (contribVal != null && double.TryParse(ContribBox.Text, out var osec) && !contribVal.Seconds.Equals(osec))
        {
            contribVal.Seconds = osec;
            hasUpdated = true;
        }
        if (contribVal != null && double.TryParse(ContribBox2.Text, out var opts) && !contribVal.Points.Equals(opts))
        {
            contribVal.Points = opts;
            hasUpdated = true;
        }
        
        var value = db.SubathonValues.FirstOrDefault(x => x.EventType == SubathonEventType.ThroneGiftPurchase && x.Meta == "");
        if (value != null && double.TryParse(GiftsBox.Text, out var gosec) && !value.Seconds.Equals(gosec))
        {
            value.Seconds = gosec;
            hasUpdated = true;
        }
        if (value != null && double.TryParse(GiftsBox2.Text, out var gpts) && !value.Points.Equals(gpts))
        {
            value.Points = gpts;
            hasUpdated = true;
        }
        return hasUpdated;
    }

    public override void UpdateCurrencyBoxes(List<string> currencies, string selected)
    {
        CurrencyBox.ItemsSource = currencies;
        CurrencyBox.SelectedItem = selected;
    }
    
    protected internal override bool UpdateConfigValueSettings()
    {
        var config = AppServices.Provider.GetRequiredService<IConfig>();
        bool hasUpdated = false;
        hasUpdated |= config.SetOrderTypeMode(configSection, $"{SubathonEventType.ThroneGiftPurchase}",
            Enum.Parse<OrderTypeModes>($"{ModeBox.SelectedItem}"));
        return hasUpdated;
    }

    public override (string seconds, string points, TextBox? timeBox, TextBox? pointsBox) GetValueBoxes(SubathonValue val)
    {
        string v = $"{val.Seconds}";
        string p = $"{val.Points}";
        TextBox? box = null;
        TextBox? box2 = null;
        switch (val.EventType)
        {
            case SubathonEventType.ThroneGiftContribution:
                box = ContribBox;
                box2 = ContribBox2;
                break;
            case SubathonEventType.ThroneGiftPurchase:
                box = GiftsBox;
                box2 = GiftsBox2;
                break;
        }
        return (v, p, box, box2);
    }
    private void TestThroneGift_Click(object sender, RoutedEventArgs e)
    {
        var amount = 10.00;
        double.TryParse((string.IsNullOrWhiteSpace(SimulateThroneContribAmountBox.Text)
            ? "10.00"
            : SimulateThroneContribAmountBox.Text), out amount);
        amount *= 100;
        var amt = ((int)amount).ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

        var data = $@" {{
                ""event_id"": ""{Guid.NewGuid()}"",
                ""event_type"": ""gift_purchased"",
                ""data"": {{
                  ""creator_id"": ""string"",
                  ""creator_username"": ""string"",
                  ""gifter_username"": ""SYSTEM"",
                  ""message"": ""string"",
                  ""item_name"": ""{GiftNameBox.Text}"",
                  ""item_thumbnail_url"": ""string"",
                  ""is_surprise_gift"": false,
                  ""price"": {amt},
                  ""currency"": ""{CurrencyBox.Text}"" 
                }}
            }}";
        AppServices.Provider.GetService<ThroneService>()?.ProcessData(data, true);
    }

    private void TestThroneContrib_Click(object sender, RoutedEventArgs e)
    {
        var amount = 10.00;
        double.TryParse((string.IsNullOrWhiteSpace(SimulateThroneContribAmountBox.Text)
            ? "10.00"
            : SimulateThroneContribAmountBox.Text), out amount);
        amount *= 100;
        var amt = ((int)amount).ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        var data = $@" {{
                ""event_id"": ""{Guid.NewGuid()}"",
                ""event_type"": ""contribution_purchased"",
                ""data"": {{
                  ""creator_id"": ""string"",
                  ""creator_username"": ""string"",
                  ""gifter_username"": ""SYSTEM"",
                  ""message"": ""string"",
                  ""item_name"": ""{GiftNameBox.Text}"",
                  ""item_thumbnail_url"": ""string"",
                  ""amount"": {amt},
                  ""currency"": ""{CurrencyBox.Text}"" 
                }}
            }}";
        AppServices.Provider.GetService<ThroneService>()?.ProcessData(data, true);
    }
    private void TestThroneCrowdfund_Click(object sender, RoutedEventArgs e)
    {      
        var amount = 10.00;
        double.TryParse((string.IsNullOrWhiteSpace(SimulateThroneContribAmountBox.Text)
            ? "10.00"
            : SimulateThroneContribAmountBox.Text), out amount);
        amount *= 100;
        var amt = ((int)amount).ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

        var data = $@" {{
                ""event_id"": ""{Guid.NewGuid()}"",
                ""event_type"": ""gift_crowdfunded"",
                ""data"": {{
                  ""creator_id"": ""string"",
                  ""creator_username"": ""string"",
                  ""item_name"": ""{GiftNameBox.Text}"",
                  ""item_thumbnail_url"": ""string"",
                  ""is_surprise_gift"": false,
                  ""price"": {amt},
                  ""currency"": ""{CurrencyBox.Text}"" 
                }}
            }}";
        AppServices.Provider.GetService<ThroneService>()?.ProcessData(data, true);
    }
    
    protected internal override void LoadValues(AppDbContext db)
    {
        SuppressUnsavedChanges(() =>
        {
            LoadConfigValues();
        });
    }
    
    private void LoadConfigValues()
    {
        var config = AppServices.Provider.GetRequiredService<IConfig>();
        ModeBox.ItemsSource = Enum.GetNames<OrderTypeModes>().Where(x => x != $"{OrderTypeModes.Order}").ToList();
        ModeBox.SelectedItem = $"{config.GetOrderTypeMode(configSection,
            nameof(SubathonEventType.ThroneGiftPurchase), OrderTypeModes.Dollar)}";
    }

    private async void DisconnectThrone_Click(object sender, RoutedEventArgs e)
    {
        var config = AppServices.Provider.GetRequiredService<IConfig>();
        bool hasUpdated = config.SetBool(configSection, "Enabled", false);
        if (hasUpdated) config.Save();
        
        await ServiceManager.Throne.StopAsync();
    }

    private async void ConnectThrone_Click(object sender, RoutedEventArgs e)
    {
        var config = AppServices.Provider.GetRequiredService<IConfig>();
        bool hasUpdated = config.SetBool(configSection, "Enabled", true);
        if (hasUpdated) config.Save();
        
        await ServiceManager.Throne.Initialize();
    }

    private void OpenThroneLink_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://throne.com/profile/integrations/webhook",
                UseShellExecute = true
            });
        }
        catch { /**/ }
    }
}