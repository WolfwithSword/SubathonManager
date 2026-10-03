using System.Diagnostics.CodeAnalysis;
using SubathonManager.Core.Models;

namespace SubathonManager.Core.Events;

[ExcludeFromCodeCoverage]
public static class ActionEvents {
    public static event Action<SubathonEvent>? CustomActionRunRequested;

    public static void RaiseCustomActionRunRequested(SubathonEvent ev) {
        CustomActionRunRequested?.Invoke(ev);
    }
}
