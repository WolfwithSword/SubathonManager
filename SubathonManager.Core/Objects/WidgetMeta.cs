using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using SubathonManager.Core.Enums;

namespace SubathonManager.Core.Objects;

public class WidgetMeta {
    public string Author { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public int Width { get; set; } = 300;
    public int Height { get; set; } = 300;
    public Dictionary<string, WidgetMetaVar> Vars { get; set; } = new();

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WidgetMetaGlobals? Globals { get; set; }
}

[ExcludeFromCodeCoverage]
public class WidgetMetaGlobals {
    public bool All { get; set; }

    public Dictionary<string, ActionValueType> Vars { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string ToWidgetValue() {
        return Models.Widget.JoinGlobalVars(All, Vars.Keys);
    }

    public static WidgetMetaGlobals? From(Models.Widget widget, IReadOnlyDictionary<string, ActionValueType>? types,
        WidgetMetaGlobals? previous = null) {
        if (string.IsNullOrWhiteSpace(widget.GlobalVars)) return null;

        var result = new WidgetMetaGlobals { All = widget.ListensToAllGlobals };
        foreach (string name in widget.GlobalVarNames) {
            ActionValueType? old = previous?.Vars.FirstOrDefault(v =>
                string.Equals(v.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

            result.Vars[name] = types != null && types.TryGetValue(name, out ActionValueType type)
                ? type : old ?? ActionValueType.Text;
        }

        return result;
    }
}

[ExcludeFromCodeCoverage]
public class WidgetMetaVar {
    [JsonIgnore] public string Name { get; set; } = string.Empty;

    public WidgetVariableType Type { get; set; } = WidgetVariableType.String;
    public string Description { get; set; } = string.Empty;

    public object Value { get; set; } = string.Empty;
    public List<string>? Options { get; set; }

    public string ValueToString() {
        string value = Value switch {
            JsonElement { ValueKind: JsonValueKind.String } el => el.GetString() ?? string.Empty,
            JsonElement { ValueKind: JsonValueKind.Number } el => el.GetRawText(),
            JsonElement { ValueKind: JsonValueKind.True } => "true",
            JsonElement { ValueKind: JsonValueKind.False } => "false",
            JsonElement { ValueKind: JsonValueKind.Array } el =>
                string.Join(",", el.EnumerateArray().Select(e => e.GetString() ?? e.GetRawText())),
            string s => s,
            int i => i.ToString(),
            float f => $"{f}",
            bool b => b.ToString(),
            _ => string.Empty
        };
        return value;
    }
}