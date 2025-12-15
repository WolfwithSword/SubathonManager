using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;
using SubathonManager.Core.Enums;

namespace SubathonManager.Core.Models;

[ExcludeFromCodeCoverage]
public class SubathonGoalSet
{
    [Key, Column(Order = 0)] public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New Goal List";
    public bool IsActive { get; set; } = true;
    
    public List<SubathonGoal> Goals { get; set; } = new();

    public GoalsType? Type { get; set; } = GoalsType.Points;
}