namespace SubathonManager.Core.Enums;

using System.Diagnostics.CodeAnalysis;
public enum SubathonEventSubType
{
    Unknown,
    SubLike,
    GiftSubLike,
    DonationLike,
    TokenLike,
    FollowLike,
    RaidLike,
    TrainLike,
    CommandLike,
    SalesLike // not like merch, but sale using an affil code. In future, will add MerchLike when needed maybe?
}

[ExcludeFromCodeCoverage]
public static class SubathonEventSubTypeHelper
{
    private static readonly SubathonEventSubType[] NotTrueEvent = new[]
    {
        SubathonEventSubType.CommandLike,
        SubathonEventSubType.Unknown,
    };
    
    public static bool IsTrueEvent(this SubathonEventSubType? eventType) =>
        eventType.HasValue && !NotTrueEvent.Contains(eventType.Value);
}