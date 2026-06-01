using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;

namespace SubathonManager.Core.Models;

[ExcludeFromCodeCoverage]
public class SubathonPromptSet
{
    [Key, Column(Order = 0)] public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "My Prompts";
    public bool Enabled { get; set; } = true;
    public bool IsActive { get; set; } = true;
    
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(20);
    public TimeSpan RandomOffset { get; set; } = TimeSpan.Zero;
    public TimeSpan Cooldown { get; set; } = TimeSpan.FromMinutes(20); // time after one ends before a new one can start
    
    public List<SubathonPrompt> Prompts { get; set; } = [];
    
    
    public void ClampRandomOffset()
    {
        if (RandomOffset > Interval)
            RandomOffset = Interval;
    }
    
    public IEnumerable<SubathonPrompt> PickablePrompts()
        => Prompts.Where(p => p.IsPickable());
}