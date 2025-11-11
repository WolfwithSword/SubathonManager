using SubathonManager.Core.Models;

namespace SubathonManager.Core.Events;

public static class WidgetEvents
{
    public static event Action<Widget>? WidgetPositionUpdated;
    public static event Action<Guid>? SelectEditorWidget;

    public static void RaisePositionUpdated(Widget widget)
    {
        WidgetPositionUpdated?.Invoke(widget);
    }

    public static void RaiseSelectEditorWidget(Guid widgetId)
    {
        SelectEditorWidget?.Invoke(widgetId);
    }
}