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
    [EventSourceMeta(Description = "Generic External Services", SourceGroup = SubathonSourceGroup.ExternalService, SourceOrder=1000, Order=99, IsExternalSource = true)]
    External,
    [EventSourceMeta(Description = "Blerp", SourceGroup = SubathonSourceGroup.StreamExtension, SourceOrder=81, Order=30, IsExternalSource = true)]
    Blerp,
    [EventSourceMeta(Description = "Picarto", SourceGroup = SubathonSourceGroup.Stream, SourceOrder=3, Order=12)]
    Picarto,
    [EventSourceMeta(Description = "GoAffPro Affiliate Stores", SourceGroup = SubathonSourceGroup.ExternalService, SourceOrder=61, Order=50, Visible = true)]
    GoAffPro,
    [EventSourceMeta(Description = "Ko-Fi (Tunnel)", SourceGroup = SubathonSourceGroup.ExternalService, SourceOrder=42, Visible=false, TrueSource=KoFi, Order=41)]
    KoFiTunnel,
    [EventSourceMeta(Description = "Dev Tunnels", SourceGroup = SubathonSourceGroup.ExternalSoftware, SourceOrder=904, Visible=false, Order=903)]
    DevTunnels,
    [EventSourceMeta(Description="FourthWall", SourceGroup = SubathonSourceGroup.ExternalService, SourceOrder=62, Order=51)]
    FourthWall,
    [EventSourceMeta(Description="OBS", SourceGroup = SubathonSourceGroup.ExternalSoftware, SourceOrder=901, Order=900, Visible = false)]
    OBS,
    [EventSourceMeta(Description="Throne", SourceGroup = SubathonSourceGroup.ExternalService, SourceOrder=63, Order=52)]
    Throne,
    [EventSourceMeta(Description="TipeeeStream", SourceGroup = SubathonSourceGroup.StreamExtension, SourceOrder=23, Order=22)]
    TipeeeStream,
    [EventSourceMeta(Description="Wheel Spin", Visible = false, Order = 990, SourceGroup = SubathonSourceGroup.WheelSpin)]
    WheelSpin,
    [EventSourceMeta(Description="Tangia", SourceGroup = SubathonSourceGroup.StreamExtension, SourceOrder=82, Order=31)]
    Tangia,
    [EventSourceMeta(Description="Stream Deck", SourceGroup = SubathonSourceGroup.ExternalSoftware, SourceOrder=903, Order=902, Visible = false, IsExternalSource = true)]
    StreamDeck,
    [EventSourceMeta(Description="StreamerBot", SourceGroup = SubathonSourceGroup.ExternalSoftware, SourceOrder=902, Order=901, Visible = false, IsExternalSource = true)]
    StreamerBot,
    [EventSourceMeta(Description="Pally.GG", SourceGroup = SubathonSourceGroup.ExternalService, SourceOrder=64, Order=53)]
    PallyGG,
    [EventSourceMeta(Description="TreatStream", SourceGroup = SubathonSourceGroup.StreamExtension, SourceOrder=24, Order=23)]
    TreatStream,
    [EventSourceMeta(Description="MakeShip", SourceGroup = SubathonSourceGroup.ExternalService, SourceOrder=65, Order=54)]
    MakeShip,
    [EventSourceMeta(Description="Juniper Creates", SourceGroup = SubathonSourceGroup.ExternalService, SourceOrder=66, Order=55, Visible = true)]
    JuniperCreates // like goaffpro, do lookup on product id to find store name as source
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
    
    public static bool IsExternalSource(this SubathonEventSource? source) => source.Meta()?.IsExternalSource ?? false;
    public static bool IsExternalSource(this SubathonEventSource source) => IsExternalSource((SubathonEventSource?)source);

    public static SubathonSourceGroup GetGroup(this SubathonEventSource source) =>
        ((SubathonEventSource?)source).Meta()?.SourceGroup ?? SubathonSourceGroup.Unknown;

    public static string GetGroupLabel(this SubathonEventSource source) => ((SubathonEventSource?)source).Meta()?.Label ?? source.ToString();
    
    public static int GetGroupLabelOrder(this SubathonEventSource source) => ((SubathonEventSource?)source).Meta()?.Order ?? 99999;
}