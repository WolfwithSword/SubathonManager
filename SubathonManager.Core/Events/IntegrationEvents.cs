using System.Diagnostics.CodeAnalysis;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Objects;

namespace SubathonManager.Core.Events;

[ExcludeFromCodeCoverage]
public static class IntegrationEvents {
    public static event Action<IntegrationConnection>? ConnectionUpdated; // status, src, acc name, service
    public static event Action<SubathonEventSource, IReadOnlyCollection<string>>? MembershipTiersSynced;
    public static event Action? DevTunnelLegacyNotification;
    public static event Action<SubathonEventSource>? ExternalSourceSeen;
    public static event Action<SubathonEventSource>? ReauthRequired; // previously connected, startup reauth failed

    public static void RaiseReauthRequired(SubathonEventSource source) {
        ReauthRequired?.Invoke(source);
    }

    public static void RaiseConnectionUpdate(IntegrationConnection connection) {
        Utils.UpdateConnection(connection);
        ConnectionUpdated?.Invoke(connection);
    }

    public static void RaiseExternalSourceSeen(SubathonEventSource source) {
        ExternalSourceSeen?.Invoke(source);
    }

    public static void RaiseMembershipTiersSynced(SubathonEventSource source, IReadOnlyCollection<string> tierNames) {
        MembershipTiersSynced?.Invoke(source, tierNames);
    }

    public static void RaiseDevTunnelLegacyNotification() {
        DevTunnelLegacyNotification?.Invoke();
    }
}