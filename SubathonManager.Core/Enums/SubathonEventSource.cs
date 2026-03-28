using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;

namespace SubathonManager.Core.Enums;

public enum SubathonEventSource
{
    // some may not actually be event sources in the future, but also integration sources
    [Description("Twitch")]
    Twitch,
    [Description("StreamElements")]
    StreamElements,
    [Description("KoFi")]
    KoFi,
    [Description("YouTube")]
    YouTube,
    [Description("Commands")]
    Command, // can be from any chat
    [Description("Simulated")]
    Simulated, // buttons to test in UI?
    [Description("Unknown")]
    Unknown, // default
    [Description("StreamLabs")]
    StreamLabs,
    [Description("Generic External Services")]
    External,
    [Description("Blerp")]
    Blerp,
    [Description("Picarto")]
    Picarto,
    [Description("GoAffPro Affiliate Stores")]
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

    public static SubathonSourceGroup GetGroup(this SubathonEventSource source)
    {
        return source switch
        {
            SubathonEventSource.Blerp => SubathonSourceGroup.StreamExtension,
            SubathonEventSource.Command or SubathonEventSource.Unknown => SubathonSourceGroup.Misc,
            SubathonEventSource.StreamElements or SubathonEventSource.StreamLabs => SubathonSourceGroup.StreamExtension,
            SubathonEventSource.Twitch or SubathonEventSource.YouTube or SubathonEventSource.Picarto => SubathonSourceGroup.Stream,
            SubathonEventSource.KoFi or  SubathonEventSource.GoAffPro or SubathonEventSource.External => SubathonSourceGroup.ExternalService,
            _ => SubathonSourceGroup.UseSource
        };
    }

    public static string GetGroupLabel(this SubathonEventSource source)
    {
        var group = source.GetGroup();
        return group is SubathonSourceGroup.UseSource or SubathonSourceGroup.Unknown
            ? $"{source}"
            : group.GetDescription();
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
            // Chat Extensions (Still stream extensions
            SubathonEventSource.Blerp => 30,
            // Solo
            SubathonEventSource.KoFi => 40,
            SubathonEventSource.GoAffPro => 50,
            SubathonEventSource.External => 99,
            // Misc
            SubathonEventSource.Command => 100,
            SubathonEventSource.Unknown => 101,
            _ => 199
        };
    }
}