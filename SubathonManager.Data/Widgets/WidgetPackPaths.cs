using System.Text.RegularExpressions;
// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.Data.Widgets;

public static partial class WidgetPackPaths
{
    public const string PackExtension = ".smw";

    public static string ImportsRoot => Path.GetFullPath(Path.Combine("./imports", "widgets"));
    public static string PackedRoot => Path.Combine(ImportsRoot, "packed");
    public static string UnpackedRoot => Path.Combine(ImportsRoot, "unpacked");
    public static string CacheRoot => Path.GetFullPath(Path.Combine("./cache", "widgets"));

    public static string MountRoot(string packId, string version)
        => Path.Combine(PackedRoot, packId, version);

    public static string PackFile(string packId, string version)
        => Path.Combine(PackedRoot, packId, version + PackExtension);

    public static string PackFolder(string packId) => Path.Combine(PackedRoot, packId);

    private static readonly Lock VersionCacheLock = new();
    private static readonly Dictionary<string, List<string>> VersionCache = new(StringComparer.OrdinalIgnoreCase);

    public static List<string> InstalledVersions(string packId)
    {
        lock (VersionCacheLock)
        {
            if (VersionCache.TryGetValue(packId, out var cached)) return cached;

            var versions = new List<string>();
            string folder = PackFolder(packId);

            if (Directory.Exists(folder))
            {
                try
                {
                    versions = Directory.EnumerateFiles(folder, "*" + PackExtension)
                        .Select(Path.GetFileNameWithoutExtension)
                        .Where(v => !string.IsNullOrWhiteSpace(v))
                        .Select(v => v!)
                        .ToList();
                }
                catch { /**/ }
            }

            VersionCache[packId] = versions;
            return versions;
        }
    }

    public static string DisplayVersion(string? version)
        => string.IsNullOrWhiteSpace(version)
            ? string.Empty
            : VersionSeparatorRegex().Replace(version, ".");

    public static void InvalidateVersionCache(string? packId = null)
    {
        lock (VersionCacheLock)
        {
            if (packId == null) VersionCache.Clear();
            else VersionCache.Remove(packId);
        }
    }

    public static int CompareVersions(string left, string right)
    {
        var a = VersionSegments(left);
        var b = VersionSegments(right);

        for (int i = 0; i < Math.Max(a.Count, b.Count); i++)
        {
            long x = i < a.Count ? a[i] : 0;
            long y = i < b.Count ? b[i] : 0;
            if (x != y) return x.CompareTo(y);
        }

        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static List<long> VersionSegments(string version)
        => VersionSegmentRegex().Matches(version ?? string.Empty)
            .Select(m => long.TryParse(m.Value, out var n) ? n : 0L)
            .ToList();
    public const string DefaultGroup = "widgets";

    public static string NormalizeGroup(string? group)
        => string.IsNullOrWhiteSpace(group) ? DefaultGroup : group.Trim();
    
    public static string UnpackRoot(string author, string group, string name, string version)
    {
        string authorFolder = Slug(author);
        if (string.IsNullOrEmpty(authorFolder)) authorFolder = "unknown";

        string groupFolder = Slug(NormalizeGroup(group));
        if (string.IsNullOrEmpty(groupFolder)) groupFolder = DefaultGroup;

        string nameFolder = Slug(name);
        if (string.IsNullOrEmpty(nameFolder)) nameFolder = "widget";

        string versionFolder = SanitizeSegment(version);
        if (string.IsNullOrEmpty(versionFolder)) versionFolder = "1.0.0";

        return Path.Combine(UnpackedRoot, authorFolder, groupFolder, nameFolder, versionFolder);
    }

    private static string SanitizeSegment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    public static string EntryPath(string packId, string version, string entry)
        => Path.Combine(MountRoot(packId, version), entry.Replace('/', Path.DirectorySeparatorChar));

    public static bool TryResolve(string widgetPath, out string packFile, out string entry,
        out string packId, out string version)
    {
        packFile = string.Empty;
        entry = string.Empty;
        packId = string.Empty;
        version = string.Empty;

        if (string.IsNullOrWhiteSpace(widgetPath)) return false;

        string full;
        try { full = Path.GetFullPath(widgetPath); }
        catch { return false; }

        string root = PackedRoot;
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return false;

        var parts = full[(root.Length + 1)..]
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length < 3) return false;

        packId = parts[0];
        version = parts[1];
        entry = string.Join('/', parts.Skip(2));
        packFile = PackFile(packId, version);

        return File.Exists(packFile);
    }

    public static bool IsMountPath(string widgetPath)
    {
        if (string.IsNullOrWhiteSpace(widgetPath)) return false;
        try
        {
            return Path.GetFullPath(widgetPath)
                .StartsWith(PackedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
    
    public static string MakePackId(string author, string group, string name)
    {
        string n = Slug(name);
        if (string.IsNullOrEmpty(n)) n = "widget";

        var parts = new[] { Slug(author), Slug(NormalizeGroup(group)), n }
            .Where(p => !string.IsNullOrEmpty(p));

        return string.Join('.', parts);
    }

    public static string Slug(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        string slug = SlugStripRegex().Replace(value.Trim().ToLowerInvariant(), "-");
        slug = SlugCollapseRegex().Replace(slug, "-").Trim('-');
        return slug;
    }

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex SlugStripRegex();

    [GeneratedRegex(@"-{2,}")]
    private static partial Regex SlugCollapseRegex();

    [GeneratedRegex(@"\d+")]
    private static partial Regex VersionSegmentRegex();

    [GeneratedRegex(@"(?<=\d)-(?=\d)")]
    private static partial Regex VersionSeparatorRegex();
}
