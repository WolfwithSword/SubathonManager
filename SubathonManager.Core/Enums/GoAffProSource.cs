using System.Diagnostics.CodeAnalysis;

namespace SubathonManager.Core.Enums;

public enum GoAffProSource
{
    Unknown,
    GamerSupps,
    UwUMarket,
    OrchidEight
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
    public static SubathonEventType GetOrderEvent(this GoAffProSource source)
    {
        return source switch
        {
            GoAffProSource.GamerSupps => SubathonEventType.GamerSuppsOrder,
            GoAffProSource.UwUMarket => SubathonEventType.UwUMarketOrder,
            GoAffProSource.OrchidEight => SubathonEventType.OrchidEightOrder,
            _ => SubathonEventType.Unknown
        };
    }
}