using System.Diagnostics.CodeAnalysis;
namespace SubathonManager.Core.Enums;

public enum SubathonCommandType
{
    AddPoints,
    SubtractPoints,
    SetPoints,
    AddTime,
    SubtractTime,
    SetTime,
    Lock,
    Unlock,
    Pause,
    Resume,
    SetMultiplier,
    StopMultiplier,
    None,
    Unknown,
    RefreshOverlays,
    AddMoney,
    SubtractMoney
}

[ExcludeFromCodeCoverage]
public static class SubathonCommandTypeHelper
{
    private static readonly SubathonCommandType[] ParamRequiredCommands = new[]
    {
        SubathonCommandType.AddPoints,
        SubathonCommandType.SubtractPoints,
        SubathonCommandType.SetPoints,
        SubathonCommandType.AddTime,
        SubathonCommandType.SubtractTime,
        SubathonCommandType.SetTime,
        SubathonCommandType.SetMultiplier,
        SubathonCommandType.AddMoney,
        SubathonCommandType.SubtractMoney
    };

    private static readonly SubathonCommandType[] ControlTypeCommands = new[] // can't "undo"
    {
        SubathonCommandType.Resume,
        SubathonCommandType.Pause,
        SubathonCommandType.StopMultiplier,
        SubathonCommandType.Lock,
        SubathonCommandType.Unlock,
        SubathonCommandType.SetMultiplier,
        SubathonCommandType.RefreshOverlays,
        SubathonCommandType.SetPoints,
        SubathonCommandType.SetTime
    };
    
   
    public static bool IsParametersRequired(this SubathonCommandType command) => 
        ParamRequiredCommands.Contains(command);
    
    public static bool IsControlTypeCommand(this SubathonCommandType command) =>
        ControlTypeCommands.Contains(command);
    
}