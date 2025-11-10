namespace SubathonManager.Core.Events;

public static class WebServerEvents
{
    public static event Action<bool>? WebServerStatusChanged;
    
    public static void RaiseWebServerStatusChange(bool status)
    {
        WebServerStatusChanged?.Invoke(status);
    }
}