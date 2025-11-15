using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SubathonManager.Core.Enums;

namespace SubathonManager.Core.Models;

public class SubathonEvent
{
    [Key, Column(Order = 0)] public Guid Id { get; set; } = Guid.NewGuid();
    // override with a guid from actual original source if possible, like message-id.
    // If not valid guid, hash the value into a valid guid
    // we can use this to ensure there aren't double read events
    
    // do we want a bool in here for "added to timer success y/n?"

    [Key, Column(Order = 1)] public SubathonEventSource Source { get; set; } = SubathonEventSource.Unknown;

    public DateTime EventTimestamp { get; set; } = DateTime.Now;

    public int CurrentTime { get; set; } = 0;

    public int CurrentPoints { get; set; } = 0;

    public SubathonEventType? EventType { get; set; } = SubathonEventType.Unknown;

    public double? SecondsValue { get; set; } = 0;
    public int? PointsValue { get; set; } = 0;
    public string? User { get; set; } = "";
    public string Value { get; set; } = "";
    public SubathonCommandType Command { get; set; } = SubathonCommandType.None;
    public int Amount { get; set; } = 1; // how many times to multiply everything for amount, only used for giftsubs
    
    public bool ProcessedToSubathon { get; set; } = false;
    
    public Guid? SubathonId { get; set; }
    public SubathonData? LinkedSubathon  { get; set; }
    
    [CurrencyValidation] public string? Currency { get; set; } = "";

    // For determining if power hour (or negative power hour) is enabled, the multiplier.
    // when adding time to timer, take SecondsValue and always multiply by Multiplier
    public double MultiplierPoints { get; set; } = 1;
    public double MultiplierSeconds { get; set; } = 1;
    
    // do we want to later finetune power hour to be for selectable events?
    public double GetFinalSecondsValue() => Math.Ceiling(Amount * SecondsValue * (Source == SubathonEventSource.Command ? 1 : MultiplierSeconds) ?? 0); 
    public double GetFinalPointsValue() => Math.Floor(Amount * PointsValue * (Source == SubathonEventSource.Command ? 1 : Math.Round(MultiplierPoints+0.001)) ?? 0);

    public SubathonEvent ShallowClone()
    {
        return (SubathonEvent)this.MemberwiseClone();
    }
}

public class CurrencyValidationAttribute : ValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        if (value == null) return ValidationResult.Success;

        string s = value.ToString()!;
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "bits", "sub", "", "???", "member", "viewers" };

        if (allowed.Contains(s)) return ValidationResult.Success;

        // check if ISO 4217 code / 3 letters
        if (s.Length == 3 && s.All(char.IsLetter)) return ValidationResult.Success;

        return new ValidationResult("Currency must be 'bits', 'sub', empty, or a 3-letter ISO currency code.");
    }
}