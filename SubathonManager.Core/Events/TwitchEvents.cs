namespace SubathonManager.Core.Events;

public static class TwitchEvents
{
    public static event Action? TwitchConnected;
    public static void RaiseTwitchConnected()
    {
        TwitchConnected?.Invoke();
    }

}