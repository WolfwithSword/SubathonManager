using System.IO.Compression;
using System.Text.Json;
using SubathonManager.Core.Enums;
// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.Data.Widgets;

public class WidgetPackManifest
{
    public string FormatVersion { get; init; } = "1";
    public string AppVersion { get; init; } = string.Empty;
    public string PackId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;

    public string Group { get; init; } = WidgetPackPaths.DefaultGroup;
    public string Version { get; init; } = "1.0.0";
    public string DocsUrl { get; init; } = string.Empty;

    public List<string> Tags { get; init; } = new();
    public string PreviewImage { get; init; } = string.Empty;
    public string Entry { get; init; } = string.Empty;

    public int Width { get; init; }
    public int Height { get; init; }
    public float ScaleX { get; init; } = 1;
    public float ScaleY { get; init; } = 1;
}

public static class WidgetPackInstaller
{
    public const string ManifestFileName = "widget.json";
    public record InstalledPack(WidgetPackManifest Manifest, string HtmlPath, string PackFile);

    public static WidgetPackManifest? ReadManifest(string smwPath)
    {
        try
        {
            using var stream = new FileStream(smwPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

            var entry = archive.Entries.FirstOrDefault(e =>
                string.Equals(e.FullName.Replace('\\', '/'), ManifestFileName, StringComparison.OrdinalIgnoreCase));
            if (entry == null) return null;

            using var reader = new StreamReader(entry.Open());
            using var doc = JsonDocument.Parse(reader.ReadToEnd());
            return Parse(doc.RootElement, smwPath);
        }
        catch
        {
            return null;
        }
    }

    public static InstalledPack? Install(string smwPath)
    {
        var manifest = ReadManifest(smwPath);
        if (manifest == null || string.IsNullOrWhiteSpace(manifest.Entry)) return null;

        string packId = manifest.PackId;
        string version = WidgetPackPaths.Slug(manifest.Version);
        if (string.IsNullOrWhiteSpace(version)) version = "1-0-0";

        string target = WidgetPackPaths.PackFile(packId, version);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!string.Equals(Path.GetFullPath(smwPath), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                File.Copy(smwPath, target, overwrite: true);

            string cacheDir = WidgetPackPaths.CacheDirFor(target);
            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, recursive: true);

            WidgetPackMemoryCache.InvalidatePack(target);
            WidgetPackPaths.InvalidateVersionCache(packId);
            WidgetPackPaths.InvalidateResolveCache();
        }
        catch
        {
            return null;
        }

        string htmlPath = WidgetPackPaths.EntryPath(packId, version, manifest.Entry);
        return new InstalledPack(manifest, htmlPath, target);
    }

    public static InstalledPack? MountInPlace(string smwPath)
    {
        if (!File.Exists(smwPath)) return null;

        var manifest = ReadManifest(smwPath);
        if (manifest == null || string.IsNullOrWhiteSpace(manifest.Entry)) return null;

        string full = Path.GetFullPath(smwPath);
        string mountRoot = Path.Combine(
            Path.GetDirectoryName(full)!,
            Path.GetFileNameWithoutExtension(full));

        WidgetPackPaths.InvalidateResolveCache();

        return new InstalledPack(manifest, WidgetPackPaths.EntryPathIn(mountRoot, manifest.Entry), full);
    }

    public static string? DropIntoImports(string smwPath)
    {
        try
        {
            if (!File.Exists(smwPath)) return null;

            var installed = Install(smwPath);
            if (installed != null) return installed.PackFile;

            Directory.CreateDirectory(WidgetPackPaths.PackedRoot);
            string target = Path.Combine(WidgetPackPaths.PackedRoot, Path.GetFileName(smwPath));
            if (string.Equals(Path.GetFullPath(smwPath), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                return target;

            File.Copy(smwPath, target, overwrite: true);
            return target;
        }
        catch
        {
            return null;
        }
    }

    public static int SweepCache(IEnumerable<string> liveWidgetPaths)
    {
        string cacheRoot = WidgetPackPaths.CacheRoot;
        if (!Directory.Exists(cacheRoot)) return 0;

        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in liveWidgetPaths)
        {
            var location = WidgetPackPaths.Resolve(path);
            if (location != null) live.Add(WidgetPackPaths.CacheDirFor(location.PackFileStr));
        }

        int removed = 0;

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(cacheRoot))
            {
                if (live.Contains(dir)) continue;
                try
                {
                    Directory.Delete(dir, recursive: true);
                    removed++;
                }
                catch { /**/ }
            }
        }
        catch { /**/ }

        return removed;
    }

    public record PackUpdate(string PackFile, string Version, string Entry);

    public static PackUpdate? FindUpdate(string widgetHtmlPath)
    {
        var location = WidgetPackPaths.Resolve(widgetHtmlPath);
        if (location == null) return null;

        return WidgetPackPaths.IsInPresets(location.PackFileStr)
            ? FindPresetUpdate(location)
            : FindVersionedFolderUpdate(location);
    }

    private static PackUpdate? FindPresetUpdate(WidgetPackPaths.PackLocation location)
    {
        var current = WidgetCatalog.EntryForPackFile(location.PackFileStr);
        if (current == null || string.IsNullOrWhiteSpace(current.PackId)) return null;

        PackUpdate? best = null;
        string bestVersion = current.Version;

        foreach (var candidate in WidgetCatalog.EntriesForPackId(current.PackId))
        {
            if (candidate.Source != WidgetCatalogSource.Preset) continue;
            if (string.Equals(candidate.PackPath, current.PackPath, StringComparison.OrdinalIgnoreCase)) continue;
            if (WidgetPackPaths.CompareVersions(candidate.Version, bestVersion) <= 0) continue;

            bestVersion = candidate.Version;
            best = new PackUpdate(WidgetCatalog.ToAbsolutePath(candidate.PackPath), candidate.Version, candidate.Entry);
        }

        return best;
    }

    private static PackUpdate? FindVersionedFolderUpdate(WidgetPackPaths.PackLocation location)
    {
        if (!WidgetPackPaths.IsVersionName(location.VersionStr)) return null;

        string? best = null;
        foreach (var candidate in WidgetPackPaths.VersionsIn(location.PackFolderStr))
        {
            if (!WidgetPackPaths.IsVersionName(candidate)) continue;
            if (WidgetPackPaths.CompareVersions(candidate, location.VersionStr) <= 0) continue;
            if (best == null || WidgetPackPaths.CompareVersions(candidate, best) > 0) best = candidate;
        }

        if (best == null) return null;

        string file = Path.Combine(location.PackFolderStr, best + WidgetPackPaths.PackExtension);
        return new PackUpdate(file, best, ReadManifest(file)?.Entry ?? string.Empty);
    }

    public static string? FindNewerVersion(string widgetHtmlPath) => FindUpdate(widgetHtmlPath)?.Version;

    private static WidgetPackManifest Parse(JsonElement root, string smwPath)
    {
        var widget = root.TryGetProperty("widget", out var w) ? w : default;

        string name = Str(widget, "name");
        if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileNameWithoutExtension(smwPath);
        string author = Str(widget, "author");
        string group = WidgetPackPaths.NormalizeGroup(Str(widget, "group"));

        string packId = Str(widget, "pack_id");
        if (string.IsNullOrWhiteSpace(packId)) packId = WidgetPackPaths.MakePackId(author, group, name);

        string version = Str(widget, "widget_version");
        if (string.IsNullOrWhiteSpace(version)) version = "1.0.0";

        var size = widget.ValueKind == JsonValueKind.Object && widget.TryGetProperty("size", 
            out var s) ? s : default;
        var scale = widget.ValueKind == JsonValueKind.Object && widget.TryGetProperty("scale", 
            out var c) ? c : default;

        return new WidgetPackManifest
        {
            FormatVersion = Str(root, "version"),
            AppVersion = Str(root, "app_version"),
            PackId = packId,
            Name = name,
            Author = author,
            Group = group,
            Version = version,
            DocsUrl = Str(widget, "docsUrl"),
            Tags = StrList(widget, "tags"),
            PreviewImage = Str(widget, "preview_image"),
            Entry = Str(widget, "entry").Replace('\\', '/').TrimStart('/'),
            Width = Int(size, "width", 400),
            Height = Int(size, "height", 400),
            ScaleX = Flt(scale, "x", 1f),
            ScaleY = Flt(scale, "y", 1f)
        };
    }

    private static string Str(JsonElement el, string name)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;

    private static List<string> StrList(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object ||
            !el.TryGetProperty(name, out var v) ||
            v.ValueKind != JsonValueKind.Array)
            return new List<string>();

        return v.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? string.Empty)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
    }

    private static int Int(JsonElement el, string name, int fallback)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, 
            out var v) && v.TryGetInt32(out var i)
            ? i
            : fallback;

    private static float Flt(JsonElement el, string name, float fallback)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name,
            out var v) && v.TryGetSingle(out var f) && f > 0
            ? f
            : fallback;
}
