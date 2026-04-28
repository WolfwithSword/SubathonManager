using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;
using SubathonManager.Data;
using SubathonManager.Integration;
using SubathonManager.UI.Views.SettingsViews.External.KoFi;
using TextBox = System.Windows.Controls.TextBox;

namespace SubathonManager.UI.Views.SettingsViews.External;

public partial class KoFiCombinedSettings : SettingsControl
{

    private KoFiSettings? _socket;
    private KoFiWebhookSettings? _webhook;
    private readonly SubathonEventSource _source = SubathonEventSource.KoFi;
    protected override SubathonEventType? _membershipEventType => SubathonEventType.KoFiSub; 
    protected override StackPanel? _MembershipsPanel => MembershipsPanel;

    public KoFiCombinedSettings()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            RegisterUnsavedChangeHandlers();
        };
    }

    public override void Init(SettingsView host)
    {
        base.Init(host);

        _socket = new KoFiSettings();
        _webhook = new KoFiWebhookSettings();

        _socket.Init(host);
        _webhook.Init(host);

        SocketSlot.Children.Add(_socket);
        WebhookSlot.Children.Add(_webhook);
    }

    internal override void UpdateStatus(IntegrationConnection? connection) { }

    // Toggle

    private void ConnectionType_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        bool isWebhook = WebhookRadio.IsChecked == true;
        SocketSlot.Visibility = isWebhook ? Visibility.Collapsed : Visibility.Visible;
        WebhookSlot.Visibility = isWebhook ? Visibility.Visible : Visibility.Collapsed;
    }

    // Config + DB persistence

    protected internal override void LoadValues(AppDbContext db)
    {
        SuppressUnsavedChanges(() => LoadValuesCore(db));
    }

    private void LoadValuesCore(AppDbContext db)
    {
        _webhook?.LoadValues(db);

        var config = AppServices.Provider.GetRequiredService<IConfig>();
        bool hasToken = !string.IsNullOrWhiteSpace(
            config.GetFromEncoded("KoFi", "VerificationToken", string.Empty));
        WebhookRadio.IsChecked = hasToken;
        SocketRadio.IsChecked = !hasToken;
        SocketSlot.Visibility = hasToken ? Visibility.Collapsed : Visibility.Visible;
        WebhookSlot.Visibility = hasToken ? Visibility.Visible : Visibility.Collapsed;

        var values = db.SubathonValues.Where(v => v.EventType == SubathonEventType.KoFiSub)
            .OrderBy(meta => meta)
            .AsNoTracking().ToList();

        for (int i = MembershipsPanel.Children.Count - 1; i >= 0; i--)
        {
            var child = MembershipsPanel.Children[i];
            if (child is FrameworkElement fe && fe.Name != "DefaultMember" && fe.Name != "AddBtn")
                MembershipsPanel.Children.RemoveAt(i);
        }
        _dynamicSubRows.Clear();

        foreach (var value in values)
        {
            if (value is { Meta: "DEFAULT", EventType: SubathonEventType.KoFiSub })
                Host.UpdateTimePointsBoxes(KFSubDTextBox, KFSubDTextBox2, $"{value.Seconds}", $"{value.Points}");
            else if (value.EventType == SubathonEventType.KoFiSub)
                AddMembershipRow(value);
        }

        ModeBox.ItemsSource = Enum.GetNames<OrderTypeModes>().ToList();
        ModeBox.SelectedItem = config.Get(_source.ToString(), $"{SubathonEventType.KoFiShopOrder}.Mode", "Dollar")?.Trim() ?? "Dollar";
        OrderCommissionBox.IsChecked = config.GetBool(_source.ToString(), $"{nameof(SubathonEventType.KoFiShopOrder)?.Split("Order")[0]}.CommissionAsDonation", true);
        CommCommissionBox.IsChecked = config.GetBool(_source.ToString(), $"{nameof(SubathonEventType.KoFiCommissionOrder)?.Split("Order")[0]}.CommissionAsDonation", true);
        RefreshTierCombo();
    }

    protected internal override bool UpdateConfigValueSettings()
    {
        bool hasUpdated = false;
        hasUpdated |= _webhook?.UpdateConfigValueSettings() ?? false;
        var config = AppServices.Provider.GetRequiredService<IConfig>();
        hasUpdated |= config.SetBool(_source.ToString(), $"{nameof(SubathonEventType.KoFiShopOrder)?.Split("Order")[0]}.CommissionAsDonation",
            OrderCommissionBox.IsChecked);
        hasUpdated |= config.SetBool(_source.ToString(), $"{nameof(SubathonEventType.KoFiCommissionOrder)?.Split("Order")[0]}.CommissionAsDonation",
            CommCommissionBox.IsChecked);
        hasUpdated |= config.Set(_source.ToString(), $"{SubathonEventType.KoFiShopOrder}.Mode", $"{ModeBox.SelectedItem}");
        return hasUpdated;
    }

    public override bool UpdateValueSettings(AppDbContext db)
    {
        bool hasUpdated = false;

        var tipValue = db.SubathonValues.FirstOrDefault(sv => sv.EventType == SubathonEventType.KoFiDonation);
        if (tipValue != null)
        {
            if (double.TryParse(DonoBox.Text, out var s) && !s.Equals(tipValue.Seconds)) { tipValue.Seconds = s; hasUpdated = true; }
            if (double.TryParse(DonoBox2.Text, out var p) && !p.Equals(tipValue.Points)) { tipValue.Points = p; hasUpdated = true; }
        }

        var shopValue = db.SubathonValues.FirstOrDefault(sv => sv.EventType == SubathonEventType.KoFiShopOrder);
        if (shopValue != null)
        {
            if (double.TryParse(ShopOrderBox.Text, out var s) && !s.Equals(shopValue.Seconds)) { shopValue.Seconds = s; hasUpdated = true; }
            if (double.TryParse(ShopOrderBox2.Text, out var p) && !p.Equals(shopValue.Points)) { shopValue.Points = p; hasUpdated = true; }
        }

        var commValue = db.SubathonValues.FirstOrDefault(sv => sv.EventType == SubathonEventType.KoFiCommissionOrder);
        if (commValue != null)
        {
            if (double.TryParse(CommissionBox.Text, out var s) && !s.Equals(commValue.Seconds)) { commValue.Seconds = s; hasUpdated = true; }
            if (double.TryParse(CommissionBox2.Text, out var p) && !p.Equals(commValue.Points)) { commValue.Points = p; hasUpdated = true; }
        }

        var defaultSub = db.SubathonValues.FirstOrDefault(sv =>
            sv.EventType == SubathonEventType.KoFiSub && sv.Meta == "DEFAULT");
        if (defaultSub != null)
        {
            if (double.TryParse(KFSubDTextBox.Text, out var s) && !s.Equals(defaultSub.Seconds)) { defaultSub.Seconds = s; hasUpdated = true; }
            if (double.TryParse(KFSubDTextBox2.Text, out var p) && !p.Equals(defaultSub.Points)) { defaultSub.Points = p; hasUpdated = true; }
        }

        var removeRows = _dynamicSubRows.Where(r => string.IsNullOrWhiteSpace(r.NameBox.Text)).ToList();
        if (removeRows.Count > 0) hasUpdated = true;
        foreach (var row in removeRows)
            DeleteRow(row.SubValue, row);

        EnsureUniqueName(_dynamicSubRows);

        foreach (var subRow in _dynamicSubRows)
        {
            string meta = subRow.NameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(meta)) { DeleteRow(subRow.SubValue, subRow); hasUpdated = true; continue; }
            if (meta == "DEFAULT") continue;

            if (!double.TryParse(subRow.TimeBox.Text, out double seconds)) seconds = 0;
            if (!double.TryParse(subRow.PointsBox.Text, out double points)) points = 0;

#pragma warning disable CA1862
            var existing = db.SubathonValues.FirstOrDefault(sv =>
                sv.EventType == SubathonEventType.KoFiSub && sv.Meta.ToLower() == meta.ToLower());
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
            names.Add(row.NameBox.Text.Trim());

        var stale = db.SubathonValues.Where(x =>
            !names.Contains(x.Meta) && x.EventType == SubathonEventType.KoFiSub).ToList();
        if (stale.Count > 0) { db.SubathonValues.RemoveRange(stale); hasUpdated = true; }

        return hasUpdated;
    }

    public override (string, string, TextBox?, TextBox?) GetValueBoxes(SubathonValue val)
    {
        string v = $"{val.Seconds}";
        string p = $"{val.Points}";
        return val.EventType switch
        {
            SubathonEventType.KoFiDonation => (v, p, DonoBox, DonoBox2),
            SubathonEventType.KoFiShopOrder => (v, p, ShopOrderBox, ShopOrderBox2),
            SubathonEventType.KoFiCommissionOrder => (v, p, CommissionBox, CommissionBox2),
            _ => (v, p, null, null)
        };
    }

    public override void UpdateCurrencyBoxes(List<string> currencies, string selected)
    {
        CurrencyBox.ItemsSource = currencies;
        CurrencyBox.SelectedItem = selected;
        OrderCurrencyBox.ItemsSource = currencies;
        OrderCurrencyBox.SelectedItem = selected;
        CommissionCurrencyBox.ItemsSource = currencies;
        CommissionCurrencyBox.SelectedItem = selected;
    }

    // Membership tiers

    public void RefreshTierCombo()
    {
        string selectedTier = SimKoFiTierSelection.SelectedItem is ComboBoxItem item
            ? item.Content?.ToString() ?? ""
            : "";
        using var db = _factory.CreateDbContext();

        var metas = db.SubathonValues
            .Where(v => v.EventType == SubathonEventType.KoFiSub)
            .Select(v => v.Meta)
            .Where(meta => meta != "DEFAULT" && !string.IsNullOrWhiteSpace(meta))
            .Distinct()
            .OrderBy(meta => meta)
            .AsNoTracking()
            .ToList();

        SimKoFiTierSelection.Items.Clear();
        SimKoFiTierSelection.Items.Add(new ComboBoxItem { Content = "DEFAULT" });
        foreach (var meta in metas)
            SimKoFiTierSelection.Items.Add(new ComboBoxItem { Content = meta });

        foreach (var comboItem in SimKoFiTierSelection.Items)
        {
            if (comboItem is not ComboBoxItem cbi ||
                !string.Equals(cbi.Content?.ToString(), selectedTier, StringComparison.OrdinalIgnoreCase)) continue;
            SimKoFiTierSelection.SelectedItem = cbi;
            break;
        }

        SimKoFiTierSelection.SelectedItem ??= SimKoFiTierSelection.Items[0];
    }

    // Test buttons

    private void TestKoFiTip_Click(object sender, RoutedEventArgs e)
    {
        var data = new Dictionary<string, JsonElement>
        {
            { "user", JsonSerializer.SerializeToElement("SYSTEM") },
            { "type", JsonSerializer.SerializeToElement(nameof(SubathonEventType.KoFiDonation)) },
            { "currency", JsonSerializer.SerializeToElement(CurrencyBox.Text) },
            { "amount", JsonSerializer.SerializeToElement(string.IsNullOrWhiteSpace(SimulateKFTipAmountBox.Text) ? "10.00" : SimulateKFTipAmountBox.Text) }
        };
        ExternalEventService.ProcessExternalDonation(data);
    }

    private void TestKoFiShopOrder_Click(object sender, RoutedEventArgs e)
    {
        var data = new Dictionary<string, JsonElement>
        {
            { "user", JsonSerializer.SerializeToElement("SYSTEM") },
            { "type", JsonSerializer.SerializeToElement(nameof(SubathonEventType.KoFiShopOrder)) },
            { "currency", JsonSerializer.SerializeToElement(OrderCurrencyBox.Text) },
            { "quantity", JsonSerializer.SerializeToElement(string.IsNullOrWhiteSpace(OrderQuantitySimBox.Text) ? 1: int.Parse(OrderQuantitySimBox.Text)) },
            { "amount", JsonSerializer.SerializeToElement(string.IsNullOrWhiteSpace(OrderTotalSimBox.Text) ? "10.00" : OrderTotalSimBox.Text)  }
        };
        ExternalEventService.ProcessExternalOrder(data);
    }

    private void TestKoFiCommission_Click(object sender, RoutedEventArgs e)
    {
        var data = new Dictionary<string, JsonElement>
        {
            { "user", JsonSerializer.SerializeToElement("SYSTEM") },
            { "type", JsonSerializer.SerializeToElement(nameof(SubathonEventType.KoFiCommissionOrder)) },
            { "currency", JsonSerializer.SerializeToElement(CommissionCurrencyBox.Text) },
            { "quantity", JsonSerializer.SerializeToElement("1")},
            { "amount", JsonSerializer.SerializeToElement(string.IsNullOrWhiteSpace(SimulateKFCommissionAmountBox.Text) ? "10.00" : SimulateKFCommissionAmountBox.Text) }
        };
        ExternalEventService.ProcessExternalOrder(data);
    }

    private void TestKoFiSub_Click(object sender, RoutedEventArgs e)
    {
        string selectedTier = SimKoFiTierSelection.SelectedItem is ComboBoxItem item
            ? item.Content?.ToString() ?? ""
            : "";
        var data = new Dictionary<string, JsonElement>
        {
            { "user", JsonSerializer.SerializeToElement("SYSTEM") },
            { "type", JsonSerializer.SerializeToElement(nameof(SubathonEventType.KoFiSub)) },
            { "value", JsonSerializer.SerializeToElement(selectedTier) },
            { "currency", JsonSerializer.SerializeToElement("member") }
        };
        ExternalEventService.ProcessExternalSub(data);
    }
}
