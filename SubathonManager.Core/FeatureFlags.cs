using System.Diagnostics.CodeAnalysis;

namespace SubathonManager.Core;

[ExcludeFromCodeCoverage]
public static class FeatureFlags {
    public static readonly bool KoFiStreamerBotSetupEnabled = false;
    public static readonly bool VTubeStudioMarkAsExperimental = true;
}