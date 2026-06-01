using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SubathonManager.Core.Security.Interfaces;

namespace SubathonManager.Core.Security;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[ExcludeFromCodeCoverage]
public sealed class DpapiSecureStorage : ISecureStorage
{
    private readonly string _filePath;
    private readonly Lock _lock = new();
    private Dictionary<string, string> _store;
    private ILogger<ISecureStorage>? _logger;

    public DpapiSecureStorage(ILogger<ISecureStorage>?  logger, string? filePath = null)
    {
        _logger = logger;
        _filePath = filePath
            ?? Path.GetFullPath(Path.Combine(string.Empty
                , "data/secure_store.bin"));

        _store = Load();
    }

    public bool Set(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        bool hasUpdated = false;
        if (string.IsNullOrWhiteSpace(value))
        {
            Delete(key);
            return false;
        }
        if (string.Equals(GetOrDefault(key), value)) return false;
        lock (_lock)
        {
            _store[key] = value;
            Flush();
            hasUpdated = true;
        }

        if (hasUpdated)
        {
            _logger?.LogInformation("[SecureStorage] Saved {Key}", key);
        }
        return hasUpdated;
    }

    public string? Get(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (_lock)
            return _store.GetValueOrDefault(key);
    }
    
    public string? GetOrDefault(string key, string defaultValue = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (_lock)
            return _store.GetValueOrDefault(key, defaultValue);
    }

    public bool Delete(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (_lock)
        {
            if (_store.Remove(key))
            {
                Flush();
                _logger?.LogInformation("[SecureStorage] Removed {Key}", key);
                return true;
            }
        }
        return false;
    }

    public bool Exists(string key)
    {
        lock (_lock)
            return _store.ContainsKey(key);
    }

    private Dictionary<string, string> Load()
    {
        if (!File.Exists(_filePath))
            return new Dictionary<string, string>();

        try
        {
            var encrypted = File.ReadAllBytes(_filePath);
            var plaintext = ProtectedData.Unprotect(
                encrypted, null, DataProtectionScope.CurrentUser);

            return JsonSerializer.Deserialize<Dictionary<string, string>>(
                Encoding.UTF8.GetString(plaintext))
                ?? new Dictionary<string, string>();
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException(
                "Secure store could not be decrypted. " +
                "If you moved the app between user accounts, delete secure_store.bin and re-authenticate all services.",
                ex);
        }
    }

    private void Flush()
    {
        var json = JsonSerializer.Serialize(_store);
        var plaintext = Encoding.UTF8.GetBytes(json);
        var encrypted = ProtectedData.Protect(
            plaintext, null, DataProtectionScope.CurrentUser);

        var tmp = _filePath + ".tmp";
        File.WriteAllBytes(tmp, encrypted);
        File.Move(tmp, _filePath, overwrite: true);
    }
}