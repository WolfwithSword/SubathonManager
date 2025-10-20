using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SubathonManager.Core.Models;

public class SubathonGoal
{
    [Key, Column(Order = 0)] public Guid Id { get; set; } = Guid.NewGuid();
    
    public string Text { get; set; } = "New Goal";
    public int Points { get; set; } = 1;
    
    [ForeignKey("SubathonGoalSet")]
    public Guid? GoalSetId { get; set; }
    public SubathonGoalSet? LinkedGoalSet  { get; set; }
    
}