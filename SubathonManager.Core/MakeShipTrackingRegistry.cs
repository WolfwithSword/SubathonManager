
using System.Globalization;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Models;

namespace SubathonManager.Core;

public static class MakeShipTrackingRegistry
{
    public const string PetitionUrlPrefix = "https://www.makeship.com/petitions/";
    public const string ProductUrlPrefix = "https://www.makeship.com/products/";
    
    private static TextInfo _textInfo = new CultureInfo("en-US",false).TextInfo;

    private static readonly Lock Lock = new();
    private static readonly Dictionary<Guid, MakeShipTracking> ById = new();

    public static event Action<MakeShipTracking>? TrackingUpdated;

    public static void Initialize(IEnumerable<MakeShipTracking> trackings)
    {
        lock (Lock)
        {
            ById.Clear();
            foreach (var tracking in trackings)
                ById[tracking.Id] = tracking;
        }
    }

    public static List<MakeShipTracking> All()
    {
        lock (Lock) { return ById.Values.ToList(); }
    }

    public static bool TryGet(Guid id, out MakeShipTracking? tracking)
    {
        lock (Lock) { return ById.TryGetValue(id, out tracking); }
    }

    public static void Upsert(MakeShipTracking tracking)
    {
        lock (Lock) { ById[tracking.Id] = tracking; }
    }

    public static void Remove(Guid id)
    {
        lock (Lock) { ById.Remove(id); }
    }

    public static void RaiseTrackingUpdated(MakeShipTracking tracking)
        => TrackingUpdated?.Invoke(tracking);

    public static MakeShipProductType ClassifyUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return MakeShipProductType.Invalid;
        url = url.Trim();
        if (url.StartsWith(PetitionUrlPrefix, StringComparison.OrdinalIgnoreCase)
            && url.Length > PetitionUrlPrefix.Length)
            return MakeShipProductType.Petition;
        if (url.StartsWith(ProductUrlPrefix, StringComparison.OrdinalIgnoreCase)
            && url.Length > ProductUrlPrefix.Length)
            return MakeShipProductType.Campaign;
        return MakeShipProductType.Invalid;
    }

    public static string GetSlug(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        var trimmed = url.Trim().Split('?')[0].Split('#')[0].TrimEnd('/');
        int idx = trimmed.LastIndexOf('/');
        return idx >= 0 && idx < trimmed.Length - 1 ? trimmed[(idx + 1)..] : "";
    }

    public static string GetDisplayNameFromSlug(string? url)
    {
        var slug = GetSlug(url);
        return string.IsNullOrEmpty(slug) ? "" : _textInfo.ToTitleCase(slug);
    }
}
