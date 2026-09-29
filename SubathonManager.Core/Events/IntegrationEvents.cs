using System.Diagnostics.CodeAnalysis;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Objects;

namespace SubathonManager.Core.Events;

[ExcludeFromCodeCoverage]
public static class IntegrationEvents {
    public static event Action<IntegrationConnection>? ConnectionUpdated; // status, src, acc name, service
    public static event Action<Dictionary<string, string>>? FourthWallMembershipsSynced;
    public static event Action? DevTunnelLegacyNotification;
    public static event Action<SubathonEventSource>? ExternalSourceSeen;

    public static void RaiseConnectionUpdate(IntegrationConnection connection) {
        Utils.UpdateConnection(connection);
        ConnectionUpdated?.Invoke(connection);
    }

    public static void RaiseExternalSourceSeen(SubathonEventSource source) {
        ExternalSourceSeen?.Invoke(source);
    }

    public static void RaiseFourthWallMembershipsSynced(Dictionary<string, string> synced) {
        FourthWallMembershipsSynced?.Invoke(synced);
    }

    public static void RaiseDevTunnelLegacyNotification() {
        DevTunnelLegacyNotification?.Invoke();
    }
}