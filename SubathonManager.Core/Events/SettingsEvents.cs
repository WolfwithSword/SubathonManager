using System.Diagnostics.CodeAnalysis;
 
namespace SubathonManager.Core.Events;
 
[ExcludeFromCodeCoverage]
public static class SettingsEvents
{
    public static event Action<bool>? SettingsUnsavedChanges;
    public static event Action? EventVisibilityChanged;
    public static event Action? HotLinkToDevTunnelsRequested;
 
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
}