namespace SubathonManager.Core.Security.Interfaces;

public interface ISecureStorage
{
    /// <summary>Store or overwrite a value. Flushes to disk immediately.</summary>
    bool Set(string key, string value);

    /// <summary>Returns the value, or null if the key doesn't exist.</summary>
    string? Get(string key);
    
    string? GetOrDefault(string key, string defaultValue);

    /// <summary>Remove a key. No-op if it doesn't exist. Flushes to disk.</summary>
    bool Delete(string key);

    bool Exists(string key);
}