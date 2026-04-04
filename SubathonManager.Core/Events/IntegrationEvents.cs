using System.Diagnostics.CodeAnalysis;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Objects;

namespace SubathonManager.Core.Events;

[ExcludeFromCodeCoverage]
public static class IntegrationEvents
{
    public static event Action<IntegrationConnection>? ConnectionUpdated; // status, src, acc name, service
    
    public static void RaiseConnectionUpdate(IntegrationConnection connection)
    {
        Utils.UpdateConnection(connection);
        ConnectionUpdated?.Invoke(connection);
    }
}