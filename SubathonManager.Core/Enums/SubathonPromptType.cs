using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

namespace SubathonManager.Core.Enums;

public enum SubathonPromptType
{
    Points,
    Money,
    Orders,
    Follows,
    Subs,
    Tokens,
    Event
}

public enum SubathonPromptSubType
{
    Default, // count occurances
    
    Items, // items on orders
    
    // subs Default is all subs
    NormalSubs, // Default but subs only
    GiftSubs, // Default but giftsubs only
    
    // Event specific which are Subs
    // default for any
    ByTier
}

public enum SubathonPromptRunStatus
{
    Active,
    Completed,
    Expired,
    Cancelled
}

[ExcludeFromCodeCoverage]
public static class SubathonPromptTypeExtensions
{
    private static readonly ReadOnlyDictionary<SubathonPromptType, SubathonPromptSubType[]> _validSubTypes =
        new(new Dictionary<SubathonPromptType, SubathonPromptSubType[]>
        {
            [SubathonPromptType.Points] = [SubathonPromptSubType.Default],
            [SubathonPromptType.Money] = [SubathonPromptSubType.Default],
            [SubathonPromptType.Orders] = [SubathonPromptSubType.Default, SubathonPromptSubType.Items],
            [SubathonPromptType.Follows] = [SubathonPromptSubType.Default],
            [SubathonPromptType.Tokens] = [SubathonPromptSubType.Default],
            [SubathonPromptType.Subs] = [SubathonPromptSubType.Default, SubathonPromptSubType.NormalSubs, SubathonPromptSubType.GiftSubs],
            [SubathonPromptType.Event] = [SubathonPromptSubType.Default, SubathonPromptSubType.Items, SubathonPromptSubType.ByTier],
        });
 
    public static SubathonPromptSubType[] GetValidSubTypes(this SubathonPromptType type)
        => _validSubTypes.TryGetValue(type, out var st) ? st : [SubathonPromptSubType.Default];
 
    public static bool IsSubTypeValid(this SubathonPromptType type, SubathonPromptSubType subType)
        => type.GetValidSubTypes().Contains(subType);
 
    public static bool IsSelectableForPromptEvent(this SubathonEventType eventType)
    {
        var type = (SubathonEventType?)eventType;
        if (type.IsRaid() || type.IsCommand() || type == SubathonEventType.Unknown || type.IsTrain()) return false;
        return !eventType.IsDisabled();
    }

    public static SubathonPromptSubType[] GetValidSubTypes(
        this SubathonPromptType type,
        SubathonEventType? filterEventType)
    {
        if (type != SubathonPromptType.Event || filterEventType == null)
            return type.GetValidSubTypes();
 
        if (filterEventType.IsSubscription())
            return [SubathonPromptSubType.Default, SubathonPromptSubType.ByTier];
        if (filterEventType.IsOrder())
        {
            if (filterEventType.GetTypeTrueSource() == $"{SubathonEventSource.MakeShip}")
            {
                return [SubathonPromptSubType.Items];
            }
            return [SubathonPromptSubType.Default, SubathonPromptSubType.Items];
        }

        return [SubathonPromptSubType.Default];
    }
 
    public static string DisplayName(this SubathonPromptType type) => type switch
    {
        SubathonPromptType.Points => "Points",
        SubathonPromptType.Money => "Money",
        SubathonPromptType.Subs => "Subs",
        SubathonPromptType.Orders  => "Orders",
        SubathonPromptType.Follows => "Follows",
        SubathonPromptType.Tokens => "Tokens",
        SubathonPromptType.Event => "Specific Event",
        _ => type.ToString()
    };
 
    public static string DisplayName(this SubathonPromptSubType subType, SubathonPromptType type) => subType switch
    {
        SubathonPromptSubType.Default => type switch{
            SubathonPromptType.Points => "Total",
            SubathonPromptType.Money => "Units",
            SubathonPromptType.Subs => "Any",
            SubathonPromptType.Orders => "Total Orders",
            SubathonPromptType.Tokens => "Total",
            _ => "Count"
        },
        SubathonPromptSubType.Items => "Item Count",
        SubathonPromptSubType.NormalSubs => "Normal Subs Only",
        SubathonPromptSubType.GiftSubs => "Gift Subs Only",
        SubathonPromptSubType.ByTier => "By Tier",
        _  => subType.ToString()
    };
    
    public static string TierMetaDisplayName(this SubathonEventType eventType, string meta) =>
        eventType switch
        {
            SubathonEventType.TwitchSub or SubathonEventType.TwitchGiftSub => meta switch
            {
                "1000" => "T1",
                "2000" => "T2",
                "3000" => "T3",
                _ => meta
            },
            _ => meta
        };
 
    public static string ValueLabel(this SubathonPromptType type, SubathonPromptSubType subType) => type switch
    {
        SubathonPromptType.Points => "Target Points",
        SubathonPromptType.Money => "Target Money (units)",
        SubathonPromptType.Follows => "Target Follows",
        SubathonPromptType.Tokens => "Target Tokens",
        SubathonPromptType.Orders => subType == SubathonPromptSubType.Items ? "Target Items" : "Target Orders",
        SubathonPromptType.Subs => "Target Subs",
        SubathonPromptType.Event => "Target Count",
        _ => "Target Value"
    };
}
 