using SubathonManager.Core.Enums;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;

namespace SubathonManager.Core.Models;

[ExcludeFromCodeCoverage]
public class CssVariable
{
    [Key]
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    
    
    [ForeignKey("Widget")]
    public Guid WidgetId { get; set; }
    public Widget Widget { get; set; } = null!;
    
    public WidgetCssVariableType Type { get; set; } =  WidgetCssVariableType.Default;
    public string Description { get; set; } = string.Empty;
    
    public CssVariable Clone(Guid newWidgetId)
    {
        return new CssVariable
        {
            Name = Name,
            Value = Value,
            WidgetId = newWidgetId,
            Type = Type,
            Description = Description
        };
    }
    
    public static CssVariable? FromJson(JsonElement json, Guid widgetId)
    {
        var name = json.GetProperty("name").GetString() ?? string.Empty;
        var value = json.GetProperty("value").GetString() ?? string.Empty;
        if (string.IsNullOrEmpty(name)) return null;
        return new CssVariable
        {
            Name = name,
            Value = value,
            WidgetId = widgetId
        };
    }
    
    public JsonElement ToJson()
    {
        var obj = new
        {
            name = Name,
            value = Value
        };

        return JsonSerializer.SerializeToElement(obj);
    }
}

[ExcludeFromCodeCoverage]
public class JsVariable
{
    [Key]
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public WidgetVariableType Type { get; set; } =  WidgetVariableType.String;
    
    [ForeignKey("Widget")]
    public Guid WidgetId { get; set; }
    public Widget Widget { get; set; } = null!;

    public string GetInjectLine()
    {
        StringBuilder sb = new();
        sb.Append($"const {Name.Replace(' ', '_')} = ");
        if (string.IsNullOrEmpty(Value) || string.IsNullOrWhiteSpace(Value))
            sb.Append("\"\"");
        else if (Type == WidgetVariableType.Boolean && bool.TryParse(Value, out var b))
            sb.Append($"{b.ToString().ToLower()}");
        else if (Type == WidgetVariableType.Float && float.TryParse(Value, out var f))
            sb.Append($"{f}");
        else if (Type == WidgetVariableType.Int && Int32.TryParse(Value, out var intValue))
            sb.Append($"{intValue}");
        else if (Type == WidgetVariableType.Percent && Int16.TryParse(Value, out var pctValue))
            sb.Append($"{Math.Clamp((int)pctValue, 0, 100)}");
        else if (Type == WidgetVariableType.StringSelect)
        {
            string val = Value.Split(',')[0].Trim();
            sb.Append($"\"{val}\"");
        }
        else if (Type == WidgetVariableType.EventTypeList || 
                 Type == WidgetVariableType.StringList ||
                 Type == WidgetVariableType.EventSubTypeList)
        {
            string val = string.Join(",", Value.Split(',').Select(s =>
            {
                s = s.Trim();
                if (!s.StartsWith('"')) s = '"' + s;
                if (!s.EndsWith('"')) s += '"';
                return s;
            }));
            sb.Append($"[{val}]");
        }
        else if (((WidgetVariableType?)Type).IsFileVariable())
        {
            if (!Value.StartsWith("./") && !string.IsNullOrWhiteSpace(Value))
            {
                sb.Append($"\"externalPath/{Value}\"");
            }
            else
            {
                sb.Append($"\"{Value}\"");
            }
        }
        else // default as always string. Incl TypeSelects
            sb.Append($"\"{Value}\"");
        sb.Append(";\n");

        return sb.ToString();
    }

    public JsVariable Clone(Guid newWidgetId)
    {
        return new JsVariable
        {
            Name = Name,
            Value = Value,
            Type = Type,
            WidgetId = newWidgetId
        };
    }
    
    public static JsVariable? FromJson(JsonElement json, Guid widgetId)
    {
        var name =  json.GetProperty("name").GetString() ?? string.Empty;
        var typeStr =  json.GetProperty("type").GetString() ?? string.Empty;
        if (!Enum.TryParse(typeStr, out WidgetVariableType type) || string.IsNullOrEmpty(name)) return null;
        return new JsVariable
        {
            Name = name,
            Value = json.GetProperty("value").GetString() ?? string.Empty,
            Type = type,
            WidgetId = widgetId
        };
    }
    
    public JsonElement ToJson()
    {
        var obj = new
        {
            name = Name,
            value = Value, /// todo hash if file and find full path need root widget path stuff
            type = Type
        };

        return JsonSerializer.SerializeToElement(obj);
    }
}

[ExcludeFromCodeCoverage]
public partial class Widget
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; }
    public string HtmlPath { get; set; }
    
    public float X { get; set; } = 0;
    public float Y { get; set; } = 0;
    public int Z { get; set; } = 0;

    public int Width { get; set; } = 400;
    public int Height { get; set; } = 400;
    
    public float ScaleX { get; set; } = 1;
    public float ScaleY { get; set; } = 1;

    public List<CssVariable> CssVariables { get; set; } = new();
    public List<JsVariable> JsVariables { get; set; } = new();
 
    [ForeignKey("Route")]
    public Guid RouteId { get; set; }
    public Route Route { get; set; } = null!;

    public bool Visibility { get; set; } = true;

    public string? DocsUrl { get; set; } = string.Empty;

    public Widget(string name, string htmlPath)
    {
        Name = name;
        HtmlPath = htmlPath;
    }

    public Widget Clone(Guid? routeId, string? newName, int? newZ)
    {
        Widget widget = new Widget(string.IsNullOrEmpty(newName) ? Name : newName, HtmlPath);

        widget.RouteId = routeId ?? RouteId;
        widget.Visibility = Visibility;
        widget.X = X;
        widget.Y = Y;
        widget.Z = newZ ?? Z;
        widget.Width = Width;
        widget.Height = Height;
        widget.ScaleX = ScaleX;
        widget.ScaleY = ScaleY;
        
        foreach (var jsVariable in JsVariables)
            widget.JsVariables.Add(jsVariable.Clone(widget.Id));
        foreach (var cssVariable in CssVariables)
            widget.CssVariables.Add(cssVariable.Clone(widget.Id));
        
        return widget;
    }

    /// 
    /***
     * 0) add support for folder type variable -- done
     * 1) method to do best guess scan of file dependencies -- not needed except for parents of htmls
     * 2) list all files, popup with add/remove for overlay -- complex idea
     * 3) util fn to get hashed split truncated paths when given path, do not hash filename/extension -- not whole thing i think, just lowest shared root and dont need split?
     * 4) webserver backend resource fetch - if not file at all try this rel to widget parent -- dont need anymore
     *
     * RULES:
     * - Default, parent of all html files folder will be zipped
     * - fullpath vars will be replaced
     * - hardcoded full paths will be dead, die die die die die
    ***/
    ///
    // todo how to bundle stuff from included folder rel paths but not variables
    
    // in html file, find all relative files. oh god what if it's in the code only. 
    // could like, for each widget, it zips up their whole folder, need to warn that, but it would help resolve relatively imports for anything under
    
    // can also have a metadata field of like "Requires" and it's a list of file paths?
    
    // fullproof would be to export and have all listed files be visible in a list, and let them manually add files too. But then resolving internally is borked - if we can't resolve via code we wouldnt be able to reverse
    
    
    public string GetPathHash()
    {
        // each part, hash, crop to 2-4?
        using var sha256 = SHA256.Create(); 
        return Convert.ToHexString(sha256.ComputeHash(
                Encoding.UTF8.GetBytes(GetPath())))
            .ToLowerInvariant();
    }
    
    public string GetPath() => Path.GetDirectoryName(HtmlPath) ?? HtmlPath;

    public void ScanCssVariables()
    {
        CssVariables = ExtractCssVariablesFromFiles();
    }
    
    public List<CssVariable> ExtractCssVariablesFromFiles()
    {
        var extractedVars = new List<CssVariable>();

        if (!File.Exists(HtmlPath))
            return extractedVars;

        string htmlContent = File.ReadAllText(HtmlPath);
        string baseDir = Path.GetDirectoryName(HtmlPath)!;

        var cssMatches = CssLinkRegex().Matches(htmlContent);

        foreach (Match match in cssMatches)
        {
            string cssFile = match.Groups[1].Value;
            string cssPath = Path.IsPathRooted(cssFile)
                ? cssFile
                : Path.Combine(baseDir, cssFile);

            if (!File.Exists(cssPath))
                continue;

            string cssContent = File.ReadAllText(cssPath);
            var varMatches = CssVarRegex().Matches(cssContent);

            string metaPath = cssPath + ".json";
            Dictionary<string, Dictionary<string, string>>? varTypes = null;
            if (File.Exists(metaPath))
            {
                var metaJson = File.ReadAllText(metaPath);
                varTypes = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(metaJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = false });
            }

            foreach (Match varMatch in varMatches)
            {
                string name = varMatch.Groups[1].Value.Trim();
                string value = varMatch.Groups[2].Value.Trim();
                string? description = string.Empty;
                if (extractedVars.Any(v => v.Name == name)) continue;

                WidgetCssVariableType cssType = WidgetCssVariableType.Default;

                Dictionary<string, string>? metaValue = null;
                if (varTypes is { Count: > 0 })
                    varTypes.TryGetValue(name, out metaValue);
                
                if (metaValue != null)
                {
                    metaValue.TryGetValue("type", out var typeName);
                    Enum.TryParse(typeName, ignoreCase: true, out cssType);
                    metaValue.TryGetValue("description", out description);
                }
                
                extractedVars.Add(new CssVariable
                {
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

    public override string ToString()
    {
        return $"Widget {Name} (Instance {Id})";
    }

    [GeneratedRegex(@"<link[^>]+href\s*=\s*[""']((?!https?:|\/\/)[^""']+\.css)[""']", RegexOptions.IgnoreCase, "en-CA")]
    private static partial Regex CssLinkRegex();
    
    [GeneratedRegex(@"--([a-zA-Z0-9-_]+)\s*:\s*([^;]+);")]
    
    private static partial Regex CssVarRegex();
    // todo oh yeah dummy dont forget filepath stuffs for widget in json for relative shenanigans
    public JsonElement ToJson(string htmlRelPath)
    {
        var obj = new
        {
            name = Name,
            htmlPath = htmlRelPath,

            position = new
            {
                x = X,
                y = Y,
                z = Z
            },

            size = new
            {
                width = Width,
                height = Height
            },

            scale = new
            {
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

        foreach (var css in json.GetProperty("cssVariables").EnumerateArray())
        {
            CssVariable? cssVar = CssVariable.FromJson(css, widget.Id);
            if (cssVar != null) widget.CssVariables.Add(cssVar);
        }

        foreach (var js in json.GetProperty("jsVariables").EnumerateArray())
        {
            JsVariable? jsVar = JsVariable.FromJson(js, widget.Id);
            if (jsVar != null) widget.JsVariables.Add(jsVar);
        }

        return widget;
    }
    
}
