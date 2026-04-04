using System.ComponentModel;

namespace SubathonManager.Core.Enums;

public enum SubathonSourceGroup
{
    [EnumMeta(Description="Unknown", Label="Unknown")]
    Unknown,
    [EnumMeta(Description="Stream Services", Label="Stream Services")]
    Stream,
    [EnumMeta(Description="Stream Extensions", Label="Stream Extensions")]
    StreamExtension,
    // [Description("Chat Extension")]
    // ChatExtension,
    [EnumMeta(Description="Misc", Label="Misc")]
    Misc,
    [EnumMeta(Description="External Services", Label="External Services")]
    ExternalService,
    [EnumMeta(Label="")]
    UseSource
}
