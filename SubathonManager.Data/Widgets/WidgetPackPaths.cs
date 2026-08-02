using System.Text.RegularExpressions;
using SubathonManager.Core;
// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.Data.Widgets;

public static partial class WidgetPackPaths
{
    public const string PackExtension = ".smw";

    public static string ImportsRoot => Path.GetFullPath(Path.Combine("./imports", "widgets"));
    public static string PackedRoot => Path.Combine(ImportsRoot, "packed");
    public static string UnpackedRoot => Path.Combine(ImportsRoot, "unpacked");
    public static string CacheRoot => Path.GetFullPath(Path.Combine("./cache", "widgets"));
    public static string PresetsRoot => Path.GetFullPath("./presets");

    public static string MountRoot(string packId, string version)
        => Path.Combine(PackedRoot, packId, version);

    public static string PackFile(string packId, string version)
        => Path.Combine(PackedRoot, packId, version + PackExtension);

    public static string PackFolder(string packId) => Path.Combine(PackedRoot, packId);

    private static readonly Lock VersionCacheLock = new();
    private static readonly Dictionary<string, List<string>> VersionCache = new(StringComparer.OrdinalIgnoreCase);

    public static List<string> InstalledVersions(string packId) => VersionsIn(PackFolder(packId));
    public static List<string> VersionsIn(string folder)
    {
        lock (VersionCacheLock)
        {
            if (VersionCache.TryGetValue(folder, out var cached)) return cached;

            var versions = new List<string>();

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

            VersionCache[folder] = versions;
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
            else VersionCache.Remove(PackFolder(packId));
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
    
    public static bool IsInPresets(string packFileOrFolder)
    {
        try
        {
            return Path.GetFullPath(packFileOrFolder)
                .StartsWith(PresetsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static bool IsVersionName(string? name)
        => !string.IsNullOrWhiteSpace(name) && VersionNameRegex().IsMatch(name);

    public static bool IsInGlobalStore(string packFileOrFolder)
    {
        try
        {
            string full = Path.GetFullPath(packFileOrFolder);

            return full.StartsWith(PackedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(PresetsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static string UnpackRootFor(PackLocation location, string author, string group, string name)
    {
        if (IsInGlobalStore(location.PackFolderStr))
            return UnpackRoot(author, group, name, location.VersionStr);

        string container = Path.GetDirectoryName(location.PackFolderStr) ?? location.PackFolderStr;
        return Path.Combine(container, "unpacked", location.PackIdStr, location.VersionStr);
    }

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

    private static string SanitizeSegment(string? value) => SafeFileName.Sanitize(value);

    public static string EntryPath(string packId, string version, string entry)
        => Path.Combine(MountRoot(packId, version), entry.Replace('/', Path.DirectorySeparatorChar));
    
    public record PackLocation(string PackFileStr, string PackFolderStr, string MountRootStr, string PackIdStr, string VersionStr);

    private static readonly Lock ResolveCacheLock = new();
    private static readonly Dictionary<string, PackLocation?> ResolveCache = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxMountDepth = 16;

    public static PackLocation? Resolve(string widgetPath)
    {
        if (string.IsNullOrWhiteSpace(widgetPath)) return null;

        string full;
        try { full = Path.GetFullPath(widgetPath); }
        catch { return null; }

        string? dir = Path.GetDirectoryName(full);
        if (dir == null) return null;

        var found = ResolveDirectory(dir);
        return found;
    }

    private static PackLocation? ResolveDirectory(string directory)
    {
        lock (ResolveCacheLock)
        {
            if (ResolveCache.TryGetValue(directory, out var cached)) return cached;
        }

        PackLocation? result = null;
        string? current = directory;

        for (int depth = 0; depth < MaxMountDepth && current != null; depth++)
        {
            string candidate = current + PackExtension;
            if (File.Exists(candidate))
            {
                string packFolder = Path.GetDirectoryName(current) ?? current;
                result = new PackLocation(
                    PackFileStr: candidate,
                    PackFolderStr: packFolder,
                    MountRootStr: current,
                    PackIdStr: Path.GetFileName(packFolder),
                    VersionStr: Path.GetFileName(current));
                break;
            }

            current = Path.GetDirectoryName(current);
        }

        lock (ResolveCacheLock)
        {
            ResolveCache[directory] = result;
        }

        return result;
    }

    public static void InvalidateResolveCache()
    {
        lock (ResolveCacheLock)
        {
            ResolveCache.Clear();
        }
    }

    public static bool TryResolve(string widgetPath, out string packFile, out string entry,
        out string packId, out string version)
    {
        packFile = string.Empty;
        entry = string.Empty;
        packId = string.Empty;
        version = string.Empty;

        var found = Resolve(widgetPath);
        if (found == null) return false;

        packFile = found.PackFileStr;
        packId = found.PackIdStr;
        version = found.VersionStr;
        entry = Path.GetRelativePath(found.MountRootStr, Path.GetFullPath(widgetPath)).Replace('\\', '/');

        return true;
    }

    public static string EntryPathIn(string mountRoot, string entry)
        => Path.Combine(mountRoot, entry.Replace('/', Path.DirectorySeparatorChar));
    
    public static string CacheDirFor(string packFile)
    {
        string key;
        try { key = Path.GetFullPath(packFile).ToLowerInvariant(); }
        catch { key = packFile.ToLowerInvariant(); }

        byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key));
        string folder = $"{Slug(Path.GetFileNameWithoutExtension(packFile))}-{Convert.ToHexString(hash)[..8].ToLowerInvariant()}";

        return Path.Combine(CacheRoot, folder);
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

    [GeneratedRegex(@"^\d+([._-]\d+)*$")]
    private static partial Regex VersionNameRegex();

    [GeneratedRegex(@"(?<=\d)-(?=\d)")]
    private static partial Regex VersionSeparatorRegex();
}
