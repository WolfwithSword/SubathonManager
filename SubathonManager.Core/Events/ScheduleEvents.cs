using System.Diagnostics.CodeAnalysis;

namespace SubathonManager.Core.Events;

[ExcludeFromCodeCoverage]
public static class ScheduleEvents {
    public static event Action<object?>? ScheduleChanged;

    public static void RaiseScheduleChanged(object? source) {
        ScheduleChanged?.Invoke(source);
    }
}