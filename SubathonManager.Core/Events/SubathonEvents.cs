using SubathonManager.Core.Models;
namespace SubathonManager.Core.Events;

public static class SubathonEvents
{
    public static event Action<SubathonEvent>? SubathonEventCreated;
    public static event Action<SubathonEvent, bool>? SubathonEventProcessed; // Run through queue, processed or not to subathon
    public static event Action? SubathonEventsDeleted;
    public static event Action<SubathonData, DateTime>? SubathonDataUpdate;
    
    public static event Action<List<SubathonGoal>>? SubathonGoalListUpdated;
    public static event Action<SubathonGoal, int>? SubathonGoalCompleted;

    public static void RaiseSubathonEventsDeleted()
    {
        SubathonEventsDeleted?.Invoke();
    }

    public static void RaiseSubathonEventCreated(SubathonEvent subathonEvent)
    {
        SubathonEventCreated?.Invoke(subathonEvent);
    }
    
    public static void RaiseSubathonEventProcessed(SubathonEvent subathonEvent, bool wasEffective)
    {
        SubathonEventProcessed?.Invoke(subathonEvent, wasEffective);
    }
    
    public static void RaiseSubathonDataUpdate(SubathonData data, DateTime time)
    {
        SubathonDataUpdate?.Invoke(data, time);
    }

    public static void RaiseSubathonGoalCompleted(SubathonGoal goal, int points)
    {
        // do we want all goals?
        SubathonGoalCompleted?.Invoke(goal, points);
    }

    public static void RaiseSubathonGoalListUpdated(List<SubathonGoal> goals)
    {
        SubathonGoalListUpdated?.Invoke(goals);
    }
}