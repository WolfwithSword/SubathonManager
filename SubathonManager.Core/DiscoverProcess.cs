using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using SubathonManager.Core.Enums;

namespace SubathonManager.Core;

[ExcludeFromCodeCoverage]
public static class DiscoverProcess {
    public static bool IsProcessRunning(ProcessSearch target) {
        foreach (string name in target.GetQueryNames()) {
            Process[] procs = Process.GetProcessesByName(name);
            foreach (Process p in procs) p.Dispose();
            if (procs.Length > 0) return true;
        }

        return false;
    }
}