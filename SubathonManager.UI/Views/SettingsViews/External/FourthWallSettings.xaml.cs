using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Fourthwall.Client.Events;
using Fourthwall.Client.Generated.Models;
using Fourthwall.Client.Generated.Models.Openapi.Model;
using Fourthwall.Client.Generated.Models.Openapi.Model.DonationV1;
using Fourthwall.Client.Generated.Models.Openapi.Model.GiftPurchaseV1;
using Fourthwall.Client.Generated.Models.Openapi.Model.MembershipSupporterV1;
using Fourthwall.Client.Generated.Models.Openapi.Model.MembershipSupporterV1.Subscription;
using Fourthwall.Client.Generated.Models.Openapi.Model.OfferAbstractV1;
using Fourthwall.Client.Generated.Models.Openapi.Model.OfferAbstractV1.OfferVariantAbstractV1;
using Fourthwall.Client.Generated.Models.Openapi.Model.OrderV1;
using Microsoft.EntityFrameworkCore;
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
using Amounts = Fourthwall.Client.Generated.Models.Openapi.Model.DonationV1.Amounts;
using Button = Wpf.Ui.Controls.Button;
using Order = Fourthwall.Client.Generated.Models.Openapi.Model.OrderV1.Source.Order;
using PasswordBox = Wpf.Ui.Controls.PasswordBox;
using TextBlock = Wpf.Ui.Controls.TextBlock;

namespace SubathonManager.UI.Views.SettingsViews.External;

public partial class FourthWallSettings: DevTunnelSettingsControl
{
    protected override StackPanel? _MembershipsPanel => MembershipsPanel;

    protected override PasswordBox _WebhookUrlBox => WebhookUrlBox;
    protected override TextBlock _WebhookStatusText => FwWebhookStatusText;
    protected override SubathonEventSource _EventSource => SubathonEventSource.FourthWall;
    protected override StackPanel _WebhookUrlRow => WebhookUrlRow;
    protected override TextBlock _TunnelPrereqStatusText => TunnelPrereqStatusText;
    protected override Button _TunnelPrereqHint => TunnelPrereqHint;
    protected override Wpf.Ui.Controls.TextBox _WebhookForwardUrlsBox => FwWebhookForwardUrlsBox;
    protected override Popup _ForwardUrlsPopup => ForwardUrlsPopup;
    protected override Wpf.Ui.Controls.TextBox _ForwardUrlsMultiBox => ForwardUrlsMultiBox;
    protected override SubathonEventType? _membershipEventType => SubathonEventType.FourthWallMembership;
    protected override Button? _ConnectBtn => ConnectBtn;
    protected override bool allowMembershipDelete => false;

    private readonly string configSection = "FourthWall";

    public FourthWallSettings()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            IntegrationEvents.ConnectionUpdated += UpdateStatus;
            IntegrationEvents.FourthWallMembershipsSynced += SyncMemberships;
            RegisterUnsavedChangeHandlers();
            RefreshFromStoredState();
        };

        Unloaded += (_, _) =>
        {
            IntegrationEvents.ConnectionUpdated -= UpdateStatus;
            IntegrationEvents.FourthWallMembershipsSynced -= SyncMemberships;
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
        if (conn.Source != SubathonEventSource.FourthWall) return;
        Dispatcher.Invoke(() =>
            {
                DisconnBtn.Visibility = conn.Status ? Visibility.Visible : Visibility.Collapsed; 
            }
        );
    }
    
    protected internal override void LoadValues(AppDbContext db)
    {
        SuppressUnsavedChanges(() =>
        {
            LoadValuesForMemberships(db);
            LoadConfigValues();
        });
    }


    private void LoadConfigValues()
    {
        var config = AppServices.Provider.GetRequiredService<IConfig>();
        ModeBox.ItemsSource = Enum.GetNames<OrderTypeModes>().ToList();
        ModeBox.SelectedItem = config.Get(configSection, $"{SubathonEventType.FourthWallOrder}.Mode", "Dollar")?.Trim() ?? "Dollar";
        OrderCommissionBox.IsChecked = config.GetBool(configSection, $"{nameof(SubathonEventType.FourthWallOrder)?.Split("Order")[0]}.CommissionAsDonation", false);
        gModeBox.ItemsSource = Enum.GetNames<OrderTypeModes>().ToList();
        gModeBox.SelectedItem = config.Get(configSection, $"{SubathonEventType.FourthWallGiftOrder}.Mode", "Dollar")?.Trim() ?? "Dollar";
        GiftCommissionBox.IsChecked = config.GetBool(configSection, $"{nameof(SubathonEventType.FourthWallGiftOrder)?.Split("Order")[0]}.CommissionAsDonation", false);
    }
    
    public override bool UpdateValueSettings(AppDbContext db)
    {
        bool hasUpdated = false;
        var externalDonoValue = db.SubathonValues.FirstOrDefault(sv =>
            sv.EventType == SubathonEventType.FourthWallDonation
            && sv.Meta == "");
        if (externalDonoValue != null && double.TryParse(DonoBox.Text, out var exSeconds) &&
            !exSeconds.Equals(externalDonoValue.Seconds))
        {
            externalDonoValue.Seconds = exSeconds;
            hasUpdated = true;
        }

        if (externalDonoValue != null && double.TryParse(DonoBox2.Text, out var exPoints)
            && !exPoints.Equals(externalDonoValue.Points))
        {
            externalDonoValue.Points = exPoints;
            hasUpdated = true;
        }
        
        var defaultSubValue =
            db.SubathonValues.FirstOrDefault(sv =>
                sv.EventType == SubathonEventType.FourthWallMembership && sv.Meta == "DEFAULT");
        if (defaultSubValue != null && double.TryParse(FwSubDTextBox.Text, out var defaultSeconds) &&
            !defaultSeconds.Equals(defaultSubValue.Seconds))
        {
            defaultSubValue.Seconds = defaultSeconds;
            hasUpdated = true;
        }

        if (defaultSubValue != null && double.TryParse(FwSubDTextBox2.Text, out var defaultPoints) &&
            !defaultPoints.Equals(defaultSubValue.Points))
        {
            defaultSubValue.Points = defaultPoints;
            hasUpdated = true;
        }
        
        var removeRows = _dynamicSubRows
            .Where(row =>string.IsNullOrWhiteSpace(row.NameBox.Text))
            .ToList();
        if (removeRows.Any()) 
            hasUpdated = true;
        foreach (var row in removeRows)
            DeleteRow(row.SubValue, row);
        
        EnsureUniqueName(_dynamicSubRows);
        
        foreach (var subRow in _dynamicSubRows)
        {
            string meta = subRow.NameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(meta))
            {
                DeleteRow(subRow.SubValue, subRow);
                hasUpdated = true;
                continue;
            }
            if (meta == "DEFAULT" )
                continue;

            if (!double.TryParse(subRow.TimeBox.Text, out double seconds))
                seconds = 0;

            if (!double.TryParse(subRow.PointsBox.Text, out double points))
                points = 0;
            
#pragma warning disable CA1862
            var existing = db.SubathonValues
                .FirstOrDefault(sv => sv.EventType == SubathonEventType.FourthWallMembership 
                                      && sv.Meta.ToLower() == meta.ToLower());
#pragma warning restore CA1862           
            if (existing != null)
            {
                existing.Seconds = seconds;
                existing.Points = points;
                subRow.SubValue = existing;
                if (!seconds.Equals(existing.Seconds) || !points.Equals(existing.Points))
                    hasUpdated = true;
            }
            else
            {
                subRow.SubValue.Meta = meta;
                subRow.SubValue.Seconds = seconds;
                subRow.SubValue.Points = points;
                db.SubathonValues.Add(subRow.SubValue);
                hasUpdated = true;
            }
        }
        List<string> names = ["DEFAULT"];
        foreach (var row in _dynamicSubRows)
        {
            string name = row.NameBox.Text.Trim();
            names.Add(name);
        }
            
        var dbRows   = db.SubathonValues.Where(x =>
            !names.Contains(x.Meta) && x.EventType == SubathonEventType.FourthWallMembership).ToList();

        if (dbRows.Count > 0)
        {
            db.SubathonValues.RemoveRange(dbRows);
            hasUpdated = true;
        }
        
        var orderVal = db.SubathonValues.FirstOrDefault(x => x.EventType == SubathonEventType.FourthWallOrder && x.Meta == "");
        if (orderVal != null && double.TryParse(ShopOrderBox.Text, out var osec) && !orderVal.Seconds.Equals(osec))
        {
            orderVal.Seconds = osec;
            hasUpdated = true;
        }
        if (orderVal != null && double.TryParse(ShopOrderBox2.Text, out var opts) && !orderVal.Points.Equals(opts))
        {
            orderVal.Points = opts;
            hasUpdated = true;
        }
        
        var value = db.SubathonValues.FirstOrDefault(x => x.EventType == SubathonEventType.FourthWallGiftOrder && x.Meta == "");
        if (value != null && double.TryParse(gShopOrderBox.Text, out var gosec) && !value.Seconds.Equals(gosec))
        {
            value.Seconds = gosec;
            hasUpdated = true;
        }
        if (value != null && double.TryParse(gShopOrderBox2.Text, out var gpts) && !value.Points.Equals(gpts))
        {
            value.Points = gpts;
            hasUpdated = true;
        }
        return hasUpdated;
    }
    
    protected internal override bool UpdateConfigValueSettings()
    {
        var config = AppServices.Provider.GetRequiredService<IConfig>();
        bool hasUpdated = false;
        hasUpdated |= config.Set(configSection, $"{SubathonEventType.FourthWallGiftOrder}.Mode", $"{gModeBox.SelectedItem}");
        hasUpdated |= config.SetBool(configSection, $"{nameof(SubathonEventType.FourthWallGiftOrder)?.Split("Order")[0]}.CommissionAsDonation", GiftCommissionBox.IsChecked ?? false);
        hasUpdated |= config.Set(configSection, $"{SubathonEventType.FourthWallOrder}.Mode", $"{ModeBox.SelectedItem}");
        hasUpdated |= config.SetBool(configSection, $"{nameof(SubathonEventType.FourthWallOrder)?.Split("Order")[0]}.CommissionAsDonation", OrderCommissionBox.IsChecked ?? false);
        return hasUpdated;
    }

    public override void UpdateCurrencyBoxes(List<string> currencies, string selected)
    {
        CurrencyBox.ItemsSource = currencies;
        CurrencyBox.SelectedItem = selected;
        gOrderCurrencyBox.ItemsSource = currencies;
        gOrderCurrencyBox.SelectedItem = selected;
        OrderCurrencyBox.ItemsSource = currencies;
        OrderCurrencyBox.SelectedItem = selected;
    }

    public override (string seconds, string points, TextBox? timeBox, TextBox? pointsBox) GetValueBoxes(SubathonValue val)
    {
        string v = $"{val.Seconds}";
        string p = $"{val.Points}";
        TextBox? box = null;
        TextBox? box2 = null;
        switch (val.EventType)
        {
            case SubathonEventType.FourthWallDonation:
                box = DonoBox;
                box2 = DonoBox2;
                break;
            case SubathonEventType.FourthWallOrder:
                box = ShopOrderBox;
                box2 = ShopOrderBox2;
                break;
            case SubathonEventType.FourthWallGiftOrder:
                box = gShopOrderBox;
                box2 = gShopOrderBox2;
                break;
        }
        return (v, p, box, box2);
    }

    private void TestFwSub_Click(object sender, RoutedEventArgs e)
    {
        var duration = MembershipTierVariantV1_interval.MONTHLY;
        if (string.Equals(SimMembershipDuration.Text, "ANNUAL", StringComparison.OrdinalIgnoreCase))
        {
            duration = MembershipTierVariantV1_interval.ANNUAL;
        }
        
        var subEvent = new MembershipSupporterV1();
        subEvent.CreatedAt = DateTimeOffset.Now;
        subEvent.Id = Guid.NewGuid().ToString();
        subEvent.Nickname = "SYSTEM";
        subEvent.Subscription = new MembershipSupporterV1.MembershipSupporterV1_subscription();
        subEvent.Subscription.Active = new Active();
        subEvent.Subscription.Active.Variant = new MembershipTierVariantV1();
        var money = new Money();
        money.Value = 5.00;
        money.Currency = "USD";
        subEvent.Subscription.Active.Variant.Amount = money;
        subEvent.Subscription.Active.Variant.Interval = duration;
        subEvent.Subscription.Active.Variant.TierId = SimFwTierSelection.Text;

        var service = AppServices.Provider.GetService<FourthWallService>();
        if (service == null) return;

        var result = service.MembershipNames.FirstOrDefault(x =>
            string.Equals(x.Value, subEvent.Subscription.Active.Variant.TierId));
        if (result.Key != null) subEvent.Subscription.Active.Variant.TierId = result.Key;

        FourthwallWebhookEvent dono = new FourthwallSubscriptionPurchasedWebhookEvent()
        {
            Data = subEvent,
            Id = Guid.NewGuid().ToString(),
            WebhookId = "",
            ShopId = "",
            Type = "",
            ApiVersion = "",
            CreatedAt = DateTimeOffset.Now,
            TestMode = false
        };
        var ev = AppServices.Provider.GetService<FourthWallService>()?.MapToSubathonEvent(dono);
        if (ev != null)
        {
            SubathonEvents.RaiseSubathonEventCreated(ev);
        }
    }

    private void TestFwShopGiftOrder_Click(object sender, RoutedEventArgs e)
    {
        
        var oEvent = new GiftPurchaseV1();
        oEvent.Gifts = new List<GiftPurchaseV1.GiftPurchaseV1_gifts>();
        oEvent.Offer = new OfferGiftPurchaseV1();
        
        if (!int.TryParse(gOrderQuantitySimBox.Text, out var quantity)) return;
        if (quantity < 1) return;
        oEvent.Quantity = quantity;

        if (!double.TryParse(gOrderTotalSimBox.Text, out var subtotal)) return;
        oEvent.Amounts = new Fourthwall.Client.Generated.Models.Openapi.Model.GiftPurchaseV1.Amounts();
        oEvent.Amounts.Subtotal = new Money();
        oEvent.Amounts.Subtotal.Value = subtotal;
        oEvent.Amounts.Subtotal.Currency = gOrderCurrencyBox.Text;
        
        oEvent.Amounts.Profit = new Money();
        oEvent.Amounts.Profit.Value = subtotal * 0.67;
        oEvent.Amounts.Profit.Currency = gOrderCurrencyBox.Text;
        
        oEvent.Amounts.Tax = new Money();
        oEvent.Amounts.Tax.Currency = gOrderCurrencyBox.Text;
        oEvent.Amounts.Tax.Value = (subtotal * 0.10);
        
        oEvent.CreatedAt = DateTimeOffset.Now;
        oEvent.Id = Guid.NewGuid().ToString();
        oEvent.Message = "SIMULATED";
        oEvent.Username = "SYSTEM";
        
        FourthwallWebhookEvent dono = new FourthwallGiftPurchaseWebhookEvent()
        {
            Data = oEvent,
            Id = Guid.NewGuid().ToString(),
            WebhookId = "",
            ShopId = "",
            Type = "",
            ApiVersion = "",
            CreatedAt = DateTimeOffset.Now,
            TestMode = false
        };
        var ev = AppServices.Provider.GetService<FourthWallService>()?.MapToSubathonEvent(dono);
        if (ev != null)
        {
            SubathonEvents.RaiseSubathonEventCreated(ev);
        }
    }

    private void TestFwShopOrder_Click(object sender, RoutedEventArgs e)
    {
        var oEvent = new OrderV1();
        oEvent.Offers = new List<OfferOrderV1>();
        var item = new OfferOrderV1();
        item.Variant = new OfferVariantWithQuantityV1();
        if (!int.TryParse(OrderQuantitySimBox.Text, out var quantity)) return;
        if (quantity < 1) return;
        item.Variant.Quantity = quantity;
        if (!double.TryParse(OrderTotalSimBox.Text, out var subtotal)) return;
        item.Variant.Price = new Money();
        item.Variant.Price.Currency = OrderCurrencyBox.Text;
        item.Variant.Price.Value = subtotal;
        item.Variant.UnitPrice = new Money();
        item.Variant.UnitPrice.Currency = OrderCurrencyBox.Text;
        item.Variant.UnitPrice.Value = subtotal / quantity;
        
        item.Variant.Cost = new Money();
        item.Variant.Cost.Currency = OrderCurrencyBox.Text;
        item.Variant.Cost.Value = subtotal * 0.67;        
        item.Variant.UnitCost = new Money();
        item.Variant.UnitCost.Currency = OrderCurrencyBox.Text;
        item.Variant.UnitCost.Value = (subtotal * 0.67) / quantity;
        
        oEvent.Offers.Add(item);

        oEvent.Status = OrderV1_status.CONFIRMED;
        oEvent.Amounts = new OrderAmounts();
        oEvent.Amounts.Subtotal = item.Variant.Price;
        oEvent.Amounts.Total = new Money();
        oEvent.Amounts.Total.Currency = OrderCurrencyBox.Text;
        oEvent.Amounts.Total.Value = (subtotal * 1.10) + 10;
        oEvent.Amounts.Shipping = new Money();
        oEvent.Amounts.Shipping.Value = 10;
        oEvent.Amounts.Shipping.Currency = OrderCurrencyBox.Text;
        oEvent.Amounts.Tax = new Money();
        oEvent.Amounts.Tax.Currency = OrderCurrencyBox.Text;
        oEvent.Amounts.Tax.Value = (subtotal * 0.10);

        oEvent.Source = new OrderV1.OrderV1_source();
        oEvent.Source.Order = new Order();
        oEvent.Source.Order.Type = "ORDER";
        
        oEvent.CreatedAt = DateTimeOffset.Now;
        oEvent.Id = Guid.NewGuid().ToString();
        oEvent.Message = "SIMULATED";
        oEvent.Username = "SYSTEM";
        
        FourthwallWebhookEvent dono = new FourthwallOrderPlacedWebhookEvent()
        {
            Data = oEvent,
            Id = Guid.NewGuid().ToString(),
            WebhookId = "",
            ShopId = "",
            Type = "ORDER_PLACED",
            ApiVersion = "",
            CreatedAt = DateTimeOffset.Now,
            TestMode = false
        };
        var ev = AppServices.Provider.GetService<FourthWallService>()?.MapToSubathonEvent(dono);
        if (ev != null)
        {
            SubathonEvents.RaiseSubathonEventCreated(ev);
        }
    }

    private void TestFwTip_Click(object sender, RoutedEventArgs e)
    {
        var donoEvent = new DonationV1();
        donoEvent.Amounts = new Amounts();
        donoEvent.Amounts.Total = new Money();

        if (double.TryParse(SimulateFwTipAmountBox.Text, out var tipAmt))
        {
            donoEvent.Amounts.Total.Value = tipAmt;
        }
        else return;
        donoEvent.Amounts.Total.Currency = CurrencyBox.Text;
        donoEvent.CreatedAt = DateTimeOffset.Now;
        donoEvent.Id = Guid.NewGuid().ToString();
        donoEvent.Message = "SIMULATED";
        donoEvent.Username = "SYSTEM";
        
        FourthwallWebhookEvent dono = new FourthwallDonationWebhookEvent
        {
            Data = donoEvent,
            Id = Guid.NewGuid().ToString(),
            WebhookId = "",
            ShopId = "",
            Type = "DONATION",
            ApiVersion = "",
            CreatedAt = DateTimeOffset.Now,
            TestMode = false
        };
        var ev = AppServices.Provider.GetService<FourthWallService>()?.MapToSubathonEvent(dono);
        if (ev != null)
        {
            SubathonEvents.RaiseSubathonEventCreated(ev);
        }
    }

    private async void ConnectFw_Click(object sender, RoutedEventArgs e)
    {
        await ServiceManager.FourthWall.Initialize();
    }

    private async void DisconnectFw_Click(object sender, RoutedEventArgs e)
    {
        await ServiceManager.FourthWall.StopAsync();
        ServiceManager.FourthWall.RevokeTokenFile();
    }

    private void SyncMemberships(Dictionary<string, string> memberships)
    {
        var names = memberships.Values.ToList();
        var db = _factory.CreateDbContext();
        var existing = db.SubathonValues.Where(v => names.Contains(v.Meta)).Select(v => v.Meta).ToList();
        var newValues = new List<SubathonValue>();
        foreach (var tier in names.Where(x => !existing.Contains(x)))
        {
            var val = new SubathonValue { Meta = tier, Seconds = 0, Points = 0, EventType = SubathonEventType.FourthWallMembership};
            newValues.Add(val);
        }

        if (newValues.Any())
        {
            db.SubathonValues.AddRange(newValues);
            db.SaveChanges();
            Dispatcher.Invoke(() => SuppressUnsavedChanges( () => LoadValuesForMemberships(null)));
        }
    }

    internal override void AddMembership_Click(object sender, RoutedEventArgs e)
    { return; }
    
    private void LoadValuesForMemberships(AppDbContext? db)
    {
        db ??= _factory.CreateDbContext();
        var values = db.SubathonValues.Where(v => v.EventType == SubathonEventType.FourthWallMembership)
            .OrderBy(meta => meta)
            .AsNoTracking().ToList();

        for (int i = MembershipsPanel.Children.Count - 1; i >= 0; i--)
        {
            var child = MembershipsPanel.Children[i];

            if (child is FrameworkElement fe && fe.Name != "DefaultMember" && fe.Name != "AddBtn")
            {
                MembershipsPanel.Children.RemoveAt(i);
            }
        }
        _dynamicSubRows.Clear();
        foreach (var value in values)
        {
            TextBox? box1 = null;
            TextBox? box2 = null;
            var v = $"{value.Seconds}";
            var p = $"{value.Points}";
            
            if (value is { Meta: "DEFAULT", EventType: SubathonEventType.FourthWallMembership })
            {
                box1 = FwSubDTextBox;
                box2 = FwSubDTextBox2;
            }
            else if (value.EventType == SubathonEventType.FourthWallMembership)
            {
                var row = AddMembershipRow(value);
            }

            if (!string.IsNullOrWhiteSpace(v) && !string.IsNullOrWhiteSpace(p) && box1 != null && box2 != null)
            {
                Host.UpdateTimePointsBoxes(box1, box2, v, p);
            }
        }

        RefreshTierCombo();
    }
    
    private void RefreshTierCombo()
    {
        string selectedTier = (SimFwTierSelection.SelectedItem is ComboBoxItem item) 
            ? item.Content?.ToString() ?? "" 
            : "";       
        using var db = _factory.CreateDbContext();

        var metas = db.SubathonValues
            .Where(v => v.EventType == SubathonEventType.FourthWallMembership)
            .Select(v => v.Meta)
            .Where(meta => meta != "DEFAULT" && !string.IsNullOrWhiteSpace(meta))
            .Distinct()
            .OrderBy(meta => meta)
            .AsNoTracking()
            .ToList();

        SimFwTierSelection.Items.Clear();
        SimFwTierSelection.Items.Add(new ComboBoxItem{Content = "DEFAULT"});
        foreach (var meta in metas)
            SimFwTierSelection.Items.Add(new ComboBoxItem { Content = meta });

        foreach (var comboItem in SimFwTierSelection.Items)
        {
            if (comboItem is not ComboBoxItem cbi || !string.Equals(cbi.Content?.ToString(), selectedTier,
                    StringComparison.OrdinalIgnoreCase)) continue;
            SimFwTierSelection.SelectedItem = cbi;
            break;
        }

        SimFwTierSelection.SelectedItem ??= SimFwTierSelection.Items[0];
    }
}