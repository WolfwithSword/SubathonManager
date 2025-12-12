using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;

namespace SubathonManager.Core.Models;

[ExcludeFromCodeCoverage]
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
    
    public bool FromHypeTrain { get; set; } = false;

    public bool IsRunning()
    {
        return (ApplyToSeconds || ApplyToPoints) && !Multiplier.Equals(1);
    }
}