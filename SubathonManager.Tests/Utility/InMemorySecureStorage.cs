using SubathonManager.Core.Security.Interfaces;

namespace SubathonManager.Tests.Utility;

public class InMemorySecureStorage(Dictionary<string, string>? seed = null) : ISecureStorage
{
    private readonly Dictionary<string, string> _store = seed ?? new Dictionary<string, string>();
    public int SetCount { get; private set; }
    public int SetSuccessCount { get; private set; }
    public int DeleteCount { get; private set; }
    public int GetCount { get; private set; }

    public bool Set(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Delete(key);

        var changed = !_store.TryGetValue(key, out var existing) || existing != value;
        _store[key] = value;
        SetCount += 1;
        SetSuccessCount += changed ? 1 : 0;
        return changed;
    }

    public string? Get(string key)
    {
        GetCount++;
        return _store.GetValueOrDefault(key);
    }

    public string? GetOrDefault(string key, string defaultValue = "")
    {
        GetCount++;
        return _store.GetValueOrDefault(key, defaultValue);
    }

    public bool Delete(string key)
    {
        DeleteCount++;
        return _store.Remove(key);
    }

    public bool Exists(string key) =>
        _store.ContainsKey(key);
}