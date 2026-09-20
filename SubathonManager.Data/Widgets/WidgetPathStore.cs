namespace SubathonManager.Data.Widgets;

public static class WidgetPathStore {
    public static string ToStored(string? absolute) {
        if (string.IsNullOrWhiteSpace(absolute)) return absolute ?? string.Empty;

        try {
            string full = Path.GetFullPath(absolute);
            string baseDir = Directory.GetCurrentDirectory();
            string relative = Path.GetRelativePath(baseDir, full);

            return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)
                ? full
                : relative;
        }
        catch {
            return absolute;
        }
    }

    public static string ToAbsolute(string? stored) {
        if (string.IsNullOrWhiteSpace(stored)) return stored ?? string.Empty;

        try {
            return Path.IsPathRooted(stored)
                ? stored
                : Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), stored));
        }
        catch {
            return stored;
        }
    }
}
