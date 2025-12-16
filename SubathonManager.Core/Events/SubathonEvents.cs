using SubathonManager.Core.Models;
using System.Diagnostics.CodeAnalysis;
using SubathonManager.Core.Enums;

namespace SubathonManager.Core.Events;

[ExcludeFromCodeCoverage]
public static class SubathonEvents
{
    public static event Action<SubathonEvent>? SubathonEventCreated;
    public static event Action<SubathonEvent, bool>? SubathonEventProcessed; // Run through queue, processed or not to subathon
    public static event Action<List<SubathonEvent>>? SubathonEventsDeleted;
    public static event Action<SubathonData, DateTime>? SubathonDataUpdate;
    
    public static event Action<List<SubathonGoal>, long, GoalsType>? SubathonGoalListUpdated;
    public static event Action<SubathonGoal, long>? SubathonGoalCompleted;

    public static void RaiseSubathonEventsDeleted(List<SubathonEvent> subathonEvent)
    {
        SubathonEventsDeleted?.Invoke(subathonEvent);
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

    public static void RaiseSubathonGoalCompleted(SubathonGoal goal, long points)
    {
        // do we want all goals?
        SubathonGoalCompleted?.Invoke(goal, points);
    }

    public static void RaiseSubathonGoalListUpdated(List<SubathonGoal> goals, long points, GoalsType type = GoalsType.Points)
    {
        SubathonGoalListUpdated?.Invoke(goals, points, type);
    }
}