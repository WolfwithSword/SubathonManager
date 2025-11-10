using SubathonManager.Core.Models;

namespace SubathonManager.Core.Events;

public static class WidgetEvents
{
    public static event Action<Widget>? WidgetPositionUpdated;

    public static void RaisePositionUpdated(Widget widget)
    {
        WidgetPositionUpdated?.Invoke(widget);
    }
}