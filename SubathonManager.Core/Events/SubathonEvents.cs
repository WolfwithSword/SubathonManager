using SubathonManager.Core.Models;
namespace SubathonManager.Core.Events;

public static class SubathonEvents
{
    public static event Action<SubathonEvent>? SubathonEventCreated;
    public static event Action<SubathonData, DateTime>? SubathonDataUpdate;

    public static void RaiseSubathonEventCreated(SubathonEvent _event)
    {
        SubathonEventCreated?.Invoke(_event);
    }
    
    public static void RaiseSubathonDataUpdate(SubathonData data, DateTime time)
    {
        SubathonDataUpdate?.Invoke(data, time);
    }
}