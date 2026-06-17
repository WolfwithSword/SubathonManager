using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
namespace SubathonManager.Core.Enums;

public enum SubathonCommandType
{
    [CommandMeta(Description="Add Points", RequiresParameter = true)]
    AddPoints,
    [CommandMeta(Description="Remove Points", RequiresParameter = true)]
    SubtractPoints,
    [CommandMeta(Description="Set Points", RequiresParameter = true, IsControlType = true)]
    SetPoints,
    [CommandMeta(Description="Add Time", RequiresParameter = true)]
    AddTime,
    [CommandMeta(Description="Remove Time", RequiresParameter = true)]
    SubtractTime,
    [CommandMeta(Description="Set Time", RequiresParameter = true, IsControlType = true)]
    SetTime,
    [CommandMeta(Description="Lock", IsControlType = true)]
    Lock,
    [CommandMeta(Description="Unlock", IsControlType = true)]
    Unlock,
    [CommandMeta(Description="Pause Timer", IsControlType = true)]
    Pause,
    [CommandMeta(Description="Resume Timer", IsControlType = true)]
    Resume,
    [CommandMeta(Description="Set Multiplier", RequiresParameter = true, IsControlType = true)]
    SetMultiplier,
    [CommandMeta(Description="Stop Multiplier", IsControlType = true)]
    StopMultiplier,
    [CommandMeta(Description="None")]
    None,
    [CommandMeta(Description="Unknown")]
    Unknown,
    [CommandMeta(Description="Refresh Overlays", IsControlType = true)]
    RefreshOverlays,
    [CommandMeta(Description="Add Money", RequiresParameter = true)]
    AddMoney,
    [CommandMeta(Description="Remove Money", RequiresParameter = true)]
    SubtractMoney
}

[ExcludeFromCodeCoverage]
public static class SubathonCommandTypeHelper
{
    private static CommandMetaAttribute? Meta(this SubathonCommandType value)
    {
        var meta = EnumMetaCache.Get<CommandMetaAttribute>(value);
        return meta;
    }

    public static bool IsParametersRequired(this SubathonCommandType command) =>
        command.Meta()?.RequiresParameter ?? false;
    
    public static bool IsControlTypeCommand(this SubathonCommandType command) =>
        command.Meta()?.IsControlType ?? false;
    
}