using System.Diagnostics.CodeAnalysis;
namespace SubathonManager.Core.Enums;

public enum SubathonEventType
{
    [EventTypeMeta(Label="Subscription", Source=SubathonEventSource.Twitch, IsSubscription = true, Order = 1)]
    TwitchSub, // remember subs can be of Value: 1000, 2000, 3000, Prime iirc... damnit looks like the TwitchLib doesnt separate Prime??
    [EventTypeMeta(Label="Bits", Source=SubathonEventSource.Twitch, IsToken = true, Order = 3)]
    TwitchCheer, // remember 100 is 1$, either in UI we say per 100 bits and divide, or we make em divide
    [EventTypeMeta(Label="Gift Subscription", Source=SubathonEventSource.Twitch, IsGift = true, Order = 2)]
    TwitchGiftSub,
    [EventTypeMeta(Label="Raid", Source=SubathonEventSource.Twitch, IsRaid = true, Order = 5)]
    TwitchRaid,
    [EventTypeMeta(Label="Follow", Source=SubathonEventSource.Twitch, IsFollow = true, Order = 4)]
    TwitchFollow,
    [EventTypeMeta(Label="Donation", Source=SubathonEventSource.StreamElements, IsCurrencyDonation = true, Order = 1)]
    StreamElementsDonation,
    
    [EventTypeMeta(Label="Commands", Source=SubathonEventSource.Command, HasValueConfig = false, IsCommand = true, IsOther = true, Order =1)]
    Command, // from any chat or ui
    [EventTypeMeta(Label="Unknown", Source=SubathonEventSource.Unknown, HasValueConfig = false, IsOther = true, Order =1)]
    Unknown,
    
    [EventTypeMeta(Label="Donation", Source=SubathonEventSource.StreamLabs, IsCurrencyDonation = true, Order =2)]
    StreamLabsDonation,
    
    [EventTypeMeta(Label="Membership", Source=SubathonEventSource.YouTube, IsMembership = true, Order =1)]
    YouTubeMembership,
    [EventTypeMeta(Label="Gift Membership", Source=SubathonEventSource.YouTube, IsMembership = true, IsGift = true, Order =2)]
    YouTubeGiftMembership,
    [EventTypeMeta(Label="SuperChat", Source=SubathonEventSource.YouTube, IsCurrencyDonation = true, Order =3)]
    YouTubeSuperChat,
    [EventTypeMeta(Label="Hype Train", Source=SubathonEventSource.Twitch, IsTrain = true, HasValueConfig = false, Order = 6)]
    TwitchHypeTrain, // value is start, progress, end. Alt type event. Amount is level.
    
    [EventTypeMeta(Label="Charity Donation", Source=SubathonEventSource.Twitch, IsCurrencyDonation = true, Order = 7)]
    TwitchCharityDonation,
    [EventTypeMeta(Label="Donation", Source=SubathonEventSource.External, IsCurrencyDonation = true, IsExternal=true, Order = 1)]
    ExternalDonation,
    [EventTypeMeta(Label="Subscription", Source=SubathonEventSource.External, IsSubscription = true, IsExternal = true, Order = 2)]
    ExternalSub,
    [EventTypeMeta(Label="Donation", Source=SubathonEventSource.KoFi, IsCurrencyDonation = true, IsExternal=true, Order = 1)]
    KoFiDonation,
    [EventTypeMeta(Label="Membership", Source=SubathonEventSource.KoFi, IsMembership = true, IsExternal=true, Order = 2)]
    KoFiSub,
    [EventTypeMeta(Label="Donation Adjustment", Source=SubathonEventSource.Command, IsCurrencyDonation = true, 
        IsOther = true, IsCommand=true, HasValueConfig = false, Order = 1)]
    DonationAdjustment,
    [EventTypeMeta(Label="Bits", Source=SubathonEventSource.Blerp, IsToken = true, Order = 1)]
    BlerpBits, // twitch only
    [EventTypeMeta(Label="Beets", Source=SubathonEventSource.Blerp, IsToken = true, IsExtension=true, Order = 2)]
    BlerpBeets,
    [EventTypeMeta(Label="Follow", Source=SubathonEventSource.Picarto, IsFollow = true, IsExtension=true, Order = 4)]
    PicartoFollow,
    [EventTypeMeta(Label="Subscription", Source=SubathonEventSource.Picarto, IsSubscription = true, Order = 1)]
    PicartoSub,
    [EventTypeMeta(Label="Gift Subscription", Source=SubathonEventSource.Picarto, IsGift = true, Order = 2)]
    PicartoGiftSub,
    [EventTypeMeta(Label="Kudos Tip", Source=SubathonEventSource.Picarto, IsToken = true, Order = 3)]
    PicartoTip,
    [GoAffProTypeMeta(Label="GamerSupps Order", Source=SubathonEventSource.GoAffPro, IsOrder = true, Order = 1, StoreSource = GoAffProSource.GamerSupps)]
    GamerSuppsOrder,
    [GoAffProTypeMeta(Label="UwUMarket Order", Source=SubathonEventSource.GoAffPro, IsOrder = true, Order = 2, StoreSource = GoAffProSource.UwUMarket)]
    UwUMarketOrder,
    [GoAffProTypeMeta(Label="Orchid Eight Order", Source=SubathonEventSource.GoAffPro, IsOrder = true, Order = 3, StoreSource = GoAffProSource.OrchidEight)]
    OrchidEightOrder,
    [GoAffProTypeMeta(Label="KatDragonz Order", Source=SubathonEventSource.GoAffPro, IsOrder = true, Order = 4, StoreSource = GoAffProSource.KatDragonz)]
    KatDragonzOrder,
    [EventTypeMeta(Label="Redirect/Raid", Source=SubathonEventSource.YouTube, IsRaid = true, Order = 5)]
    YouTubeRedirect,
    [EventTypeMeta(Label="Shop Order", Source=SubathonEventSource.KoFi, IsOrder = true, IsExternal=true, Order = 3)]
    KoFiShopOrder,
    [EventTypeMeta(Label="Commission", Source=SubathonEventSource.KoFi, IsOrder = true, IsExternal=true, Order = 4)]
    KoFiCommissionOrder
    // any new must be added after the last
}

[ExcludeFromCodeCoverage]
public static class SubathonEventTypeHelper
{

    private static EventTypeMetaAttribute? Meta(this SubathonEventType? value)
    {
        if (!value.HasValue) return null;
        var meta = EnumMetaCache.Get<EventTypeMetaAttribute>(value);
        if (meta?.Source == SubathonEventSource.GoAffPro)
            return GoAffProMeta(value);
        return meta;
    }
    
    private static GoAffProTypeMetaAttribute? GoAffProMeta(this SubathonEventType? value)
    {
        return value != null ? EnumMetaCache.Get<GoAffProTypeMetaAttribute>(value) : null;
    }

    public static bool IsCurrencyDonation(this SubathonEventType? value)
        => value.Meta()?.IsCurrencyDonation == true;
    
    public static bool IsSubscription(this SubathonEventType? value)
        => value.Meta()?.IsSubscriptionLike == true;

    public static bool IsGift(this SubathonEventType? value)
        => value.Meta()?.IsGift == true;
    
    public static bool IsToken(this SubathonEventType? value)
        => value.Meta()?.IsToken == true;
    
    public static bool IsCommand(this SubathonEventType? value)
        => value.Meta()?.IsCommand == true;
    
    public static bool IsExternal(this SubathonEventType? value)
        => value.Meta()?.IsExternal == true;
    
    public static bool IsRaid(this SubathonEventType? value)
        => value.Meta()?.IsRaid == true;
    public static bool IsFollow(this SubathonEventType? value)
        => value.Meta()?.IsFollow == true;

    public static bool IsTrain(this SubathonEventType? value) => value.Meta()?.IsTrain == true;
    public static bool IsOrder(this SubathonEventType? value) => value.Meta()?.IsOrder == true;
    public static bool IsExtension(this SubathonEventType? value) => value.Meta()?.IsExtension == true;
    public static bool IsOther(this SubathonEventType? value)
        => value.Meta()?.IsOther == true;

    public static bool HasNoValueConfig(this SubathonEventType? value) => value.Meta()?.HasValueConfig == true;

    public static SubathonEventSource GetSource(this SubathonEventType value) =>
        ((SubathonEventType?)value).GetSource();
    public static SubathonEventSource GetSource(this SubathonEventType? value)
        => value.Meta()?.Source ?? SubathonEventSource.Unknown;

    public static string GetLabel(this SubathonEventType? value) => value.Meta()?.Label ?? value.ToString() ?? string.Empty;
    
    public static SubathonEventSubType? GetSubType(this SubathonEventType eventType) => GetSubType((SubathonEventType?)eventType);
    public static SubathonEventSubType GetSubType(this SubathonEventType? eventType)
    {
        if (eventType == null) return SubathonEventSubType.Unknown;
        if (eventType.IsGift()) return SubathonEventSubType.GiftSubLike;
        if (eventType.IsSubscription()) return SubathonEventSubType.SubLike; // important GiftType is above, so it has priority
        if (eventType.IsToken()) return SubathonEventSubType.TokenLike;
        if (eventType.IsCurrencyDonation()) return SubathonEventSubType.DonationLike;
        if (eventType.IsOrder()) return SubathonEventSubType.OrderLike;
        if (eventType.IsFollow()) return SubathonEventSubType.FollowLike;
        if (eventType.IsRaid()) return SubathonEventSubType.RaidLike;
        if (eventType.IsTrain()) return SubathonEventSubType.TrainLike;
        if (eventType.IsCommand()) return SubathonEventSubType.CommandLike;
        return SubathonEventSubType.Unknown;
    }
    
    public static string? GetTypeTrueSource(this SubathonEventType eventType) => GetTypeTrueSource((SubathonEventType?)eventType);
    
    public static string? GetTypeTrueSource(this SubathonEventType? eventType)
    {
        if (eventType is null or SubathonEventType.Command) return "Manual";
        if (eventType.GetSource() == SubathonEventSource.GoAffPro) return eventType.GoAffProMeta()?.StoreSource.ToString();
        return eventType.GetSource().ToString();
    }
    
}
