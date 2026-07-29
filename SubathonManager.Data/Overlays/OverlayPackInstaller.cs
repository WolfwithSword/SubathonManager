using System.IO.Compression;
using System.Text.Json;

namespace SubathonManager.Data.Overlays;

public class OverlayManifest
{
    public string FormatVersion { get; init; } = "1";
    public string AppVersion { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;
    public string Version { get; init; } = "1.0.0";
    public List<string> Tags { get; init; } = [];
}

public static class OverlayPackInstaller
{
    public const string ManifestFileName = "overlay.json";
    public record InstalledOverlay(OverlayManifest Manifest, string ArchiveFile, string UnpackDir);

    public static OverlayManifest? ReadManifest(string smoPath)
    {
        try
        {
            using var stream = new FileStream(smoPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

            var entry = archive.Entries.FirstOrDefault(e =>
                string.Equals(e.FullName.Replace('\\', '/'), ManifestFileName, StringComparison.OrdinalIgnoreCase));
            if (entry == null) return null;

            using var reader = new StreamReader(entry.Open());
            using var doc = JsonDocument.Parse(reader.ReadToEnd());
            return Parse(doc.RootElement, smoPath);
        }
        catch
        {
            return null;
        }
    }

    public static InstalledOverlay? Install(string smoPath)
    {
        var manifest = ReadManifest(smoPath);
        if (manifest == null) return null;

        string archiveTarget = OverlayPackPaths.ArchiveFile(manifest.Author, manifest.Name, manifest.Version);
        string unpackDir = OverlayPackPaths.UnpackDir(manifest.Author, manifest.Name);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(archiveTarget)!);

            if (!string.Equals(Path.GetFullPath(smoPath), Path.GetFullPath(archiveTarget),
                    StringComparison.OrdinalIgnoreCase))
                File.Copy(smoPath, archiveTarget, overwrite: true);

            Directory.CreateDirectory(unpackDir);
            ExtractSafely(archiveTarget, unpackDir);
        }
        catch
        {
            return null;
        }

        return new InstalledOverlay(manifest, archiveTarget, unpackDir);
    }

    private static void ExtractSafely(string archivePath, string targetDir)
    {
        string root = Path.GetFullPath(targetDir) + Path.DirectorySeparatorChar;

        using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue; // directory marker

            string relative = entry.FullName.Replace('\\', '/').TrimStart('/');
            string destination = Path.GetFullPath(
                Path.Combine(targetDir, relative.Replace('/', Path.DirectorySeparatorChar)));

            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }
    }

    private static OverlayManifest Parse(JsonElement root, string smoPath)
    {
        var route = root.TryGetProperty("route", out var r) ? r : default;
        string name = Str(route, "name");

        if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileNameWithoutExtension(smoPath);

        string version = Str(route, "overlay_version");
        if (string.IsNullOrWhiteSpace(version)) version = Str(root, "version");
        if (string.IsNullOrWhiteSpace(version)) version = "1.0.0";

        return new OverlayManifest
        {
            FormatVersion = Str(root, "format_version"),
            AppVersion = Str(root, "app_version"),
            Name = name,
            Author = Str(route, "author"),
            Version = version,
            Tags = StrList(route, "tags")
        };
    }

    private static string Str(JsonElement el, string name)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) &&
           v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;

    private static List<string> StrList(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object ||
            !el.TryGetProperty(name, out var v) ||
            v.ValueKind != JsonValueKind.Array)
            return [];

        return v.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? string.Empty)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
    }
}
