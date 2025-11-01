using System.Text.RegularExpressions;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

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

    public List<CssVariable> CssVariables { get; set; } = new();
 
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
