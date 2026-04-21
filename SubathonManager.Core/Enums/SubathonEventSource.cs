using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;

namespace SubathonManager.Core.Enums;

public enum SubathonEventSource
{
    // some may not actually be event sources in the future, but also integration sources
    [EventSourceMeta(Description = "Twitch", SourceGroup = SubathonSourceGroup.Stream, SourceOrder=1, Order=10)]
    Twitch,
    [EventSourceMeta(Description = "StreamElements", SourceGroup = SubathonSourceGroup.StreamExtension, SourceOrder=21, Order=20)]
    StreamElements,
    [EventSourceMeta(Description = "KoFi", SourceGroup = SubathonSourceGroup.ExternalService, SourceOrder=41, Order=40)]
    KoFi,
    [EventSourceMeta(Description = "YouTube", SourceGroup = SubathonSourceGroup.Stream, SourceOrder=2, Order=11)]
    YouTube,
    [EventSourceMeta(Description = "Commands", SourceGroup = SubathonSourceGroup.Misc, SourceOrder=2000, Order=100)]
    Command, // can be from any chat
    [EventSourceMeta(Description = "Simulated", SourceOrder=8000, Order=199)]
    Simulated, // buttons to test in UI?
    [EventSourceMeta(Description = "Unknown", SourceGroup=SubathonSourceGroup.Misc, SourceOrder=9000, Order=101)]
    Unknown, // default
    [EventSourceMeta(Description = "StreamLabs", SourceGroup = SubathonSourceGroup.StreamExtension, SourceOrder=22, Order=21)]
    StreamLabs,
    [EventSourceMeta(Description = "Generic External Services", SourceGroup = SubathonSourceGroup.ExternalService, SourceOrder=1000, Order=99)]
    External,
    [EventSourceMeta(Description = "Blerp", SourceGroup = SubathonSourceGroup.StreamExtension, SourceOrder=81, Order=30)]
    Blerp,
    [EventSourceMeta(Description = "Picarto", SourceGroup = SubathonSourceGroup.Stream, SourceOrder=3, Order=12)]
    Picarto,
    [EventSourceMeta(Description = "GoAffPro Affiliate Stores", SourceGroup = SubathonSourceGroup.ExternalService, SourceOrder=61, Order=50)]
    GoAffPro,
    [EventSourceMeta(Description = "Ko-Fi (Tunnel)", SourceGroup = SubathonSourceGroup.ExternalService, SourceOrder=42, Visible=false, TrueSource=KoFi, Order=41)]
    KoFiTunnel,
    [EventSourceMeta(Description = "Dev Tunnels", SourceGroup = SubathonSourceGroup.ExternalService, SourceOrder=70, Visible=false, Order=120)]
    DevTunnels
}

[ExcludeFromCodeCoverage]
public static class SubathonEventSourceHelper
{
    public static int GetSourceOrder(SubathonEventSource source) => ((SubathonEventSource?)source).Meta()?.SourceOrder ?? 99999;
    
    private static EventSourceMetaAttribute? Meta(this SubathonEventSource? value)
    {
        if (!value.HasValue) return null;
        var meta = EnumMetaCache.Get<EventSourceMetaAttribute>(value);
        return meta;
    }

    public static SubathonSourceGroup GetGroup(this SubathonEventSource source) =>
        ((SubathonEventSource?)source).Meta()?.SourceGroup ?? SubathonSourceGroup.Unknown;

    public static string GetGroupLabel(this SubathonEventSource source) => ((SubathonEventSource?)source).Meta()?.Label ?? source.ToString();
    
    public static int GetGroupLabelOrder(this SubathonEventSource source) => ((SubathonEventSource?)source).Meta()?.Order ?? 99999;
}