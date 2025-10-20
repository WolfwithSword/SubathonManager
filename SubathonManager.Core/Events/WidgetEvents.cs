using SubathonManager.Core.Models;

namespace SubathonManager.Core.Events;

public static class WidgetEvents
{
    public static event Action<Widget>? WidgetPositionUpdated;
    // TODO refresh existing browser sources on certain events such as this, or just widget update, route update by id maybe?
    
    public static void RaisePositionUpdated(Widget widget)
    {
        WidgetPositionUpdated?.Invoke(widget);
    }
}