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
    RefreshOverlays
}

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
        SubathonCommandType.SetMultiplier
    };
    
   
    public static bool IsParametersRequired(this SubathonCommandType command) => 
        ParamRequiredCommands.Contains(command);
    
}