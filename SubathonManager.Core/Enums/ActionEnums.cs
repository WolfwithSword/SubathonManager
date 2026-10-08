using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SubathonManager.Core.Enums;

public enum ActionStepType {
    [ActionStepMeta(Label = "Wait", Group = "Flow", DurationLabel = "Wait for:")]
    Wait = 0,

    [ActionStepMeta(Label = "If / Else", Group = "Flow",
        Operations = [
            ActionOperation.IsEqual, ActionOperation.NotEqual, ActionOperation.MoreThan, ActionOperation.LessThan,
            ActionOperation.AtLeast, ActionOperation.AtMost, ActionOperation.Contains, ActionOperation.NotContains,
            ActionOperation.IsEmpty, ActionOperation.NotEmpty
        ],
        ScopeLabel = "Check:", OperationLabel = "Comparison:", TargetLabel = "Against:",
        NoTargetOperations = [ActionOperation.IsEmpty, ActionOperation.NotEmpty], AllowsVariables = true)]
    Condition = 1,

    [ActionStepMeta(Label = "Set Global", Group = "Flow",
        Operations = [ActionOperation.Set, ActionOperation.Adjust, ActionOperation.Toggle],
        TargetLabel = "Global:", BodyLabel = "Value:", NoBodyOperations = [ActionOperation.Toggle],
        AllowsVariables = true)]
    SetGlobal = 2,

    [ActionStepMeta(Label = "Trigger", Group = "Triggers")]
    Trigger = 3,

    [ActionStepMeta(Label = "Enable / Disable Action", Group = "Custom Actions",
        Operations = [ActionOperation.Enable, ActionOperation.Disable, ActionOperation.Toggle],
        TargetLabel = "Action:")]
    SetActionEnabled = 4,

    [ActionStepMeta(Label = "Run Action", Group = "Custom Actions",
        Operations = [ActionOperation.Run, ActionOperation.Start], TargetLabel = "Action:")]
    RunAction = 5,

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

    [ActionStepMeta(Label = "Refresh Browser", Group = "OBS", TargetLabel = "Browser source:")]
    ObsBrowserRefresh = 404,

    [ActionStepMeta(Label = "Raw Request", Group = "OBS", TargetLabel = "Request type:",
        BodyLabel = "Request data (JSON object, optional):", AllowsVariables = true, SavesOutput = true)]
    ObsRaw = 405,

    // "name=value" per line, sent as the action's arguments, found as %name%
    [ActionStepMeta(Label = "Run Action", Group = "Streamer.bot", Operations = [ActionOperation.Run],
        TargetLabel = "Action:", BodyLabel = "Arguments (one name=value per line):", AllowsVariables = true)]
    StreamerBotAction = 500,

    [ActionStepMeta(Label = "GET Request", Group = "Web", TargetLabel = "URL:",
        DurationLabel = "Timeout (blank = 10s):",
        AllowsVariables = true, IsWebRequest = true)]
    HttpGet = 600,

    // body is sent as-is
    [ActionStepMeta(Label = "POST Request", Group = "Web", TargetLabel = "URL:", BodyLabel = "Body:",
        DurationLabel = "Timeout (blank = 10s):", AllowsVariables = true, IsWebRequest = true)]
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
    Stop,

    // If / Else comparisons
    IsEqual,
    NotEqual,
    MoreThan,
    LessThan,
    AtLeast,
    AtMost,
    Contains,
    NotContains,
    IsEmpty,
    NotEmpty,

    // Set/Adjust Global on a number
    Adjust,
    
    // Start another action, do not wait for result
    Start
}

public enum ActionStoreKind {
    Global,
    Secret
}

public enum ActionValueType {
    Text,
    Number,
    Boolean
}

public enum ActionInputMode {
    Wait,
    Multiple
}

public enum ActionHttpAuth {
    None,
    Bearer,
    Basic
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
    Skip,
    Queue
}

public enum ActionVariable {
    [ActionVariableMeta(Token = "seconds_remaining", Group = "Timer", ValueType = "number",
        Description = "Seconds left on the timer")]
    SecondsRemaining,

    [ActionVariableMeta(Token = "time_remaining", Group = "Timer", ValueType = "text",
        Description = "Time left as hh:mm:ss, e.g. 26:04:59")]
    TimeRemaining,

    [ActionVariableMeta(Token = "seconds_elapsed", Group = "Timer", ValueType = "number",
        Description = "Seconds the timer has run")]
    SecondsElapsed,

    [ActionVariableMeta(Token = "timer_paused", Group = "Timer", ValueType = "true/false",
        Description = "Whether the timer is paused")]
    TimerPaused,

    [ActionVariableMeta(Token = "timer_locked", Group = "Timer", ValueType = "true/false",
        Description = "Whether the subathon is locked")]
    TimerLocked,

    [ActionVariableMeta(Token = "timer_reversed", Group = "Timer", ValueType = "true/false",
        Description = "Is the timer is in reverse mode?")]
    TimerReversed,

    [ActionVariableMeta(Token = "multiplier_active", Group = "Multiplier", ValueType = "true/false",
        Description = "Whether a multiplier is active")]
    MultiplierActive,

    [ActionVariableMeta(Token = "multiplier_amount", Group = "Multiplier", ValueType = "number",
        Description = "Current multiplier amount, 1 if inactive")]
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
        Description = "The primary currency from settings, should match subathon currency")]
    DefaultCurrency,

    [ActionVariableMeta(Token = "goal_text", Group = "Goals", ValueType = "text",
        Description = "The current unfinished goal, empty once all are done")]
    GoalText,

    [ActionVariableMeta(Token = "goal_points", Group = "Goals", ValueType = "number",
        Description = "Points (or money) needed for the current goal")]
    GoalPoints,

    [ActionVariableMeta(Token = "goal_index", Group = "Goals", ValueType = "number",
        Description = "Position of the current goal in the list, starting at 1; 0 when all done")]
    GoalIndex,

    [ActionVariableMeta(Token = "goal_count", Group = "Goals", ValueType = "number",
        Description = "How many goals in total")]
    GoalCount,

    // should match points or money, but whatever is relevant for goals list
    [ActionVariableMeta(Token = "goal_progress", Group = "Goals", ValueType = "number",
        Description = "Points (or whole money) counted towards all goals")]
    GoalProgress,

    [ActionVariableMeta(Token = "last_goal_text", Group = "Goals", ValueType = "text",
        Description = "Most recently completed goal, empty if none yet")]
    LastGoalText,

    [ActionVariableMeta(Token = "last_goal_points", Group = "Goals", ValueType = "number",
        Description = "Points (or money) of the most recently completed goal")]
    LastGoalPoints,

    [ActionVariableMeta(Token = "goal_list_name", Group = "Goals", ValueType = "text",
        Description = "Name of the active goal list")]
    GoalListName,

    [ActionVariableMeta(Token = "goal_type", Group = "Goals", ValueType = "text",
        Description = "Are goals tracking `Points` or `Money`?")]
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
    public const int MaxNameLength = 64;

    ////////////////// variable work

    private static readonly Dictionary<string, ActionVariable> ByToken = Enum.GetValues<ActionVariable>()
        .ToDictionary(v => v.GetToken(), v => v, StringComparer.OrdinalIgnoreCase);

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

    public static string GetOperationLabel(this ActionStepType type) {
        return Meta(type)?.OperationLabel ?? "Do:";
    }

    public static bool HasTarget(this ActionStepType type) {
        return type.GetTargetLabel() != null;
    }

    public static bool NeedsTarget(this ActionStepType type, ActionOperation operation) {
        return type.HasTarget() && !(Meta(type)?.NoTargetOperations.Contains(operation) ?? false);
    }

    public static bool IsWebRequest(this ActionStepType type) {
        return Meta(type)?.IsWebRequest ?? false;
    }

    public static bool SavesOutput(this ActionStepType type) {
        return type.IsWebRequest() || (Meta(type)?.SavesOutput ?? false);
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

    public static bool NeedsBody(this ActionStepType type, ActionOperation operation) {
        return type.HasBody() && !(Meta(type)?.NoBodyOperations.Contains(operation) ?? false);
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
            ActionOperation.IsEqual => "Equals",
            ActionOperation.NotEqual => "Not Equals",
            ActionOperation.MoreThan => "Greater Than",
            ActionOperation.LessThan => "Less Than",
            ActionOperation.AtLeast => "Greater or Equal to",
            ActionOperation.AtMost => "Less or Equal to",
            ActionOperation.Contains => "Contains",
            ActionOperation.NotContains => "Does Not Contain",
            ActionOperation.IsEmpty => "Is Empty",
            ActionOperation.NotEmpty => "Is Not Empty",
            ActionOperation.Set => "Set To",
            ActionOperation.Adjust => "Add/Subtract (numbers)",
            ActionOperation.Run => "Run",
            ActionOperation.Start => "Start - Do not wait",
            _ => $"{operation}"
        };
    }

    public static string GetLabel(this ActionValueType type) {
        return type switch {
            ActionValueType.Number => "number",
            ActionValueType.Boolean => "true/false",
            _ => "text"
        };
    }

    public static string DefaultValue(this ActionValueType type) {
        return type switch {
            ActionValueType.Number => "0",
            ActionValueType.Boolean => "false",
            _ => ""
        };
    }

    public static string? NormalizeValue(this ActionValueType type, string? raw) {
        raw ??= "";
        return type switch {
            ActionValueType.Number =>
                double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
                && double.IsFinite(number)
                    ? number.ToString(CultureInfo.InvariantCulture)
                    : null,
            ActionValueType.Boolean => raw.Trim().ToLowerInvariant() switch {
                "true" or "yes" or "on" or "1" => "true",
                "false" or "no" or "off" or "0" => "false",
                _ => null
            },
            _ => raw
        };
    }

    public static object ToTypedValue(this ActionValueType type, string? value) {
        return type switch {
            ActionValueType.Number => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture,
                out double number)
                ? number : 0d,
            ActionValueType.Boolean => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase),
            _ => value ?? ""
        };
    }

    public static string GetJsonType(this ActionValueType type) {
        return type switch {
            ActionValueType.Number => "number",
            ActionValueType.Boolean => "boolean",
            _ => "text"
        };
    }

    public static string? ConvertValue(string? value, ActionValueType from, ActionValueType to) {
        if (value == null) return null;
        if (from == ActionValueType.Boolean && to == ActionValueType.Number) return value == "true" ? "1" : "0";
        return to.NormalizeValue(value);
    }

    public static bool Compare(string left, ActionOperation operation, string right) {
        left = left.Trim();
        right = right.Trim();
        bool numbers = double.TryParse(left, NumberStyles.Float, CultureInfo.InvariantCulture, out double l)
                       & double.TryParse(right, NumberStyles.Float, CultureInfo.InvariantCulture, out double r);
        int order = numbers ? l.CompareTo(r) : string.Compare(left, right, StringComparison.OrdinalIgnoreCase);

        return operation switch {
            // any
            ActionOperation.IsEqual => order == 0,
            ActionOperation.NotEqual => order != 0,
            // num
            ActionOperation.MoreThan => order > 0,
            ActionOperation.LessThan => order < 0,
            ActionOperation.AtLeast => order >= 0,
            ActionOperation.AtMost => order <= 0,
            // strings
            ActionOperation.Contains => left.Contains(right, StringComparison.OrdinalIgnoreCase),
            ActionOperation.NotContains => !left.Contains(right, StringComparison.OrdinalIgnoreCase),
            ActionOperation.IsEmpty => left.Length == 0,
            ActionOperation.NotEmpty => left.Length > 0,
            _ => false
        };
    }

    public static bool IsAvailable(this ActionStepType type) {
        return type switch {
            ActionStepType.VtsExpression or ActionStepType.VtsParameter or ActionStepType.VtsHotkey
                => FeatureFlags.VTubeStudioEnabled,
            _ => true
        };
    }

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
            TryParseToken(m.Groups[1].Value, out ActionVariable variable) &&
            values.TryGetValue(variable, out string? value)
                ? value
                : m.Value);
    }

    public static IEnumerable<string> FindTokens(string? text) {
        if (string.IsNullOrEmpty(text)) yield break;
        foreach (Match match in TokenRegex().Matches(text)) yield return match.Groups[1].Value;
    }

    public static string ReplaceTokens(string? text, Func<string, string?> resolve) {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        return TokenRegex().Replace(text, m => resolve(m.Groups[1].Value) ?? m.Value);
    }

    public static bool IsValidName(string? name) {
        return !string.IsNullOrEmpty(name) && name.Length <= MaxNameLength && NameRegex().IsMatch(name);
    }

    public static string TokenPrefix(this ActionStoreKind kind) {
        return $"{kind}".ToLower();
    }

    public static bool TryGetStoreRef(string token, out ActionStoreKind kind, out string name) {
        foreach (ActionStoreKind candidate in Enum.GetValues<ActionStoreKind>()) {
            var prefix = $"{candidate.TokenPrefix()}.";
            if (!token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            kind = candidate;
            name = token[prefix.Length..];
            return IsValidName(name);
        }

        kind = default;
        name = "";
        return false;
    }

    public static IEnumerable<(ActionStoreKind Kind, string Name)> FindStoreRefs(string? text) {
        foreach (string token in FindTokens(text))
            if (TryGetStoreRef(token, out ActionStoreKind kind, out string name))
                yield return (kind, name);
    }

    public static string StorePlaceholder(ActionStoreKind kind, string name) {
        return $"%{kind.TokenPrefix()}.{name}%";
    }

    public static string TokenRoot(string token) {
        int cut = token.IndexOfAny(['.', '[']);
        return cut < 0 ? token : token[..cut];
    }

    public static string ReadJsonPath(string json, string path) {
        JsonNode? node;
        try {
            node = JsonNode.Parse(json);
        }
        catch (JsonException) {
            return "";
        }

        foreach (Match part in PathPartRegex().Matches(path)) {
            if (part.Groups[1].Success)
                node = node is JsonObject obj ? obj[part.Groups[1].Value] : null;
            else
                node = node is JsonArray array && int.TryParse(part.Groups[2].Value, out int index) &&
                       index < array.Count
                    ? array[index]
                    : null;
            if (node == null) return "";
        }

        return node switch {
            null => "",
            JsonValue value when value.TryGetValue(out string? text) => text,
            _ => node.ToJsonString()
        };
    }

    [GeneratedRegex(@"%([A-Za-z0-9_]+(?:\.[A-Za-z0-9_]+|\[\d+\])*)%")]
    private static partial Regex TokenRegex();

    [GeneratedRegex("^[A-Za-z0-9_]+$")]
    private static partial Regex NameRegex();

    [GeneratedRegex(@"\.([A-Za-z0-9_]+)|\[(\d+)\]")]
    private static partial Regex PathPartRegex();
}