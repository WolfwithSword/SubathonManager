using System.Diagnostics.CodeAnalysis;
namespace SubathonManager.Core.Events;

[ExcludeFromCodeCoverage]
public static class WebServerEvents
{
    public static event Action<bool>? WebServerStatusChanged;
    
    public static void RaiseWebServerStatusChange(bool status)
    {
        WebServerStatusChanged?.Invoke(status);
    }
}