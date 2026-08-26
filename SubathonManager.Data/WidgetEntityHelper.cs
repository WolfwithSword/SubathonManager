using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;

namespace SubathonManager.Data;

public class WidgetEntityHelper {
    private static readonly JsonSerializerOptions JsonOptions = new() {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IDbContextFactory<AppDbContext> _factory;

    private readonly ILogger? _logger;

    private readonly List<string> _protectedVarNames = ["height", "width", "url", "author", "version"];

    public WidgetEntityHelper(IDbContextFactory<AppDbContext>? factory, ILogger? logger) {
        _factory = factory ?? AppServices.Provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        _logger = logger ?? AppServices.Provider?.GetRequiredService<ILogger<WidgetEntityHelper>>();
    }

    public void SyncCssVariables(Widget widget) {
        using AppDbContext db = _factory.CreateDbContext();
        List<CssVariable> extracted = widget.ExtractCssVariablesFromFiles();
        var extractedNames = new List<string>();

        foreach (CssVariable variable in extracted) {
            CssVariable? cssVar = db.CssVariables
                .FirstOrDefault(v => v.WidgetId == widget.Id && v.Name == variable.Name);

            if (cssVar == null) {
                db.CssVariables.Add(variable);
                _logger?.LogDebug($"[Widget {widget.Name}] Added new CSS variable: {variable.Name}");
            }
            else {
                if (cssVar.Type != variable.Type) cssVar.Type = variable.Type;

                if (cssVar.Description != variable.Description) cssVar.Description = variable.Description;
            }

            extractedNames.Add(variable.Name);
        }

        db.SaveChanges();
        foreach (CssVariable variable in db.CssVariables.AsNoTracking()
                     .Where(v => v.WidgetId == widget.Id && !extractedNames.Contains(v.Name))
                     .ToList())
            db.CssVariables.Remove(variable);

        //dedupe
        var seenNames = new HashSet<string>();
        foreach (CssVariable variable in db.CssVariables.AsNoTracking()
                     .Where(v => v.WidgetId == widget.Id)
                     .ToList())
            if (!seenNames.Add(variable.Name))
                db.CssVariables.Remove(variable);

        db.SaveChanges();
    }

    public void SyncJsVariables(Widget widget) {
        WidgetMeta metadata = ExtractWidgetMetadataSync(widget.HtmlPath);
        string? oldUrl = widget.DocsUrl;
        widget.DocsUrl =
            !string.IsNullOrWhiteSpace(metadata.Url) && Uri.IsWellFormedUriString(metadata.Url, UriKind.Absolute)
                                                     && !metadata.Url.Trim().Equals(widget.DocsUrl)
                ? metadata.Url.Trim()
                : widget.DocsUrl;

        (List<JsVariable> jsVars, List<string> extractedNames, List<JsVariable> updatedVars) =
            LoadNewJsVariables(widget, metadata);

        using AppDbContext db = _factory.CreateDbContext();
        if (oldUrl != widget.DocsUrl) db.Widgets.First(w => w.Id == widget.Id).DocsUrl = widget.DocsUrl;

        db.JsVariables.AddRange(jsVars);
        // db.JsVariables.UpdateRange(updatedVars);
        foreach (JsVariable updated in updatedVars) {
            JsVariable? tracked = db.JsVariables
                .FirstOrDefault(v => v.WidgetId == widget.Id && v.Name == updated.Name);
            if (tracked != null) {
                tracked.Value = updated.Value;
                tracked.Description = updated.Description;
                tracked.Type = updated.Type;
            }
        }

        db.SaveChanges();

        _logger?.LogDebug($"[Widget {widget.Name}] Added new JS variables: {jsVars.Count}");

        foreach (JsVariable variable in db.JsVariables
                     .Where(v => v.WidgetId == widget.Id && !extractedNames.Contains(v.Name))
                     .ToList()) {
            if (variable.Type.IsFontVariable() && string.Equals(variable.Name, $"{variable.Type}s")) continue;
            db.JsVariables.Remove(variable);
        }

        var seenNames = new HashSet<string>();
        foreach (JsVariable variable in db.JsVariables.AsNoTracking()
                     .Where(v => v.WidgetId == widget.Id)
                     .ToList())
            // dupe check
            if (!seenNames.Add(variable.Name))
                db.JsVariables.Remove(variable);

        List<WidgetVariableType> existingFontTypesInDb = db.JsVariables
            .Where(v => v.WidgetId == widget.Id && WidgetVariableTypeHelper.FontVariables.ToList().Contains(v.Type))
            .Select(v => v.Type)
            .Distinct()
            .ToList();

        List<WidgetVariableType> missingFontTypes = WidgetVariableTypeHelper.FontVariables
            .Where(x => !existingFontTypesInDb.Contains(x))
            .ToList();
        foreach (WidgetVariableType fontType in missingFontTypes)
            db.JsVariables.Add(new JsVariable {
                WidgetId = widget.Id,
                Type = fontType,
                Name = $"{fontType}s",
                Description = $"Custom font names to include from {fontType}s, comma separated",
                Value = string.Empty
            });

        db.SaveChanges();
    }


    public (List<JsVariable>, List<string>, List<JsVariable>) LoadNewJsVariables(Widget widget,
        Dictionary<string, string> metadata) {
        return LoadNewJsVariables(widget, ConvertHtmlMetaToJsonMeta(metadata));
    }


    public (List<JsVariable>, List<string>, List<JsVariable>) LoadNewJsVariables(Widget widget, WidgetMeta metadata) {
        var extractedVars = new List<JsVariable>();
        var extractedNames = new List<string>();
        var updatedVars = new List<JsVariable>();

        foreach ((string varName, WidgetMetaVar metaVar) in metadata.Vars) {
            if (string.IsNullOrEmpty(varName) || "/?<>~!@#$%^&*()_+=-{}|\\]['\";:,.".Contains(varName[0])) continue;
            if (extractedNames.Contains(varName)) continue;
            extractedNames.Add(varName);

            JsVariable? existingVar = widget.JsVariables.Find(v => v.Name == varName);
            string description = metaVar.Description;

            if (metaVar.Type == WidgetVariableType.StringSelect && existingVar != null) {
                List<string> oldVals = existingVar.Value.Split(',').Select(v => v.Trim()).ToList();
                List<string> newVals =
                    metaVar.Options ?? ((string)metaVar.Value).Split(',').Select(v => v.Trim()).ToList();
                foreach (string v in newVals)
                    if (!oldVals.Contains(v))
                        oldVals.Add(v);
                oldVals.RemoveAll(v => !newVals.Contains(v));
                existingVar.Value = string.Join(",", oldVals);
                if (!string.Equals(description, existingVar.Description)) existingVar.Description = description;
                updatedVars.Add(existingVar);
                continue;
            }


            if (existingVar != null && existingVar.Type != WidgetVariableType.StringSelect) {
                if (!string.Equals(description, existingVar.Description) || existingVar.Type != metaVar.Type) {
                    existingVar.Description = description;
                    existingVar.Type = metaVar.Type;
                    updatedVars.Add(existingVar);
                }

                continue;
            }

            string value = metaVar.ValueToString();

            if (metaVar.Type is WidgetVariableType.EventTypeSelect or WidgetVariableType.EventSubTypeSelect)
                if (!string.IsNullOrWhiteSpace(value) &&
                    !Enum.TryParse(metaVar.Type.GetClsSingleType(), value, true, out _))
                    value = string.Empty;

            extractedVars.Add(new JsVariable {
                Name = varName,
                WidgetId = widget.Id,
                Type = metaVar.Type,
                Value = value,
                Description = description
            });
        }

        return (extractedVars, extractedNames, updatedVars);
    }

    public WidgetMeta ExtractWidgetMetadataSync(string htmlpath) {
        string jsonPath = htmlpath + ".json";
        if (!EnsureMetaSidecar(htmlpath, jsonPath, out WidgetMeta packedFallback))
            return packedFallback;

        try {
            string? json = WidgetFiles.Current.ReadAllText(jsonPath);
            if (json != null)
                return JsonSerializer.Deserialize<WidgetMeta>(json, JsonOptions) ?? new WidgetMeta();
        }
        catch (Exception ex) {
            _logger?.LogWarning(ex, "Failed to read widget meta JSON at {Path}", jsonPath);
        }

        return new WidgetMeta();
    }

    public Task<WidgetMeta> ExtractWidgetMetadata(string htmlpath) {
        return Task.FromResult(ExtractWidgetMetadataSync(htmlpath));
    }

    private bool EnsureMetaSidecar(string htmlpath, string jsonPath, out WidgetMeta packedFallback) {
        packedFallback = new WidgetMeta();

        if (WidgetFiles.Current.Exists(jsonPath)) return true;
        if (WidgetFiles.Current.IsPacked(htmlpath)) return false;

        string? html = WidgetFiles.Current.ReadAllText(htmlpath);
        if (html == null) return false;

        try {
            WidgetMeta meta = ConvertHtmlMetaToJsonMeta(GetMetaDataHtml(html));
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(meta, JsonOptions));
            _logger?.LogDebug("Wrote widget meta JSON to {Path}", jsonPath);
            return true;
        }
        catch (Exception ex) {
            _logger?.LogWarning(ex, "Failed to write widget meta JSON to {Path}", jsonPath);
            return false;
        }
    }

    internal WidgetMeta ConvertHtmlMetaToJsonMeta(Dictionary<string, string> data) {
        WidgetMeta meta = new() {
            Author = data.GetValueOrDefault("Author", string.Empty),
            Url = data.GetValueOrDefault("Url", string.Empty),
            Width = int.TryParse(data.GetValueOrDefault("Width", "400"), out int width) ? width : 400,
            Height = int.TryParse(data.GetValueOrDefault("Height", "200"), out int height) ? height : 200
        };

        foreach (string key in data.Keys) //.Where(x => x.Contains('.')))
        {
            string[] parts = key.Split('.');
            string varName = parts[0];
            if (_protectedVarNames.Contains(varName.ToLower())) continue;
            if (parts.Length < 2) parts = [varName, "String"]; // default case if missing
            if (string.IsNullOrEmpty(varName) || parts.Length < 2) continue;

            if (!Enum.TryParse(parts[1], true, out WidgetVariableType type)) continue;

            string rawValue = data.GetValueOrDefault(key, string.Empty);
            if (string.Equals(rawValue, "NONE", StringComparison.OrdinalIgnoreCase))
                rawValue = string.Empty;

            WidgetMetaVar wVar = new() {
                Name = varName,
                Type = type
            };

            if (type is WidgetVariableType.StringSelect) {
                List<string> options = rawValue.Split(',')
                    .Select(v => v.Trim())
                    .Where(v => !string.IsNullOrEmpty(v) && v != "NONE")
                    .ToList();
                wVar.Options = options;
                wVar.Value = options.Count > 0 ? options[0] : string.Empty;
            }
            else if (type is WidgetVariableType.EventSubTypeSelect or WidgetVariableType.EventTypeSelect) {
                wVar.Value = rawValue;
            }
            else if (type.IsListType()) {
                List<string> items = rawValue.Split(',')
                    .Select(v => v.Trim())
                    .Where(v => !string.IsNullOrEmpty(v) && v != "NONE")
                    .ToList();
                wVar.Value = items;
            }
            else {
                wVar.Value = type switch {
                    WidgetVariableType.Boolean => bool.TryParse(rawValue, out bool b) && b,
                    WidgetVariableType.Int => int.TryParse(rawValue, out int i) ? i : 0,
                    WidgetVariableType.Percent => int.TryParse(rawValue, out int i) ? i : 0,
                    WidgetVariableType.Float => float.TryParse(rawValue, out float d) ? d : 0,
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
        Match match = Regex.Match(html, pattern, RegexOptions.Singleline);

        if (!match.Success)
            return result;

        string block = match.Groups[1].Value;

        string[] lines = block.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (string line in lines) {
            string trimmed = line.Trim();

            int index = trimmed.IndexOf(':');
            if (index <= 0 || index == trimmed.Length - 1)
                continue;

            string key = trimmed.Substring(0, index).Trim();
            string value = trimmed.Substring(index + 1).Trim();

            if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(value))
                result[key] = value;
        }

        return result;
    }

    private async Task<(Widget?, DbContext?)>
        GetWidgetForUpdate(string widgetId, Dictionary<string, JsonElement> data) {
        if (data.Count == 0 || !Guid.TryParse(widgetId, out Guid widgetGuid)) return (null, null);
        AppDbContext db = await _factory.CreateDbContextAsync();
        Widget? widget = await db.Widgets.FirstOrDefaultAsync(w => w.Id == widgetGuid);
        return widget == null ? (null, db) : (widget, db);
    }

    public async Task<bool> UpdateWidgetScale(string widgetId, Dictionary<string, JsonElement> data) {
        (Widget?, DbContext?) result = await GetWidgetForUpdate(widgetId, data);
        Widget? widget = result.Item1;
        await using DbContext? db = result.Item2;
        if (widget == null || db == null) return false;

        float origX = widget.X;
        float origY = widget.Y;
        if (data.TryGetValue("scaleX", out JsonElement sxElem) && sxElem.TryGetSingle(out float sx)) widget.ScaleX = sx;
        if (data.TryGetValue("scaleY", out JsonElement syElem) && syElem.TryGetSingle(out float sy)) widget.ScaleY = sy;
        if (data.TryGetValue("x", out JsonElement xElem) && xElem.TryGetSingle(out float x)) widget.X = x;
        if (data.TryGetValue("y", out JsonElement yElem) && yElem.TryGetSingle(out float y)) widget.Y = y;

        await db.SaveChangesAsync();
        WidgetEvents.RaiseScaleUpdated(widget);
        if (!origX.Equals(widget.X) || !origY.Equals(widget.Y))
            WidgetEvents.RaisePositionUpdated(widget);
        await db.Entry(widget).ReloadAsync();
        return true;
    }

    public async Task<bool> UpdateWidgetPosition(string widgetId, Dictionary<string, JsonElement> data) {
        (Widget?, DbContext?) result = await GetWidgetForUpdate(widgetId, data);
        Widget? widget = result.Item1;
        await using DbContext? db = result.Item2;
        if (widget == null || db == null) return false;

        if (data.TryGetValue("x", out JsonElement xElem) && xElem.TryGetSingle(out float x)) widget.X = x;
        if (data.TryGetValue("y", out JsonElement yElem) && yElem.TryGetSingle(out float y)) widget.Y = y;
        if (data.TryGetValue("z", out JsonElement zElem) && zElem.TryGetInt32(out int z)) widget.Z = z;

        await db.SaveChangesAsync();
        WidgetEvents.RaisePositionUpdated(widget);
        await db.Entry(widget).ReloadAsync();
        return true;
    }

    public async Task<bool> UpdateWidgetDimensions(string widgetId, Dictionary<string, JsonElement> data) {
        (Widget?, DbContext?) result = await GetWidgetForUpdate(widgetId, data);
        Widget? widget = result.Item1;
        await using DbContext? db = result.Item2;
        if (widget == null || db == null) return false;

        if (data.TryGetValue("width", out JsonElement wEl) && wEl.TryGetInt32(out int w)) widget.Width = w;
        if (data.TryGetValue("height", out JsonElement hEl) && hEl.TryGetInt32(out int h)) widget.Height = h;
        if (data.TryGetValue("x", out JsonElement xEl) && xEl.TryGetSingle(out float x)) widget.X = x;
        if (data.TryGetValue("y", out JsonElement yEl) && yEl.TryGetSingle(out float y)) widget.Y = y;

        await db.SaveChangesAsync();
        WidgetEvents.RaiseSizeUpdated(widget);
        await db.Entry(widget).ReloadAsync();
        return true;
    }
}