using System.ComponentModel;

namespace SubathonManager.Core.Enums;

public enum SubathonSourceGroup
{
    [Description("Unknown")]
    Unknown,
    [Description("Stream Services")]
    Stream,
    [Description("Stream Extensions")]
    StreamExtension,
    // [Description("Chat Extension")]
    // ChatExtension,
    [Description("Misc")]
    Misc,
    [Description("External Services")]
    ExternalService,
    [Description("")]
    UseSource
}
