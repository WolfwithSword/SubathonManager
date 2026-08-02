using System.IO.Compression;
using System.Text;
using System.Text.Json;
using SubathonManager.Data.Widgets;
// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.Tests.Utility;

public sealed class TempWorkspace : IDisposable
{
    private readonly string _previousCwd;

    public string Root { get; }

    public TempWorkspace(string? label = null)
    {
        _previousCwd = Directory.GetCurrentDirectory();
        Root = Path.Combine(Path.GetTempPath(), "SubathonManagerTests",
            $"{label ?? "ws"}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
        Directory.SetCurrentDirectory(Root);
    }

    public string Path_(params string[] segments)
    {
        string full = System.IO.Path.Combine(new[] { Root }.Concat(segments).ToArray());
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        return full;
    }

    public string Dir(params string[] segments)
    {
        string full = System.IO.Path.Combine(new[] { Root }.Concat(segments).ToArray());
        Directory.CreateDirectory(full);
        return full;
    }

    public string WriteFile(string relativePath, string content)
    {
        string full = Path_(relativePath.Split('/', '\\'));
        File.WriteAllText(full, content);
        return full;
    }

    public string WriteBytes(string relativePath, byte[] content)
    {
        string full = Path_(relativePath.Split('/', '\\'));
        File.WriteAllBytes(full, content);
        return full;
    }

    public void Dispose()
    {
        try { Directory.SetCurrentDirectory(_previousCwd); } catch { /**/ }
        TestPacks.ResetPathCaches();
        try { Directory.Delete(Root, recursive: true); } catch { /**/ }
    }
}

public static class TestPacks
{
    public static void ResetPathCaches()
    {
        WidgetPackPaths.InvalidateResolveCache();
        WidgetPackPaths.InvalidateVersionCache();
        WidgetPackMemoryCache.Clear();
    }

    public static string WriteZip(string path, IEnumerable<KeyValuePair<string, byte[]>> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (File.Exists(path)) File.Delete(path);

        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, bytes) in entries)
        {
            var entry = zip.CreateEntry(name);
            using var stream = entry.Open();
            stream.Write(bytes);
        }

        return path;
    }

    public static string WriteZip(string path, IEnumerable<KeyValuePair<string, string>> entries)
        => WriteZip(path, entries.Select(kv =>
            new KeyValuePair<string, byte[]>(kv.Key, Encoding.UTF8.GetBytes(kv.Value))));

    public static string WidgetManifestJson(
        string name = "Timer",
        string author = "Wolf",
        string group = "widgets",
        string version = "1.0.0",
        string entry = "content/widget.html",
        string? packId = null,
        string preview = "",
        string docsUrl = "",
        IEnumerable<string>? tags = null,
        int width = 400,
        int height = 300,
        float scaleX = 1f,
        float scaleY = 1f)
    {
        var obj = new Dictionary<string, object?>
        {
            ["version"] = "1",
            ["app_version"] = "9.9.9",
            ["widget"] = new Dictionary<string, object?>
            {
                ["pack_id"] = packId,
                ["name"] = name,
                ["author"] = author,
                ["group"] = group,
                ["widget_version"] = version,
                ["tags"] = tags?.ToList() ?? new List<string>(),
                ["preview_image"] = preview,
                ["docsUrl"] = docsUrl,
                ["entry"] = entry,
                ["size"] = new { width, height },
                ["scale"] = new { x = scaleX, y = scaleY }
            }
        };
        return JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true });
    }

    public static string WriteSmw(string path, string manifestJson,
        IEnumerable<KeyValuePair<string, string>>? files = null)
    {
        var entries = new List<KeyValuePair<string, string>>
        {
            new("widget.json", manifestJson)
        };
        if (files != null) entries.AddRange(files);
        return WriteZip(path, entries);
    }

    public static string CollectionManifestJson(string name = "Pack", string author = "Wolf",
        string version = "2.0.0", string description = "desc", IEnumerable<string>? tags = null)
    {
        var obj = new Dictionary<string, object?>
        {
            ["format_version"] = "1",
            ["app_version"] = "9.9.9",
            ["collection"] = new Dictionary<string, object?>
            {
                ["name"] = name,
                ["author"] = author,
                ["version"] = version,
                ["description"] = description,
                ["tags"] = tags?.ToList() ?? []
            }
        };
        return JsonSerializer.Serialize(obj);
    }

    public static string OverlayManifestJson(string name = "My Overlay", string author = "Wolf",
        string? overlayVersion = "1.2.0", string formatVersion = "1", string rootVersion = "",
        IEnumerable<string>? tags = null)
    {
        var route = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["author"] = author,
            ["tags"] = tags?.ToList() ?? []
        };
        if (overlayVersion != null) route["overlay_version"] = overlayVersion;

        var obj = new Dictionary<string, object?>
        {
            ["format_version"] = formatVersion,
            ["app_version"] = "9.9.9",
            ["route"] = route
        };
        if (!string.IsNullOrEmpty(rootVersion)) obj["version"] = rootVersion;

        return JsonSerializer.Serialize(obj);
    }

    public static string WriteSmo(string path, string manifestJson,
        IEnumerable<KeyValuePair<string, string>>? files = null)
    {
        var entries = new List<KeyValuePair<string, string>> { new("overlay.json", manifestJson) };
        if (files != null) entries.AddRange(files);
        return WriteZip(path, entries);
    }
}
