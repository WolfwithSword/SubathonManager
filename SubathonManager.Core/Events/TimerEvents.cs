using System.Diagnostics.CodeAnalysis;
namespace SubathonManager.Core.Events;

[ExcludeFromCodeCoverage]
public static class TimerEvents
{
    public static event Action<TimeSpan>? TimerTickEvent;
    
    public static void RaiseTimerTickEvent(TimeSpan time)
    {
        TimerTickEvent?.Invoke(time);
    }
}