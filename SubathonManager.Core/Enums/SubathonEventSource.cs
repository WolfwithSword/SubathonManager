using System.Diagnostics.CodeAnalysis;

namespace SubathonManager.Core.Enums;

public enum SubathonEventSource
{
    // some may not actually be event sources in the future, but also integration sources
    Twitch,
    StreamElements,
    KoFi,
    YouTube,
    Command, // can be from any chat
    Simulated, // buttons to test in UI? 
    Unknown, // default
    StreamLabs,
    External,
    Blerp,
    Picarto,
    GoAffPro
}

[ExcludeFromCodeCoverage]
public static class SubathonEventSourceHelper
{
    private static readonly List<SubathonEventSource> SourceOrder =
    [
        SubathonEventSource.Twitch,
        SubathonEventSource.YouTube,
        SubathonEventSource.Picarto,
        SubathonEventSource.StreamElements,
        SubathonEventSource.StreamLabs,
        SubathonEventSource.KoFi,
        SubathonEventSource.GoAffPro,
        SubathonEventSource.Blerp
    ];

    private static readonly List<SubathonEventSource> SourceOrderEnd =
    [
        SubathonEventSource.External, SubathonEventSource.Command
    ];

    public static int GetSourceOrder(SubathonEventSource source)
    {
        var idx = SourceOrder.IndexOf(source);
        if (idx >= 0) return idx;

        var endIdx = SourceOrderEnd.IndexOf(source);
        if (endIdx >= 0) return SourceOrder.Count + 1000 + endIdx;

        return SourceOrder.Count + endIdx;
    }

    public static string GetGroupLabel(this SubathonEventSource source)
    {
        return source switch
        {
            SubathonEventSource.Blerp => "Chat Extensions",
            SubathonEventSource.Command or SubathonEventSource.Unknown => "Misc",
            SubathonEventSource.StreamElements or SubathonEventSource.StreamLabs => "Stream Extensions",
            _ => $"{source}"
        };
    }

    public static int GetGroupLabelOrder(this SubathonEventSource source)
    {
        return source switch
        {
            // Streaming Services
            SubathonEventSource.Twitch => 10,
            SubathonEventSource.YouTube => 11,
            SubathonEventSource.Picarto => 12,
            // Stream Extensions
            SubathonEventSource.StreamElements => 20,
            SubathonEventSource.StreamLabs => 21,
            // Chat Extensions
            SubathonEventSource.Blerp => 30,
            // Solo
            SubathonEventSource.KoFi => 40,
            SubathonEventSource.GoAffPro => 50,
            SubathonEventSource.External => 60,
            // Misc
            SubathonEventSource.Command => 100,
            SubathonEventSource.Unknown => 101,
            _ => 199
        };
    }
}