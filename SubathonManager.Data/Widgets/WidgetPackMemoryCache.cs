namespace SubathonManager.Data.Widgets;

internal static class WidgetPackMemoryCache
{
    private const long Budget = 32 * 1024 * 1024;
    public const long MaxEntrySize = 2 * 1024 * 1024;

    private static readonly Lock CacheLock = new();
    private static readonly Dictionary<string, LinkedListNode<CacheItem>> Map = new(StringComparer.OrdinalIgnoreCase);

    private static readonly LinkedList<CacheItem> Order = [];

    private static long _size;

    private sealed class CacheItem(string key, byte[] data)
    {
        public string Key { get; } = key;
        public byte[] Data { get; } = data;
    }

    public static string MakeKey(string packFile, string entry) => $"{packFile}|{entry}";

    public static bool TryGet(string key, out byte[] data)
    {
        lock (CacheLock)
        {
            if (!Map.TryGetValue(key, out var node))
            {
                data = [];
                return false;
            }

            Order.Remove(node);
            Order.AddFirst(node);
            data = node.Value.Data;
            return true;
        }
    }

    public static void Set(string key, byte[] data)
    {
        if (data.LongLength >= MaxEntrySize) return;

        lock (CacheLock)
        {
            if (Map.TryGetValue(key, out var existing))
            {
                _size -= existing.Value.Data.LongLength;
                Order.Remove(existing);
                Map.Remove(key);
            }

            var node = Order.AddFirst(new CacheItem(key, data));
            Map[key] = node;
            _size += data.LongLength;

            while (_size > Budget && Order.Last is { } tail)
            {
                Order.RemoveLast();
                Map.Remove(tail.Value.Key);
                _size -= tail.Value.Data.LongLength;
            }
        }
    }

    public static void InvalidatePack(string packFile)
    {
        string prefix = packFile + "|";

        lock (CacheLock)
        {
            var doomed = Map.Keys
                .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var key in doomed)
            {
                if (!Map.TryGetValue(key, out var node)) continue;
                Order.Remove(node);
                Map.Remove(key);
                _size -= node.Value.Data.LongLength;
            }
        }
    }

    public static void Clear()
    {
        lock (CacheLock)
        {
            Map.Clear();
            Order.Clear();
            _size = 0;
        }
    }

    internal static long CurrentSize
    {
        get { lock (CacheLock) return _size; }
    }
}
