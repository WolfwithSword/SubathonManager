using SubathonManager.Core.Enums;

namespace SubathonManager.Core.Objects;

public class SubscriptionTotals {
    public int SubTotal { get; init; } = 0;
    public Dictionary<SubathonEventType, int> SubTotalByEvent { get; init; } = new();

    public Dictionary<SubathonEventType, Dictionary<string, int>> SubTotalByEventTier { get; init; } = new();

    public SubscriptionSimulatedTotals Simulated { get; init; } = new();
}

public class SubscriptionSimulatedTotals {
    public int SubTotal { get; init; } = 0;
    public Dictionary<SubathonEventType, int> SubTotalByEvent { get; init; } = new();
    public Dictionary<SubathonEventType, Dictionary<string, int>> SubTotalByEventTier { get; init; } = new();
}