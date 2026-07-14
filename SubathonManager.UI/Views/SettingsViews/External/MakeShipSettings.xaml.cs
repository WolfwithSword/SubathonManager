using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;
using SubathonManager.Data;
using SubathonManager.UI.Services;
using TextBox = System.Windows.Controls.TextBox;

namespace SubathonManager.UI.Views.SettingsViews.External;

public partial class MakeShipSettings : SettingsControl
{
    private readonly ILogger? _logger = AppServices.Provider.GetRequiredService<ILogger<MakeShipSettings>>();
    private readonly List<TrackingRow> _trackingRows = new();

    private sealed class TrackingRow
    {
        public MakeShipTracking? Tracking { get; set; }
        public required Wpf.Ui.Controls.TextBox UrlBox { get; init; }
        public required TextBlock SalesText { get; init; }
        public required TextBlock OrdersText { get; init; }
        public required TextBlock StatusText { get; init; }
        public required Grid RowGrid { get; init; }
    }

    public MakeShipSettings()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            IntegrationEvents.ConnectionUpdated += UpdateStatus;
            MakeShipTrackingRegistry.TrackingUpdated -= OnTrackingUpdated;
            MakeShipTrackingRegistry.TrackingUpdated += OnTrackingUpdated;
            RegisterUnsavedChangeHandlers();
            UpdateStatus(Utils.GetConnection(SubathonEventSource.MakeShip, nameof(SubathonEventSource.MakeShip)));
        };
        Unloaded += (_, _) =>
        {
            IntegrationEvents.ConnectionUpdated -= UpdateStatus;
        };
    }

    public override void Init(SettingsView host)
    {
        Host = host;
        Dispatcher.Invoke(ReloadTrackingRows);
    }

    private void ReloadTrackingRows()
    {
        TrackingsPanel.Children.Clear();
        _trackingRows.Clear();
        foreach (var tracking in MakeShipTrackingRegistry.All().OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
            AddTrackingRow(tracking);
    }

    internal override void UpdateStatus(IntegrationConnection? connection)
    {
        if (connection is not { Source: SubathonEventSource.MakeShip }) return;
        Host.UpdateConnectionStatus(connection.Status, MakeShipStatusText, null);
    }

    private void OnTrackingUpdated(MakeShipTracking tracking)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var row = _trackingRows.FirstOrDefault(r => r.Tracking?.Id == tracking.Id);
            if (row == null) return;
            row.Tracking = tracking;
            RefreshRowStatus(row);
        });
    }

    private void AddTracking_Click(object sender, RoutedEventArgs e) => AddTrackingRow(null);

    private void TestMakeShip_Click(object sender, RoutedEventArgs e)
    {
        bool isPetition = (SimTypeBox.SelectedItem as ComboBoxItem)?.Content?.ToString() != "Campaign";
        if (!int.TryParse(SimCountBox.Text, out int count) || count <= 0) count = 1;
        Integration.MakeShipService.Simulate(SimNameBox.Text, isPetition, count);
    }

    private void AddTrackingRow(MakeShipTracking? tracking)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        var panelRow = new StackPanel { Orientation = Orientation.Horizontal };

        var urlBox = new Wpf.Ui.Controls.TextBox
        {
            Width = 420,
            Text = tracking?.Url ?? "",
            PlaceholderText = MakeShipTrackingRegistry.PetitionUrlPrefix + "...",
            ToolTip = "MakeShip petition or product campaign url",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };

        var deleteBtn = new Wpf.Ui.Controls.Button
        {
            ToolTip = "Delete",
            Icon = new Wpf.Ui.Controls.SymbolIcon
            {
                Symbol = Wpf.Ui.Controls.SymbolRegular.Delete24,
                Margin = new Thickness(2),
                HorizontalAlignment = HorizontalAlignment.Center
            },
            Foreground = Brushes.Red,
            Cursor = System.Windows.Input.Cursors.Hand,
            Width = 36, Height = 36,
            Margin = new Thickness(0, 0, 12, 0)
        };

        var salesText = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Width = 92,
            ToolTip = "Current sales quantity"
        };
        var ordersText = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Width = 96,
            ToolTip = "Current individual pledge/order count"
        };
        var statusText = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 260
        };

        WireControl(urlBox);

        panelRow.Children.Add(urlBox);
        panelRow.Children.Add(deleteBtn);
        panelRow.Children.Add(salesText);
        panelRow.Children.Add(ordersText);
        panelRow.Children.Add(statusText);
        row.Children.Add(panelRow);
        TrackingsPanel.Children.Add(row);

        var trackingRow = new TrackingRow
        {
            Tracking = tracking, UrlBox = urlBox,
            SalesText = salesText, OrdersText = ordersText,
            StatusText = statusText, RowGrid = row
        };
        _trackingRows.Add(trackingRow);
        RefreshRowStatus(trackingRow);

        deleteBtn.Click += (_, _) => DeleteTrackingRow(trackingRow);
    }

    private void DeleteTrackingRow(TrackingRow row)
    {
        try
        {
            if (row.Tracking != null)
            {
                MakeShipTrackingRegistry.Remove(row.Tracking.Id);
                using var db = _factory.CreateDbContext();
                var dbRow = db.MakeShipTrackings.FirstOrDefault(t => t.Id == row.Tracking.Id);
                if (dbRow != null) { db.MakeShipTrackings.Remove(dbRow); db.SaveChanges(); }
                _ = Task.Run(() => ServiceManager.MakeShip.RestartAsync());
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to delete MakeShip Tracked Product");
        }
        _trackingRows.Remove(row);
        TrackingsPanel.Children.Remove(row.RowGrid);
    }

    private void RefreshRowStatus(TrackingRow row)
    {
        var tracking = row.Tracking;
        bool tracked = tracking?.ProductType is MakeShipProductType.Petition or MakeShipProductType.Campaign;
        bool isPetition = tracking?.ProductType == MakeShipProductType.Petition;

        row.SalesText.Text = tracked ? $"Sales: {tracking!.Sales}" : "Sales: -";
        row.OrdersText.Text = tracked
            ? (isPetition ? $"Pledges: {tracking!.Orders}" : $"Orders: {tracking!.Orders}")
            : "Orders: -";

        row.SalesText.FontWeight = tracked && !isPetition ? FontWeights.Bold : FontWeights.Normal;
        row.OrdersText.FontWeight = tracked && isPetition ? FontWeights.Bold : FontWeights.Normal;

        if (tracking == null)
        {
            row.StatusText.Text = "Not saved";
            row.StatusText.Foreground = Brushes.Gray;
            return;
        }

        switch (tracking.ProductType)
        {
            case MakeShipProductType.Petition:
                row.StatusText.Text = $"Petition: {tracking.Name}";
                row.StatusText.Foreground = Brushes.Green;
                row.StatusText.ToolTip = tracking.Name;
                break;
            case MakeShipProductType.Campaign:
                row.StatusText.Text = $"Campaign: {tracking.Name}";
                row.StatusText.Foreground = Brushes.Green;
                row.StatusText.ToolTip = tracking.Name;
                break;
            case MakeShipProductType.Invalid:
                row.StatusText.Text = "Invalid URL";
                row.StatusText.Foreground = Brushes.IndianRed;
                break;
            default:
                row.StatusText.Text = "Pending...";
                row.StatusText.Foreground = Brushes.Gray;
                break;
        }
    }

    public override bool UpdateValueSettings(AppDbContext db)
    {
        bool hasUpdated = SaveValue(db, SubathonEventType.MakeShipPledge, PledgeBox, PledgeBox2);
        hasUpdated |= SaveValue(db, SubathonEventType.MakeShipOrder, OrderBox, OrderBox2);
        hasUpdated |= SaveTrackings(db);
        return hasUpdated;
    }

    private static bool SaveValue(AppDbContext db, SubathonEventType eventType, TextBox secondsBox, TextBox pointsBox)
    {
        bool hasUpdated = false;
        var value = db.SubathonValues.FirstOrDefault(sv => sv.EventType == eventType && sv.Meta == "");
        if (value == null) return false;
        if (double.TryParse(secondsBox.Text, out var seconds) && !seconds.Equals(value.Seconds))
        {
            value.Seconds = seconds;
            hasUpdated = true;
        }
        
        if (double.TryParse(pointsBox.Text, out var points) && !points.Equals(value.Points))
        {
            value.Points = points;
            hasUpdated = true;
        }
        
        return hasUpdated;
    }

    private bool SaveTrackings(AppDbContext db)
    {
        bool hasUpdated = false;
        foreach (var row in _trackingRows.Where(r => string.IsNullOrWhiteSpace(r.UrlBox.Text)).ToList())
        {
            DeleteTrackingRow(row);
            hasUpdated = true;
        }

        foreach (var row in _trackingRows)
        {
            string url = row.UrlBox.Text.Trim();

            if (row.Tracking == null)
            {
                var tracking = new MakeShipTracking
                {
                    Url = url,
                    Name = MakeShipTrackingRegistry.GetDisplayNameFromSlug(url)
                };
                db.MakeShipTrackings.Add(tracking);
                MakeShipTrackingRegistry.Upsert(tracking);
                row.Tracking = tracking;
                hasUpdated = true;
            }
            else if (!string.Equals(row.Tracking.Url, url, StringComparison.Ordinal))
            {
                var dbRow = db.MakeShipTrackings.FirstOrDefault(t => t.Id == row.Tracking.Id) ?? row.Tracking;
                dbRow.Url = url;
                dbRow.Name = MakeShipTrackingRegistry.GetDisplayNameFromSlug(url);
                dbRow.ShopifyProductId = "";
                dbRow.ProductType = MakeShipProductType.Unknown;
                dbRow.Sales = 0;
                dbRow.Orders = 0;
                row.Tracking.Url = dbRow.Url;
                row.Tracking.Name = dbRow.Name;
                row.Tracking.ShopifyProductId = "";
                row.Tracking.ProductType = MakeShipProductType.Unknown;
                row.Tracking.Sales = 0;
                row.Tracking.Orders = 0;
                MakeShipTrackingRegistry.Upsert(row.Tracking);
                hasUpdated = true;
            }

            RefreshRowStatus(row);
        }

        if (hasUpdated)
            _ = Task.Run(() => ServiceManager.MakeShip.RestartAsync());

        return hasUpdated;
    }

    public override void UpdateCurrencyBoxes(List<string> currencies, string selected) { }

    public override (string, string, TextBox?, TextBox?) GetValueBoxes(SubathonValue val)
    {
        string v = $"{val.Seconds}";
        string p = $"{val.Points}";
        return val.EventType switch
        {
            SubathonEventType.MakeShipPledge => (v, p, PledgeBox, PledgeBox2),
            SubathonEventType.MakeShipOrder => (v, p, OrderBox, OrderBox2),
            _ => (v, p, null, null)
        };
    }
}
