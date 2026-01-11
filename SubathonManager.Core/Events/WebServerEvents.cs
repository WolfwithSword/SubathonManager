using System.Diagnostics.CodeAnalysis;
namespace SubathonManager.Core.Events;

[ExcludeFromCodeCoverage]
public static class WebServerEvents
{
    public static event Action<bool>? WebServerStatusChanged;
    public static event Action<string, bool>? WebSocketIntegrationSourceChange;
    
    public static void RaiseWebServerStatusChange(bool status)
    {
        WebServerStatusChanged?.Invoke(status);
    }

    public static void RaiseWebSocketIntegrationSourceChange(string integrationSource, bool status)
    {
        WebSocketIntegrationSourceChange?.Invoke(integrationSource, status);
    }
}