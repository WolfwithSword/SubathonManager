using SubathonManager.Core.Models;
namespace SubathonManager.Core.Events;

public static class SubathonEvents
{
    public static event Action<SubathonEvent>? SubathonEventCreated;

    public static void RaiseSubathonEventCreated(SubathonEvent _event)
    {
        SubathonEventCreated?.Invoke(_event);
    }
}