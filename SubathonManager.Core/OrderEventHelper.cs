using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Models;

namespace SubathonManager.Core;

public static class JuniperStoreRegistry
{
    private static readonly Dictionary<BigInteger, JuniperProduct> ByProductId = new();
    private static readonly Dictionary<Guid, JuniperStore> ByStoreId = new();
    private static readonly object Lock = new();

    public static event Action<JuniperStore>? StoreDiscovered;
    public static event Action<JuniperStore>? StoreUpdated;
    public static event Action<JuniperProduct>? ProductUpdated;

    public static void Initialize(IEnumerable<JuniperStore> stores)
    {
        lock (Lock)
        {
            ByProductId.Clear();
            ByStoreId.Clear();
            foreach (var store in stores)
                RegisterCore(store);
        }
    }

    public static void RegisterStore(JuniperStore store)
    {
        lock (Lock) { RegisterCore(store); }
    }

    private static void RegisterCore(JuniperStore store)
    {
        ByStoreId[store.RowId] = store;
        foreach (var product in store.Products)
            ByProductId[product.ProductId] = product;
    }

    public static void RegisterProduct(JuniperProduct product)
    {
        lock (Lock)
        {
            ByProductId[product.ProductId] = product;
            if (ByStoreId.TryGetValue(product.StoreId, out var store) &&
                store.Products.All(p => p.ProductId != product.ProductId))
                store.Products.Add(product);
        }
    }
    
    public static bool TryParseProductId(string? value, out BigInteger productId)
    {
        productId = default;
        return !string.IsNullOrWhiteSpace(value)
               && BigInteger.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out productId)
               && productId > 0;
    }

    public static bool TryGetProduct(BigInteger productId, [NotNullWhen(true)] out JuniperProduct? product)
    {
        lock (Lock) { return ByProductId.TryGetValue(productId, out product); }
    }

    public static bool TryGetProduct(string? productId, [NotNullWhen(true)] out JuniperProduct? product)
    {
        product = null;
        return TryParseProductId(productId, out var id) && TryGetProduct(id, out product);
    }
    
    public static List<BigInteger> AllValidProductIds()
    {
        lock (Lock)
        {
            return ByProductId.Values.Where(p => p.Valid).Select(p => p.ProductId).ToList();
        }
    }

    public static bool TryGetStore(Guid rowId, [NotNullWhen(true)] out JuniperStore? store)
    {
        lock (Lock) { return ByStoreId.TryGetValue(rowId, out store); }
    }
    
    public static string GetStoreName(Guid rowId)
    {
        return TryGetStore(rowId, out var store) ? store.StoreName : string.Empty;
    }

    public static IReadOnlyList<JuniperStore> AllStores()
    {
        lock (Lock) { return ByStoreId.Values.OrderBy(s => s.StoreName, StringComparer.OrdinalIgnoreCase).ToList(); }
    }

    public static IReadOnlyList<JuniperProduct> AllProducts()
    {
        lock (Lock) { return ByProductId.Values.OrderBy(p => p.ProductName, StringComparer.OrdinalIgnoreCase).ToList(); }
    }

    public static void RemoveProduct(BigInteger productId)
    {
        lock (Lock)
        {
            if (!ByProductId.Remove(productId, out var product)) return;
            if (ByStoreId.TryGetValue(product.StoreId, out var store))
                store.Products.RemoveAll(p => p.ProductId == productId);
        }
    }

    public static void RemoveStore(Guid rowId)
    {
        lock (Lock)
        {
            if (!ByStoreId.Remove(rowId, out var store)) return;
            foreach (var product in store.Products)
                ByProductId.Remove(product.ProductId);
        }
    }

    public static JuniperStore GetOrProvisionStore(string storeName)
    {
        JuniperStore store;
        lock (Lock)
        {
            var existing = ByStoreId.Values.FirstOrDefault(s =>
                string.Equals(s.StoreName, storeName, StringComparison.OrdinalIgnoreCase));
            if (existing != null) return existing;

            store = new JuniperStore { RowId = Guid.NewGuid(), StoreName = storeName };
            RegisterCore(store);
        }
        StoreDiscovered?.Invoke(store);
        return store;
    }

    public static void RaiseStoreUpdated(JuniperStore store) => StoreUpdated?.Invoke(store);
    public static void RaiseProductUpdated(JuniperProduct product) => ProductUpdated?.Invoke(product);

    public static bool TryParseProductUrl(string? url, out string storeName, out BigInteger productId)
    {
        storeName = "";
        productId = default;
        if (string.IsNullOrWhiteSpace(url)) return false;

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return false;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        int pIndex = Array.FindIndex(segments, s => string.Equals(s, "p", StringComparison.OrdinalIgnoreCase));
        if (pIndex < 0 || pIndex + 1 >= segments.Length) return false;
        if (!TryParseProductId(segments[pIndex + 1], out productId)) return false;

        storeName = uri.Host.ToLowerInvariant();
        return storeName.Length > 0;
    }
}

[ExcludeFromCodeCoverage]
public static class JuniperOrderHelper
{
    public static bool TryGetProduct(string? meta, [NotNullWhen(true)] out JuniperProduct? product)
    {
        product = null;
        return !string.IsNullOrWhiteSpace(meta) && JuniperStoreRegistry.TryGetProduct(meta, out product);
    }

    public static string GetProductLabel(string? meta)
    {
        if (!TryGetProduct(meta, out var product))
            return $"Juniper Product ({meta})";
        return product.ProductName;
    }

    public static string GetStoreLabel(string? meta)
    {
        if (TryGetProduct(meta, out var product))
        {
            var name = JuniperStoreRegistry.GetStoreName(product.StoreId);
            if (!string.IsNullOrWhiteSpace(name)) return name;
        }

        return $"{SubathonEventSource.JuniperCreates}";
    }
}


[ExcludeFromCodeCoverage]
public static class OrderMetaFilter
{
    public static bool Matches(SubathonEventType? eventType, string? eventMeta, string? filter)
    {
        if (string.IsNullOrEmpty(filter)) return true;
        if (string.Equals(filter, eventMeta, StringComparison.OrdinalIgnoreCase)) return true;
        if (eventType == SubathonEventType.JuniperMerchSale && Guid.TryParse(filter, out var storeId)
            && JuniperOrderHelper.TryGetProduct(eventMeta, out var product))
            return product.StoreId == storeId;
        return false;
    }

    public static int Specificity(SubathonEventType? eventType, string? eventMeta, string? filter)
    {
        if (string.IsNullOrEmpty(filter)) return 0;
        if (string.Equals(filter, eventMeta, StringComparison.OrdinalIgnoreCase)) return 2;
        return Matches(eventType, eventMeta, filter) ? 1 : -1;
    }

    public static string Describe(SubathonEventType? eventType, string? filter)
    {
        if (string.IsNullOrEmpty(filter))
        {
            return eventType switch
            {
                SubathonEventType.MakeShipPledge => "Any Pledge",
                SubathonEventType.MakeShipSale or SubathonEventType.JuniperMerchSale => "Any Sale",
                SubathonEventType.GoAffProOrder => "Any Order",
                _ => "Any"
            };
        }

        if (eventType == SubathonEventType.JuniperMerchSale)
        {
            if (Guid.TryParse(filter, out var storeId))
            {
                var storeName = JuniperStoreRegistry.TryGetStore(storeId, out var store)
                    ? store.StoreName
                    : "Unknown Store";
                return $"{storeName} - Any Sale";
            }
            return $"{JuniperOrderHelper.GetStoreLabel(filter)} - {JuniperOrderHelper.GetProductLabel(filter)}";
        }

        if (eventType == SubathonEventType.GoAffProOrder)
            return GoAffProOrderHelper.GetOrderLabel(filter);

        return filter;
    }
}

public static class GoAffProStoreRegistry
{
    private static readonly Dictionary<int, GoAffProStore> BySiteId = new();
    private static readonly Dictionary<string, GoAffProStore> ByInternalName = new();
    private static readonly HashSet<int> ActiveSiteIds = new();
    private static readonly object Lock = new();

    public static event Action<GoAffProStore>? StoreDiscovered;

    public static void Initialize(IEnumerable<GoAffProStore> stores)
    {
        lock (Lock)
        {
            BySiteId.Clear();
            ByInternalName.Clear();
            foreach (var store in stores)
                Register(store);
        }
    }

    public static void Register(GoAffProStore store)
    {
        lock (Lock)
        {
            BySiteId[store.SiteId] = store;
            ByInternalName[store.InternalName] = store;
        }
    }

    public static bool TryGetBySiteId(int siteId, [NotNullWhen(true)] out GoAffProStore? store)
    {
        lock (Lock) { return BySiteId.TryGetValue(siteId, out store); }
    }

    public static bool TryGetByInternalName(string name, [NotNullWhen(true)] out GoAffProStore? store)
    {
        lock (Lock) { return ByInternalName.TryGetValue(name, out store); }
    }

    public static IReadOnlyList<GoAffProStore> All()
    {
        lock (Lock) { return BySiteId.Values.OrderBy(s => s.RowId).ToList(); }
    }

    public static void MarkActiveOnAccount(int siteId, bool active = true)
    {
        lock (Lock)
        {
            if (active) ActiveSiteIds.Add(siteId);
            else ActiveSiteIds.Remove(siteId);
        }
    }

    public static bool IsActiveOnAccount(int siteId)
    {
        lock (Lock) { return ActiveSiteIds.Contains(siteId); }
    }

    public static GoAffProStore GetOrProvision(int siteId, string fallbackName = "")
    {
        lock (Lock)
        {
            if (BySiteId.TryGetValue(siteId, out var existing))
                return existing;

            var store = new GoAffProStore
            {
                SiteId = siteId,
                StoreName = string.IsNullOrWhiteSpace(fallbackName)
                    ? $"Unknown Store ({siteId})"
                    : fallbackName,
                EventName = string.IsNullOrWhiteSpace(fallbackName)
                    ? $"Unknown Store ({siteId}) Order"
                    : $"{fallbackName} Order",
                Enabled = true
            };

            Register(store);
            StoreDiscovered?.Invoke(store);
            return store;
        }
    }
}

[ExcludeFromCodeCoverage]
public static class GoAffProOrderHelper
{
    public static bool TryParseMeta(string? meta, out int siteId)
    {
        siteId = -1;
        return meta != null && int.TryParse(meta, out siteId);
    }

    public static bool TryGetStore(string? meta, [NotNullWhen(true)] out GoAffProStore? store)
    {
        store = null;
        return TryParseMeta(meta, out var siteId) && GoAffProStoreRegistry.TryGetBySiteId(siteId, out store);
    }

    public static string GetOrderLabel(string? meta)
    {
        if (!TryGetStore(meta, out var store))
            return $"GoAffPro Order ({meta})";
        return store.EventName;
    }

    public static string GetLabel(string? meta)
    {
        if (!TryGetStore(meta, out var store))
            return $"GoAffPro Store ({meta})";
        return store.StoreName;
    }

    public static string GetOrderKey(SubathonEventType? eventType, string? meta = null)
    {
        if (eventType != SubathonEventType.GoAffProOrder)
            return eventType?.ToString() ?? string.Empty;

        if (string.IsNullOrEmpty(meta))
            return "GoAffProOrder";

        if (TryGetStore(meta, out var store))
            return store.InternalEventName;

        return $"GoAffProOrder_{meta}";
    }

    public static bool TryGetStoreByOrderKey(string key, [NotNullWhen(true)] out GoAffProStore? store)
    {
        store = GoAffProStoreRegistry.All()
            .FirstOrDefault(s => string.Equals(s.InternalEventName, key, StringComparison.OrdinalIgnoreCase));
        return store != null;
    }

    public static string GetOrderEventDisplayLabel(SubathonEventType? eventType, string? meta = null)
    {
        if (eventType != SubathonEventType.GoAffProOrder)
            return eventType.GetLabel();
        return string.IsNullOrEmpty(meta)
            ? "GoAffPro Order"
            : GetOrderLabel(meta);
    }

    public static string GetOrderEventDisplayDescription(SubathonEventType? eventType, string? meta = null)
    {
        if (eventType != SubathonEventType.GoAffProOrder)
            return eventType?.GetDescription() ?? string.Empty;
        return string.IsNullOrEmpty(meta)
            ? "GoAffPro Order"
            : GetOrderLabel(meta);
    }
}
