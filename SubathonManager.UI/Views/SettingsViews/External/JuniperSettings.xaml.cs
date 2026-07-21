using System.Numerics;
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

public partial class JuniperSettings : SettingsControl
{
    private readonly ILogger? _logger = AppServices.Provider.GetRequiredService<ILogger<JuniperSettings>>();

    private sealed class RowInfo
    {
        public required TextBlock StoreText { get; init; }
    }

    public JuniperSettings()
    {
        InitializeComponent();

        TrackingRows.KeyBoxWidth = 273;
        TrackingRows.KeyPlaceholder = "https://yourstore.com/p/...";
        TrackingRows.KeyToolTip = "Product page url (https://<store>/p/<product id>)";
        TrackingRows.ShowNameBox = true;
        TrackingRows.NamePlaceholder = "Product Name";
        TrackingRows.NameToolTip = "Product name to use";
        TrackingRows.SecondsToolTip = "Seconds per unit sold. Blank = use the default above.";
        TrackingRows.PointsToolTip = "Points per unit sold. Blank = use the default above.";
        TrackingRows.WireInput = WireControl;
        TrackingRows.RowAdded += DecorateRow;
        TrackingRows.RowDeleted += OnRowDeleted;

        Loaded += (_, _) =>
        {
            IntegrationEvents.ConnectionUpdated += UpdateStatus;
            JuniperStoreRegistry.ProductUpdated -= OnProductUpdated;
            JuniperStoreRegistry.ProductUpdated += OnProductUpdated;
            RegisterUnsavedChangeHandlers();
            UpdateStatus(Utils.GetConnection(SubathonEventSource.JuniperCreates, nameof(SubathonEventSource.JuniperCreates)));
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

    private static string ProductUrl(JuniperProduct product)
        => $"https://{JuniperStoreRegistry.GetStoreName(product.StoreId)}/p/{product.ProductId}";

    private void ReloadTrackingRows()
    {
        TrackingRows.ClearRows();

        List<SubathonValue> overrides;
        using (var db = _factory.CreateDbContext())
        {
            overrides = db.SubathonValues.AsNoTracking()
                .Where(sv => sv.EventType == SubathonEventType.JuniperMerchSale)
                .Where(sv => sv.Meta != "DEFAULT" && sv.Meta != "")
                .ToList();
        }

        foreach (var product in JuniperStoreRegistry.AllProducts())
        {
            string meta = product.ProductId.ToString();
            var ov = overrides.FirstOrDefault(sv => sv.Meta == meta);
            var row = TrackingRows.AddRow(product, ProductUrl(product), product.ProductName,
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
        names.AddRange(JuniperStoreRegistry.AllProducts()
            .Select(p => p.ProductName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
        SimNameBox.ItemsSource = names;
        if (string.IsNullOrWhiteSpace(current))
            SimNameBox.SelectedIndex = 0;
        else
            SimNameBox.Text = current;
    }

    internal override void UpdateStatus(IntegrationConnection? connection)
    {
        if (connection is not { Source: SubathonEventSource.JuniperCreates } conn) return;
        if (conn.Service != nameof(SubathonEventSource.JuniperCreates)) return;
        Host.UpdateConnectionStatus(connection.Status, JuniperStatusText, null);
    }

    private void OnProductUpdated(JuniperProduct product)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var row = TrackingRows.Rows.FirstOrDefault(r =>
                (r.Item as JuniperProduct)?.ProductId == product.ProductId);
            if (row == null) return;
            row.Item = product;
            RefreshRowStatus(row);
            RefreshSimNameCombo();
        });
    }

    private void TestJuniper_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(SimCountBox.Text, out int count) || count <= 0) count = 1;
        string text = SimNameBox.Text.Trim();
        string meta = text;
        var product = JuniperStoreRegistry.AllProducts().FirstOrDefault(p =>
            string.Equals(p.ProductName, text, StringComparison.OrdinalIgnoreCase));
        if (product != null) meta = product.ProductId.ToString();
        Integration.JuniperService.Simulate(meta, count);
    }

    private void DecorateRow(TrackedValueRow row)
    {
        var storeText = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 260
        };
        row.InfoPanel.Children.Add(storeText);
        row.HostState = new RowInfo { StoreText = storeText };
        RefreshRowStatus(row);
    }

    private void OnRowDeleted(TrackedValueRow row)
    {
        if (row.Item is not JuniperProduct product) return;
        try
        {
            using var db = _factory.CreateDbContext();
            RemoveProductRecord(db, product);
            db.SaveChanges();
            _ = Task.Run(() => ServiceManager.Juniper.RestartAsync());
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to delete Juniper product");
        }
        RefreshSimNameCombo();
    }

    private static void RemoveProductRecord(AppDbContext db, JuniperProduct product)
    {
        string meta = product.ProductId.ToString();
        JuniperStoreRegistry.RemoveProduct(product.ProductId);

        var dbRow = db.JuniperProducts.FirstOrDefault(p => p.ProductId == product.ProductId);
        if (dbRow != null) db.JuniperProducts.Remove(dbRow);

        var orphanValues = db.SubathonValues
            .Where(sv => sv.EventType == SubathonEventType.JuniperMerchSale && sv.Meta == meta).ToList();
        db.SubathonValues.RemoveRange(orphanValues);

        var storeRow = db.JuniperStores.Include(s => s.Products)
            .FirstOrDefault(s => s.RowId == product.StoreId);
        if (storeRow != null && storeRow.Products.All(p => p.ProductId == product.ProductId))
        {
            db.JuniperStores.Remove(storeRow);
            JuniperStoreRegistry.RemoveStore(storeRow.RowId);
        }
    }

    private static void RefreshRowStatus(TrackedValueRow row)
    {
        if (row.HostState is not RowInfo info) return;

        if (row.Item is JuniperProduct product)
        {
            string storeName = JuniperStoreRegistry.GetStoreName(product.StoreId);
            if (product.Valid)
            {
                info.StoreText.Text = storeName;
                info.StoreText.Foreground = Brushes.Green;
                info.StoreText.ToolTip = $"{storeName}: {product.ProductId}";
            }
            else
            {
                info.StoreText.Text = $"{storeName} (error)";
                info.StoreText.Foreground = Brushes.IndianRed;
                info.StoreText.ToolTip = "Product could not be tracked";
            }
            return;
        }

        string url = row.KeyBox.Text.Trim();
        if (url.Length > 0 && !JuniperStoreRegistry.TryParseProductUrl(url, out _, out _))
        {
            info.StoreText.Text = "Invalid URL";
            info.StoreText.Foreground = Brushes.IndianRed;
            info.StoreText.ToolTip = "Must look like https://<store>/p/<product id>";
        }
        else
        {
            info.StoreText.Text = "Not saved";
            info.StoreText.Foreground = Brushes.Gray;
            info.StoreText.ToolTip = null;
        }
    }

    public override bool UpdateValueSettings(AppDbContext db)
    {
        bool hasUpdated = SaveValue(db, SubathonEventType.JuniperMerchSale, ItemBox, ItemBox2);
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

        var juniperValues = db.SubathonValues
            .Where(sv => sv.EventType == SubathonEventType.JuniperMerchSale)
            .ToList();
        var validMetas = new List<string> { "DEFAULT", "" };
        var seenIds = new HashSet<BigInteger>();

        foreach (var row in TrackingRows.Rows)
        {
            string url = row.KeyBox.Text.Trim();
            string name = (row.NameBox?.Text ?? "").Trim();
            var existing = row.Item as JuniperProduct;

            if (!JuniperStoreRegistry.TryParseProductUrl(url, out var storeName, out var productId)
                || !seenIds.Add(productId))
            {
                if (existing != null)
                {
                    RemoveProductRecord(db, existing);
                    row.Item = null;
                    trackingsChanged = true;
                }
                RefreshRowStatus(row);
                continue;
            }

            if (existing == null || existing.ProductId != productId)
            {
                if (existing != null) RemoveProductRecord(db, existing);

                var store = GetOrCreateStore(db, storeName);
                var product = new JuniperProduct
                {
                    ProductId = productId,
                    StoreId = store.RowId,
                    Store = store,
                    ProductName = string.IsNullOrWhiteSpace(name) ? $"Product {productId}" : name
                };
                db.JuniperProducts.Add(product);
                JuniperStoreRegistry.RegisterProduct(product);
                row.Item = product;
                trackingsChanged = true;
            }
            else if (!string.IsNullOrWhiteSpace(name) && !name.Equals(existing.ProductName, StringComparison.Ordinal))
            {
                existing.ProductName = name;
                var dbRow = db.JuniperProducts.FirstOrDefault(p => p.ProductId == existing.ProductId);
                if (dbRow != null) dbRow.ProductName = name;
                valuesChanged = true;
            }

            validMetas.Add(productId.ToString());
            valuesChanged |= SaveOverride(db, juniperValues, row, productId);
            RefreshRowStatus(row);
        }

        foreach (var orphan in juniperValues.Where(sv => !validMetas.Contains(sv.Meta)).ToList())
        {
            db.SubathonValues.Remove(orphan);
            valuesChanged = true;
        }

        if (trackingsChanged)
            _ = Task.Run(() => ServiceManager.Juniper.RestartAsync());

        RefreshSimNameCombo();
        return trackingsChanged || valuesChanged;
    }

    private JuniperStore GetOrCreateStore(AppDbContext db, string storeName)
    {
        var store = db.JuniperStores.Include(s => s.Products).AsEnumerable()
            .FirstOrDefault(s => string.Equals(s.StoreName, storeName, StringComparison.OrdinalIgnoreCase));
        if (store == null)
        {
            store = new JuniperStore { RowId = Guid.NewGuid(), StoreName = storeName };
            db.JuniperStores.Add(store);
            _logger?.LogInformation("[Juniper] New store '{Store}' added", storeName);
        }
        JuniperStoreRegistry.RegisterStore(store);
        return store;
    }

    private static bool SaveOverride(AppDbContext db, List<SubathonValue> juniperValues,
        TrackedValueRow row, BigInteger productId)
    {
        string meta = productId.ToString();
        var existing = juniperValues.Where(sv => sv.Meta == meta).ToList();

        string secondsText = row.SecondsBox.Text.Trim();
        string pointsText = row.PointsBox.Text.Trim();
        if (secondsText.Length == 0 && pointsText.Length == 0)
        {
            foreach (var sv in existing)
            {
                db.SubathonValues.Remove(sv);
                juniperValues.Remove(sv);
            }
            return existing.Count > 0;
        }

        double seconds = double.TryParse(secondsText, out var s) ? s : 0;
        double points = double.TryParse(pointsText, out var p) ? p : 0;

        var keep = existing.FirstOrDefault();
        if (keep == null)
        {
            keep = new SubathonValue
            {
                EventType = SubathonEventType.JuniperMerchSale, Meta = meta,
                Seconds = seconds, Points = points
            };
            db.SubathonValues.Add(keep);
            juniperValues.Add(keep);
            return true;
        }

        bool updated = false;
        if (!keep.Seconds.Equals(seconds)) { keep.Seconds = seconds; updated = true; }
        if (!keep.Points.Equals(points)) { keep.Points = points; updated = true; }
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
            SubathonEventType.JuniperMerchSale => (v, p, ItemBox, ItemBox2),
            _ => (v, p, null, null)
        };
    }
}
