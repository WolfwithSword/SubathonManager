using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;
using SubathonManager.Core.Events;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Models;
using SubathonManager.Core;

namespace SubathonManager.Data;

public class WidgetEntityHelper
{
    
    private readonly ILogger? _logger = AppServices.Provider.GetRequiredService<ILogger<WidgetEntityHelper>>();
    public void SyncCssVariables(Widget widget)
    {
        using var db = new AppDbContext();
        var extracted = widget.ExtractCssVariablesFromFiles();
        List<string> extractedNames = new List<string>();

        foreach (var variable in extracted)
        {
            bool exists = db.CssVariables.Any(v =>
                v.WidgetId == widget.Id && v.Name == variable.Name);

            if (!exists)
            {
                db.CssVariables.Add(variable);
                _logger?.LogDebug($"[Widget {widget.Name}] Added new CSS variable: {variable.Name}");
            }
            extractedNames.Add(variable.Name);
        }

        foreach (var variable in db.CssVariables
                     .Where(v => v.WidgetId == widget.Id && !extractedNames.Contains(v.Name))
                     .ToList())
        {
            db.CssVariables.Remove(variable);
        }
        db.SaveChanges();
    }

    public void SyncJsVariables(Widget widget)
    {
        Dictionary<string, string> metadata = ExtractWidgetMetadataSync(widget.HtmlPath);
        
        (var jsVars, var extractedNames) = LoadNewJsVariables(widget, metadata);
        
        using var db = new AppDbContext();
        db.JsVariables.AddRange(jsVars);
        _logger?.LogDebug($"[Widget {widget.Name}] Added new JS variables: {jsVars.Count}");
        
        foreach (var variable in db.JsVariables
                     .Where(v => v.WidgetId == widget.Id && !extractedNames.Contains(v.Name))
                     .ToList())
        {
            db.JsVariables.Remove(variable);
        }
        
        db.SaveChanges();
    }
    
    public (List<JsVariable>, List<string>) LoadNewJsVariables(Widget widget, Dictionary<string, string> metadata)
    {
        var extractedVars = new List<JsVariable>();
        var extractedNames = new List<string>();
        foreach (var key in metadata.Keys)
        {
            if (key.Count(c => c == '.') != 1) continue;
            JsVariable jVar = new JsVariable();
            jVar.Name = key.Split('.')[0];
            if (string.IsNullOrEmpty(jVar.Name) || "/?<>~!@#$%^&*()_+=-{}|\\]['\";:,.".Contains(jVar.Name[0])) continue;
            extractedNames.Add(jVar.Name);
            if (widget.JsVariables.Any(v => v.Name == jVar.Name)) continue;
            
            jVar.Value = metadata[key];
            if (Enum.TryParse<WidgetVariableType>(key.Split('.')[1], ignoreCase: true, out var type))
                jVar.Type = type;
            jVar.WidgetId = widget.Id;
            extractedVars.Add(jVar);
        }
        return (extractedVars, extractedNames);
    }

    public Dictionary<string, string> ExtractWidgetMetadataSync(string htmlpath)
    {
        var html = File.ReadAllText(htmlpath);
        return GetMetaData(html);
    }

    public async Task<Dictionary<string, string>> ExtractWidgetMetadata(string htmlpath)
    {

        var html = await File.ReadAllTextAsync(htmlpath);
        return GetMetaData(html);
    }
    
    private Dictionary<string, string> GetMetaData(string html) {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pattern = @"<!--\s*WIDGET_META(.*?)END_WIDGET_META\s*-->";
        var match  = Regex.Match(html, pattern, RegexOptions.Singleline);

        if (!match.Success)
            return result;

        var block = match.Groups[1].Value;

        var lines = block.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        
        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            var index = trimmed.IndexOf(':');
            if (index <= 0 || index == trimmed.Length - 1)
                continue;

            var key = trimmed.Substring(0, index).Trim();
            var value = trimmed.Substring(index + 1).Trim();

            if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(value))
                result[key] = value;
        }
        return result;
    }
    
    public async Task<bool> UpdateWidgetScale(string widgetId, Dictionary<string, JsonElement> data)
    {
        if (!data.Any()) return false;
        
        if (Guid.TryParse(widgetId, out var widgetGuid))
        {
            using var db = new AppDbContext();
            var widget = await db.Widgets.FirstOrDefaultAsync(w => w.Id == widgetGuid);
            if (widget != null)
            {
                float origX = widget.X;
                float origY = widget.Y;
                if (data.TryGetValue("scaleX", out var sxElem) && sxElem.TryGetSingle(out var sx)) widget.ScaleX = sx;
                if (data.TryGetValue("scaleY", out var syElem) && syElem.TryGetSingle(out var sy)) widget.ScaleY = sy;
                if (data.TryGetValue("x", out var xElem) && xElem.TryGetSingle(out var x)) widget.X = x;
                if (data.TryGetValue("y", out var yElem) && yElem.TryGetSingle(out var y)) widget.Y = y;
                
                await db.SaveChangesAsync();
                WidgetEvents.RaiseScaleUpdated(widget);
                if (!origX.Equals(widget.X) || !origY.Equals(widget.Y))
                    WidgetEvents.RaisePositionUpdated(widget);
                await db.Entry(widget).ReloadAsync();
                return true;
            }
        }
        return false;
    }
    
    public async Task<bool> UpdateWidgetPosition(string widgetId, Dictionary<string, JsonElement> data)
    {
        if (!data.Any()) return false;
        
        if (Guid.TryParse(widgetId, out var widgetGuid))
        {
            using var db = new AppDbContext();
            var widget = await db.Widgets.FirstOrDefaultAsync(w => w.Id == widgetGuid);
            if (widget != null)
            {
                if (data.TryGetValue("x", out var xElem) && xElem.TryGetSingle(out var x)) widget.X = x;
                if (data.TryGetValue("y", out var yElem) && yElem.TryGetSingle(out var y)) widget.Y = y;
                if (data.TryGetValue("z", out var zElem) && zElem.TryGetInt32(out var z)) widget.Z = z;
                
                await db.SaveChangesAsync();
                WidgetEvents.RaisePositionUpdated(widget);
                await db.Entry(widget).ReloadAsync();
                return true;
            }
        }

        return false;
    }
}