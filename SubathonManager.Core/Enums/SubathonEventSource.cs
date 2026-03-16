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
}