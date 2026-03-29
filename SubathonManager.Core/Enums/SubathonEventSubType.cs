namespace SubathonManager.Core.Enums;

using System.Diagnostics.CodeAnalysis;
public enum SubathonEventSubType
{
    [EnumMeta(Description="Unknown",Label="Unknown", Order = 200)]
    Unknown,
    [EnumMeta(Description="Subscriptions", Label="Subscriptions", Order=1)]
    SubLike,
    [EnumMeta(Description="Gift Subscriptions", Label="Gift Subscriptions", Order=2)]
    GiftSubLike,
    [EnumMeta(Description="Donations", Label="Donations", Order=4)]
    DonationLike,
    [EnumMeta(Description="Tokens/Bits", Label="Tokens/Bits", Order=3)]
    TokenLike,
    [EnumMeta(Description="Follows", Label="Follows", Order=5)]
    FollowLike,
    [EnumMeta(Description="Raids", Label="Raids" , Order=6)]
    RaidLike,
    [EnumMeta(Description="Hype Trains", Label="Hype Trains", Order = 7)]
    TrainLike,
    [EnumMeta(Description="Commands", Label="Commands", Order = 100)]
    CommandLike,
    [EnumMeta(Description="Store Orders", Label="Store Orders", Order=8)]
    OrderLike 
}

[ExcludeFromCodeCoverage]
public static class SubathonEventSubTypeHelper
{    
    public static readonly List<SubathonEventType> OrderEventTypes = Enum.GetValues<SubathonEventType>()
        .Where(e => e.GetSubType() == SubathonEventSubType.OrderLike && e.IsEnabled())
        .ToList();
    public static readonly List<SubathonEventType> FollowEventTypes = Enum.GetValues<SubathonEventType>()
        .Where(e => e.GetSubType() == SubathonEventSubType.FollowLike && e.IsEnabled())
        .ToList();
    public static readonly List<SubathonEventType> SubEventTypes = Enum.GetValues<SubathonEventType>()
        .Where(e => e.GetSubType() is SubathonEventSubType.SubLike or SubathonEventSubType.GiftSubLike && e.IsEnabled())
        .ToList();
    public static readonly List<SubathonEventType> TokenEventTypes = Enum.GetValues<SubathonEventType>()
        .Where(e => e.GetSubType() == SubathonEventSubType.TokenLike && e.IsEnabled())
        .ToList();
    public static readonly List<SubathonEventType> DonationEventTypes = Enum.GetValues<SubathonEventType>()
        .Where(e => e.GetSubType() == SubathonEventSubType.DonationLike && e.IsEnabled())
        .ToList();

    private static readonly SubathonEventSubType[] NotTrueEvent =
    [
        SubathonEventSubType.CommandLike,
        SubathonEventSubType.Unknown
    ];
    
    public static bool IsTrueEvent(this SubathonEventSubType? eventType) =>
        eventType.HasValue && !NotTrueEvent.Contains(eventType.Value);
    
}