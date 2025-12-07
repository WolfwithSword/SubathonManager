using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SubathonManager.Core.Models;

public class SubathonData
{
    [Key, Column(Order = 0)] public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New Subathon";
    
    public long MillisecondsCumulative { get; set; } = 0;
    public long MillisecondsElapsed { get; set; } = 0;

    public int Points  { get; set; } = 0;
    
    public bool IsPaused { get; set; } = true;
    public bool IsActive { get; set; } = true;
    
    // continue existing but do not accept new events
    public bool IsLocked { get; set; } = true;
    
    // proper power hour value is here
    public MultiplierData Multiplier { get; set; } = new();

    public DateTime PredictedEndTime()
    {
        return DateTime.Now.AddMilliseconds(MillisecondsRemaining());
    }

    public long MillisecondsRemaining()
    {
        return MillisecondsCumulative - MillisecondsElapsed;
    }

    public TimeSpan TimeRemaining()
    {
        return TimeSpan.FromMilliseconds(MillisecondsRemaining());
    }

    public TimeSpan TimeRemainingRounded()
    {
        double totalSeconds = TimeRemaining().TotalSeconds;
        return TimeSpan.FromSeconds(Math.Round(totalSeconds));
    }

    // isActive // only ever one
    
}