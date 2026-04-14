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

    private readonly ILogger? _logger;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public WidgetEntityHelper(IDbContextFactory<AppDbContext>? factory, ILogger? logger)
    {
        _factory = factory ?? AppServices.Provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        _logger = logger ?? AppServices.Provider?.GetRequiredService<ILogger<WidgetEntityHelper>>();
    }
    
    public void SyncCssVariables(Widget widget)
    {
        using var db = _factory.CreateDbContext();
        var extracted = widget.ExtractCssVariablesFromFiles();
        List<string> extractedNames = new List<string>();

        foreach (var variable in extracted)
        {
            var cssVar = db.CssVariables
                .FirstOrDefault(v => v.WidgetId == widget.Id && v.Name == variable.Name);
            
            if (cssVar == null)
            {
                db.CssVariables.Add(variable);
                _logger?.LogDebug($"[Widget {widget.Name}] Added new CSS variable: {variable.Name}");
            }
            else
            {
                if (cssVar.Type != variable.Type)
                {
                    cssVar.Type = variable.Type;
                }

                if (cssVar.Description != variable.Description)
                {
                    cssVar.Description = variable.Description;
                }
            }
            extractedNames.Add(variable.Name);
        }

        db.SaveChanges();
        foreach (var variable in db.CssVariables.AsNoTracking()
                     .Where(v => v.WidgetId == widget.Id && !extractedNames.Contains(v.Name))
                     .ToList())
        {
            db.CssVariables.Remove(variable);
        }
        
        //dedupe
        var seenNames = new HashSet<string>();
        foreach (var variable in db.CssVariables.AsNoTracking()
                     .Where(v => v.WidgetId == widget.Id)
                     .ToList())
        {
            if (!seenNames.Add(variable.Name))
            {
                db.CssVariables.Remove(variable);
            }
        }
        db.SaveChanges();
    }

    public void SyncJsVariables(Widget widget)
    {
        Dictionary<string, string> metadata = ExtractWidgetMetadataSync(widget.HtmlPath);
        var oldUrl = widget.DocsUrl;
        widget.DocsUrl = metadata.TryGetValue("Url", out var u) && !string.IsNullOrWhiteSpace(u)
                                                                && Uri.IsWellFormedUriString(u, UriKind.Absolute)
                                                                && !u.Trim().Equals(widget.DocsUrl)
            ? u.Trim()
            : widget.DocsUrl;
        
        var (jsVars, extractedNames, updatedVars) = LoadNewJsVariables(widget, metadata);
        
        using var db = _factory.CreateDbContext();
        if (oldUrl != widget.DocsUrl)
        {
            db.Widgets.First(w => w.Id == widget.Id).DocsUrl =  widget.DocsUrl;
        }
        db.JsVariables.AddRange(jsVars);
        // db.JsVariables.UpdateRange(updatedVars);
        foreach (var updated in updatedVars)
        {
            var tracked = db.JsVariables
                .FirstOrDefault(v => v.WidgetId == widget.Id && v.Name == updated.Name);
            if (tracked != null)
                tracked.Value = updated.Value;
        }
        db.SaveChanges();

        _logger?.LogDebug($"[Widget {widget.Name}] Added new JS variables: {jsVars.Count}");
        
        foreach (var variable in db.JsVariables
                     .Where(v => v.WidgetId == widget.Id && !extractedNames.Contains(v.Name))
                     .ToList())
        {
            db.JsVariables.Remove(variable);
        }
        
        var seenNames = new HashSet<string>();
        foreach (var variable in db.JsVariables.AsNoTracking()
                     .Where(v => v.WidgetId == widget.Id)
                     .ToList())
        {
            if (!seenNames.Add(variable.Name))
            {
                db.JsVariables.Remove(variable);
            }
        }

        db.SaveChanges();
    }
    
    public (List<JsVariable>, List<string>, List<JsVariable>) LoadNewJsVariables(Widget widget, Dictionary<string, string> metadata)
    {
        var extractedVars = new List<JsVariable>();
        var extractedNames = new List<string>();
        var updatedVars = new List<JsVariable>();
        foreach (var key in metadata.Keys)
        {
            if (key.Count(c => c == '.') != 1) continue;
            JsVariable jVar = new JsVariable
            {
                Name = key.Split('.')[0]
            };
            if (string.IsNullOrEmpty(jVar.Name) || "/?<>~!@#$%^&*()_+=-{}|\\]['\";:,.".Contains(jVar.Name[0])) continue;
            if (extractedNames.Contains(jVar.Name)) continue;
            extractedNames.Add(jVar.Name);
            if (widget.JsVariables.Any(v => v.Name == jVar.Name 
                                            && v.Type != WidgetVariableType.StringSelect)) continue;
            
            jVar.Value = metadata[key];
            if (Enum.TryParse<WidgetVariableType>(key.Split('.')[1], ignoreCase: true, out var type))
                jVar.Type = type;
            if (jVar.Value == "NONE") jVar.Value = string.Empty;
            if (jVar.Type is WidgetVariableType.EventTypeSelect or WidgetVariableType.EventSubTypeSelect)
            {
                if (!string.IsNullOrWhiteSpace(jVar.Value) &&
                    !Enum.TryParse(jVar.Type.GetClsSingleType(), jVar.Value, true, out _))
                {
                    jVar.Value = string.Empty;
                }
            }
            else if (jVar.Type == WidgetVariableType.StringSelect && widget.JsVariables.Any(v => v.Name == jVar.Name))
            {
                var oldJVar = widget.JsVariables.Find(v => v.Name == jVar.Name);
                if (oldJVar != null)
                {
                    var oldVals = oldJVar.Value.Split(',').ToList();
                    var newVals = jVar.Value.Split(',').ToList();
                    foreach (var v in newVals)
                    {
                        if (!oldVals.Contains(v)) oldVals.Add(v);
                    }

                    oldVals.RemoveAll(v => !newVals.Contains(v));
                    oldJVar.Value = string.Join(",", oldVals);
                    updatedVars.Add(oldJVar);
                    continue;
                }
            }
            jVar.WidgetId = widget.Id;
            extractedVars.Add(jVar);
        }
        return (extractedVars, extractedNames, updatedVars);
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

    private async Task<(Widget?, DbContext?)> GetWidgetForUpdate(string widgetId, Dictionary<string, JsonElement> data)
    {
        if (data.Count == 0 || !Guid.TryParse(widgetId, out var widgetGuid)) return (null, null);
        var db = await _factory.CreateDbContextAsync();
        var widget = await db.Widgets.FirstOrDefaultAsync(w => w.Id == widgetGuid);
        return widget == null ? (null, db) : (widget, db);
    }
    
    public async Task<bool> UpdateWidgetScale(string widgetId, Dictionary<string, JsonElement> data)
    {
        (Widget?, DbContext?) result = await GetWidgetForUpdate(widgetId, data);
        var widget = result.Item1;
        await using var db = result.Item2;
        if (widget == null || db == null) return false;
        
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
    
    public async Task<bool> UpdateWidgetPosition(string widgetId, Dictionary<string, JsonElement> data)
    {
        (Widget?, DbContext?) result = await GetWidgetForUpdate(widgetId, data);
        var widget = result.Item1;
        await using var db = result.Item2;
        if (widget == null || db == null) return false;
            
        if (data.TryGetValue("x", out var xElem) && xElem.TryGetSingle(out var x)) widget.X = x;
        if (data.TryGetValue("y", out var yElem) && yElem.TryGetSingle(out var y)) widget.Y = y;
        if (data.TryGetValue("z", out var zElem) && zElem.TryGetInt32(out var z)) widget.Z = z;
                
        await db.SaveChangesAsync();
        WidgetEvents.RaisePositionUpdated(widget);
        await db.Entry(widget).ReloadAsync();
        return true;

    }
    
    public async Task<bool> UpdateWidgetDimensions(string widgetId, Dictionary<string, JsonElement> data)
    {
        (Widget?, DbContext?) result = await GetWidgetForUpdate(widgetId, data);
        var widget = result.Item1;
        await using var db = result.Item2;
        if (widget == null || db == null) return false;
        
        if (data.TryGetValue("width", out var wEl) && wEl.TryGetInt32(out int w)) widget.Width  = w;
        if (data.TryGetValue("height", out var hEl) && hEl.TryGetInt32(out int h)) widget.Height = h;
        if (data.TryGetValue("x", out var xEl) && xEl.TryGetSingle(out float x)) widget.X = x;
        if (data.TryGetValue("y", out var yEl) && yEl.TryGetSingle(out float y)) widget.Y = y;
                
        await db.SaveChangesAsync();
        WidgetEvents.RaiseSizeUpdated(widget);
        await db.Entry(widget).ReloadAsync();
        return true;
    }
}