using System.Reflection;
using System.Collections.Concurrent;
// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.Core.Enums;

public static class EnumExtensions
{
    public static string GetDescription(this Enum value)
    {
        var field = value.GetType().GetField(value.ToString());
        if (field == null) return value.ToString();

        var attr = field.GetCustomAttribute<EnumMetaAttribute>();
        return attr?.Description ?? value.ToString();
    }
    
    public static string GetLabel(this Enum value)
    {
        var field = value.GetType().GetField(value.ToString());
        if (field == null) return value.ToString();

        var attr = field.GetCustomAttribute<EnumMetaAttribute>();
        return attr?.Label ?? value.ToString();
    }
    
    public static bool IsDisabled(this Enum value)
    {
        var field = value.GetType().GetField(value.ToString());
        if (field == null) return true;
        var attr = field.GetCustomAttribute<EnumMetaAttribute>();
        return !attr?.Enabled ?? true;
    }    
    
    public static bool IsEnabled(this Enum value)
    {
        return !IsDisabled(value);
    }
    
    public static int GetOrderNumber(this Enum value)
    {
        var field = value.GetType().GetField(value.ToString());
        if (field == null) return 99999;
        var attr = field.GetCustomAttribute<EnumMetaAttribute>();
        return attr?.Order ?? 99999;
    }   
    
}

[AttributeUsage(AttributeTargets.Field)]
public class EnumMetaAttribute : Attribute
{
    public virtual string? Description { get; set; } = "";
    public virtual string? Label { get; init; }
    public int Order { get; init; }

    public bool Enabled { get; init; } = true;
}

public class EventSourceMetaAttribute : EnumMetaAttribute
{
    public override string? Label => SourceGroup is (SubathonSourceGroup.UseSource or SubathonSourceGroup.Unknown) ? ToString() : SourceGroup.GetLabel(); 
    public SubathonSourceGroup SourceGroup { get; init; } = SubathonSourceGroup.UseSource;
    
    public int SourceOrder { get; init; } = 99999;
}

public class EventTypeMetaAttribute : EnumMetaAttribute
{
    public override string? Description => Source is not (SubathonEventSource.Command or SubathonEventSource.Unknown) ? $"{Source.ToString()} {Label}".Trim( ): Label;
    public bool IsCurrencyDonation { get; init; }
    public bool IsGift { get; init; }
    public bool IsMembership { get; init; }
    public bool IsSubscription { get; init; }
    public bool IsSubscriptionLike => IsSubscription || IsGift || IsMembership;
    public bool IsExternal { get; init; }
    
    public bool IsExtension { get; init; }
    public bool IsToken { get; init; }
    public bool IsRaid { get; init; }
    public bool IsTrain { get; init; }
    public bool IsFollow { get; init; }
    public bool IsOrder { get; init; }
    public bool IsCommand { get; init; }
    public bool IsOther { get; init; }
    public bool HasValueConfig { get; init; } = true;

    public SubathonEventSource Source { get; set; } = SubathonEventSource.Unknown;
}

public class GoAffProTypeMetaAttribute : EventTypeMetaAttribute
{
    public override string? Description =>  Label;
    public GoAffProSource StoreSource { get; init; } = GoAffProSource.Unknown; 
}


public class GoAffProSourceMetaAttribute : EnumMetaAttribute
{
    public SubathonEventType OrderEvent { get; init; } = SubathonEventType.Unknown;
    
    public int SiteId { get; init; } = -1;
}

public class CommandMetaAttribute : EnumMetaAttribute
{
    public bool RequiresParameter { get; init; }
    public bool IsControlType { get; init; }
}


public static class EnumMetaCache
{
    private static readonly ConcurrentDictionary<Type, Dictionary<object, EnumMetaAttribute?>> Cache = new();

    public static T? Get<T>(Enum value) where T : EnumMetaAttribute
    {
        var type = value.GetType();

        var map = Cache.GetOrAdd(type, t =>
        {
            var dict = new Dictionary<object, EnumMetaAttribute?>();

            foreach (var field in t.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                var enumValue = field.GetValue(null)!;
                var attr2 = field.GetCustomAttribute<EnumMetaAttribute>();

                dict[enumValue] = attr2;
            }

            return dict;
        });

        if (!map.TryGetValue(value, out var attr))
            return null;

        return attr as T;
    }
}