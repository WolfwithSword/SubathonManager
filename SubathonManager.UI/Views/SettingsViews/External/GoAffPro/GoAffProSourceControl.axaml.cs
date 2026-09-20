using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;
using SubathonManager.Data;
using SubathonManager.UI.Services;

namespace SubathonManager.UI.Views.SettingsViews.External.GoAffPro;

public partial class GoAffProSourceControl : SettingsControl {
    private readonly SettingsView _host;
    private readonly ILogger? _logger = AppServices.Provider.GetRequiredService<ILogger<GoAffProSettings>>();

    public GoAffProSourceControl() : this(null!, new GoAffProStore()) {
    }

    public GoAffProSourceControl(SettingsView host, GoAffProStore store) {
        _host = host;
        Store = store;
        InitializeComponent();

        StoreNameText.Text = Store.StoreName;

        ToolTip.SetTip(TotalSimBox, "Order Total $");
        ToolTip.SetTip(CommSimBox, "Commission Total $");
        ToolTip.SetTip(QuantitySimBox, "Items Ordered");
        SuppressUnsavedChanges(() => WireControl(SourcePanel));
    }

    public GoAffProStore Store { get; }

    private string Meta => Store.SiteId.ToString();

    public void UpdateStatus(bool status, string currencyName) {
        Dispatcher.UIThread.Post(() => {
            _host.UpdateConnectionStatus(status, StatusText, null);
            CurrencyText.Text = string.IsNullOrWhiteSpace(currencyName)
                ? string.Empty
                : $"[{currencyName}]";
        });
    }

    public void LoadValues(AppDbContext db, IConfig config, string configSection) {
        SubathonValue? value = db.SubathonValues.AsNoTracking()
            .FirstOrDefault(v => v.EventType == SubathonEventType.GoAffProOrder && v.Meta == Meta);

        if (value != null)
            _host.UpdateTimePointsBoxes(SecondsBox, PointsBox, $"{value.Seconds}", $"{value.Points}");

        ModeBox.ItemsSource = Enum.GetNames<OrderTypeModes>().ToList();
        ModeBox.SelectedItem = $"{config.GetOrderTypeMode(configSection, Store.InternalName, OrderTypeModes.Dollar)}";

        CommissionBox.IsChecked = config.GetBool(configSection, $"{Store.InternalName}.CommissionAsDonation");
        EnabledBox.IsChecked = config.GetBool(configSection, $"{Store.InternalName}.Enabled", true);
    }

    internal override void UpdateStatus(IntegrationConnection? connection) {
    }

    public override bool UpdateValueSettings(AppDbContext db) {
        var hasUpdated = false;
        SubathonValue? value = db.SubathonValues.FirstOrDefault(x =>
            x.EventType == SubathonEventType.GoAffProOrder && x.Meta == Meta);
        if (value == null) {
            value = new SubathonValue { EventType = SubathonEventType.GoAffProOrder, Meta = Meta, Seconds = 12 };
            db.SubathonValues.Add(value);
            hasUpdated = true;
        }

        if (double.TryParse(SecondsBox.Text, out double seconds) && !value.Seconds.Equals(seconds)) {
            value.Seconds = seconds;
            hasUpdated = true;
        }

        if (double.TryParse(PointsBox.Text, out double points) && !value.Points.Equals(points)) {
            value.Points = points;
            hasUpdated = true;
        }

        return hasUpdated;
    }

    public override void UpdateCurrencyBoxes(List<string> currencies, string selected) {
    }

    public override (string seconds, string points, TextBox? timeBox, TextBox? pointsBox) GetValueBoxes(
        SubathonValue val) {
        return ("", "", null, null);
    }

    public bool UpdateConfigSettings(IConfig config, string configSection) {
        var hasUpdated = false;
        if (!Enum.TryParse<OrderTypeModes>($"{ModeBox.SelectedItem}", out OrderTypeModes mode))
            mode = OrderTypeModes.Dollar;
        hasUpdated |= config.SetOrderTypeMode(configSection, Store.InternalName, mode);
        hasUpdated |= config.SetBool(configSection, $"{Store.InternalName}.CommissionAsDonation",
            CommissionBox.IsChecked ?? false);
        hasUpdated |= config.SetBool(configSection, $"{Store.InternalName}.Enabled", EnabledBox.IsChecked ?? true);
        return hasUpdated;
    }

    public void SimulateOrder() {
        decimal total = decimal.TryParse(TotalSimBox.Text, out decimal r) ? r : 0;
        decimal commTotal = decimal.TryParse(CommSimBox.Text, out decimal r2) ? r2 : 0;
        int itemCount = int.TryParse(QuantitySimBox.Text, out int r3) ? r3 : 0;
        string currency = (CurrencyText.Text ?? "").Replace("[", "").Replace("]", "");
        if (string.IsNullOrWhiteSpace(currency)) currency = "USD";

        ServiceManager.GoAffPro.SimulateOrder(total, itemCount, commTotal, Store, currency);
    }

    private void TestOrder_Click(object? sender, RoutedEventArgs e) {
        SimulateOrder();
    }
}