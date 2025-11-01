namespace SubathonManager.Core.Events;

public static class OverlayEvents
{
    public static event Action<Guid>? OverlayRefreshRequested;
    public static void RaiseOverlayRefreshRequested(Guid routeGuid)
    {
        OverlayRefreshRequested?.Invoke(routeGuid);
    }

    public static void RaiseOverlayRefreshAllRequested()
    {
        OverlayRefreshRequested?.Invoke(Guid.Empty);
    }
}