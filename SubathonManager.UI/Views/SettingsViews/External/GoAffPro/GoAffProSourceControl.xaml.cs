using System.Windows;
using System.Windows.Controls;
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

public partial class GoAffProSourceControl : SettingsControl
{
    private readonly ILogger? _logger = AppServices.Provider.GetRequiredService<ILogger<GoAffProSettings>>();
    private readonly SettingsView _host;
    public GoAffProSource Source { get; }

    public GoAffProSourceControl(SettingsView host, GoAffProSource source)
    {
        _host = host;
        Source = source;
        InitializeComponent();
    
        //SourceExpander.Header = source.ToString();
        TotalSimBox.ToolTip = "Order Total $";
        CommSimBox.ToolTip = "Commission Total $";
        QuantitySimBox.ToolTip = "Items Ordered";
        SuppressUnsavedChanges(() => WireControl(SourcePanel));
    }

    public void UpdateStatus(bool status, string currencyName)
    {
        Dispatcher.Invoke(() =>
        {
            _host.UpdateConnectionStatus(status, StatusText, null);
            CurrencyText.Text = string.IsNullOrWhiteSpace(currencyName) 
                ? string.Empty 
                : $"[{currencyName}]";
            
        });
    }

    public void LoadValues(AppDbContext db, IConfig config, string configSection)
    {
        var eventType = Source.GetOrderEvent();
        if (eventType == SubathonEventType.Unknown) return;

        var value = db.SubathonValues.AsNoTracking()
            .FirstOrDefault(v => v.EventType == eventType);

        if (value != null)
            _host.UpdateTimePointsBoxes(SecondsBox, PointsBox, $"{value.Seconds}", $"{value.Points}");

        ModeBox.ItemsSource = Enum.GetNames<OrderTypeModes>().ToList();
        ModeBox.SelectedItem = config.Get(configSection, $"{Source}.Mode", "Dollar")?.Trim() ?? "Dollar";
        CommissionBox.IsChecked = config.GetBool(configSection, $"{Source}.CommissionAsDonation", false);
        EnabledBox.IsChecked = config.GetBool(configSection, $"{Source}.Enabled", true);
        
    }

    internal override void UpdateStatus(IntegrationConnection? connection)
    {
        return;
    }

    public override bool UpdateValueSettings(AppDbContext db)
    {
        bool hasUpdated = false;
        var eventType = Source.GetOrderEvent();
        if (eventType == SubathonEventType.Unknown) return false;

        var value = db.SubathonValues.FirstOrDefault(x => x.EventType == eventType && x.Meta == "");
        if (value != null && double.TryParse(SecondsBox.Text, out var seconds) && !value.Seconds.Equals(seconds))
        {
            value.Seconds = seconds;
            hasUpdated = true;
        }
        if (value != null && double.TryParse(PointsBox.Text, out var points) && !value.Points.Equals(points))
        {
            value.Points = points;
            hasUpdated = true;
        }
        return hasUpdated;
    }

    public override void UpdateCurrencyBoxes(List<string> currencies, string selected)
    {
        return;
    }

    public override (string seconds, string points, TextBox? timeBox, TextBox? pointsBox) GetValueBoxes(SubathonValue val)
    {
        return ("", "", null, null);
    }

    public bool UpdateConfigSettings(IConfig config, string configSection)
    {
        bool hasUpdated = false;
        hasUpdated |= config.Set(configSection, $"{Source}.Mode", $"{ModeBox.SelectedItem}");
        hasUpdated |= config.SetBool(configSection, $"{Source}.CommissionAsDonation", CommissionBox.IsChecked ?? false);
        hasUpdated |= config.SetBool(configSection, $"{Source}.Enabled", EnabledBox.IsChecked ?? true);
        return hasUpdated;
    }
    

    public void SimulateOrder()
    {
        decimal total = decimal.TryParse(TotalSimBox.Text, out var r) ? r : 0;
        decimal commTotal = decimal.TryParse(CommSimBox.Text, out var r2) ? r2 : 0;
        int itemCount = int.TryParse(QuantitySimBox.Text, out var r3) ? r3 : 0;
        string currency = CurrencyText.Text.Replace("[", "").Replace("]", "");
        if (string.IsNullOrWhiteSpace(currency)) currency = "USD";

        ServiceManager.GoAffPro.SimulateOrder(total, itemCount, commTotal, Source, currency);
    }

    private void TestOrder_Click(object sender, RoutedEventArgs e) => SimulateOrder();
}