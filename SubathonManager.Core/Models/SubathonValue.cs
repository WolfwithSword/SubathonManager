using SubathonManager.Core.Enums;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;

namespace SubathonManager.Core.Models;

[ExcludeFromCodeCoverage]
public class SubathonValue
{
    [Key, Column(Order = 0)] public SubathonEventType EventType { get; set; }
    [Key, Column(Order = 1)] public string Meta { get; set; } = "";
    
    // EventType + Meta for settings. Most will have empty Meta.
    // Meta will be for like TwitchSub and TwitchGiftSub, where it will be the Tier (from Value field). string 1000 2000 3000
    
    // in future, may want to add a condition column to compare stuff with? i.e., raids of min viewer count
    // follows could do a user lookup in the api too and do a min account age for it to count, and have that in settings.
    
    public double Seconds { get; set; } = 0;
    public int Points { get; set; } = 0; // like subpoints for goals
}