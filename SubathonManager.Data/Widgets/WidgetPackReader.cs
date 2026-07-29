using System.IO.Compression;
using System.Text;

namespace SubathonManager.Data.Widgets;

internal sealed class WidgetPackReader(string packFile, string cacheDir)
{
    private const long MaterializeThreshold = WidgetPackMemoryCache.MaxEntrySize;

    private readonly Lock _indexLock = new();
    private Dictionary<string, IndexedEntry>? _index;
    private DateTime _indexedAt;

    private readonly record struct IndexedEntry(string FullName, long Length);

    private Dictionary<string, IndexedEntry> Index
    {
        get
        {
            lock (_indexLock)
            {
                var stamp = File.Exists(packFile) ? File.GetLastWriteTimeUtc(packFile) : DateTime.MinValue;
                if (_index != null && stamp == _indexedAt) return _index;

                var index = new Dictionary<string, IndexedEntry>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    using var stream = new FileStream(packFile, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
                    foreach (var entry in archive.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name)) continue; // dir
                        index[Normalize(entry.FullName)] = new IndexedEntry(entry.FullName, entry.Length);
                    }
                }
                catch { /**/ }

                WidgetPackMemoryCache.InvalidatePack(packFile);
                _index = index;
                _indexedAt = stamp;
                return index;
            }
        }
    }

    public bool Contains(string entry) => Index.ContainsKey(Normalize(entry));

    public IEnumerable<string> Entries => Index.Keys;

    public long LengthOf(string entry)
        => Index.TryGetValue(Normalize(entry), out var found) ? found.Length : -1;

    public byte[]? Read(string entry)
    {
        string key = Normalize(entry);
        string cacheKey = WidgetPackMemoryCache.MakeKey(packFile, key);

        if (WidgetPackMemoryCache.TryGet(cacheKey, out var cached)) return cached;
        if (!Index.TryGetValue(key, out var indexed)) return null;

        byte[]? bytes = ReadRaw(indexed.FullName);
        if (bytes == null) return null;

        if (indexed.Length < MaterializeThreshold)
            WidgetPackMemoryCache.Set(cacheKey, bytes);

        return bytes;
    }

    public string? ReadText(string entry)
    {
        var bytes = Read(entry);
        if (bytes == null) return null;
        return bytes is [0xEF, 0xBB, 0xBF, ..]
            ? Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3)
            : Encoding.UTF8.GetString(bytes);
    }

    public string? Materialize(string entry)
    {
        string key = Normalize(entry);
        if (!Index.TryGetValue(key, out var indexed)) return null;
        if (indexed.Length < MaterializeThreshold) return null;

        string target = Path.Combine(cacheDir, key.Replace('/', Path.DirectorySeparatorChar));

        try
        {
            var info = new FileInfo(target);
            if (info.Exists && info.Length == indexed.Length) return target;

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            string temp = target + ".partial";
            using (var stream = new FileStream(packFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                var zipEntry = archive.GetEntry(indexed.FullName);
                if (zipEntry == null) return null;
                zipEntry.ExtractToFile(temp, overwrite: true);
            }

            File.Move(temp, target, overwrite: true);
            return target;
        }
        catch
        {
            return null;
        }
    }

    public bool ExtractAll(string targetDir)
    {
        try
        {
            Directory.CreateDirectory(targetDir);
            using var stream = new FileStream(packFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;

                string destination = Path.GetFullPath(
                    Path.Combine(targetDir, Normalize(entry.FullName).Replace('/', Path.DirectorySeparatorChar)));

                if (!destination.StartsWith(Path.GetFullPath(targetDir) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                    continue;

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: true);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private byte[]? ReadRaw(string fullName)
    {
        try
        {
            using var stream = new FileStream(packFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var entry = archive.GetEntry(fullName);
            if (entry == null) return null;

            using var entryStream = entry.Open();
            using var buffer = new MemoryStream(entry.Length > 0 ? (int)Math.Min(entry.Length, int.MaxValue) : 0);
            entryStream.CopyTo(buffer);
            return buffer.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static string Normalize(string entry) => entry.Replace('\\', '/').TrimStart('/');
}