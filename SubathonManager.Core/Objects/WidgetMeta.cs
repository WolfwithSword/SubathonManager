using System.Text.Json;
using SubathonManager.Core.Enums;

namespace SubathonManager.Core.Objects;

public class WidgetMeta
{
    public string Author { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public int Width { get; set; } = 300;
    public int Height { get; set; } = 300;
    public Dictionary<string, WidgetMetaVar> Vars { get; set; } = new();
}

public class WidgetMetaVar
{
    public string Name { get; set; } = string.Empty;
    public WidgetVariableType Type { get; set; } = WidgetVariableType.String;
    public string Description { get; set; } = string.Empty;
    
    public object Value { get; set; } = string.Empty;
    public List<string>? Options { get; set; }

    public string ValueToString()
    {
        var value = Value switch
        {
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