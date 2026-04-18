using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Events;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;
using SubathonManager.Data;
using SubathonManager.Integration;
using SubathonManager.UI.Validation;
using TextBox = System.Windows.Controls.TextBox;

namespace SubathonManager.UI.Views.SettingsViews.External;

public partial class KoFiCombinedSettings : SettingsControl
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private List<KoFiSubRow> _dynamicSubRows = new();

    private KoFiSettings? _socket;
    private KoFiWebhookSettings? _webhook;

    public KoFiCombinedSettings()
    {
        _factory = AppServices.Provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
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

        RefreshTierCombo();
    }

    protected internal override bool UpdateConfigValueSettings()
        => _webhook?.UpdateConfigValueSettings() ?? false;

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

    private KoFiSubRow AddMembershipRow(SubathonValue subathonValue)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });

        var panelRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        var nameBox = new Wpf.Ui.Controls.TextBox
        {
            Width = 154, Text = subathonValue.Meta ?? "",
            ToolTip = "Tier Name", PlaceholderText = "Tier Name",
            Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center
        };
        var secondsBox = new Wpf.Ui.Controls.TextBox
        {
            Width = 100, Text = $"{subathonValue.Seconds}", PlaceholderText = "Seconds",
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0)
        };
        var pointsBox = new Wpf.Ui.Controls.TextBox
        {
            Width = 100, Text = $"{subathonValue.Points}", PlaceholderText = "Points",
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(128, 0, 0, 0)
        };
        var deleteBtn = new Wpf.Ui.Controls.Button
        {
            ToolTip = "Delete",
            Icon = new Wpf.Ui.Controls.SymbolIcon
            {
                Symbol = Wpf.Ui.Controls.SymbolRegular.Delete24,
                Margin = new Thickness(2), HorizontalAlignment = HorizontalAlignment.Center
            },
            Foreground = System.Windows.Media.Brushes.Red,
            Cursor = System.Windows.Input.Cursors.Hand,
            Width = 36, Height = 36, Margin = new Thickness(64, 0, 0, 0)
        };

        InputValidationBehavior.SetIsDecimalOnly(secondsBox, true);
        InputValidationBehavior.SetIsDecimalOnly(pointsBox, true);

        WireControl(nameBox);
        WireControl(secondsBox);
        WireControl(pointsBox);

        panelRow.Children.Add(nameBox);
        panelRow.Children.Add(secondsBox);
        panelRow.Children.Add(pointsBox);
        panelRow.Children.Add(deleteBtn);
        row.Children.Add(panelRow);
        MembershipsPanel.Children.Add(row);

        var subRow = new KoFiSubRow
        {
            SubValue = subathonValue,
            NameBox = nameBox,
            TimeBox = secondsBox,
            PointsBox = pointsBox,
            RowGrid = row
        };
        _dynamicSubRows.Add(subRow);

        deleteBtn.Click += (_, _) => DeleteRow(subathonValue, subRow);
        return subRow;
    }

    private void DeleteRow(SubathonValue subathonValue, KoFiSubRow subRow)
    {
        using var db = _factory.CreateDbContext();
        var dbRow = db.SubathonValues.FirstOrDefault(x =>
            x.Meta == subathonValue.Meta && x.EventType == subathonValue.EventType);
        if (dbRow != null) { db.SubathonValues.Remove(dbRow); db.SaveChanges(); }
        _dynamicSubRows.Remove(subRow);
        MembershipsPanel.Children.Remove(subRow.RowGrid);
    }

    private void AddMembership_Click(object sender, RoutedEventArgs e)
    {
        var name = $"New {_dynamicSubRows.Count}";
        var allNames = _dynamicSubRows.Select(x => x.NameBox.Text.Trim()).ToArray();
        while (allNames.Contains(name)) name = $"New {name}";
        allNames = _dynamicSubRows.Select(x => x.SubValue.Meta.Trim()).ToArray();
        while (allNames.Contains(name)) name = $"New {name}";
        AddMembershipRow(new SubathonValue { EventType = SubathonEventType.KoFiSub, Meta = name, Seconds = 0, Points = 0 });
    }

    private static void EnsureUniqueName(List<KoFiSubRow> rows)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            string current = row.NameBox.Text.Trim();
            while (!seen.Add(current.ToLower()))
                current = "New " + current;
            row.NameBox.Text = current;
        }
    }

    // Test buttons

    private void TestKoFiTip_Click(object sender, RoutedEventArgs e)
    {
        var data = new Dictionary<string, JsonElement>
        {
            { "user", JsonSerializer.SerializeToElement("SYSTEM") },
            { "type", JsonSerializer.SerializeToElement(nameof(SubathonEventType.KoFiDonation)) },
            { "currency", JsonSerializer.SerializeToElement(CurrencyBox.Text) },
            { "amount", JsonSerializer.SerializeToElement(SimulateKFTipAmountBox.Text) }
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
            { "amount", JsonSerializer.SerializeToElement(SimulateKFOrderAmountBox.Text) }
        };
        ExternalEventService.ProcessExternalDonation(data);
    }

    private void TestKoFiCommission_Click(object sender, RoutedEventArgs e)
    {
        var data = new Dictionary<string, JsonElement>
        {
            { "user", JsonSerializer.SerializeToElement("SYSTEM") },
            { "type", JsonSerializer.SerializeToElement(nameof(SubathonEventType.KoFiCommissionOrder)) },
            { "currency", JsonSerializer.SerializeToElement(CommissionCurrencyBox.Text) },
            { "amount", JsonSerializer.SerializeToElement(SimulateKFCommissionAmountBox.Text) }
        };
        ExternalEventService.ProcessExternalDonation(data);
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

public class KoFiSubRow
{
    public required SubathonValue SubValue { get; set; }
    public required Wpf.Ui.Controls.TextBox NameBox { get; set; }
    public required Wpf.Ui.Controls.TextBox TimeBox { get; set; }
    public required Wpf.Ui.Controls.TextBox PointsBox { get; set; }
    public required Grid RowGrid { get; set; }
}
