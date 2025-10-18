namespace SubathonManager.Core.Events;

public class TimerEvents
{
    public static event Action<TimeSpan>? TimerTickEvent;
    
    public static void RaiseTimerTickEvent(TimeSpan time)
    {
        TimerTickEvent?.Invoke(time);
    }
}