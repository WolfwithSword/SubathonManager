using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;

namespace SubathonManager.Data.Widgets;

public static class WidgetPathRepair {
    private static readonly string[] Anchors = ["presets", "imports"];

    public static int Run(IDbContextFactory<AppDbContext> factory, ILogger? logger = null) {
        WidgetPackPaths.InvalidateResolveCache();
        WidgetPackPaths.InvalidateVersionCache();

        using AppDbContext db = factory.CreateDbContext();
        List<Widget> widgets = db.Widgets.ToList();

        var repaired = 0;
        foreach (Widget widget in widgets) {
            if (WidgetFiles.Current.Exists(widget.HtmlPath)) continue;
            if (!TryRebase(widget.HtmlPath, out string rebased)) continue;

            logger?.LogInformation("Repointed widget {Name} ({Id}) from {Old} to {New}",
                widget.Name, widget.Id, widget.HtmlPath, rebased);

            widget.HtmlPath = rebased;
            repaired++;
        }

        if (repaired > 0) {
            db.SaveChanges();
            WidgetPackPaths.InvalidateResolveCache();
        }

        return repaired;
    }

    public static int Normalize(IDbContextFactory<AppDbContext> factory) {
        using AppDbContext db = factory.CreateDbContext();
        List<Widget> widgets = db.Widgets.ToList();

        var rewritten = 0;
        foreach (Widget widget in widgets) {
            if (Path.IsPathRooted(WidgetPathStore.ToStored(widget.HtmlPath))) continue;

            db.Entry(widget).Property(w => w.HtmlPath).IsModified = true;
            rewritten++;
        }

        if (rewritten > 0) db.SaveChanges();
        return rewritten;
    }

    public static bool TryRebase(string storedPath, out string repaired) {
        repaired = string.Empty;
        if (string.IsNullOrWhiteSpace(storedPath)) return false;

        string[] parts = storedPath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);

        for (var i = parts.Length - 2; i >= 0; i--) {
            string segment = parts[i];
            int anchor = Array.FindIndex(Anchors,
                a => string.Equals(a, segment, StringComparison.OrdinalIgnoreCase));
            if (anchor < 0) continue;

            string[] segments = [Path.GetFullPath(Anchors[anchor]), .. parts[(i + 1)..]];
            string candidate = Path.GetFullPath(Path.Combine(segments));

            if (!WidgetFiles.Current.Exists(candidate)) continue;

            repaired = candidate;
            return true;
        }

        return false;
    }
}
