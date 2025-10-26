using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SubathonManager.Core.Events;
using SubathonManager.Core.Models;

namespace SubathonManager.Data;

public class WidgetEntityHelper
{
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
                Console.WriteLine($"[Widget {widget.Name}] Added new CSS variable: {variable.Name}");
            }
        }
        db.SaveChanges();
        // db.Entry(widget).Reload();
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