using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SubathonManager.Core.Models;

public class MultiplierData
{
    
    [Key, Column(Order = 0)] public Guid Id { get; set; } = Guid.NewGuid();
    
    public double Multiplier { get; set; } = 1;
    
    public TimeSpan? Duration {get; set;} = null;
    public DateTime? Started { get; set; } = null;
    
    [ForeignKey("SubathonData")]
    public Guid? SubathonId { get; set; }
    public SubathonData? LinkedSubathon  { get; set; }
    
    public bool ApplyToSeconds { get; set; } = true;
    public bool ApplyToPoints { get; set; } = true;
    
}