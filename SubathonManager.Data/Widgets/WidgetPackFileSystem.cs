using System.Collections.Concurrent;
using SubathonManager.Core.Interfaces;

namespace SubathonManager.Data.Widgets;

public sealed class WidgetPackFileSystem : IWidgetFileSystem {
    private readonly DiskWidgetFileSystem _disk = new();
    private readonly ConcurrentDictionary<string, WidgetPackReader> _readers = new(StringComparer.OrdinalIgnoreCase);

    public bool Exists(string path) {
        WidgetPackReader? reader = ReaderFor(path, out string entry);
        return reader?.Contains(entry) ?? _disk.Exists(path);
    }

    public string? ReadAllText(string path) {
        WidgetPackReader? reader = ReaderFor(path, out string entry);
        return reader != null ? reader.ReadText(entry) : _disk.ReadAllText(path);
    }

    public byte[]? ReadAllBytes(string path) {
        WidgetPackReader? reader = ReaderFor(path, out string entry);
        return reader != null ? reader.Read(entry) : _disk.ReadAllBytes(path);
    }

    public bool IsPacked(string path) {
        return ReaderFor(path, out _) != null;
    }

    public string? GetRealFilePath(string path) {
        WidgetPackReader? reader = ReaderFor(path, out string entry);
        return reader != null ? reader.Materialize(entry) : _disk.GetRealFilePath(path);
    }

    public IEnumerable<string> EnumerateFiles(string directory) {
        WidgetPackReader? reader = ReaderFor(directory, out string prefix, out string packId,
            out string version, false);

        if (reader == null) return _disk.EnumerateFiles(directory);

        string? mountRoot = MountRootFor(Path.Combine(directory, "_"));
        if (mountRoot == null) return _disk.EnumerateFiles(directory);
        string normalized = string.IsNullOrEmpty(prefix) ? string.Empty : prefix.TrimEnd('/') + "/";

        return reader.Entries
            .Where(e => normalized.Length == 0 || e.StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
            .Select(e => WidgetPackPaths.EntryPathIn(mountRoot, e))
            .ToList();
    }

    public bool Unpack(string packedPath, string targetDir) {
        WidgetPackReader? reader = ReaderFor(packedPath, out _, out _, out _, false);
        return reader != null && reader.ExtractAll(targetDir);
    }

    private WidgetPackReader? ReaderFor(string path, out string entry) {
        return ReaderFor(path, out entry, out _, out _);
    }

    private WidgetPackReader? ReaderFor(string path, out string entry, out string packId, out string version,
        bool requireEntry = true) {
        entry = string.Empty;
        packId = string.Empty;
        version = string.Empty;

        if (string.IsNullOrWhiteSpace(path)) return null;

        string probe = requireEntry ? path : Path.Combine(path, "_");
        WidgetPackPaths.PackLocation? location = WidgetPackPaths.Resolve(probe);
        if (location == null) return null;

        packId = location.PackIdStr;
        version = location.VersionStr;

        entry = Path.GetRelativePath(location.MountRootStr, Path.GetFullPath(probe)).Replace('\\', '/');
        if (!requireEntry) entry = TrimProbe(entry);

        return _readers.GetOrAdd(location.PackFileStr,
            key => new WidgetPackReader(key, WidgetPackPaths.CacheDirFor(key)));
    }

    private static string? MountRootFor(string path) {
        return WidgetPackPaths.Resolve(path)?.MountRootStr;
    }

    private static string TrimProbe(string resolved) {
        int slash = resolved.LastIndexOf('/');
        return slash < 0 ? string.Empty : resolved[..slash];
    }
}