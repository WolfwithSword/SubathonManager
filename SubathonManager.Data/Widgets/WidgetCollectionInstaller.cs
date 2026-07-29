using System.IO.Compression;
using System.Text.Json;

namespace SubathonManager.Data.Widgets;

public class WidgetCollectionManifest
{
    public string FormatVersion { get; init; } = "1";
    public string AppVersion { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;
    public string Version { get; init; } = "1.0.0";
    public string Description { get; init; } = string.Empty;
    public List<string> Tags { get; init; } = new();
}

public static class WidgetCollectionInstaller
{
    public const string CollectionExtension = ".smwc";
    public const string ManifestFileName = "collection.json";

    public record InstalledCollection(
        WidgetCollectionManifest? Manifest,
        List<WidgetPackInstaller.InstalledPack> Packs,
        int Failed);

    public static WidgetCollectionManifest? ReadManifest(string smwcPath)
    {
        try
        {
            using var stream = new FileStream(smwcPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

            var entry = archive.Entries.FirstOrDefault(e =>
                string.Equals(e.FullName.Replace('\\', '/'), ManifestFileName, StringComparison.OrdinalIgnoreCase));
            if (entry == null) return null;

            using var reader = new StreamReader(entry.Open());
            using var doc = JsonDocument.Parse(reader.ReadToEnd());
            return Parse(doc.RootElement, smwcPath);
        }
        catch
        {
            return null;
        }
    }

    public static InstalledCollection? InstallAll(string smwcPath)
    {
        if (!File.Exists(smwcPath)) return null;

        var manifest = ReadManifest(smwcPath);
        var installed = new List<WidgetPackInstaller.InstalledPack>();
        int failed = 0;

        string scratch = Path.Combine(Path.GetTempPath(), "smwc-" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(scratch);

            using var stream = new FileStream(smwcPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;
                if (!entry.Name.EndsWith(WidgetPackPaths.PackExtension, StringComparison.OrdinalIgnoreCase))
                    continue;

                string staged = Path.Combine(scratch, $"{installed.Count + failed}-{entry.Name}");

                try
                {
                    entry.ExtractToFile(staged, overwrite: true);
                    var pack = WidgetPackInstaller.Install(staged);
                    if (pack != null) installed.Add(pack);
                    else failed++;
                }
                catch
                {
                    failed++;
                }
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            try
            {
                if (Directory.Exists(scratch))
                    Directory.Delete(scratch, recursive: true);
            }
            catch { /**/ }
        }

        if (installed.Count == 0 && failed == 0) return null;

        return new InstalledCollection(manifest, installed, failed);
    }

    private static WidgetCollectionManifest Parse(JsonElement root, string smwcPath)
    {
        var collection = root.TryGetProperty("collection", out var c) ? c : default;

        string name = Str(collection, "name");
        if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileNameWithoutExtension(smwcPath);

        string version = Str(collection, "version");
        if (string.IsNullOrWhiteSpace(version)) version = "1.0.0";

        return new WidgetCollectionManifest
        {
            FormatVersion = Str(root, "format_version"),
            AppVersion = Str(root, "app_version"),
            Name = name,
            Author = Str(collection, "author"),
            Version = version,
            Description = Str(collection, "description"),
            Tags = StrList(collection, "tags")
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
            return new List<string>();

        return v.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? string.Empty)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
    }
}
