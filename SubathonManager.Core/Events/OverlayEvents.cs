using System.Diagnostics.CodeAnalysis;
using SubathonManager.Core.Models;

namespace SubathonManager.Core.Events;

[ExcludeFromCodeCoverage]
public static class OverlayEvents
{
    public static event Action<Guid>? OverlayRefreshRequested;
    public static event Action<Guid, float, float, int, int, float, float>? WidgetRefreshRequested;
    public static event Action<Guid, IEnumerable<CssVariable>, IEnumerable<JsVariable>>? WidgetVarsUpdated;

    public static void RaiseOverlayRefreshRequested(Guid routeGuid)
    {
        OverlayRefreshRequested?.Invoke(routeGuid);
    }

    public static void RaiseOverlayRefreshAllRequested()
    {
        OverlayRefreshRequested?.Invoke(Guid.Empty);
    }

    public static void RaiseWidgetVarsUpdated(Guid widgetId,
        IEnumerable<CssVariable> cssVars, IEnumerable<JsVariable> jsVars) =>
        WidgetVarsUpdated?.Invoke(widgetId, cssVars, jsVars);
    
    public static void RaiseWidgetRefreshRequested(Guid widgetId, float x, float y, int width, int height, float scaleX, float scaleY)
        => WidgetRefreshRequested?.Invoke(widgetId, x, y, width, height, scaleX, scaleY);
}