using System.Diagnostics.CodeAnalysis;

namespace SubathonManager.Core.Enums;

public enum GoAffProSource
{
    Unknown,
    GamerSupps,
    UwUMarket,
    OrchidEight,
    KatDragonz
}

public enum GoAffProModes
{
    Item,
    Order,
    Dollar
}

[ExcludeFromCodeCoverage]
public static class GoAffProSourceeHelper
{
    private static readonly Dictionary<int, GoAffProSource> SiteIdToSource = new()
    {
        { 165328, GoAffProSource.GamerSupps },
        { 132230, GoAffProSource.UwUMarket },
        { 7142837, GoAffProSource.OrchidEight },
        { 7160049, GoAffProSource.KatDragonz }
    };

    // Not currently added, but identified site id's for. Will add on demand or spare time
    private static readonly List<GoAffProSource> DisabledSources =
    [
        GoAffProSource.Unknown, // always disabled
        //GoAffProSource.KatDragonz
    ];
    
    public static bool IsDisabled(this GoAffProSource source) => DisabledSources.Contains(source);
    
    private static readonly Dictionary<GoAffProSource, int> SourceToSiteId =
        SiteIdToSource.ToDictionary(kvp => kvp.Value, kvp => kvp.Key);

    public static bool TryGetSource(int siteId, out GoAffProSource source) =>
        SiteIdToSource.TryGetValue(siteId, out source);

    public static int GetSiteId(this GoAffProSource source) =>
        SourceToSiteId.GetValueOrDefault(source, -1);
    
    public static bool TryGetSiteId(this GoAffProSource source, out int siteId) =>
        SourceToSiteId.TryGetValue(source, out siteId);
    
    public static SubathonEventType GetOrderEvent(this GoAffProSource source)
    {
        return source switch
        {
            GoAffProSource.GamerSupps => SubathonEventType.GamerSuppsOrder,
            GoAffProSource.UwUMarket => SubathonEventType.UwUMarketOrder,
            GoAffProSource.OrchidEight => SubathonEventType.OrchidEightOrder,
            GoAffProSource.KatDragonz => SubathonEventType.KatDragonzOrder,
            _ => SubathonEventType.Unknown
        };
    }

}