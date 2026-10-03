using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using SubathonManager.Core.Enums;

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

    [JsonIgnore]
    public TimeSpan Duration => Seconds is > 0.0 ? TimeSpan.FromSeconds(Seconds.Value) : TimeSpan.Zero;

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
        }

        if (Type.HasScope() && string.IsNullOrWhiteSpace(Scope)) {
            error = $"Pick the {Type.GetScopeLabel()!.TrimEnd(':').ToLowerInvariant()} first";
            return false;
        }

        if (Type.HasTarget() && string.IsNullOrWhiteSpace(Target)) {
            error = $"{Type.GetGroup()} {Type.GetLabel()} needs a {Type.GetTargetLabel()!.TrimEnd(':').ToLowerInvariant()}";
            return false;
        }

        return true;
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
            ActionStepType.StreamerBotAction => $"Streamer.bot \"{target}\"",
            ActionStepType.HttpGet => $"GET {ShortUrl(Target)}",
            ActionStepType.HttpPost => $"POST {ShortUrl(Target)}",
            _ => Type.GetLabel()
        };
    }

    public IEnumerable<(string Name, string Value)> BodyArguments() {
        return ParseArguments(Body);
    }

    public static IEnumerable<(string Name, string Value)> ParseArguments(string? body) {
        if (string.IsNullOrWhiteSpace(body)) yield break;
        foreach (string raw in body.Split('\n')) {
            string line = raw.Trim();
            int split = line.IndexOf('=');
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
}

public sealed class ActionEdge {
    public string From { get; set; } = "";
    public string To { get; set; } = "";
}

public sealed class ActionGraph {
    public static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public ActionRepeatMode OnRepeat { get; set; } = ActionRepeatMode.Restart;
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

        foreach (ActionEdge edge in Edges) {
            if (ids.Contains(edge.From) && ids.Contains(edge.To) && edge.From != edge.To) continue;
            error = $"Connection {edge.From} -> {edge.To} is not between two different steps";
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

public sealed record ActionContext(SubathonEventSource Source, string User, string RepeatKey, string? Label = null);

public sealed class ActionRunProgress {
    private readonly Lock _lock = new();
    public HashSet<string> Done { get; set; } = [];
    public Dictionary<string, double> Values { get; set; } = [];

    public bool IsDone(string nodeId) {
        lock (_lock) return Done.Contains(nodeId);
    }

    public void MarkDone(string nodeId) {
        lock (_lock) Done.Add(nodeId);
    }

    public bool TryGetValue(string key, out double value) {
        lock (_lock) return Values.TryGetValue(key, out value);
    }

    public void Remember(string key, double value) {
        lock (_lock) Values.TryAdd(key, value);
    }

    public string ToJson() {
        lock (_lock) return JsonSerializer.Serialize(this, ActionGraph.JsonOptions);
    }

    public static ActionRunProgress Parse(string? json) {
        if (string.IsNullOrWhiteSpace(json)) return new ActionRunProgress();
        try {
            return JsonSerializer.Deserialize<ActionRunProgress>(json, ActionGraph.JsonOptions)
                   ?? new ActionRunProgress();
        }
        catch (JsonException) {
            return new ActionRunProgress();
        }
    }
}
