using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Models;

namespace SubathonManager.Core.Objects;

public sealed class ActionStep {
    public ActionStepType Type { get; set; } = ActionStepType.Wait;
    public ActionOperation Operation { get; set; } = ActionOperation.None;

    public string Target { get; set; } = "";
    public string? TargetName { get; set; }
    public string? Scope { get; set; }
    public double? Value { get; set; }
    public double? Seconds { get; set; }
    public string? Body { get; set; }
    public string? Headers { get; set; }

    public ActionHttpAuth? Auth { get; set; }
    public string? AuthUser { get; set; }
    public string? AuthToken { get; set; }
    public string? OutputVariable { get; set; }

    public SubathonTrigger? Trigger { get; set; }
    public List<string>? EventTypes { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IgnoreSimulated { get; set; }

    [JsonIgnore] public TimeSpan Duration => Seconds is > 0.0 ? TimeSpan.FromSeconds(Seconds.Value) : TimeSpan.Zero;

    [JsonIgnore] public string AllText => string.Join('\n', Target, Scope, Body, Headers, AuthUser, AuthToken);

    private string OutputSuffix => string.IsNullOrEmpty(OutputVariable) ? "" : $" -> %{OutputVariable}%";

    public ActionStep Fill(Func<string, string> fill) {
        return new ActionStep {
            Type = Type,
            Operation = Operation,
            Target = fill(Target),
            TargetName = TargetName,
            Scope = Scope == null ? null : fill(Scope),
            Value = Value,
            Seconds = Seconds,
            Body = Body == null ? null : fill(Body),
            Headers = Headers == null ? null : fill(Headers),
            Auth = Auth,
            AuthUser = AuthUser == null ? null : fill(AuthUser),
            AuthToken = AuthToken == null ? null : fill(AuthToken),
            OutputVariable = OutputVariable,
            Trigger = Trigger,
            EventTypes = EventTypes == null ? null : [..EventTypes],
            IgnoreSimulated = IgnoreSimulated
        };
    }

    public bool IsValid(out string error) {
        error = "";
        IReadOnlyList<ActionOperation> ops = Type.GetOps();
        if (ops.Count > 0 && !ops.Contains(Operation)) {
            error = $"{Type.GetLabel()} cannot use '{Operation}'";
            return false;
        }

        switch (Type) {
            case ActionStepType.Wait:
            case ActionStepType.AddTime:
            case ActionStepType.SubtractTime:
                if (Duration > TimeSpan.Zero) return true;
                error = $"{Type.GetLabel()} needs a duration";
                return false;
            case ActionStepType.SetMultiplier:
                if (Value is > 0.0) return true;
                error = "Multiplier needs an amount above 0";
                return false;
            case ActionStepType.Reroll:
                if (Value is >= 1.0) return true;
                error = "Rerolls need a count of at least 1";
                return false;
            case ActionStepType.VtsParameter when Type.HasValue(Operation) && Value == null:
                error = "Pick a value to set the parameter to";
                return false;
            case ActionStepType.HttpGet or ActionStepType.HttpPost when !LooksLikeUrl(Target):
                error = "The URL needs to start with http:// or https://";
                return false;
            case ActionStepType.Trigger when Trigger == null:
                error = "Pick a trigger for this";
                return false;
            case ActionStepType.Trigger when Trigger == SubathonTrigger.SubathonEvent && (EventTypes?.Count ?? 0) == 0:
                error = "Pick at least one event type";
                return false;
            case ActionStepType.SetGlobal when !ActionStepTypeHelper.IsValidName(Target.Trim()):
                error = "Pick a global by name";
                return false;
            case ActionStepType.SetGlobal when Operation == ActionOperation.Adjust && string.IsNullOrWhiteSpace(Body):
                error = "Give an amount to add (negative to remove)";
                return false;
        }

        if (Type.SavesOutput() && !string.IsNullOrEmpty(OutputVariable) && !IsValidOutputName(OutputVariable)) {
            error =
                $"\"{OutputVariable}\" can't be an output name: use letters, numbers, _, and not a built-in variable's name";
            return false;
        }

        if (Type == ActionStepType.ObsRaw && !string.IsNullOrWhiteSpace(Body) && !Body.Contains('%')
            && !IsJsonObject(Body)) {
            error = "Request data needs to be a valid JSON object, e.g. {\"sceneName\": \"Main\"}";
            return false;
        }

        if (Type.IsWebRequest()) {
            if (Auth == ActionHttpAuth.Bearer && string.IsNullOrWhiteSpace(AuthToken)) {
                error = "Bearer auth needs a token, e.g. %secret.my_token%";
                return false;
            }

            if (Auth == ActionHttpAuth.Basic && string.IsNullOrWhiteSpace(AuthUser)) {
                error = "Basic auth needs a username";
                return false;
            }
        }

        if (Type.HasScope() && string.IsNullOrWhiteSpace(Scope)) {
            error = $"Pick the {Type.GetScopeLabel()!.TrimEnd(':').ToLowerInvariant()} first";
            return false;
        }

        if (Type.NeedsTarget(Operation) && string.IsNullOrWhiteSpace(Target)) {
            error =
                $"{Type.GetGroup()} {Type.GetLabel()} needs a {Type.GetTargetLabel()!.TrimEnd(':').ToLowerInvariant()}";
            return false;
        }

        return true;
    }

    public static bool IsValidOutputName(string name) {
        return ActionStepTypeHelper.IsValidName(name)
               && !ActionStepTypeHelper.TryParseToken(name, out _)
               && !name.Equals(ActionRunProgress.TriggerVariable, StringComparison.OrdinalIgnoreCase)
               && !Enum.GetValues<ActionStoreKind>()
                   .Any(k => name.Equals(k.TokenPrefix(), StringComparison.OrdinalIgnoreCase));
    }

    public bool MatchesTrigger(SubathonTrigger trigger, SubathonEvent? subathonEvent) {
        if (Type != ActionStepType.Trigger || Trigger != trigger) return false;
        if (trigger != SubathonTrigger.SubathonEvent) return true;
        if (subathonEvent == null) return false;
        if (IgnoreSimulated && subathonEvent.Source == SubathonEventSource.Simulated) return false;

        string eventType = $"{subathonEvent.EventType}";
        return EventTypes?.Contains(eventType, StringComparer.OrdinalIgnoreCase) ?? false;
    }

    private static string EventTypeLabel(string eventType) {
        return Enum.TryParse(eventType, out SubathonEventType type)
            ? $"{((SubathonEventType?)type).GetSource()} {((SubathonEventType?)type).GetLabel()}" : eventType;
    }

    private static bool IsJsonObject(string text) {
        try {
            return JsonNode.Parse(text) is JsonObject;
        }
        catch (JsonException) {
            return false;
        }
    }

    private static bool LooksLikeUrl(string url) {
        url = url.Trim();
        return url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
               || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
               || url.StartsWith('%');
    }

    public string Describe() {
        string target = string.IsNullOrWhiteSpace(TargetName) ? Target : TargetName;
        string value = Value?.ToString(CultureInfo.InvariantCulture) ?? "?";
        string duration = Utils.FormatShortDuration(Duration);
        string applies = Operation switch {
            ActionOperation.Points => "points",
            ActionOperation.Time => "time",
            _ => "points & time"
        };

        // messy for all but cant think of how else to maintain rn
        return Type switch {
            ActionStepType.Wait => $"Wait {duration}",
            ActionStepType.AddTime => $"Add {duration}",
            ActionStepType.SubtractTime => $"Subtract {duration}",
            ActionStepType.SetMultiplier =>
                $"x{value} {applies} {(Duration > TimeSpan.Zero ? $"for {duration}" : "until stopped")}",
            ActionStepType.Reroll => $"+{value} reroll{(Value is 1.0 ? "" : "s")}",
            ActionStepType.VtsExpression => $"{target} {Operation}",
            ActionStepType.VtsParameter => Operation switch {
                ActionOperation.Hold => $"{target} = {value} (held)",
                ActionOperation.Set => $"{target} = {value}",
                ActionOperation.Restore => $"Restore {target}",
                _ => $"Release {target}"
            },
            ActionStepType.VtsHotkey => $"Hotkey \"{target}\"",
            ActionStepType.MixItUpCommand => $"MixItUp \"{target}\"",
            ActionStepType.ObsSourceVisibility => $"{Operation} \"{target}\" in \"{Scope}\"",
            ActionStepType.ObsFilter => $"{Operation} filter \"{target}\" on \"{Scope}\"",
            ActionStepType.ObsAudio or ActionStepType.ObsMedia => $"{Operation} \"{target}\"",
            ActionStepType.SetActionEnabled => $"{Operation} action \"{target}\"",
            ActionStepType.RunAction => Operation == ActionOperation.Start
                ? $"Start action \"{target}\""
                : $"Run action \"{target}\"",
            ActionStepType.ObsBrowserRefresh => $"Refresh \"{target}\"",
            ActionStepType.ObsRaw => $"OBS {target}{OutputSuffix}",
            ActionStepType.StreamerBotAction => $"Streamer.bot \"{target}\"",
            ActionStepType.Condition => Type.NeedsTarget(Operation)
                ? $"If {Scope} is {Operation.GetOpLabel().ToLowerInvariant()} {Target}"
                : $"If {Scope} is {Operation.GetOpLabel().ToLowerInvariant()}",
            ActionStepType.Trigger => Trigger switch {
                null => "When ...",
                SubathonTrigger.SubathonEvent => (EventTypes?.Count ?? 0) switch {
                    0 => "When a subathon event (none picked)",
                    1 => $"When {EventTypeLabel(EventTypes![0])}",
                    _ => $"When {EventTypeLabel(EventTypes![0])} +{EventTypes!.Count - 1} more"
                } + (IgnoreSimulated ? ", not simulated" : ""),
                _ => $"When {Trigger.Value.GetLabel().ToLowerInvariant()}"
            },
            ActionStepType.SetGlobal => Operation switch {
                ActionOperation.Toggle => $"Toggle %global.{Target}%",
                ActionOperation.Adjust => $"%global.{Target}% += {Body?.Trim()}",
                _ => $"%global.{Target}% = {Body?.Trim()}"
            },
            ActionStepType.HttpGet => $"GET {ShortUrl(Target)}{OutputSuffix}",
            ActionStepType.HttpPost => $"POST {ShortUrl(Target)}{OutputSuffix}",
            _ => Type.GetLabel()
        };
    }

    public IEnumerable<(string Name, string Value)> BodyArguments() {
        return ParseArguments(Body);
    }

    public IEnumerable<(string Name, string Value)> HeaderLines() {
        return ParseArguments(Headers, ':');
    }

    public static IEnumerable<(string Name, string Value)> ParseArguments(string? body, char separator = '=') {
        if (string.IsNullOrWhiteSpace(body)) yield break;
        foreach (string raw in body.Split('\n')) {
            string line = raw.Trim();
            int split = line.IndexOf(separator);
            if (split <= 0) continue;
            yield return (line[..split].Trim(), line[(split + 1)..].Trim());
        }
    }

    private static string ShortUrl(string url) {
        // save space on node cards via trimming urls
        string trimmed = url.Trim();
        int scheme = trimmed.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) trimmed = trimmed[(scheme + 3)..];
        return trimmed.Length > 48 ? $"{trimmed[..45]}..." : trimmed;
    }
}

public sealed class ActionNode {
    public string Id { get; set; } = "";
    public ActionStep Step { get; set; } = new();
    public double X { get; set; }
    public double Y { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Disabled { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IgnoreErrors { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ActionInputMode Inputs { get; set; }
}

public sealed class ActionEdge {
    public const string ElsePort = "else";

    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string? Port { get; set; }
}

public sealed class ActionGraph {
    public static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public ActionRepeatMode OnRepeat { get; set; } = ActionRepeatMode.Restart;

    [JsonIgnore]
    public bool HasTriggers => Nodes.Any(n => n.Step.Type == ActionStepType.Trigger);

    public static bool AllowedWithTriggers(ActionRepeatMode mode) {
        return mode is ActionRepeatMode.Parallel or ActionRepeatMode.Queue;
    }

    [JsonIgnore]
    public ActionRepeatMode EffectiveRepeat =>
        HasTriggers && !AllowedWithTriggers(OnRepeat) ? ActionRepeatMode.Parallel : OnRepeat;
    public List<ActionNode> Nodes { get; set; } = [];
    public List<ActionEdge> Edges { get; set; } = [];

    public static ActionGraph Sequence(params ActionStep[] steps) {
        var graph = new ActionGraph();
        for (var i = 0; i < steps.Length; i++) {
            var id = $"{i + 1}";
            graph.Nodes.Add(new ActionNode { Id = id, Step = steps[i], X = i * 220 });
            if (i > 0) graph.Edges.Add(new ActionEdge { From = $"{i}", To = id });
        }

        return graph;
    }

    public IEnumerable<string> Incoming(string nodeId) {
        return Edges.Where(e => e.To == nodeId).Select(e => e.From);
    }

    public IEnumerable<string> Outgoing(string nodeId) {
        return Edges.Where(e => e.From == nodeId).Select(e => e.To);
    }

    public IEnumerable<ActionEdge> IncomingEdges(string nodeId) {
        return Edges.Where(e => e.To == nodeId);
    }

    public HashSet<string> OutputVariables() {
        return Nodes.Where(n => !n.Disabled && !string.IsNullOrEmpty(n.Step.OutputVariable))
            .Select(n => n.Step.OutputVariable!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public HashSet<string> Upstream(string nodeId) {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>(Incoming(nodeId));
        while (stack.TryPop(out string? id))
            if (seen.Add(id))
                foreach (string previous in Incoming(id))
                    stack.Push(previous);
        return seen;
    }

    public bool WouldLoop(string from, string to) {
        if (from == to) return true;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>([to]);
        while (stack.TryPop(out string? id)) {
            if (id == from) return true;
            if (!seen.Add(id)) continue;
            foreach (string next in Outgoing(id)) stack.Push(next);
        }

        return false;
    }

    public bool IsValid(out string error) {
        error = "";
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (ActionNode node in Nodes) {
            if (string.IsNullOrWhiteSpace(node.Id) || !ids.Add(node.Id)) {
                error = $"Duplicate or empty node id '{node.Id}'";
                return false;
            }

            if (!node.Disabled && !node.Step.IsValid(out string stepError)) {
                error = stepError;
                return false;
            }
        }

        var links = new HashSet<(string, string)>();
        foreach (ActionEdge edge in Edges) {
            if (!ids.Contains(edge.From) || !ids.Contains(edge.To) || edge.From == edge.To) {
                error = $"Connection {edge.From} -> {edge.To} is not between two different steps";
                return false;
            }

            if (!links.Add((edge.From, edge.To))) {
                error = $"Steps {edge.From} and {edge.To} are connected twice";
                return false;
            }

            if (Nodes.First(n => n.Id == edge.To).Step.Type == ActionStepType.Trigger) {
                error = "Triggers start an action, you cannot lead into them";
                return false;
            }

            if (edge.Port == null) continue;
            if (edge.Port == ActionEdge.ElsePort
                && Nodes.First(n => n.Id == edge.From).Step.Type == ActionStepType.Condition) continue;
            error = $"Connection {edge.From} -> {edge.To} uses an output its step does not have";
            return false;
        }

        var outputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ActionNode node in Nodes.Where(node => !node.Disabled &&
                                                        !string.IsNullOrEmpty(node.Step.OutputVariable) &&
                                                        !outputs.Add(node.Step.OutputVariable))) {
            error = $"More than one step saves its response as %{node.Step.OutputVariable}%";
            return false;
        }

        Dictionary<string, int> inDegree = ids.ToDictionary(id => id, id => Incoming(id).Count());
        var queue = new Queue<string>(inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        var visited = 0;
        while (queue.TryDequeue(out string? id)) {
            visited++;
            foreach (string next in Outgoing(id))
                if (--inDegree[next] == 0)
                    queue.Enqueue(next);
        }

        if (visited == ids.Count) return true;
        error = "Steps are connected in a loop";
        return false;
    }

    public string Describe() {
        if (Nodes.Count == 0) return "No steps";
        bool linear = Nodes.All(n => Incoming(n.Id).Count() <= 1 && Outgoing(n.Id).Count() <= 1)
                      && Nodes.Count(n => !Incoming(n.Id).Any()) == 1;
        if (!linear) return $"{Nodes.Count} steps";

        var ordered = new List<ActionNode>();
        ActionNode? current = Nodes.First(n => !Incoming(n.Id).Any());
        while (current != null && ordered.Count < Nodes.Count) {
            ordered.Add(current);
            string? nextId = Outgoing(current.Id).FirstOrDefault();
            current = nextId == null ? null : Nodes.FirstOrDefault(n => n.Id == nextId);
        }

        return string.Join(" -> ", ordered.Select(n => n.Disabled ? $"({n.Step.Describe()}, off)" : n.Step.Describe()));
    }

    public string ToJson() {
        return JsonSerializer.Serialize(this, JsonOptions);
    }

    public static bool TryParse(string? json, [NotNullWhen(true)] out ActionGraph? graph) {
        graph = null;
        if (string.IsNullOrWhiteSpace(json)) return false;
        try {
            graph = JsonSerializer.Deserialize<ActionGraph>(json, JsonOptions);
            return graph != null;
        }
        catch (JsonException) {
            return false;
        }
    }
}

public sealed class CustomAction {
    public const int CurrentFormatVersion = 1;
    public const string FileExtension = ".sma";

    private static readonly JsonSerializerOptions FileOptions = new(ActionGraph.JsonOptions) { WriteIndented = true };

    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New Action";
    public string? Author { get; set; }
    public string? Description { get; set; }
    public string Version { get; set; } = "1.0.0";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Disabled { get; set; }
    public ActionGraph Graph { get; set; } = new();

    public string ToJson() {
        return JsonSerializer.Serialize(this, FileOptions);
    }

    public CustomAction Clone() {
        return TryParse(ToJson(), out CustomAction? copy) ? copy : new CustomAction();
    }

    public static bool TryParse(string? json, [NotNullWhen(true)] out CustomAction? action) {
        action = null;
        if (string.IsNullOrWhiteSpace(json)) return false;
        try {
            action = JsonSerializer.Deserialize<CustomAction>(json, FileOptions);
            return action != null && action.Id != Guid.Empty;
        }
        catch (JsonException) {
            return false;
        }
    }
}

public sealed record ActionContext(SubathonEventSource Source, string User, string RepeatKey, string? Label = null,
    SubathonTrigger? Trigger = null, IReadOnlySet<string>? TriggerNodes = null,
    IReadOnlyDictionary<string, string>? TriggerValues = null, int Depth = 0);

public sealed class ActionRunProgress {
    public const int MaxVariableLength = 64 * 1024 * 2;

    public const string TriggerVariable = "trigger";

    // a step saving to %resp% also sets %resp_status%
    public const string StatusSuffix = "_status";

    public void SetOutput(string name, string value, string status) {
        SetVariable(name, value);
        SetVariable($"{name}{StatusSuffix}", status);
    }
    private readonly Lock _lock = new();

    public HashSet<string> Done { get; set; } = [];
    public HashSet<string> Skipped { get; set; } = [];
    public Dictionary<string, string> Ports { get; set; } = [];

    public Dictionary<string, int> Fired { get; set; } = [];
    public Dictionary<string, string> Variables { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, double> Values { get; set; } = [];

    public bool IsDone(string nodeId) {
        lock (_lock) {
            return Done.Contains(nodeId);
        }
    }

    public void MarkDone(string nodeId) {
        lock (_lock) {
            Done.Add(nodeId);
        }
    }

    public bool IsResolved(string nodeId) {
        lock (_lock) {
            return Done.Contains(nodeId) || Skipped.Contains(nodeId);
        }
    }

    public void MarkSkipped(string nodeId) {
        lock (_lock) {
            Skipped.Add(nodeId);
        }
    }

    public void SetPort(string nodeId, string? port) {
        lock (_lock) {
            if (port == null) Ports.Remove(nodeId);
            else Ports[nodeId] = port;
        }
    }

    private static string FireKey(string nodeId, string? port) {
        return $"{nodeId}|{port}";
    }

    public void RecordRun(string nodeId, string? port) {
        lock (_lock) {
            string key = FireKey(nodeId, port);
            Fired[key] = Fired.GetValueOrDefault(key) + 1;
        }
    }

    public int Runs(string nodeId) {
        var prefix = $"{nodeId}|";
        lock (_lock) {
            return Fired.Where(f => f.Key.StartsWith(prefix, StringComparison.Ordinal)).Sum(f => f.Value);
        }
    }

    public int Firings(ActionEdge edge) {
        var prefix = $"{edge.From}|";
        lock (_lock) {
            if (Fired.TryGetValue(FireKey(edge.From, edge.Port), out int count)) return count;
            if (Fired.Keys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal))) return 0;
            return Done.Contains(edge.From) && Ports.GetValueOrDefault(edge.From) == edge.Port ? 1 : 0;
        }
    }

    public bool IsLive(ActionEdge edge) {
        return Firings(edge) > 0;
    }

    public void SetVariable(string name, string value) {
        if (value.Length > MaxVariableLength) value = value[..MaxVariableLength];
        lock (_lock) {
            Variables[name] = value;
        }
    }

    public bool TryReadVariable(string token, out string value) {
        string root = ActionStepTypeHelper.TokenRoot(token);
        string? raw;
        lock (_lock) {
            if (!Variables.TryGetValue(root, out raw)) {
                value = "";
                return false;
            }
        }

        value = root.Length == token.Length ? raw : ActionStepTypeHelper.ReadJsonPath(raw, token[root.Length..]);
        return true;
    }

    public bool TryGetValue(string key, out double value) {
        lock (_lock) {
            return Values.TryGetValue(key, out value);
        }
    }

    public void Remember(string key, double value) {
        lock (_lock) {
            Values.TryAdd(key, value);
        }
    }

    public string ToJson() {
        lock (_lock) {
            return JsonSerializer.Serialize(this, ActionGraph.JsonOptions);
        }
    }

    public static ActionRunProgress Parse(string? json) {
        if (string.IsNullOrWhiteSpace(json)) return new ActionRunProgress();
        try {
            ActionRunProgress progress = JsonSerializer.Deserialize<ActionRunProgress>(json, ActionGraph.JsonOptions)
                                         ?? new ActionRunProgress();
            progress.Variables = new Dictionary<string, string>(progress.Variables, StringComparer.OrdinalIgnoreCase);
            return progress;
        }
        catch (JsonException) {
            return new ActionRunProgress();
        }
    }
}