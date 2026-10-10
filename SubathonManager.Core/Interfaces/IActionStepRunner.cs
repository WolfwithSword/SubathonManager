using SubathonManager.Core.Enums;
using SubathonManager.Core.Objects;

namespace SubathonManager.Core.Interfaces;

public interface IActionStepRunner {
    IReadOnlyCollection<ActionStepType> StepTypes { get; }

    Task<bool> RunStepAsync(ActionStep step, ActionContext ctx, ActionRunProgress progress, CancellationToken ct);
}
