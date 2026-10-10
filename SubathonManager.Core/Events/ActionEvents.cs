using System.Diagnostics.CodeAnalysis;
using SubathonManager.Core.Models;

namespace SubathonManager.Core.Events;

[ExcludeFromCodeCoverage]
public static class ActionEvents {
    public static event Action<SubathonEvent>? CustomActionRunRequested;

    public static void RaiseCustomActionRunRequested(SubathonEvent ev) {
        CustomActionRunRequested?.Invoke(ev);
    }

    public static event Action<IReadOnlyCollection<string>>? GlobalsUpdated;

    public static void RaiseGlobalsUpdated(IReadOnlyCollection<string> names) {
        if (names.Count > 0) GlobalsUpdated?.Invoke(names);
    }
}
