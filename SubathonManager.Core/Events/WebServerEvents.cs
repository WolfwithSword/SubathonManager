using System.Diagnostics.CodeAnalysis;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Objects;

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
        if (!Enum.TryParse(integrationSource, out SubathonEventSource subathonEventSource)) return;
        IntegrationConnection conn = new IntegrationConnection
        {
            Name = "External",
            Status = status,
            Source = subathonEventSource,
            Service = "Socket"
        };
        IntegrationEvents.RaiseConnectionUpdate(conn);
    }
}