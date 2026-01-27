using System.Diagnostics.CodeAnalysis;
using SubathonManager.Core.Enums;

namespace SubathonManager.Core.Events;

[ExcludeFromCodeCoverage]
public static class IntegrationEvents
{
    public static event Action<bool, SubathonEventSource, string, string>? ConnectionUpdated; // status, src, acc name, service
    
    public static void RaiseConnectionUpdate(bool status, SubathonEventSource source, string name, string service)
    {
        ConnectionUpdated?.Invoke(status, source, name, service);
    }
}