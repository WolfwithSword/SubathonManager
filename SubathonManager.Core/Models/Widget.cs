using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Interfaces;

// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.Core.Models;

[ExcludeFromCodeCoverage]
public class CssVariable : INotifyPropertyChanged {
    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    [Key] public int Id { get; set; }
    public string Name { get; set; } = string.Empty;

    // to assist the live change for editing
    private string _value = string.Empty;

    public string Value {
        get => _value;
        set {
            if (_value == value) return;
            _value = value;
            if (string.IsNullOrWhiteSpace(_value)) {
                if (Type is WidgetCssVariableType.Int or WidgetCssVariableType.Float)
                    _value = "0";
                else if (Type is WidgetCssVariableType.Size) _value = "0px";
            }

            OnPropertyChanged();
        }
    }


    [ForeignKey("Widget")] public Guid WidgetId { get; set; }
    public Widget Widget { get; set; } = null!;

    public WidgetCssVariableType Type { get; set; } = WidgetCssVariableType.Default;
    public string Description { get; set; } = string.Empty;

    public CssVariable Clone(Guid newWidgetId) {
        return new CssVariable {
            Name = Name,
            Value = Value,
            WidgetId = newWidgetId,
            Type = Type,
            Description = Description
        };
    }

    public static CssVariable? FromJson(JsonElement json, Guid widgetId) {
        string name = json.GetProperty("name").GetString() ?? string.Empty;
        string value = json.GetProperty("value").GetString() ?? string.Empty;
        if (string.IsNullOrEmpty(name)) return null;
        return new CssVariable {
            Name = name,
            Value = value,
            WidgetId = widgetId
        };
    }

    public JsonElement ToJson() {
        var obj = new {
            name = Name,
            value = Value
        };

        return JsonSerializer.SerializeToElement(obj);
    }
}

[ExcludeFromCodeCoverage]
public class JsVariable {
    [Key] public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public WidgetVariableType Type { get; set; } = WidgetVariableType.String;

    [ForeignKey("Widget")] public Guid WidgetId { get; set; }
    public Widget Widget { get; set; } = null!;

    public string Description { get; set; } = string.Empty;

    public string GetInjectLine() {
        StringBuilder sb = new();
        if (Type.IsFontVariable()) {
            string fnName = Type switch {
                WidgetVariableType.GoogleFont => "loadGoogleFont",
                WidgetVariableType.CdnFont => "loadCdnFont",
                WidgetVariableType.BunnyFont => "loadBunnyFont",
                _ => ""
            };
            if (string.IsNullOrWhiteSpace(Value) || string.IsNullOrWhiteSpace(fnName)) return "\n";
            foreach (string font in Value.Split(',',
                         StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                sb.Append($"{fnName}(\"{font.Trim()}\");\n");
            return sb.ToString();
        }

        sb.Append($"const {Name.Replace(' ', '_')} = ");
        if (string.IsNullOrEmpty(Value) || string.IsNullOrWhiteSpace(Value)) {
            sb.Append("\"\"");
        }
        else if (Type == WidgetVariableType.Boolean && bool.TryParse(Value, out bool b)) {
            sb.Append($"{b.ToString().ToLower()}");
        }
        else if (Type == WidgetVariableType.Float && float.TryParse(Value, out float f)) {
            sb.Append($"{f}");
        }
        else if (Type == WidgetVariableType.Int && int.TryParse(Value, out int intValue)) {
            sb.Append($"{intValue}");
        }
        else if (Type == WidgetVariableType.Percent && short.TryParse(Value, out short pctValue)) {
            sb.Append($"{Math.Clamp((int)pctValue, 0, 100)}");
        }
        else if (Type == WidgetVariableType.StringSelect) {
            string val = Value.Split(',')[0].Trim();
            sb.Append($"\"{val}\"");
        }
        else if (Type is WidgetVariableType.EventTypeList or
                 WidgetVariableType.StringList or
                 WidgetVariableType.EventSubTypeList) {
            string val = string.Join(",", Value.Split(',').Select(s => {
                s = s.Trim();
                if (!s.StartsWith('"')) s = '"' + s;
                if (!s.EndsWith('"')) s += '"';
                return s;
            }));
            sb.Append($"[{val}]");
        }
        else if (((WidgetVariableType?)Type).IsFileVariable()) {
            if (ResourcePaths.IsResourceUrl(Value))
                sb.Append($"\"{Value}\"");
            else if (!Value.StartsWith("./") && !string.IsNullOrWhiteSpace(Value))
                sb.Append($"\"externalPath/{Value}\"");
            else
                sb.Append($"\"{Value}\"");
        }
        else // default as always string. Incl TypeSelects
        {
            sb.Append($"\"{Value}\"");
        }

        sb.Append(";\n");

        return sb.ToString();
    }

    public JsVariable Clone(Guid newWidgetId) {
        return new JsVariable {
            Name = Name,
            Value = Value,
            Type = Type,
            WidgetId = newWidgetId,
            Description = Description
        };
    }

    public static JsVariable? FromJson(JsonElement json, Guid widgetId) {
        string name = json.GetProperty("name").GetString() ?? string.Empty;
        if (string.IsNullOrEmpty(name)) return null;

        JsonElement typeEl = json.GetProperty("type");
        WidgetVariableType type;

        var description = string.Empty;
        json.TryGetProperty("description", out JsonElement descriptEl);

        if (descriptEl.ValueKind == JsonValueKind.String) description = descriptEl.GetString();

        if (typeEl.ValueKind == JsonValueKind.Number
            && typeEl.TryGetInt32(out int typeInt)
            && Enum.IsDefined(typeof(WidgetVariableType), typeInt))
            type = (WidgetVariableType)typeInt;
        else if (!Enum.TryParse(typeEl.GetString() ?? string.Empty, out type)) return null;
        return new JsVariable {
            Name = name,
            Value = json.GetProperty("value").GetString() ?? string.Empty,
            Type = type,
            WidgetId = widgetId,
            Description = description ?? string.Empty
        };
    }

    public JsonElement ToJson() {
        var obj = new {
            name = Name,
            value = Value,
            type = Type,
            description = Description
        };

        return JsonSerializer.SerializeToElement(obj);
    }
}

[ExcludeFromCodeCoverage]
public partial class Widget {
    [Key] public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; }
    public string HtmlPath { get; set; }

    public float X { get; set; }
    public float Y { get; set; }
    public int Z { get; set; }

    public int Width { get; set; } = 400;
    public int Height { get; set; } = 400;

    public float ScaleX { get; set; } = 1;
    public float ScaleY { get; set; } = 1;

    public List<CssVariable> CssVariables { get; set; } = new();
    public List<JsVariable> JsVariables { get; set; } = new();

    [ForeignKey("Route")] public Guid RouteId { get; set; }
    public Route Route { get; set; } = null!;

    public bool Visibility { get; set; } = true;

    public string? DocsUrl { get; set; } = string.Empty;

    public WidgetType Type { get; set; } = WidgetType.Html;

    public Widget(string name, string htmlPath) {
        Name = name;
        HtmlPath = htmlPath;
    }

    public Widget Clone(Guid? routeId, string? newName, int? newZ) {
        var widget = new Widget(string.IsNullOrEmpty(newName) ? Name : newName, HtmlPath);

        widget.RouteId = routeId ?? RouteId;
        widget.Visibility = Visibility;
        widget.X = X;
        widget.Y = Y;
        widget.Z = newZ ?? Z;
        widget.Width = Width;
        widget.Height = Height;
        widget.ScaleX = ScaleX;
        widget.ScaleY = ScaleY;
        widget.Type = Type;

        foreach (JsVariable jsVariable in JsVariables)
            widget.JsVariables.Add(jsVariable.Clone(widget.Id));
        foreach (CssVariable cssVariable in CssVariables)
            widget.CssVariables.Add(cssVariable.Clone(widget.Id));

        return widget;
    }

    public string GetPath() {
        return Path.GetDirectoryName(HtmlPath) ?? HtmlPath;
    }

    public void ScanCssVariables() {
        CssVariables = ExtractCssVariablesFromFiles();
    }

    public List<string> GetLinkedCssPaths() {
        var paths = new List<string>();

        string? html = WidgetFiles.Current.ReadAllText(HtmlPath);
        if (html == null)
            return paths;

        string baseDir = Path.GetDirectoryName(HtmlPath)!;

        foreach (Match match in CssLinkMatches(html)) {
            string cssFile = match.Groups[1].Value;
            string cssPath = Path.IsPathRooted(cssFile)
                ? cssFile
                : Path.Combine(baseDir, cssFile);

            if (!WidgetFiles.Current.Exists(cssPath))
                continue;
            if (paths.Contains(cssPath, StringComparer.OrdinalIgnoreCase))
                continue;

            paths.Add(cssPath);
        }

        return paths;
    }

    public static MatchCollection CssLinkMatches(string htmlContent) {
        return CssLinkRegex().Matches(htmlContent);
    }

    public List<CssVariable> ExtractCssVariablesFromFiles() {
        var extractedVars = new List<CssVariable>();

        foreach (string cssPath in GetLinkedCssPaths()) {
            string? cssContent = WidgetFiles.Current.ReadAllText(cssPath);
            if (cssContent == null) continue;
            MatchCollection varMatches = CssVarRegex().Matches(cssContent);

            string metaPath = cssPath + ".json";
            Dictionary<string, Dictionary<string, string>>? varTypes = null;
            string? metaJson = WidgetFiles.Current.ReadAllText(metaPath);
            if (metaJson != null)
                varTypes = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(metaJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = false });

            foreach (Match varMatch in varMatches) {
                string name = varMatch.Groups[1].Value.Trim();
                string value = varMatch.Groups[2].Value.Trim();
                var description = string.Empty;
                if (extractedVars.Any(v => v.Name == name)) continue;

                var cssType = WidgetCssVariableType.Default;

                Dictionary<string, string>? metaValue = null;
                if (varTypes is { Count: > 0 })
                    varTypes.TryGetValue(name, out metaValue);

                if (metaValue != null) {
                    metaValue.TryGetValue("type", out string? typeName);
                    Enum.TryParse(typeName, true, out cssType);
                    metaValue.TryGetValue("description", out description);
                }

                extractedVars.Add(new CssVariable {
                    Name = name,
                    Value = value,
                    WidgetId = Id,
                    Type = cssType,
                    Description = string.IsNullOrWhiteSpace(description) ? string.Empty : description
                });
            }
        }

        return extractedVars;
    }

    public override string ToString() {
        return $"Widget {Name} (Instance {Id})";
    }

    [GeneratedRegex(@"<link[^>]+href\s*=\s*[""']((?!https?:|\/\/)[^""']+\.css)[""']", RegexOptions.IgnoreCase, "en-CA")]
    private static partial Regex CssLinkRegex();

    [GeneratedRegex(@"--([a-zA-Z0-9-_]+)\s*:\s*([^;]+);")]
    private static partial Regex CssVarRegex();

    public JsonElement ToJson(string htmlRelPath) {
        var obj = new {
            name = Name,
            htmlPath = htmlRelPath,
            type = Type.ToString(),

            position = new {
                x = X,
                y = Y,
                z = Z
            },

            size = new {
                width = Width,
                height = Height
            },

            scale = new {
                x = ScaleX,
                y = ScaleY
            },

            visibility = Visibility,
            docsUrl = DocsUrl,

            cssVariables = CssVariables.Select(v => v.ToJson()).ToArray(),
            jsVariables = JsVariables.Select(v => v.ToJson()).ToArray()
        };

        return JsonSerializer.SerializeToElement(obj);
    }

    /*
    public static Widget? FromJson(JsonElement json, string rootPath, Guid routeId)
    {
        var htmlPath = Path.Join(rootPath, json.GetProperty("htmlPath").GetString());
        var name = json.GetProperty("name").GetString() ?? string.Empty;
        if (string.IsNullOrEmpty(name)) return null;

        var widget = new Widget(name, htmlPath)
        {
            RouteId = routeId,
            Visibility = json.GetProperty("visibility").GetBoolean(),
            DocsUrl = json.TryGetProperty("docsUrl", out var d) ? d.GetString() : null
        };

        var pos = json.GetProperty("position");
        widget.X = pos.GetProperty("x").GetSingle();
        widget.Y = pos.GetProperty("y").GetSingle();
        widget.Z = pos.GetProperty("z").GetInt32();

        var size = json.GetProperty("size");
        widget.Width = size.GetProperty("width").GetInt32();
        widget.Height = size.GetProperty("height").GetInt32();

        var scale = json.GetProperty("scale");
        widget.ScaleX = scale.GetProperty("x").GetSingle();
        widget.ScaleY = scale.GetProperty("y").GetSingle();

        foreach (var cssVar in json.GetProperty("cssVariables").EnumerateArray().Select(css =>
                     CssVariable.FromJson(css, widget.Id)).OfType<CssVariable>())
        {
            widget.CssVariables.Add(cssVar);
        }

        foreach (var jsVar in json.GetProperty("jsVariables").EnumerateArray().Select(js =>
                     JsVariable.FromJson(js, widget.Id)).OfType<JsVariable>())
        {
            widget.JsVariables.Add(jsVar);
        }

        return widget;
    }
    */
}