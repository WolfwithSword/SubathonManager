// using System.Diagnostics.CodeAnalysis;
// using SubathonManager.Core.Enums;
// using SubathonManager.Core.Models;
//
// namespace SubathonManager.Core;
//
// public static class OrderEventHelper
// {
//     public static List<OrderEventOption> GetOrderEventOptions()
//     {
//         var options = new List<OrderEventOption>();
//     
//         foreach (var t in SubathonEventSubTypeHelper.OrderEventTypes
//                      .Where(e => e != SubathonEventType.GoAffProOrder && e.IsEnabled()))
//             options.Add(new OrderEventOption(t));
//     
//         foreach (var store in GoAffProStoreRegistry.All().Where(s => s.Enabled))
//             options.Add(new OrderEventOption(store));
//     
//         return options;
//     }
// }
//
// public static class GoAffProStoreRegistry
// {
//     private static readonly Dictionary<int, GoAffProStore> BySiteId = new();
//     private static readonly Dictionary<string, GoAffProStore> ByInternalName = new();
//     private static readonly object Lock = new();
//
//     public static event Action<GoAffProStore>? StoreDiscovered;
//     
//     public static void Initialize(IEnumerable<GoAffProStore> stores)
//     {
//         lock (Lock)
//         {
//             BySiteId.Clear();
//             ByInternalName.Clear();
//             foreach (var store in stores)
//                 Register(store);
//         }
//     }
//
//     public static void Register(GoAffProStore store)
//     {
//         lock (Lock)
//         {
//             BySiteId[store.SiteId] = store;
//             ByInternalName[store.InternalName] = store;
//         }
//     }
//
//     public static bool TryGetBySiteId(int siteId, [NotNullWhen(true)] out GoAffProStore? store)
//     {
//         lock (Lock) { return BySiteId.TryGetValue(siteId, out store); }
//     }
//
//     public static bool TryGetByInternalName(string name, [NotNullWhen(true)] out GoAffProStore? store)
//     {
//         lock (Lock) { return ByInternalName.TryGetValue(name, out store); }
//     }
//
//     public static IReadOnlyList<GoAffProStore> All()
//     {
//         lock (Lock) { return BySiteId.Values.OrderBy(s => s.RowId).ToList(); }
//     }
//
//     public static GoAffProStore GetOrProvision(int siteId, string fallbackName = "")
//     {
//         lock (Lock)
//         {
//             if (BySiteId.TryGetValue(siteId, out var existing))
//                 return existing;
//
//             var store = new GoAffProStore
//             {
//                 SiteId = siteId,
//                 StoreName = string.IsNullOrWhiteSpace(fallbackName)
//                     ? $"Unknown Store ({siteId})"
//                     : fallbackName,
//                 EventName = string.IsNullOrWhiteSpace(fallbackName)
//                     ? $"Unknown Store ({siteId}) Order"
//                     : $"{fallbackName} Order",
//                 Enabled = true
//             };
//
//             Register(store);
//             StoreDiscovered?.Invoke(store);
//             return store;
//         }
//     }
// }
//
// public static class GoAffProOrderHelper
// {
//     public static bool TryParseMeta(string meta, out int siteId) =>
//         int.TryParse(meta, out siteId);
//     
//     public static bool TryGetStore(string meta, [NotNullWhen(true)] out GoAffProStore? store)
//     {
//         store = null;
//         return TryParseMeta(meta, out var siteId) && GoAffProStoreRegistry.TryGetBySiteId(siteId, out store);
//     }
//
//     public static string GetOrderLabel(string meta)
//     {
//         if (!TryGetStore(meta, out var store))
//             return $"GoAffPro Order ({meta})";
//         return store.EventName;
//     }
//
//     public static string GetLabel(string meta)
//     {
//         if (!TryGetStore(meta, out var store))
//             return $"GoAffPro Store ({meta})";
//         return store.StoreName;
//     }
//     
//     public static string GetOrderKey(SubathonEventType eventType, string? meta = null)
//     {
//         if (eventType != SubathonEventType.GoAffProOrder)
//             return eventType.ToString();
//
//         if (string.IsNullOrEmpty(meta))
//             return "GoAffProOrder";
//
//         if (TryGetStore(meta, out var store))
//             return store.InternalEventName;
//
//         return $"GoAffProOrder_{meta}";
//     }
//     
//     public static bool TryGetStoreByOrderKey(string key, [NotNullWhen(true)] out GoAffProStore? store)
//     {
//         store = GoAffProStoreRegistry.All()
//             .FirstOrDefault(s => s.InternalEventName == key);
//         return store != null;
//     }
//     
//     public static string GetOrderEventDisplayLabel(SubathonEventType eventType, string? meta = null)
//     {
//         if (eventType != SubathonEventType.GoAffProOrder)
//             return eventType.GetLabel();
//         return string.IsNullOrEmpty(meta)
//             ? "GoAffPro Order"
//             : GetOrderLabel(meta);
//     }    
//     
//     public static string GetOrderEventDisplayDescription(SubathonEventType eventType, string? meta = null)
//     {
//         if (eventType != SubathonEventType.GoAffProOrder)
//             return eventType.GetDescription();
//         return string.IsNullOrEmpty(meta)
//             ? "GoAffPro Order"
//             : GetOrderLabel(meta);
//     }
// }
//
// public class OrderEventOption
// {
//     public SubathonEventType EventType { get; }
//     public string? Meta { get; }
//     public string Key { get; }
//     
//     public string Label { get; }
//
//     public OrderEventOption(SubathonEventType eventType)
//     {
//         EventType = eventType;
//         Meta = null;
//         Key = eventType.ToString();
//         Label = eventType.GetLabel();
//     }
//
//     public OrderEventOption(GoAffProStore store)
//     {
//         EventType = SubathonEventType.GoAffProOrder;
//         Meta = store.SiteId.ToString();
//         Key = store.InternalEventName;
//         Label = store.EventName;
//     }
//
//     public override string ToString() => Label;
// }