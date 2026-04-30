using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;

namespace SubathonManager.Core.Models;

[ExcludeFromCodeCoverage]
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
    
    public string? Currency { get; set; } = ""; // load from default if not set
    public double? MoneySum { get; set; } = 0; 
    
    public bool? ReversedTime { get; set; } = false;
    
    public DateTime? CapDateTime { get; set; } = null;

    public bool IsSubathonReversed()
    {
        return ReversedTime ?? false;
    }

    public bool IsCapInEffect()
    {
        if (CapDateTime == null) return false;
        long value = 0;
        if (IsSubathonReversed())
            value = MillisecondsCumulative + MillisecondsElapsed;
        else
            value = MillisecondsCumulative - MillisecondsElapsed;
        return CapDateTime.Value < DateTime.Now + TimeSpan.FromMilliseconds(value);
    }

    public long MillisecondsRemaining()
    {
        long value = 0;
        if (IsSubathonReversed())
            value = MillisecondsCumulative + MillisecondsElapsed;
        else
            value = MillisecondsCumulative - MillisecondsElapsed;

        if (CapDateTime != null)
        {
            double timeUntilCap = 0;
            var x = DateTime.Now;
            if (CapDateTime.Value > x)
                timeUntilCap = (CapDateTime.Value - x).TotalMilliseconds;
            value = Math.Min(value, (long)timeUntilCap);
        }
        return value;
    }

    public TimeSpan TimeRemaining()
    {
        return TimeSpan.FromMilliseconds(MillisecondsRemaining());
    }

    public TimeSpan TimeRemainingRounded()
    {
        double totalSeconds = TimeRemaining().TotalSeconds;
        return TimeSpan.FromSeconds(Math.Floor(totalSeconds));
    }

    public long GetRoundedMoneySum()
    {
        return (long)Math.Floor(MoneySum ?? 0);
    }
    
    public double GetRoundedMoneySumWithCents()
    {
        return Math.Round(MoneySum ?? 0, 2);
    }
}