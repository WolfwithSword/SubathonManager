using System.Diagnostics.CodeAnalysis;

namespace SubathonManager.Core;

[ExcludeFromCodeCoverage]
public static class FeatureFlags {
    public static readonly bool KoFiStreamerBotSetupEnabled = false;
    public static readonly bool VTubeStudioEnabled = true;
    public static readonly bool VTubeStudioMarkAsExperimental = true;
    public static readonly bool GoAffProBackfillPollingEnabled = true;
}