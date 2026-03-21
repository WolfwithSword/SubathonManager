using SubathonManager.Core.Enums;

namespace SubathonManager.Core.Objects;

public class SubathonTotals
{
    public double MoneySum { get; init; } = 0;
    public string? Currency { get; init; } = "USD";

    public int SubLikeTotal { get; init; } = 0;
    public Dictionary<SubathonEventType, int> SubLikeByEvent { get; init; } = new();

    public long TokenLikeTotal { get; init; } = 0;
    public Dictionary<SubathonEventType, long> TokenLikeByEvent { get; init; } = new();

    public Dictionary<SubathonEventType, int> OrderCountByType { get; init; } = new();
    
    public int FollowLikeTotal { get; init; } = 0;
    public Dictionary<SubathonEventType, int> FollowLikeByEvent { get; init; } = new();
    
    public SubathonSimulatedTotals Simulated { get; init; } = new();
}

public class SubathonSimulatedTotals
{
    public int SubLikeTotal { get; init; } = 0;
    public Dictionary<SubathonEventType, int> SubLikeByEvent { get; init; } = new();

    public long TokenLikeTotal { get; init; } = 0;
    public Dictionary<SubathonEventType, long> TokenLikeByEvent { get; init; } = new();

    public Dictionary<SubathonEventType, int> OrderCountByType { get; init; } = new();
    
    public int FollowLikeTotal { get; init; } = 0;
    public Dictionary<SubathonEventType, int> FollowLikeByEvent { get; init; } = new();
}