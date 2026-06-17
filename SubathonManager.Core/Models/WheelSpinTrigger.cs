using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;
using SubathonManager.Core.Enums;

namespace SubathonManager.Core.Models;

[ExcludeFromCodeCoverage]
public class WheelSpinTrigger
{
    [Key] public Guid Id { get; set; } = Guid.NewGuid();
    public bool IsEnabled { get; set; } = true;
    public int SpinsToAdd { get; set; } = 1;

    public SubathonEventType EventType { get; set; }

    // For sub/membership events, matches ev.Value (meta tier)
    public string? TierValue { get; set; }
    // by item or reg count like subs or tokens
    public int? CountThreshold { get; set; }
    // by money only
    public double? MoneyThreshold { get; set; }
    public string? Currency { get; set; }

    public List<WheelSpinTriggerHistory> History { get; set; } = [];
    
    // for future thought, consider a "every" progress mode, useful for say,
    // 100 followers, or non gift subs. But then attribution doesn't make sense
}

[ExcludeFromCodeCoverage]
public class WheelSpinTriggerHistory
{
    [Key] public Guid Id { get; set; } = Guid.NewGuid();

    [ForeignKey("WheelSpinTrigger")]
    public Guid TriggerId { get; set; }
    public WheelSpinTrigger? Trigger { get; set; }

    public DateTime TriggeredAt { get; set; } = DateTime.Now;
    public string? TriggerUser { get; set; }
    public SubathonEventSource TriggerSource { get; set; }
    public int SpinsAdded { get; set; }

    // Loose reference to the source event, survives event deletion
    public Guid? SubathonEventId { get; set; }
    public SubathonEventType? SubathonEventType { get; set; }
}
