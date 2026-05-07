using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;
using SubathonManager.Core.Enums;

namespace SubathonManager.Core.Models;

[ExcludeFromCodeCoverage]
public class SubathonPrompt
{
    [Key, Column(Order = 0)] public Guid Id { get; set; } = Guid.NewGuid();
    
    public string Text { get; set; } = "New Prompt";
    public long Value { get; set; } = 10;
    public TimeSpan CompletionDuration { get; set; } = TimeSpan.FromMinutes(5);
    public int Quantity { get; set; } = 5;
    public bool IsInfinite { get; set; } = false;
    public bool Enabled { get; set; } = false;

    public SubathonPromptType Type { get; set; } = SubathonPromptType.Points;
    public SubathonPromptSubType SubType { get; set; } = SubathonPromptSubType.Default;

    public SubathonEventType? FilterEventType { get; set; } = null;
    public string? FilterMeta { get; set; } = null;
    public SubathonEventSubType? FilterSubType { get; set; } = null;
    
    [ForeignKey("SubathonPromptSet")]
    public Guid? SetId { get; set; }
    public SubathonPromptSet? LinkedSet  { get; set; }
    
    public int Index { get; set; } = 0;
    
    public bool HasStock() => IsInfinite || Quantity > 0;
    public bool IsPickable() => Enabled && HasStock();
}

[ExcludeFromCodeCoverage]
public class SubathonPromptRun
{
    [Key] public Guid Id { get; set; } = Guid.NewGuid();
    
    [ForeignKey(nameof(SubathonPrompt))]
    public Guid PromptId { get; set; }
    public SubathonPrompt? LinkedPrompt { get; set; }
    
    [ForeignKey(nameof(SubathonPromptSet))]
    public Guid SetId { get; set; }
    public SubathonPromptSet? LinkedSet { get; set; }
    
    public DateTime StartedAt { get; set; } = DateTime.Now;
    public DateTime ExpiresAt { get; set; }
    public DateTime? EndedAt { get; set; }
    
    public SubathonPromptRunStatus Status { get; set; } = SubathonPromptRunStatus.Active;
    public long SnapshotTargetValue { get; set; }
    public long BaselineCount { get; set; } = 0;
 
    public bool IsActive => Status == SubathonPromptRunStatus.Active;
    public bool IsExpired => DateTime.Now >= ExpiresAt && Status == SubathonPromptRunStatus.Active;
    
    public TimeSpan TimeRemaining()
    {
        if (Status != SubathonPromptRunStatus.Active) return TimeSpan.Zero;
        var remaining = ExpiresAt - DateTime.Now;
        return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
    }
    
    
}