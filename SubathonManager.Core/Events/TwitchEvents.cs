namespace SubathonManager.Core.Events;

public static class TwitchEvents
{
    public static event Action? TwitchConnected;
    public static event Action? CommandSettingsUpdated; // TODO can be reused by YT or copied? or split out?

    public static void RaiseTwitchConnected()
    {
        TwitchConnected?.Invoke();
    }

    public static void RaiseCommandSettingsUpdated()
    {
        CommandSettingsUpdated?.Invoke();
    }
}