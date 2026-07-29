using System.Collections.Concurrent;
using SubathonManager.Core.Interfaces;

namespace SubathonManager.Data.Widgets;

public sealed class WidgetPackFileSystem : IWidgetFileSystem
{
    private readonly DiskWidgetFileSystem _disk = new();
    private readonly ConcurrentDictionary<string, WidgetPackReader> _readers = new(StringComparer.OrdinalIgnoreCase);

    public bool Exists(string path)
    {
        var reader = ReaderFor(path, out var entry);
        return reader?.Contains(entry) ?? _disk.Exists(path);
    }

    public string? ReadAllText(string path)
    {
        var reader = ReaderFor(path, out var entry);
        return reader != null ? reader.ReadText(entry) : _disk.ReadAllText(path);
    }

    public byte[]? ReadAllBytes(string path)
    {
        var reader = ReaderFor(path, out var entry);
        return reader != null ? reader.Read(entry) : _disk.ReadAllBytes(path);
    }

    public bool IsPacked(string path) => ReaderFor(path, out _) != null;

    public string? GetRealFilePath(string path)
    {
        var reader = ReaderFor(path, out var entry);
        return reader != null ? reader.Materialize(entry) : _disk.GetRealFilePath(path);
    }

    public IEnumerable<string> EnumerateFiles(string directory)
    {
        var reader = ReaderFor(directory, out var prefix, out var packId,
            out var version, requireEntry: false);
        
        if (reader == null) return _disk.EnumerateFiles(directory);
        string normalized = string.IsNullOrEmpty(prefix) ? string.Empty : prefix.TrimEnd('/') + "/";

        return reader.Entries
            .Where(e => normalized.Length == 0 || e.StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
            .Select(e => WidgetPackPaths.EntryPath(packId, version, e))
            .ToList();
    }

    public bool Unpack(string packedPath, string targetDir)
    {
        var reader = ReaderFor(packedPath, out _, out _, out _, requireEntry: false);
        return reader != null && reader.ExtractAll(targetDir);
    }

    private WidgetPackReader? ReaderFor(string path, out string entry) =>
        ReaderFor(path, out entry, out _, out _);

    private WidgetPackReader? ReaderFor(string path, out string entry, out string packId, out string version,
        bool requireEntry = true)
    {
        entry = string.Empty;
        packId = string.Empty;
        version = string.Empty;

        if (string.IsNullOrWhiteSpace(path) || !WidgetPackPaths.IsMountPath(path)) return null;

        string probe = requireEntry ? path : Path.Combine(path, "_");
        if (!WidgetPackPaths.TryResolve(probe, out var packFile,
                out var resolved, out packId, out version))
            return null;

        entry = requireEntry ? resolved : TrimProbe(resolved);

        string cacheDir = Path.Combine(WidgetPackPaths.CacheRoot, packId, version);
        return _readers.GetOrAdd(packFile, key => new WidgetPackReader(key, cacheDir));
    }

    private static string TrimProbe(string resolved)
    {
        int slash = resolved.LastIndexOf('/');
        return slash < 0 ? string.Empty : resolved[..slash];
    }
}
