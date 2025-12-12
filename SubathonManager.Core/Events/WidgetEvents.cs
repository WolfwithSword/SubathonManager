using SubathonManager.Core.Models;
using System.Diagnostics.CodeAnalysis;
namespace SubathonManager.Core.Events;

[ExcludeFromCodeCoverage]
public static class WidgetEvents
{
    public static event Action<Widget>? WidgetPositionUpdated;
    public static event Action<Widget>? WidgetScaleUpdated;
    public static event Action<Guid>? SelectEditorWidget;

    public static void RaisePositionUpdated(Widget widget)
    {
        WidgetPositionUpdated?.Invoke(widget);
    }

    public static void RaiseSelectEditorWidget(Guid widgetId)
    {
        SelectEditorWidget?.Invoke(widgetId);
    }
    
    public static void RaiseScaleUpdated(Widget widget)
    {
        WidgetScaleUpdated?.Invoke(widget);
    }
}