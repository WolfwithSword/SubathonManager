using System.Diagnostics.CodeAnalysis;
using SubathonManager.Core.Enums;

namespace SubathonManager.Core.Events;

[ExcludeFromCodeCoverage]
public static class SettingsEvents
{
    public static event Action<bool>? SettingsUnsavedChanges;
    public static event Action? EventVisibilityChanged;
    public static event Action? HotLinkToDevTunnelsRequested;
    public static event Action<SubathonEventSource, string?>? HotLinkToSourceRequested;

    public static void RaiseSettingsUnsavedChanges(bool hasPendingChanges)
    {
        SettingsUnsavedChanges?.Invoke(hasPendingChanges);
    }

    public static void RaiseEventVisibilityChanged()
    {
        EventVisibilityChanged?.Invoke();
    }

    public static void RaiseHotLinkToDevTunnelsRequest()
    {
        HotLinkToDevTunnelsRequested?.Invoke();
    }

    public static void RaiseHotLinkToSourceRequest(SubathonEventSource source, string? detail = null)
    {
        HotLinkToSourceRequested?.Invoke(source, detail);
    }
}
