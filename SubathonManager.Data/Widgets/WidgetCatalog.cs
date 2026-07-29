using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Models;
// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.Data.Widgets;

public static class WidgetCatalog
{
    public static string PresetsRoot => Path.GetFullPath("./presets");
    public static string ImportsRoot => WidgetPackPaths.ImportsRoot;
    public static string PreviewCacheRoot => Path.GetFullPath(Path.Combine("./cache", "widget-previews"));

    public record ScanRoot(string Path, WidgetCatalogSource Source);

    public static IEnumerable<ScanRoot> Roots()
    {
        yield return new ScanRoot(PresetsRoot, WidgetCatalogSource.Preset);
        yield return new ScanRoot(ImportsRoot, WidgetCatalogSource.Imported);
    }

    public static async Task<List<WidgetCatalogEntry>> RefreshAsync(
        IDbContextFactory<AppDbContext> factory, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var rows = await db.WidgetCatalogEntries.ToListAsync(ct);
        var byPath = new Dictionary<string, WidgetCatalogEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
            byPath.TryAdd(row.PackPath, row);

        var live = new List<WidgetCatalogEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;

        foreach (var root in Roots())
        {
            foreach (var file in EnumeratePackages(root.Path))
            {
                ct.ThrowIfCancellationRequested();

                string stored = ToStoredPath(file);
                if (!seen.Add(stored)) continue;

                FileInfo info;
                try { info = new FileInfo(file); }
                catch { continue; }

                byPath.TryGetValue(stored, out var entry);

                bool unchanged = entry != null &&
                                 entry.FileSize == info.Length &&
                                 entry.FileModifiedTicks == info.LastWriteTimeUtc.Ticks;

                if (unchanged)
                {
                    if (!string.IsNullOrWhiteSpace(entry!.PreviewImage) &&
                        (string.IsNullOrWhiteSpace(entry.PreviewCachePath) || !File.Exists(entry.PreviewCachePath)))
                        entry.PreviewCachePath = ExtractPreview(file, entry.PreviewImage) ?? string.Empty;

                    entry.Source = root.Source;
                    entry.LastSeenUtc = now;
                    live.Add(entry);
                    continue;
                }

                var manifest = WidgetPackInstaller.ReadManifest(file);
                if (manifest == null || string.IsNullOrWhiteSpace(manifest.Entry))
                {
                    if (entry != null) db.WidgetCatalogEntries.Remove(entry);
                    continue;
                }

                if (entry == null)
                {
                    entry = new WidgetCatalogEntry { PackPath = stored };
                    db.WidgetCatalogEntries.Add(entry);
                }

                entry.FileSize = info.Length;
                entry.FileModifiedTicks = info.LastWriteTimeUtc.Ticks;
                entry.Source = root.Source;
                entry.PackId = manifest.PackId;
                entry.Name = manifest.Name;
                entry.Author = manifest.Author;
                entry.Group = WidgetPackPaths.NormalizeGroup(manifest.Group);
                entry.Version = manifest.Version;
                entry.Entry = manifest.Entry;
                entry.DocsUrl = manifest.DocsUrl;
                entry.Tags = string.Join(", ", manifest.Tags);
                entry.PreviewImage = manifest.PreviewImage;
                entry.PreviewCachePath = ExtractPreview(file, manifest.PreviewImage, force: true) ?? string.Empty;
                entry.ScaleX = manifest.ScaleX;
                entry.ScaleY = manifest.ScaleY;
                entry.LastSeenUtc = now;

                live.Add(entry);
            }
        }

        foreach (var row in rows.Where(row => !seen.Contains(row.PackPath)))
        {
            db.WidgetCatalogEntries.Remove(row);
            DeletePreview(row.PreviewCachePath);
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch { /**/ }

        return live
            .OrderBy(e => e.Author, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Group, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(e => e.Version, Comparer<string>.Create(WidgetPackPaths.CompareVersions))
            .ToList();
    }

    public static async Task<bool> DeleteAsync(IDbContextFactory<AppDbContext> factory, WidgetCatalogEntry entry,
        CancellationToken ct = default)
    {
        if (entry.Source == WidgetCatalogSource.Preset) return false;

        string file = ToAbsolutePath(entry.PackPath);

        try
        {
            if (File.Exists(file)) File.Delete(file);

            string cacheDir = WidgetPackPaths.CacheDirFor(file);
            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, recursive: true);
        }
        catch
        {
            return false;
        }

        DeletePreview(entry.PreviewCachePath);
        WidgetPackMemoryCache.InvalidatePack(file);
        WidgetPackPaths.InvalidateVersionCache();
        WidgetPackPaths.InvalidateResolveCache();

        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await db.WidgetCatalogEntries.Where(e => e.PackPath == entry.PackPath).ExecuteDeleteAsync(ct);
        }
        catch { /**/ }

        try
        {
            string? folder = Path.GetDirectoryName(file);
            if (folder != null && Directory.Exists(folder) &&
                !Directory.EnumerateFileSystemEntries(folder).Any() &&
                WidgetPackPaths.IsInGlobalStore(folder))
                Directory.Delete(folder);
        }
        catch { /**/ }

        return true;
    }

    public static string ToAbsolutePath(string storedPath)
    {
        try
        {
            return Path.IsPathRooted(storedPath)
                ? storedPath
                : Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), storedPath));
        }
        catch { return storedPath; }
    }

    private static string ToStoredPath(string absolute)
    {
        try
        {
            string full = Path.GetFullPath(absolute);
            string baseDir = Directory.GetCurrentDirectory();
            string relative = Path.GetRelativePath(baseDir, full);

            return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)
                ? full
                : relative;
        }
        catch { return absolute; }
    }

    private static IEnumerable<string> EnumeratePackages(string root)
    {
        if (!Directory.Exists(root)) yield break;

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "*" + WidgetPackPaths.PackExtension, SearchOption.AllDirectories);
        }
        catch { yield break; }

        foreach (var file in files)
            yield return file;
    }

    private static string? ExtractPreview(string packFile, string previewEntry, bool force = false)
    {
        if (string.IsNullOrWhiteSpace(previewEntry)) return null;

        string ext = Path.GetExtension(previewEntry);
        if (string.IsNullOrWhiteSpace(ext)) ext = ".png";

        string target = Path.Combine(PreviewCacheRoot, PreviewKey(packFile) + ext);
        if (!force && File.Exists(target)) return target;

        try
        {
            Directory.CreateDirectory(PreviewCacheRoot);

            using var stream = new FileStream(packFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

            string wanted = previewEntry.Replace('\\', '/').TrimStart('/');
            var entry = archive.Entries.FirstOrDefault(e =>
                string.Equals(e.FullName.Replace('\\', '/'), wanted, StringComparison.OrdinalIgnoreCase));
            if (entry == null) return null;

            entry.ExtractToFile(target, overwrite: true);
            return target;
        }
        catch
        {
            return null;
        }
    }

    private static void DeletePreview(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            string full = Path.GetFullPath(path);
            if (!full.StartsWith(PreviewCacheRoot, StringComparison.OrdinalIgnoreCase)) return;
            if (File.Exists(full)) File.Delete(full);
        }
        catch { /**/ }
    }

    private static string PreviewKey(string packFile)
    {
        string key;
        try { key = Path.GetFullPath(packFile).ToLowerInvariant(); }
        catch { key = packFile.ToLowerInvariant(); }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return $"{WidgetPackPaths.Slug(Path.GetFileNameWithoutExtension(packFile))}-{Convert.ToHexString(hash)[..12].ToLowerInvariant()}";
    }
}
