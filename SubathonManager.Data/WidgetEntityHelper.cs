using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using SubathonManager.Core.Events;
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

        foreach (var variable in extracted)
        {
            bool exists = db.CssVariables.Any(v =>
                v.WidgetId == widget.Id && v.Name == variable.Name);

            if (!exists)
            {
                db.CssVariables.Add(variable);
                _logger?.LogDebug($"[Widget {widget.Name}] Added new CSS variable: {variable.Name}");
            }
        }
        db.SaveChanges();
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