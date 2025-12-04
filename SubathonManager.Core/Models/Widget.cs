using System.Text.RegularExpressions;
using System.Text;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SubathonManager.Core.Enums;

namespace SubathonManager.Core.Models;

public class CssVariable
{
    [Key]
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;

    
    [ForeignKey("Widget")]
    public Guid WidgetId { get; set; }
    public Widget Widget { get; set; } = null!;
}

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
        else if (Type == WidgetVariableType.Float && float.TryParse(Value, out var f))
            sb.Append($"{f}");
        else if (Type == WidgetVariableType.Int && Int16.TryParse(Value, out var intValue))
            sb.Append($"{intValue}");
        else if (Type == WidgetVariableType.Float || Type == WidgetVariableType.Int || Type == WidgetVariableType.String)
            sb.Append($"\"{Value}\"");
        else if (Type == WidgetVariableType.EventTypeList || Type == WidgetVariableType.StringList)
        {
            string val = string.Join(",", Value.Split(',').Select(s =>
            {
                s = s.Trim();
                if (!s.StartsWith("\"")) s = "\"" + s;
                if (!s.EndsWith("\"")) s = s + "\"";
                return s;
            }));
            sb.Append($"[{val}]");
        }
        sb.Append(";\n");

        return sb.ToString();
    }
}

public class Widget
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

    public Widget(string name, string htmlPath)
    {
        Name = name;
        HtmlPath = htmlPath;
    }

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

        var cssMatches = Regex.Matches(
            htmlContent,
            @"<link[^>]+href\s*=\s*[""']((?!https?:|\/\/)[^""']+\.css)[""']",
            RegexOptions.IgnoreCase
        );

        foreach (Match match in cssMatches)
        {
            string cssFile = match.Groups[1].Value;
            string cssPath = Path.IsPathRooted(cssFile)
                ? cssFile
                : Path.Combine(baseDir, cssFile);

            if (!File.Exists(cssPath))
                continue;

            string cssContent = File.ReadAllText(cssPath);
            var varMatches = Regex.Matches(cssContent, @"--([a-zA-Z0-9-_]+)\s*:\s*([^;]+);");

            foreach (Match varMatch in varMatches)
            {
                string name = varMatch.Groups[1].Value.Trim();
                string value = varMatch.Groups[2].Value.Trim();

                if (!extractedVars.Any(v => v.Name == name))
                {
                    extractedVars.Add(new CssVariable
                    {
                        Name = name,
                        Value = value,
                        WidgetId = Id
                    });
                }
            }
        }
        return extractedVars;
    }

    public override string ToString()
    {
        return $"Widget {Name} (Instance {Id})";
    }
    
}
