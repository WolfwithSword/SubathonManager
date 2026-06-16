using System.Diagnostics.CodeAnalysis;

namespace SubathonManager.Core.Enums;

public enum WheelSpinActionType
{
    [WheelSpinActionMeta(HasAction = false, Label="Manual / Other")]
    Manual = 0,
    [WheelSpinActionMeta(IsCommand = true, IsDoneImmediately = true,  Label = "Add Time")]
    AddTime = SubathonCommandType.AddTime,
    [WheelSpinActionMeta(IsCommand = true, IsDoneImmediately = true, Label = "Subtract Time")]
    SubtractTime = SubathonCommandType.SubtractTime,
    [WheelSpinActionMeta(IsCommand = true, Label = "Multiplier")]
    SetMultiplier = SubathonCommandType.SetMultiplier,
    [WheelSpinActionMeta(IsDoneImmediately = true, Label = "Add Rerolls")]
    Reroll = 1000
}

[ExcludeFromCodeCoverage]
public static class WheelSpinActionTypeHelper
{
    public static string GetLabel(this WheelSpinActionType type)
        => EnumMetaCache.Get<WheelSpinActionMetaAttribute>(type)?.Label ?? $"{type}";
    
    public static bool HasAction(this WheelSpinActionType type)
        => EnumMetaCache.Get<WheelSpinActionMetaAttribute>(type)?.HasAction ?? true;

    public static bool IsCommand(this WheelSpinActionType type)
        => EnumMetaCache.Get<WheelSpinActionMetaAttribute>(type)?.IsCommand ?? false;

    public static bool IsDoneImmediately(this WheelSpinActionType type)
        => EnumMetaCache.Get<WheelSpinActionMetaAttribute>(type)?.IsDoneImmediately ?? false;

    public static SubathonCommandType ToCommandType(this WheelSpinActionType type)
    {
        if (!type.IsCommand())
            return SubathonCommandType.Unknown;
        return (SubathonCommandType)(int)type;
    }
}

public enum WheelSpinHistoryStatus
{
    Pending,
    Done,
    Cancelled
}
