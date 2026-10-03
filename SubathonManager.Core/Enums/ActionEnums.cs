using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace SubathonManager.Core.Enums;

public enum ActionStepType {
    [ActionStepMeta(Label = "Wait", Group = "Flow", DurationLabel = "Wait for:")]
    Wait = 0,

    [ActionStepMeta(Label = "Add Time", Group = "Subathon", DurationLabel = "Time to add:")]
    AddTime = 100,

    [ActionStepMeta(Label = "Subtract Time", Group = "Subathon", DurationLabel = "Time to remove:")]
    SubtractTime = 101,

    [ActionStepMeta(Label = "Multiplier", Group = "Subathon",
        Operations = [ActionOperation.PointsAndTime, ActionOperation.Points, ActionOperation.Time],
        ValueLabel = "Multiplier (x):", DurationLabel = "Lasts for (blank = until stopped):")]
    SetMultiplier = 102,

    [ActionStepMeta(Label = "Add Rerolls", Group = "Subathon", ValueLabel = "Rerolls to add:")]
    Reroll = 103,

    [ActionStepMeta(Label = "Expression", Group = "VTube Studio",
        Operations = [ActionOperation.On, ActionOperation.Off, ActionOperation.Toggle],
        TargetLabel = "Expression file:")]
    VtsExpression = 200,

    [ActionStepMeta(Label = "Parameter", Group = "VTube Studio",
        Operations = [ActionOperation.Hold, ActionOperation.Set, ActionOperation.Release, ActionOperation.Restore],
        TargetLabel = "Parameter:", ValueLabel = "Set value to:",
        ValueOperations = [ActionOperation.Hold, ActionOperation.Set])]
    VtsParameter = 201,

    [ActionStepMeta(Label = "Hotkey", Group = "VTube Studio", Operations = [ActionOperation.Trigger],
        TargetLabel = "Hotkey:")]
    VtsHotkey = 202,

    [ActionStepMeta(Label = "Run Command", Group = "Mix It Up", Operations = [ActionOperation.Run],
        TargetLabel = "Command:", BodyLabel = "Special identifiers (one name=value per line, read as $name in MIU):",
        AllowsVariables = true)]
    MixItUpCommand = 300,

    // in a scene
    [ActionStepMeta(Label = "Source", Group = "OBS",
        Operations = [ActionOperation.Show, ActionOperation.Hide, ActionOperation.Toggle],
        TargetLabel = "Source:", ScopeLabel = "Scene:")]
    ObsSourceVisibility = 400,

    // on a source
    [ActionStepMeta(Label = "Filter", Group = "OBS",
        Operations = [ActionOperation.Enable, ActionOperation.Disable, ActionOperation.Toggle],
        TargetLabel = "Filter:", ScopeLabel = "On source:")]
    ObsFilter = 401,

    [ActionStepMeta(Label = "Audio", Group = "OBS",
        Operations = [ActionOperation.Mute, ActionOperation.Unmute, ActionOperation.Toggle],
        TargetLabel = "Input:")]
    ObsAudio = 402,

    [ActionStepMeta(Label = "Media", Group = "OBS",
        Operations = [ActionOperation.Play, ActionOperation.Pause, ActionOperation.Restart, ActionOperation.Stop],
        TargetLabel = "Input:")]
    ObsMedia = 403,

    // "name=value" per line, sent as the action's arguments, found as %name%
    [ActionStepMeta(Label = "Run Action", Group = "Streamer.bot", Operations = [ActionOperation.Run],
        TargetLabel = "Action:", BodyLabel = "Arguments (one name=value per line):", AllowsVariables = true)]
    StreamerBotAction = 500,

    [ActionStepMeta(Label = "GET Request", Group = "Web", TargetLabel = "URL:", AllowsVariables = true)]
    HttpGet = 600,

    // body is sent as-is
    [ActionStepMeta(Label = "POST Request", Group = "Web", TargetLabel = "URL:", BodyLabel = "Body:",
        AllowsVariables = true)]
    HttpPost = 601
}

public enum ActionOperation {
    None,
    On,
    Off,
    Toggle,
    Hold,
    Set,
    Release,
    Restore,
    Trigger,
    Run,
    PointsAndTime,
    Points,
    Time,
    Show,
    Hide,
    Enable,
    Disable,
    Mute,
    Unmute,
    Play,
    Pause,
    Restart,
    Stop
}

public enum ActionRunResult {
    Done,
    Paused,
    Cancelled,
    Skipped
}

public enum ActionRepeatMode {
    Restart,
    Parallel,
    Skip
}

public enum ActionVariable {
    [ActionVariableMeta(Token = "seconds_remaining", Group = "Timer", ValueType = "number",
        Description = "Whole seconds left on the timer")]
    SecondsRemaining,

    [ActionVariableMeta(Token = "time_remaining", Group = "Timer", ValueType = "text",
        Description = "Time left as hours:minutes:seconds, e.g. 26:04:59")]
    TimeRemaining,

    [ActionVariableMeta(Token = "seconds_elapsed", Group = "Timer", ValueType = "number",
        Description = "Whole seconds the timer has run")]
    SecondsElapsed,

    [ActionVariableMeta(Token = "timer_paused", Group = "Timer", ValueType = "true/false",
        Description = "Whether the timer is paused")]
    TimerPaused,

    [ActionVariableMeta(Token = "timer_locked", Group = "Timer", ValueType = "true/false",
        Description = "Whether the subathon is locked to new events")]
    TimerLocked,

    [ActionVariableMeta(Token = "timer_reversed", Group = "Timer", ValueType = "true/false",
        Description = "Whether the timer counts up instead of down")]
    TimerReversed,

    [ActionVariableMeta(Token = "multiplier_active", Group = "Multiplier", ValueType = "true/false",
        Description = "Whether a multiplier is running")]
    MultiplierActive,

    [ActionVariableMeta(Token = "multiplier_amount", Group = "Multiplier", ValueType = "number",
        Description = "Current multiplier, 1 when none is running")]
    MultiplierAmount,

    [ActionVariableMeta(Token = "multiplier_seconds_remaining", Group = "Multiplier", ValueType = "number",
        Description = "Seconds until the multiplier ends, 0 when it has no end or none is running")]
    MultiplierSecondsRemaining,

    [ActionVariableMeta(Token = "multiplier_points", Group = "Multiplier", ValueType = "true/false",
        Description = "Whether the multiplier applies to points")]
    MultiplierPoints,

    [ActionVariableMeta(Token = "multiplier_time", Group = "Multiplier", ValueType = "true/false",
        Description = "Whether the multiplier applies to time")]
    MultiplierTime,

    [ActionVariableMeta(Token = "points", Group = "Points & Money", ValueType = "number",
        Description = "Current subathon points")]
    Points,

    [ActionVariableMeta(Token = "money", Group = "Points & Money", ValueType = "number",
        Description = "Money raised this subathon, to 2 decimals")]
    Money,

    [ActionVariableMeta(Token = "currency", Group = "Points & Money", ValueType = "text",
        Description = "The subathon's currency code, e.g. USD")]
    Currency,

    // should always match subathon currency, but included just in case / to open up config reading
    [ActionVariableMeta(Token = "default_currency", Group = "Points & Money", ValueType = "text",
        Description = "The primary currency from settings")]
    DefaultCurrency,

    [ActionVariableMeta(Token = "goal_text", Group = "Goals", ValueType = "text",
        Description = "The goal currently being worked towards, empty once all are done")]
    GoalText,

    [ActionVariableMeta(Token = "goal_points", Group = "Goals", ValueType = "number",
        Description = "Points (or money) needed for the current goal")]
    GoalPoints,

    [ActionVariableMeta(Token = "goal_index", Group = "Goals", ValueType = "number",
        Description = "Position of the current goal in the list, starting at 1; 0 when all are done")]
    GoalIndex,

    [ActionVariableMeta(Token = "goal_count", Group = "Goals", ValueType = "number",
        Description = "How many goals the active list has")]
    GoalCount,

    // should match points or money, but whatever is relevant for goals list
    [ActionVariableMeta(Token = "goal_progress", Group = "Goals", ValueType = "number",
        Description = "Points (or whole money) counted towards goals")]
    GoalProgress,

    [ActionVariableMeta(Token = "last_goal_text", Group = "Goals", ValueType = "text",
        Description = "The most recently completed goal, empty if none yet")]
    LastGoalText,

    [ActionVariableMeta(Token = "last_goal_points", Group = "Goals", ValueType = "number",
        Description = "Points (or money) of the most recently completed goal")]
    LastGoalPoints,

    [ActionVariableMeta(Token = "goal_list_name", Group = "Goals", ValueType = "text",
        Description = "Name of the active goal list")]
    GoalListName,

    [ActionVariableMeta(Token = "goal_type", Group = "Goals", ValueType = "text",
        Description = "What the active goal list counts: Points or Money")]
    GoalType,

    [ActionVariableMeta(Token = "user", Group = "Action", ValueType = "text",
        Description = "Who started the action run, e.g. WheelSpin, a chat user, SYSTEM or CustomAction")]
    User,

    [ActionVariableMeta(Token = "source", Group = "Action", ValueType = "text",
        Description = "What source started the run, e.g. WheelSpin")]
    Source,

    [ActionVariableMeta(Token = "label", Group = "Action", ValueType = "text",
        Description = "Name of what started the run, e.g. the wheel item's text")]
    Label,

    [ActionVariableMeta(Token = "now", Group = "Action", ValueType = "text",
        Description = "Local date and time the step ran, e.g. 2026-10-02 18:30:00")]
    Now
}

[ExcludeFromCodeCoverage]
public static partial class ActionStepTypeHelper {
    private static ActionStepMetaAttribute? Meta(ActionStepType type) {
        return EnumMetaCache.Get<ActionStepMetaAttribute>(type);
    }

    public static string GetLabel(this ActionStepType type) {
        return Meta(type)?.Label ?? $"{type}";
    }

    public static string GetGroup(this ActionStepType type) {
        return Meta(type)?.Group ?? "";
    }

    public static IReadOnlyList<ActionOperation> GetOps(this ActionStepType type) {
        return Meta(type)?.Operations ?? [];
    }

    public static ActionOperation DefaultOp(this ActionStepType type) {
        IReadOnlyList<ActionOperation> ops = type.GetOps();
        return ops.Count > 0 ? ops[0] : ActionOperation.None;
    }

    public static string? GetTargetLabel(this ActionStepType type) {
        return Meta(type)?.TargetLabel;
    }

    public static string? GetScopeLabel(this ActionStepType type) {
        return Meta(type)?.ScopeLabel;
    }

    public static string? GetValueLabel(this ActionStepType type) {
        return Meta(type)?.ValueLabel;
    }

    public static string? GetDurationLabel(this ActionStepType type) {
        return Meta(type)?.DurationLabel;
    }

    public static string? GetBodyLabel(this ActionStepType type) {
        return Meta(type)?.BodyLabel;
    }

    public static bool HasTarget(this ActionStepType type) {
        return type.GetTargetLabel() != null;
    }

    public static bool HasScope(this ActionStepType type) {
        return type.GetScopeLabel() != null;
    }

    public static bool HasDuration(this ActionStepType type) {
        return type.GetDurationLabel() != null;
    }

    public static bool HasBody(this ActionStepType type) {
        return type.GetBodyLabel() != null;
    }

    public static bool HasValue(this ActionStepType type, ActionOperation operation) {
        ActionStepMetaAttribute? meta = Meta(type);
        if (meta?.ValueLabel == null) return false;
        return meta.ValueOperations.Length == 0 || meta.ValueOperations.Contains(operation);
    }

    public static bool AllowsVariables(this ActionStepType type) {
        return Meta(type)?.AllowsVariables ?? false;
    }

    public static string GetOpLabel(this ActionOperation operation) {
        return operation switch {
            ActionOperation.None => "Do Nothing",
            ActionOperation.PointsAndTime => "Points & Time",
            ActionOperation.Play => "Play / Resume",
            _ => $"{operation}"
        };
    }

    public static bool IsAvailable(this ActionStepType type) {
        return type switch {
            ActionStepType.VtsExpression or ActionStepType.VtsParameter or ActionStepType.VtsHotkey
                => FeatureFlags.VTubeStudioEnabled,
            _ => true
        };
    }

    ////////////////// variable work

    private static readonly Dictionary<string, ActionVariable> ByToken = Enum.GetValues<ActionVariable>()
        .ToDictionary(v => v.GetToken(), v => v, StringComparer.OrdinalIgnoreCase);

    public static string GetToken(this ActionVariable variable) {
        return EnumMetaCache.Get<ActionVariableMetaAttribute>(variable)?.Token ?? $"{variable}".ToLowerInvariant();
    }

    public static string GetGroup(this ActionVariable variable) {
        return EnumMetaCache.Get<ActionVariableMetaAttribute>(variable)?.Group ?? "";
    }

    public static string GetValueType(this ActionVariable variable) {
        return EnumMetaCache.Get<ActionVariableMetaAttribute>(variable)?.ValueType ?? "text";
    }

    public static string GetDescription(this ActionVariable variable) {
        return EnumMetaCache.Get<ActionVariableMetaAttribute>(variable)?.Description ?? "";
    }

    public static string GetPlaceholder(this ActionVariable variable) {
        return $"%{variable.GetToken()}%";
    }

    public static bool TryParseToken(string token, out ActionVariable variable) {
        return ByToken.TryGetValue(token, out variable);
    }

    public static IEnumerable<ActionVariable> FindVariables(string? text) {
        if (string.IsNullOrEmpty(text)) yield break;
        foreach (Match match in TokenRegex().Matches(text))
            if (TryParseToken(match.Groups[1].Value, out ActionVariable variable))
                yield return variable;
    }

    public static string ReplaceVariables(string? text, IReadOnlyDictionary<ActionVariable, string> values) {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        return TokenRegex().Replace(text, m =>
            TryParseToken(m.Groups[1].Value, out ActionVariable variable) && values.TryGetValue(variable, out string? value)
                ? value
                : m.Value);
    }

    [GeneratedRegex("%([A-Za-z0-9_]+)%")]
    private static partial Regex TokenRegex();
}
