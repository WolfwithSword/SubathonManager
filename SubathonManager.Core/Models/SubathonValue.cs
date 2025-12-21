using SubathonManager.Core.Enums;
using System.Text.Json.Serialization;
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
    
    public double Seconds { get; set; } = 0;
    public int Points { get; set; } = 0;

    
    public SubathonValueDto ToObject()
    {
        return new SubathonValueDto
        {
            EventType = EventType,
            Source = ((SubathonEventType?)EventType).GetSource(),
            Meta = Meta,
            Seconds = Seconds,
            Points = Points
        };
    }

    public bool PatchByObject(SubathonValueDto dto)
    {
        bool modified = false;
        if (dto.EventType != EventType)
            return false;
        if (dto.Source != ((SubathonEventType?)EventType).GetSource())
            return false;
        if (dto.Meta != Meta)
            return false;
        if (dto.Seconds == null && dto.Points == null)
            return false;
        
        if (dto.Seconds != null && dto.Seconds >= 0 && !Seconds.Equals(dto.Seconds))
        {
            Seconds = (double) dto.Seconds;
            modified = true;
        }

        if (dto.Points != null && dto.Points >= 0 && !Points.Equals(dto.Points))
        {
            Points = (int) dto.Points;
            modified = true;
        }

        return modified;
    }
}

public class SubathonValueDto
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SubathonEventType EventType { get; set; }
    
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SubathonEventSource Source { get; set; }
    public string Meta { get; set; } = "";
    public double? Seconds { get; set; }
    public int? Points { get; set; }

    public override string ToString()
    {
        return $"{Source} {EventType} [{Meta}]: {Seconds}s, {Points}pts";
    }

    public string ToValueString()
    {
        var secondsStr = Seconds == null ? string.Empty : $"{Seconds}s";
        var pointsStr = Points == null ? string.Empty : $"{Points}pts";
        return $"{secondsStr} {pointsStr}".Trim();
    }
}