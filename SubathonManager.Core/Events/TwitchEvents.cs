using System.Diagnostics.CodeAnalysis;
namespace SubathonManager.Core.Events;

[ExcludeFromCodeCoverage]
public static class TwitchEvents
{
    public static event Action? TwitchConnected;
    public static void RaiseTwitchConnected()
    {
        TwitchConnected?.Invoke();
    }

}