namespace SubathonManager.Core.Events;

public static class ErrorMessageEvents
{
    public static event Action<string, string, string, DateTime>? ErrorEventOccured;
    
    public static void RaiseErrorEvent(string level, string source, string message, DateTime time)
    {
        ErrorEventOccured?.Invoke(level, source, message, time);
    }
}