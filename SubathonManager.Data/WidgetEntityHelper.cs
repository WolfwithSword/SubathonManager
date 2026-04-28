using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;
using SubathonManager.Core.Events;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Models;
using SubathonManager.Core;
using SubathonManager.Core.Objects;

namespace SubathonManager.Data;

public class WidgetEntityHelper
{

    private readonly ILogger? _logger;
    private readonly IDbContextFactory<AppDbContext> _factory;

    private readonly List<string> _protectedVarNames = ["height", "width", "url", "author", "version"];

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
        WidgetMeta metadata = ExtractWidgetMetadataSync(widget.HtmlPath);
        var oldUrl = widget.DocsUrl;
        widget.DocsUrl =
            !string.IsNullOrWhiteSpace(metadata.Url) && Uri.IsWellFormedUriString(metadata.Url, UriKind.Absolute)
                                                     && !metadata.Url.Trim().Equals(widget.DocsUrl)
                ? metadata.Url.Trim()
                : widget.DocsUrl;

        var (jsVars, extractedNames, updatedVars) = LoadNewJsVariables(widget, metadata);

        using var db = _factory.CreateDbContext();
        if (oldUrl != widget.DocsUrl)
        {
            db.Widgets.First(w => w.Id == widget.Id).DocsUrl = widget.DocsUrl;
        }

        db.JsVariables.AddRange(jsVars);
        // db.JsVariables.UpdateRange(updatedVars);
        foreach (var updated in updatedVars)
        {
            var tracked = db.JsVariables
                .FirstOrDefault(v => v.WidgetId == widget.Id && v.Name == updated.Name);
            if (tracked != null)
            {
                tracked.Value = updated.Value;
                tracked.Description = updated.Description;
                tracked.Type = updated.Type;
            }
        }

        db.SaveChanges();

        _logger?.LogDebug($"[Widget {widget.Name}] Added new JS variables: {jsVars.Count}");

        foreach (var variable in db.JsVariables
                     .Where(v => v.WidgetId == widget.Id && !extractedNames.Contains(v.Name))
                     .ToList())
        {
            if (variable.Type.IsFontVariable() && string.Equals(variable.Name, $"{variable.Type}s"))
            {
                continue;
            }
            db.JsVariables.Remove(variable);
        }

        var seenNames = new HashSet<string>();
        foreach (var variable in db.JsVariables.AsNoTracking()
                     .Where(v => v.WidgetId == widget.Id)
                     .ToList())
        {
            // dupe check
            if (!seenNames.Add(variable.Name))
            {
                db.JsVariables.Remove(variable);
            }
        }

        db.SaveChanges();
    }


    public (List<JsVariable>, List<string>, List<JsVariable>) LoadNewJsVariables(Widget widget, Dictionary<string, string> metadata)
    {
        return LoadNewJsVariables(widget, ConvertHtmlMetaToJsonMeta(metadata));
    }


    public (List<JsVariable>, List<string>, List<JsVariable>) LoadNewJsVariables(Widget widget, WidgetMeta metadata)
    {
        var extractedVars = new List<JsVariable>();
        var extractedNames = new List<string>();
        var updatedVars = new List<JsVariable>();

        foreach (var (varName, metaVar) in metadata.Vars)
        {
            if (string.IsNullOrEmpty(varName) || "/?<>~!@#$%^&*()_+=-{}|\\]['\";:,.".Contains(varName[0])) continue;
            if (extractedNames.Contains(varName)) continue;
            extractedNames.Add(varName);

            var existingVar = widget.JsVariables.Find(v => v.Name == varName);
            var description = metaVar.Description;

            if (metaVar.Type == WidgetVariableType.StringSelect && existingVar != null)
            {
                var oldVals = existingVar.Value.Split(',').Select(v => v.Trim()).ToList();
                var newVals = metaVar.Options ?? ((string)metaVar.Value).Split(',').Select(v => v.Trim()).ToList();
                foreach (var v in newVals)
                    if (!oldVals.Contains(v)) oldVals.Add(v);
                oldVals.RemoveAll(v => !newVals.Contains(v));
                existingVar.Value = string.Join(",", oldVals);
                if (!string.Equals(description, existingVar.Description))
                {
                    existingVar.Description = description;
                }
                updatedVars.Add(existingVar);
                continue;
            }


            if (existingVar != null && existingVar.Type != WidgetVariableType.StringSelect)
            {
                if (!string.Equals(description, existingVar.Description) || existingVar.Type != metaVar.Type)
                {
                    existingVar.Description = description;
                    existingVar.Type = metaVar.Type;
                    updatedVars.Add(existingVar);
                }

                continue;
            }

            var value = metaVar.ValueToString();
            
            if (metaVar.Type is WidgetVariableType.EventTypeSelect or WidgetVariableType.EventSubTypeSelect)
            {
                if (!string.IsNullOrWhiteSpace(value) &&
                    !Enum.TryParse(metaVar.Type.GetClsSingleType(), value, true, out _))
                {
                    value = string.Empty;
                }
            }
            
            extractedVars.Add(new JsVariable
            {
                Name = varName,
                WidgetId = widget.Id,
                Type = metaVar.Type,
                Value = value,
                Description = description
            });
        }

        return (extractedVars, extractedNames, updatedVars);
    }
    
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public WidgetMeta ExtractWidgetMetadataSync(string htmlpath)
    {
        var jsonPath = htmlpath + ".json";
        if (!File.Exists(jsonPath))
        {
            var html = File.ReadAllText(htmlpath);
            Dictionary<string, string> data = GetMetaDataHtml(html);
            try
            {
                File.WriteAllText(jsonPath, JsonSerializer.Serialize(ConvertHtmlMetaToJsonMeta(data), JsonOptions));
                _logger?.LogDebug("Wrote widget meta JSON to {Path}", jsonPath);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to write widget meta JSON to {Path}", jsonPath);
                return new WidgetMeta();
            }
            
        }
        try
        {
            var json = File.ReadAllText(jsonPath);
            return JsonSerializer.Deserialize<WidgetMeta>(json, JsonOptions) ?? new WidgetMeta();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to read widget meta JSON at {Path}", jsonPath);
        }

        return new WidgetMeta();
    }

    public async Task<WidgetMeta> ExtractWidgetMetadata(string htmlpath)
    {
        var jsonPath = htmlpath + ".json";
        if (!File.Exists(jsonPath))
        {
            var html = await File.ReadAllTextAsync(htmlpath);
            Dictionary<string, string> data = GetMetaDataHtml(html);
            try
            {
                await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(ConvertHtmlMetaToJsonMeta(data), JsonOptions));
                _logger?.LogDebug("Wrote widget meta JSON to {Path}", jsonPath);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to write widget meta JSON to {Path}", jsonPath);
                return new WidgetMeta();
            }
        }
        
        try
        {
            var json = await File.ReadAllTextAsync(jsonPath);
            return JsonSerializer.Deserialize<WidgetMeta>(json, JsonOptions) ?? new WidgetMeta();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to read widget meta JSON at {Path}", jsonPath);
        }

        return new WidgetMeta();
    }

   internal WidgetMeta ConvertHtmlMetaToJsonMeta(Dictionary<string, string> data)
    {
        WidgetMeta meta = new()
        {
            Author = data.GetValueOrDefault("Author", string.Empty),
            Url = data.GetValueOrDefault("Url", string.Empty),
            Width = int.TryParse(data.GetValueOrDefault("Width", "400"), out var width) ? width : 400,
            Height = int.TryParse(data.GetValueOrDefault("Height", "200"), out var height) ? height : 200
        };

        foreach (var key in data.Keys)//.Where(x => x.Contains('.')))
        {
            var parts = key.Split('.');
            var varName = parts[0];
            if (_protectedVarNames.Contains(varName.ToLower())) continue; 
            if (parts.Length < 2)
            {
                parts = [varName, "String"]; // default case if missing
            }
            if (string.IsNullOrEmpty(varName) || parts.Length < 2) continue;

            if (!Enum.TryParse(parts[1], true, out WidgetVariableType type)) continue;

            var rawValue = data.GetValueOrDefault(key, string.Empty);
            if (string.Equals(rawValue, "NONE", StringComparison.OrdinalIgnoreCase))
                rawValue = string.Empty;

            WidgetMetaVar wVar = new()
            {
                Name = varName,
                Type = type,
            };

            if (type is WidgetVariableType.StringSelect)
            {
                var options = rawValue.Split(',')
                    .Select(v => v.Trim())
                    .Where(v => !string.IsNullOrEmpty(v) && v != "NONE")
                    .ToList();
                wVar.Options = options;
                wVar.Value = options.Count > 0 ? options[0] : string.Empty;
            }
            else if (type is WidgetVariableType.EventSubTypeSelect or WidgetVariableType.EventTypeSelect)
            {
                wVar.Value = rawValue;
            }
            else if (type.IsListType())
            {
                var items = rawValue.Split(',')
                    .Select(v => v.Trim())
                    .Where(v => !string.IsNullOrEmpty(v) && v != "NONE")
                    .ToList();
                wVar.Value = items;
            }
            else
            {
                wVar.Value = type switch
                {
                    WidgetVariableType.Boolean => bool.TryParse(rawValue, out var b) && b,
                    WidgetVariableType.Int => int.TryParse(rawValue, out var i) ? i : 0,
                    WidgetVariableType.Percent => int.TryParse(rawValue, out var i) ? i : 0,
                    WidgetVariableType.Float => float.TryParse(rawValue, out var d) ? d : 0,
                    _ => rawValue
                };
            }

            meta.Vars[varName] = wVar;
        }

        return meta;
    }
    
    
    private Dictionary<string, string> GetMetaDataHtml(string html) {
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