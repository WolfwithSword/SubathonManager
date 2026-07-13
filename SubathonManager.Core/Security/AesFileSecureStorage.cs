using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SubathonManager.Core.Security.Interfaces;

namespace SubathonManager.Core.Security;

[ExcludeFromCodeCoverage]
public sealed class AesFileSecureStorage : ISecureStorage
{
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly string _filePath;
    private readonly string _keyPath;
    private readonly Lock _lock = new();
    private readonly byte[] _key;
    private Dictionary<string, string> _store;
    private ILogger<ISecureStorage>? _logger;

    public AesFileSecureStorage(ILogger<ISecureStorage>? logger, string? filePath = null, string? keyPath = null)
    {
        _logger = logger;
        _filePath = filePath
            ?? Path.GetFullPath(Path.Combine(string.Empty
                , "data/secure_store.bin"));
        _keyPath = keyPath
            ?? Path.ChangeExtension(_filePath, ".key");

        _key = LoadOrCreateKey();
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

    private byte[] LoadOrCreateKey()
    {
        if (File.Exists(_keyPath))
        {
            var existing = File.ReadAllBytes(_keyPath);
            if (existing.Length == KeySize) return existing;
            throw new InvalidOperationException(
                $"Secure store key file is invalid ({_keyPath}). " +
                "Delete secure_store.bin and secure_store.key and re-authenticate all services.");
        }

        var key = RandomNumberGenerator.GetBytes(KeySize);
        var dir = Path.GetDirectoryName(_keyPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(_keyPath, key);
        RestrictToOwner(_keyPath);
        return key;
    }

    private Dictionary<string, string> Load()
    {
        if (!File.Exists(_filePath))
            return new Dictionary<string, string>();

        try
        {
            var raw = File.ReadAllBytes(_filePath);
            if (raw.Length < NonceSize + TagSize)
                throw new CryptographicException("Secure store file is too short to be valid.");

            var nonce = raw.AsSpan(0, NonceSize);
            var tag = raw.AsSpan(NonceSize, TagSize);
            var ciphertext = raw.AsSpan(NonceSize + TagSize);
            var plaintext = new byte[ciphertext.Length];

            using var aes = new AesGcm(_key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);

            return JsonSerializer.Deserialize<Dictionary<string, string>>(
                Encoding.UTF8.GetString(plaintext))
                ?? new Dictionary<string, string>();
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException(
                "Secure store could not be decrypted. " +
                "If you moved the app between machines or lost secure_store.key, " +
                "delete secure_store.bin and re-authenticate all services.",
                ex);
        }
    }

    private void Flush()
    {
        var json = JsonSerializer.Serialize(_store);
        var plaintext = Encoding.UTF8.GetBytes(json);

        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var tag = new byte[TagSize];
        var ciphertext = new byte[plaintext.Length];

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        var raw = new byte[NonceSize + TagSize + ciphertext.Length];
        nonce.CopyTo(raw, 0);
        tag.CopyTo(raw, NonceSize);
        ciphertext.CopyTo(raw, NonceSize + TagSize);

        var tmp = _filePath + ".tmp";
        File.WriteAllBytes(tmp, raw);
        RestrictToOwner(tmp);
        File.Move(tmp, _filePath, overwrite: true);
    }

    private static void RestrictToOwner(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
