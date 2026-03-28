namespace SubathonManager.Core.Enums;

using System.Diagnostics.CodeAnalysis;
public enum SubathonEventSubType
{
    [EnumMeta(Description="Unknown",Label="Unknown")]
    Unknown,
    [EnumMeta(Description="Subscriptions", Label="Subscriptions")]
    SubLike,
    [EnumMeta(Description="Gift Subscriptions", Label="Gift Subscriptions")]
    GiftSubLike,
    [EnumMeta(Description="Donations", Label="Donations")]
    DonationLike,
    [EnumMeta(Description="Tokens/Bits", Label="Tokens/Bits")]
    TokenLike,
    [EnumMeta(Description="Follows", Label="Follows")]
    FollowLike,
    [EnumMeta(Description="Raids", Label="Raids")]
    RaidLike,
    [EnumMeta(Description="Hype Trains", Label="Hype Trains")]
    TrainLike,
    [EnumMeta(Description="Commands", Label="Commands")]
    CommandLike,
    [EnumMeta(Description="Store Orders", Label="Store Orders")]
    OrderLike 
}

[ExcludeFromCodeCoverage]
public static class SubathonEventSubTypeHelper
{    
    public static readonly List<SubathonEventType> OrderEventTypes = Enum.GetValues<SubathonEventType>()
        .Where(e => e.GetSubType() == SubathonEventSubType.OrderLike)
        .ToList();
    public static readonly List<SubathonEventType> FollowEventTypes = Enum.GetValues<SubathonEventType>()
        .Where(e => e.GetSubType() == SubathonEventSubType.FollowLike)
        .ToList();
    public static readonly List<SubathonEventType> SubEventTypes = Enum.GetValues<SubathonEventType>()
        .Where(e => e.GetSubType() is SubathonEventSubType.SubLike or SubathonEventSubType.GiftSubLike)
        .ToList();
    public static readonly List<SubathonEventType> TokenEventTypes = Enum.GetValues<SubathonEventType>()
        .Where(e => e.GetSubType() == SubathonEventSubType.TokenLike)
        .ToList();
    public static readonly List<SubathonEventType> DonationEventTypes = Enum.GetValues<SubathonEventType>()
        .Where(e => e.GetSubType() == SubathonEventSubType.DonationLike)
        .ToList();

    private static readonly SubathonEventSubType[] NotTrueEvent =
    [
        SubathonEventSubType.CommandLike,
        SubathonEventSubType.Unknown
    ];
    
    public static bool IsTrueEvent(this SubathonEventSubType? eventType) =>
        eventType.HasValue && !NotTrueEvent.Contains(eventType.Value);
    
}