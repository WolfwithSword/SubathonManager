using System.Collections.Concurrent;
using System.Text;
using NSec.Cryptography;

namespace SubathonManager.Core.Security;

public static class SignatureVerification
{
    private static readonly ConcurrentDictionary<string, PublicKey> KeyCache = new();

    private static PublicKey ImportFromPem(string pem)
    {
        return KeyCache.GetOrAdd(pem, p =>
        {
            var b64 = p
                .Replace("-----BEGIN PUBLIC KEY-----", "")
                .Replace("-----END PUBLIC KEY-----", "")
                .Replace("\n", "").Replace("\r", "").Trim();
            var spki = Convert.FromBase64String(b64);
            return PublicKey.Import(SignatureAlgorithm.Ed25519, spki[^32..], KeyBlobFormat.RawPublicKey);
        });
    }
    
    public static bool VerifyEd25519Signature(string rawBody, string? timestampHeader, 
        string? signatureHex, string publicKeyPem)
    {
        if (string.IsNullOrWhiteSpace(timestampHeader) || !long.TryParse(timestampHeader, out _))
            return false;

        if (string.IsNullOrWhiteSpace(signatureHex))
            return false;

        byte[] signature;
        try { signature = Convert.FromHexString(signatureHex); }
        catch { return false; }

        if (signature.Length != 64)
            return false;

        var key = ImportFromPem(publicKeyPem);

        var message = Encoding.UTF8.GetBytes(timestampHeader + "." + rawBody);

        return SignatureAlgorithm.Ed25519.Verify(key, message, signature);
    }
}