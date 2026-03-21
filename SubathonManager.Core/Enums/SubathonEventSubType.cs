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
    OrderLike 
}

[ExcludeFromCodeCoverage]
public static class SubathonEventSubTypeHelper
{    
    public static readonly List<SubathonEventType> OrderEventTypes = Enum.GetValues<SubathonEventType>()
        .Where(e => ((SubathonEventType?)e).GetSubType() == SubathonEventSubType.OrderLike)
        .ToList();
    public static readonly List<SubathonEventType> FollowEventTypes = Enum.GetValues<SubathonEventType>()
        .Where(e => ((SubathonEventType?)e).GetSubType() == SubathonEventSubType.FollowLike)
        .ToList();
    public static readonly List<SubathonEventType> SubEventTypes = Enum.GetValues<SubathonEventType>()
        .Where(e => ((SubathonEventType?)e).GetSubType() is SubathonEventSubType.SubLike or SubathonEventSubType.GiftSubLike)
        .ToList();
    public static readonly List<SubathonEventType> TokenEventTypes = Enum.GetValues<SubathonEventType>()
        .Where(e => ((SubathonEventType?)e).GetSubType() == SubathonEventSubType.TokenLike)
        .ToList();
    public static readonly List<SubathonEventType> DonationEventTypes = Enum.GetValues<SubathonEventType>()
        .Where(e => ((SubathonEventType?)e).GetSubType() == SubathonEventSubType.DonationLike)
        .ToList();

    private static readonly SubathonEventSubType[] NotTrueEvent =
    [
        SubathonEventSubType.CommandLike,
        SubathonEventSubType.Unknown
    ];
    
    public static bool IsTrueEvent(this SubathonEventSubType? eventType) =>
        eventType.HasValue && !NotTrueEvent.Contains(eventType.Value);
    
}