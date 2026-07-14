using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;
using SubathonManager.Data;
using SubathonManager.UI.Services;
using SubathonManager.UI.Views.SettingsViews.Components;
using TextBox = System.Windows.Controls.TextBox;

namespace SubathonManager.UI.Views.SettingsViews.External;

public partial class MakeShipSettings : SettingsControl
{
    private readonly ILogger? _logger = AppServices.Provider.GetRequiredService<ILogger<MakeShipSettings>>();

    private sealed class RowInfo
    {
        public required TextBlock SalesText { get; init; }
        public required TextBlock OrdersText { get; init; }
        public required TextBlock StatusText { get; init; }
    }

    public MakeShipSettings()
    {
        InitializeComponent();

        TrackingRows.KeyBoxWidth = 273;
        TrackingRows.KeyPlaceholder = MakeShipTrackingRegistry.PetitionUrlPrefix + "...";
        TrackingRows.KeyToolTip = "MakeShip petition or product campaign url";
        TrackingRows.SecondsToolTip = "Seconds per pledge/sale for this item. Blank = use the default above.";
        TrackingRows.PointsToolTip = "Points per pledge/sale for this item. Blank = use the default above.";
        TrackingRows.WireInput = WireControl;
        TrackingRows.RowAdded += DecorateRow;
        TrackingRows.RowDeleted += OnRowDeleted;

        SimNameBox.SelectionChanged += SimNameBox_SelectionChanged;

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
        TrackingRows.ClearRows();

        List<SubathonValue> overrides;
        using (var db = _factory.CreateDbContext())
        {
            overrides = db.SubathonValues.AsNoTracking()
                .Where(sv => sv.EventType == SubathonEventType.MakeShipPledge ||
                             sv.EventType == SubathonEventType.MakeShipOrder)
                .Where(sv => sv.Meta != "DEFAULT" && sv.Meta != "")
                .ToList();
        }

        foreach (var tracking in MakeShipTrackingRegistry.All().OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
        {
            var ov = overrides.FirstOrDefault(sv =>
                string.Equals(sv.Meta, tracking.Name, StringComparison.OrdinalIgnoreCase));
            var row = TrackingRows.AddRow(tracking, tracking.Url,
                seconds: ov != null ? $"{ov.Seconds}" : "",
                points: ov != null ? $"{ov.Points}" : "");
            DecorateRow(row);
        }

        RefreshSimNameCombo();
    }

    private void RefreshSimNameCombo()
    {
        string current = SimNameBox.Text;
        var names = new List<string> { "DEFAULT" };
        names.AddRange(MakeShipTrackingRegistry.All()
            .Select(t => t.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
        SimNameBox.ItemsSource = names;
        if (string.IsNullOrWhiteSpace(current))
            SimNameBox.SelectedIndex = 0;
        else
            SimNameBox.Text = current;
    }

    private void SimNameBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // picking a tracked product pre-selects its type so its override value is what gets tested
        if (SimNameBox.SelectedItem is not string name) return;
        var tracking = MakeShipTrackingRegistry.All().FirstOrDefault(t =>
            string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        if (tracking == null) return;
        SimTypeBox.SelectedIndex =
            MakeShipTrackingRegistry.ClassifyUrl(tracking.Url) == MakeShipProductType.Campaign ? 1 : 0;
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
            var row = TrackingRows.Rows.FirstOrDefault(r => (r.Item as MakeShipTracking)?.Id == tracking.Id);
            if (row == null) return;
            row.Item = tracking;
            RefreshRowStatus(row);
            RefreshSimNameCombo();
        });
    }

    private void TestMakeShip_Click(object sender, RoutedEventArgs e)
    {
        bool isPetition = (SimTypeBox.SelectedItem as ComboBoxItem)?.Content?.ToString() != "Campaign";
        if (!int.TryParse(SimCountBox.Text, out int count) || count <= 0) count = 1;
        Integration.MakeShipService.Simulate(SimNameBox.Text, isPetition, count);
    }

    private void DecorateRow(TrackedValueRow row)
    {
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

        row.InfoPanel.Children.Add(salesText);
        row.InfoPanel.Children.Add(ordersText);
        row.InfoPanel.Children.Add(statusText);
        row.HostState = new RowInfo { SalesText = salesText, OrdersText = ordersText, StatusText = statusText };
        RefreshRowStatus(row);
    }

    private void OnRowDeleted(TrackedValueRow row)
    {
        if (row.Item is not MakeShipTracking tracking) return;
        try
        {
            MakeShipTrackingRegistry.Remove(tracking.Id);
            using var db = _factory.CreateDbContext();
            var dbRow = db.MakeShipTrackings.FirstOrDefault(t => t.Id == tracking.Id);
            if (dbRow != null) db.MakeShipTrackings.Remove(dbRow);
            if (!string.IsNullOrWhiteSpace(tracking.Name))
            {
                var orphans = db.SubathonValues
                    .Where(sv => sv.EventType == SubathonEventType.MakeShipPledge ||
                                 sv.EventType == SubathonEventType.MakeShipOrder)
                    .ToList()
                    .Where(sv => sv.Meta != "DEFAULT" &&
                                 string.Equals(sv.Meta, tracking.Name, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                db.SubathonValues.RemoveRange(orphans);
            }
            db.SaveChanges();
            _ = Task.Run(() => ServiceManager.MakeShip.RestartAsync());
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to delete MakeShip Tracked Product");
        }
        RefreshSimNameCombo();
    }

    private static void RefreshRowStatus(TrackedValueRow row)
    {
        if (row.HostState is not RowInfo info) return;
        var tracking = row.Item as MakeShipTracking;
        bool tracked = tracking?.ProductType is MakeShipProductType.Petition or MakeShipProductType.Campaign;
        bool isPetition = tracking?.ProductType == MakeShipProductType.Petition;

        info.SalesText.Text = tracked ? $"Sales: {tracking!.Sales}" : "Sales: -";
        info.OrdersText.Text = tracked
            ? (isPetition ? $"Pledges: {tracking!.Orders}" : $"Orders: {tracking!.Orders}")
            : "Orders: -";

        info.SalesText.FontWeight = tracked && !isPetition ? FontWeights.Bold : FontWeights.Thin;
        info.OrdersText.FontWeight = tracked && isPetition ? FontWeights.Bold : FontWeights.Thin;

        if (tracking == null)
        {
            info.StatusText.Text = "Not saved";
            info.StatusText.Foreground = Brushes.Gray;
            return;
        }

        switch (tracking.ProductType)
        {
            case MakeShipProductType.Petition:
                info.StatusText.Text = $"Petition: {tracking.Name}";
                info.StatusText.Foreground = Brushes.Green;
                info.StatusText.ToolTip = tracking.Name;
                break;
            case MakeShipProductType.Campaign:
                info.StatusText.Text = $"Campaign: {tracking.Name}";
                info.StatusText.Foreground = Brushes.Green;
                info.StatusText.ToolTip = tracking.Name;
                break;
            case MakeShipProductType.Invalid:
                info.StatusText.Text = "Invalid URL";
                info.StatusText.Foreground = Brushes.IndianRed;
                break;
            default:
                info.StatusText.Text = "Pending...";
                info.StatusText.Foreground = Brushes.Gray;
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
        var value = db.SubathonValues.FirstOrDefault(sv =>
            sv.EventType == eventType && (sv.Meta == "DEFAULT" || sv.Meta == ""));
        if (value == null) return false;
        if (value.Meta != "DEFAULT")
        {
            value.Meta = "DEFAULT";
            hasUpdated = true;
        }
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
        bool trackingsChanged = false;
        bool valuesChanged = false;

        foreach (var row in TrackingRows.Rows.Where(r => string.IsNullOrWhiteSpace(r.KeyBox.Text)).ToList())
        {
            trackingsChanged |= row.Item != null;
            TrackingRows.RemoveRow(row);
        }

        var makeShipValues = db.SubathonValues
            .Where(sv => sv.EventType == SubathonEventType.MakeShipPledge ||
                         sv.EventType == SubathonEventType.MakeShipOrder)
            .ToList();
        var validMetas = new List<string> { "DEFAULT", "" };

        foreach (var row in TrackingRows.Rows)
        {
            string url = row.KeyBox.Text.Trim();

            if (row.Item is not MakeShipTracking tracking)
            {
                tracking = new MakeShipTracking
                {
                    Url = url,
                    Name = MakeShipTrackingRegistry.GetDisplayNameFromSlug(url)
                };
                db.MakeShipTrackings.Add(tracking);
                MakeShipTrackingRegistry.Upsert(tracking);
                row.Item = tracking;
                trackingsChanged = true;
            }
            else if (!string.Equals(tracking.Url, url, StringComparison.Ordinal))
            {
                var dbRow = db.MakeShipTrackings.FirstOrDefault(t => t.Id == tracking.Id) ?? tracking;
                dbRow.Url = url;
                dbRow.Name = MakeShipTrackingRegistry.GetDisplayNameFromSlug(url);
                dbRow.ShopifyProductId = "";
                dbRow.ProductType = MakeShipProductType.Unknown;
                dbRow.Sales = 0;
                dbRow.Orders = 0;
                tracking.Url = dbRow.Url;
                tracking.Name = dbRow.Name;
                tracking.ShopifyProductId = "";
                tracking.ProductType = MakeShipProductType.Unknown;
                tracking.Sales = 0;
                tracking.Orders = 0;
                MakeShipTrackingRegistry.Upsert(tracking);
                trackingsChanged = true;
            }

            validMetas.Add(tracking.Name);
            valuesChanged |= SaveOverride(db, makeShipValues, row, tracking);
            RefreshRowStatus(row);
        }

        foreach (var orphan in makeShipValues
                     .Where(sv => !validMetas.Contains(sv.Meta, StringComparer.OrdinalIgnoreCase)).ToList())
        {
            db.SubathonValues.Remove(orphan);
            valuesChanged = true;
        }

        if (trackingsChanged)
            _ = Task.Run(() => ServiceManager.MakeShip.RestartAsync());

        RefreshSimNameCombo();
        return trackingsChanged || valuesChanged;
    }

    private static bool SaveOverride(AppDbContext db, List<SubathonValue> makeShipValues,
        TrackedValueRow row, MakeShipTracking tracking)
    {
        if (string.IsNullOrWhiteSpace(tracking.Name)) return false;

        var desiredType = MakeShipTrackingRegistry.ClassifyUrl(tracking.Url) == MakeShipProductType.Campaign
            ? SubathonEventType.MakeShipOrder
            : SubathonEventType.MakeShipPledge;

        var existing = makeShipValues
            .Where(sv => string.Equals(sv.Meta, tracking.Name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        string secondsText = row.SecondsBox.Text.Trim();
        string pointsText = row.PointsBox.Text.Trim();
        if (secondsText.Length == 0 && pointsText.Length == 0)
        {
            foreach (var sv in existing)
            {
                db.SubathonValues.Remove(sv);
                makeShipValues.Remove(sv);
            }
            return existing.Count > 0;
        }

        double seconds = double.TryParse(secondsText, out var s) ? s : 0;
        double points = double.TryParse(pointsText, out var p) ? p : 0;

        bool updated = false;
        var keep = existing.FirstOrDefault(sv => sv.EventType == desiredType);
        foreach (var sv in existing.Where(sv => !ReferenceEquals(sv, keep)))
        {
            db.SubathonValues.Remove(sv);
            makeShipValues.Remove(sv);
            updated = true;
        }

        if (keep == null)
        {
            keep = new SubathonValue
            {
                EventType = desiredType, Meta = tracking.Name,
                Seconds = seconds, Points = points
            };
            db.SubathonValues.Add(keep);
            makeShipValues.Add(keep);
            return true;
        }

        if (!keep.Seconds.Equals(seconds)) { keep.Seconds = seconds; updated = true; }
        if (!keep.Points.Equals(points)) { keep.Points = points; updated = true; }
        if (!string.Equals(keep.Meta, tracking.Name, StringComparison.Ordinal))
        {
            keep.Meta = tracking.Name;
            updated = true;
        }
        return updated;
    }

    public override void UpdateCurrencyBoxes(List<string> currencies, string selected) { }

    public override (string, string, TextBox?, TextBox?) GetValueBoxes(SubathonValue val)
    {
        string v = $"{val.Seconds}";
        string p = $"{val.Points}";
        if (val.Meta is not ("" or "DEFAULT")) return (v, p, null, null);
        return val.EventType switch
        {
            SubathonEventType.MakeShipPledge => (v, p, PledgeBox, PledgeBox2),
            SubathonEventType.MakeShipOrder => (v, p, OrderBox, OrderBox2),
            _ => (v, p, null, null)
        };
    }
}
