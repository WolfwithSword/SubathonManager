using System.Diagnostics.CodeAnalysis;

namespace SubathonManager.Core.Enums;

public enum GoAffProSource
{
    [GoAffProSourceMeta(Description="Unknown", Enabled=false)]
    Unknown,
    [GoAffProSourceMeta(Description="GamerSupps", SiteId=165328, OrderEvent = SubathonEventType.GamerSuppsOrder)]
    GamerSupps,
    [GoAffProSourceMeta(Description="UwUMarket", SiteId=132230, OrderEvent = SubathonEventType.UwUMarketOrder)]
    UwUMarket,
    [GoAffProSourceMeta(SiteId=7142837, OrderEvent = SubathonEventType.OrchidEightOrder, Description = "Orchid Eight", Label = "Orchid Eight")]
    OrchidEight,
    [GoAffProSourceMeta(Description="KatDragonz", SiteId=7160049, OrderEvent = SubathonEventType.KatDragonzOrder, Enabled=true)]
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
    private static GoAffProSourceMetaAttribute? Meta(this GoAffProSource? value)
    {
        if (!value.HasValue) return null;
        var meta = EnumMetaCache.Get<GoAffProSourceMetaAttribute>(value);
        return meta;
    }
    
    private static readonly Lazy<Dictionary<int, GoAffProSource>> SiteIdToSource =
        new(() =>
            Enum.GetValues<GoAffProSource>()
                .Select(e => (Source: e, Meta: ((GoAffProSource?)e).Meta()))
                .Where(x => x.Meta?.SiteId > 0)
                .ToDictionary(x => x.Meta!.SiteId, x => x.Source)
            );
    
    public static bool TryGetSource(int siteId, out GoAffProSource source) =>
        SiteIdToSource.Value.TryGetValue(siteId, out source);

    public static int GetSiteId(this GoAffProSource source) =>
        ((GoAffProSource?)source).Meta()?.SiteId ?? -1;
    
    public static bool TryGetSiteId(this GoAffProSource source, out int siteId)
    {
        siteId = ((GoAffProSource?)source).Meta()?.SiteId ?? -1;
        return siteId != -1;
    }
    
    public static SubathonEventType GetOrderEvent(this GoAffProSource source) =>
        ((GoAffProSource?)source).Meta()?.OrderEvent ?? SubathonEventType.Unknown;

}