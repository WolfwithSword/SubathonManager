using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;
using SubathonManager.Core.Enums;

namespace SubathonManager.Core.Models;

[ExcludeFromCodeCoverage]
public class WheelSet
{
    [Key, Column(Order = 0)] public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "My Wheel";
    public int SpinCount { get; set; } = 0;
    public bool IsActive { get; set; } = true;

    public List<WheelItem> WheelItems { get; set; } = [];

    public IEnumerable<WheelItem> SpinnableItems()
        => WheelItems.Where(i => i.IsSpinnable());
}

[ExcludeFromCodeCoverage]
public class WheelItem
{
    [Key, Column(Order = 0)] public Guid Id { get; set; } = Guid.NewGuid();

    public string Text { get; set; } = "New Item";
    public int Weight { get; set; } = 1;
    public int Quantity { get; set; } = 1;
    public bool IsInfinite { get; set; } = false;
    public bool Enabled { get; set; } = false;
    public int Index { get; set; } = 0;

    [ForeignKey("WheelSet")]
    public Guid? WheelId { get; set; }
    public WheelSet? LinkedWheel { get; set; }

    public WheelSpinAction? Action { get; set; }

    public bool HasStock() => IsInfinite || Quantity > 0;
    public bool IsSpinnable() => Enabled && HasStock();
}

[ExcludeFromCodeCoverage]
public class WheelSpinAction
{
    [Key, Column(Order = 0)] public Guid Id { get; set; } = Guid.NewGuid();

    public WheelSpinActionType ActionType { get; set; } = WheelSpinActionType.Manual;
    public string Parameter { get; set; } = "";

    [ForeignKey("WheelItem")]
    public Guid WheelItemId { get; set; }
    public WheelItem? LinkedItem { get; set; }
}

[ExcludeFromCodeCoverage]
public class WheelSpinHistory
{
    [Key] public Guid Id { get; set; } = Guid.NewGuid();

    [ForeignKey("WheelSet")]
    public Guid WheelId { get; set; }
    public WheelSet? LinkedWheel { get; set; }

    [ForeignKey("WheelItem")]
    public Guid WheelItemId { get; set; }
    public WheelItem? LinkedItem { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public WheelSpinHistoryStatus Status { get; set; } = WheelSpinHistoryStatus.Pending;
}
