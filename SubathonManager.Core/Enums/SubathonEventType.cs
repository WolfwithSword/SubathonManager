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
    YouTubeSuperChat
    
    //KoFiDonation,
    //KoFiSub,
    // any new must be added after the last
}

public static class SubathonEventTypeHelper
{
    private static readonly SubathonEventType[] CurrencyDontationEvents = new[]
    {
        SubathonEventType.YouTubeSuperChat,
        SubathonEventType.StreamElementsDonation,
        SubathonEventType.StreamLabsDonation
    };
    
    private static readonly SubathonEventType[] MembershipTypes = new[]
    {
        SubathonEventType.YouTubeMembership,
        SubathonEventType.YouTubeGiftMembership
    };
    private static readonly SubathonEventType[] SubscriptionTypes = new[]
    {
        SubathonEventType.TwitchSub,
        SubathonEventType.TwitchGiftSub
    };
    
    public static bool IsCurrencyDonation(this SubathonEventType? eventType) => 
        eventType.HasValue && CurrencyDontationEvents.Contains(eventType.Value);
    
    public static bool IsMembershipType(this SubathonEventType? eventType) => 
        eventType.HasValue && MembershipTypes.Contains(eventType.Value);
    
    public static bool IsSubscriptionType(this SubathonEventType? eventType) => 
        eventType.HasValue && SubscriptionTypes.Contains(eventType.Value);

    public static bool IsSubOrMembershipType(this SubathonEventType? eventType) =>
        eventType.IsMembershipType() || eventType.IsSubscriptionType();
}