using System.Diagnostics.CodeAnalysis;
namespace SubathonManager.Core.Enums;

public enum SubathonEventType
{
    TwitchSub, // remember subs can be of Value: 1000, 2000, 3000, Prime iirc... damnit looks like the TwitchLib doesnt separate Prime??
    TwitchCheer, // remember 100 is 1$, either in UI we say per 100 bits and divide, or we make em divide
    TwitchGiftSub,
    TwitchRaid,
    TwitchFollow,
    StreamElementsDonation,
    Command, // from any chat or ui
    Unknown,
    StreamLabsDonation,
    YouTubeMembership,
    YouTubeGiftMembership,
    YouTubeSuperChat,
    TwitchHypeTrain, // value is start, progress, end. Alt type event. Amount is level.
    TwitchCharityDonation,
    ExternalDonation,
    ExternalSub,
    KoFiDonation,
    KoFiSub,
    DonationAdjustment,
    BlerpBits, // twitch only
    BlerpBeets,
    PicartoFollow,
    PicartoSub,
    PicartoGiftSub,
    PicartoTip,
    GamerSuppsOrder,
    UwUMarketOrder
    // any new must be added after the last
}

[ExcludeFromCodeCoverage]
public static class SubathonEventTypeHelper
{
    private static readonly SubathonEventType[] DisabledEvents =
    [
        //SubathonEventType.GamerSuppsOrder,
        //SubathonEventType.UwUMarketOrder
    ];
    
    private static readonly SubathonEventType[] CurrencyDonationEvents = new[]
    {
        SubathonEventType.YouTubeSuperChat,
        SubathonEventType.StreamElementsDonation,
        SubathonEventType.StreamLabsDonation,
        SubathonEventType.TwitchCharityDonation,
        SubathonEventType.ExternalDonation,
        SubathonEventType.KoFiDonation,
        SubathonEventType.DonationAdjustment
    };
    
    public static readonly SubathonEventType[] CheerTypes = new[]
    {
        SubathonEventType.TwitchCheer,
        SubathonEventType.BlerpBeets,
        SubathonEventType.BlerpBits, // needs a modifier
        SubathonEventType.PicartoTip
    };

    private static readonly SubathonEventType[] GiftTypes = new[]
    {

        SubathonEventType.YouTubeGiftMembership,
        SubathonEventType.TwitchGiftSub,
        SubathonEventType.PicartoGiftSub,
    };
    
    private static readonly SubathonEventType[] MembershipTypes = new[]
    {
        SubathonEventType.YouTubeMembership,
        SubathonEventType.YouTubeGiftMembership,
        SubathonEventType.KoFiSub
    };
    
    private static readonly SubathonEventType[] SubscriptionTypes = new[]
    {
        SubathonEventType.TwitchSub,
        SubathonEventType.TwitchGiftSub,
        SubathonEventType.ExternalSub,
        SubathonEventType.PicartoSub,
        SubathonEventType.PicartoGiftSub
    };

    private static readonly SubathonEventType[] ExternalTypes = new[]
    {
        SubathonEventType.ExternalDonation,
        SubathonEventType.ExternalSub,
        SubathonEventType.KoFiSub,
        SubathonEventType.KoFiDonation,
    };

    private static readonly SubathonEventType[] NoValueConfigTypes = new[]
    {
        SubathonEventType.Command,
        SubathonEventType.DonationAdjustment,
        SubathonEventType.ExternalSub,
        SubathonEventType.Unknown,
        SubathonEventType.TwitchHypeTrain
    };

    private static readonly SubathonEventType[] ExtensionType = new[]
    {
        SubathonEventType.BlerpBeets,
        SubathonEventType.BlerpBits
    };
    
    private static readonly SubathonEventType[] FollowTypes = new[]
    {
        SubathonEventType.PicartoFollow,
        SubathonEventType.TwitchFollow
    };

    private static readonly SubathonEventType[] OrderTypes = new[]
    {
        SubathonEventType.UwUMarketOrder,
        SubathonEventType.GamerSuppsOrder
    };

    public static SubathonEventSubType GetSubType(this SubathonEventType? eventType)
    {
        if (!eventType.HasValue) return SubathonEventSubType.Unknown;
        if (eventType.IsGiftType()) return SubathonEventSubType.GiftSubLike;
        if (eventType.IsSubOrMembershipType()) return SubathonEventSubType.SubLike;
        if (eventType.IsCheerType()) return SubathonEventSubType.TokenLike;
        if (eventType.IsCurrencyDonation()) return SubathonEventSubType.DonationLike;
        if (eventType.IsOrderType()) return SubathonEventSubType.OrderLike;
        if (eventType.IsFollowType()) return SubathonEventSubType.FollowLike;
            
        return eventType.Value switch
        {
            SubathonEventType.TwitchRaid => SubathonEventSubType.RaidLike,
            SubathonEventType.TwitchHypeTrain =>  SubathonEventSubType.TrainLike,
            SubathonEventType.Command => SubathonEventSubType.CommandLike,
            _ => SubathonEventSubType.Unknown
        };
    }
    
    public static bool IsFollowType(this SubathonEventType? eventType) => 
        eventType.HasValue && FollowTypes.Contains(eventType.Value);

    public static bool IsOrderType(this SubathonEventType? eventType) => 
        eventType.HasValue && OrderTypes.Contains(eventType.Value);

    public static bool IsEnabled(this SubathonEventType? eventType) => 
        eventType.HasValue && !DisabledEvents.Contains(eventType.Value);
    
    public static bool IsExtensionType(this SubathonEventType? eventType) => 
        eventType.HasValue && ExtensionType.Contains(eventType.Value);
    
    public static bool IsCurrencyDonation(this SubathonEventType? eventType) => 
        eventType.HasValue && CurrencyDonationEvents.Contains(eventType.Value);
    
    public static bool IsGiftType(this SubathonEventType? eventType) => 
        eventType.HasValue && GiftTypes.Contains(eventType.Value);
    
    public static bool IsMembershipType(this SubathonEventType? eventType) => 
        eventType.HasValue && MembershipTypes.Contains(eventType.Value);
    
    public static bool IsSubscriptionType(this SubathonEventType? eventType) => 
        eventType.HasValue && SubscriptionTypes.Contains(eventType.Value);

    public static bool IsSubOrMembershipType(this SubathonEventType? eventType) =>
        eventType.IsMembershipType() || eventType.IsSubscriptionType();
    
    public static bool IsExternalType(this SubathonEventType? eventType) =>
        eventType.HasValue && ExternalTypes.Contains(eventType.Value);
    
    public static bool IsCheerType(this SubathonEventType? eventType) =>
        eventType.HasValue && CheerTypes.Contains(eventType.Value);
    
    public static bool HasNoValueConfig(this SubathonEventType? eventType) =>
        eventType.HasValue && !NoValueConfigTypes.Contains(eventType.Value);
    
    public static SubathonEventSource GetSource(this SubathonEventType? eventType) {
        if (!eventType.HasValue) return SubathonEventSource.Unknown;

        return eventType.Value switch
        {
            SubathonEventType.TwitchHypeTrain => SubathonEventSource.Twitch,
            SubathonEventType.TwitchCharityDonation => SubathonEventSource.Twitch,
            SubathonEventType.TwitchGiftSub => SubathonEventSource.Twitch,
            SubathonEventType.TwitchRaid => SubathonEventSource.Twitch,
            SubathonEventType.TwitchFollow => SubathonEventSource.Twitch,
            SubathonEventType.TwitchSub => SubathonEventSource.Twitch,
            SubathonEventType.TwitchCheer => SubathonEventSource.Twitch,
            
            SubathonEventType.BlerpBits => SubathonEventSource.Blerp, // but twitch only
            SubathonEventType.BlerpBeets => SubathonEventSource.Blerp,
            
            SubathonEventType.StreamElementsDonation => SubathonEventSource.StreamElements,
            SubathonEventType.StreamLabsDonation => SubathonEventSource.StreamLabs,
            
            SubathonEventType.ExternalDonation => SubathonEventSource.External,
            SubathonEventType.ExternalSub => SubathonEventSource.External,
            
            SubathonEventType.KoFiSub => SubathonEventSource.KoFi,
            SubathonEventType.KoFiDonation => SubathonEventSource.KoFi,
            
            SubathonEventType.Command => SubathonEventSource.Command,
            
            SubathonEventType.YouTubeGiftMembership => SubathonEventSource.YouTube,
            SubathonEventType.YouTubeMembership => SubathonEventSource.YouTube,
            SubathonEventType.YouTubeSuperChat => SubathonEventSource.YouTube,
            
            SubathonEventType.PicartoFollow => SubathonEventSource.Picarto,
            SubathonEventType.PicartoSub => SubathonEventSource.Picarto,
            SubathonEventType.PicartoGiftSub => SubathonEventSource.Picarto,
            SubathonEventType.PicartoTip => SubathonEventSource.Picarto,
            
            SubathonEventType.DonationAdjustment => SubathonEventSource.Command,
            
            SubathonEventType.GamerSuppsOrder => SubathonEventSource.GoAffPro,
            SubathonEventType.UwUMarketOrder => SubathonEventSource.GoAffPro,
            
            _ => SubathonEventSource.Unknown
        };
    }
    
}