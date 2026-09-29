using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Web;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Objects;
using SubathonManager.Core.Security.Interfaces;

namespace SubathonManager.Services;

public record OAuthProvider(
    string Name,
    string AccessTokenKey,
    string? RefreshTokenKey = null,
    string? ExpiryKey = null,
    string? ClientIdKey = null,
    TimeSpan? DefaultLifetime = null,
    TimeSpan? RefreshMargin = null);

public class OAuthService(
    ILogger<OAuthService>? logger,
    IHttpClientFactory httpClientFactory,
    ISecureStorage secureStorage) {
    private const string BaseUrl = "https://oauth.subathonmanager.app/auth";

    private readonly ConcurrentDictionary<string, TaskCompletionSource<OAuthCallback?>> _pending =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _refreshLocks = new(StringComparer.OrdinalIgnoreCase);
    internal TimeSpan CallbackTimeout = TimeSpan.FromMinutes(15);

    internal Action<string> OpenBrowser =
        url => Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });

    public static string LoginUrl(string provider) {
        return $"{BaseUrl}/{provider}/login";
    }

    public static string RefreshUrl(string provider) {
        return $"{BaseUrl}/{provider}/refresh";
    }

    public string? GetAccessToken(OAuthProvider provider) {
        return Read(provider.AccessTokenKey);
    }

    public string? GetRefreshToken(OAuthProvider provider) {
        return Read(provider.RefreshTokenKey);
    }

    public string? GetClientId(OAuthProvider provider) {
        return Read(provider.ClientIdKey);
    }

    public bool HasTokens(OAuthProvider provider) {
        return GetAccessToken(provider) != null &&
               (provider.RefreshTokenKey == null || GetRefreshToken(provider) != null);
    }

    public void RevokeTokens(OAuthProvider provider) {
        foreach (string? key in new[] {
                     provider.AccessTokenKey, provider.RefreshTokenKey, provider.ExpiryKey, provider.ClientIdKey
                 })
            if (key != null)
                secureStorage.Delete(key);
    }

    public DateTime? GetExpiry(OAuthProvider provider) {
        if (provider.ExpiryKey != null)
            return DateTime.TryParse(Read(provider.ExpiryKey), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime dt)
                ? dt
                : null;

        string? access = GetAccessToken(provider);
        return access == null ? null : Utils.GetAccessTokenExpiry(access);
    }

    public bool NeedsRefresh(OAuthProvider provider) {
        if (GetAccessToken(provider) == null) return false;
        DateTime? expires = GetExpiry(provider);
        if (expires == null) return provider.ExpiryKey != null;
        return DateTime.UtcNow >= expires.Value - (provider.RefreshMargin ?? TimeSpan.FromSeconds(60));
    }

    internal void StoreTokens(OAuthProvider provider, OAuthCallback tokens) {
        // ReSharper disable once NullableWarningSuppressionIsUsed
        secureStorage.Set(provider.AccessTokenKey, tokens.AccessToken!);
        if (provider.RefreshTokenKey != null && !string.IsNullOrWhiteSpace(tokens.RefreshToken))
            secureStorage.Set(provider.RefreshTokenKey, tokens.RefreshToken);
        if (provider.ClientIdKey != null && !string.IsNullOrWhiteSpace(tokens.ClientId))
            secureStorage.Set(provider.ClientIdKey, tokens.ClientId);
        if (provider.ExpiryKey == null) return;

        TimeSpan? lifetime = double.TryParse(tokens.ExpiresIn, NumberStyles.Any, CultureInfo.InvariantCulture,
            out double seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : provider.DefaultLifetime;
        if (lifetime != null)
            secureStorage.Set(provider.ExpiryKey,
                DateTime.UtcNow.Add(lifetime.Value).ToString("O", CultureInfo.InvariantCulture));
    }

    public async Task<bool> AuthorizeAsync(OAuthProvider provider, CancellationToken ct = default) {
        RevokeTokens(provider);
        OAuthCallback? cb = await WaitForCallbackAsync(provider.Name, ct);
        if (cb == null || (provider.RefreshTokenKey != null && string.IsNullOrEmpty(cb.RefreshToken))) {
            logger?.LogWarning("[OAuth] {Provider} login did not produce tokens", provider.Name);
            return false;
        }

        StoreTokens(provider, cb);
        if (logger?.IsEnabled(LogLevel.Information) ?? false) 
            logger?.LogInformation("[OAuth] {Provider} tokens stored", provider.Name);
        return true;
    }

    private async Task<OAuthCallback?> WaitForCallbackAsync(string name, CancellationToken ct) {
        var tcs = new TaskCompletionSource<OAuthCallback?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending.AddOrUpdate(name, tcs, (_, previous) => {
            previous.TrySetResult(null);
            return tcs;
        });

        try {
            if (logger?.IsEnabled(LogLevel.Debug) ?? false)
                logger?.LogDebug("[OAuth] Opening {Provider} login...", name);
            OpenBrowser(LoginUrl(name));

            OAuthCallback? cb = await tcs.Task.WaitAsync(CallbackTimeout, ct);
            if (cb == null) {
                if (logger?.IsEnabled(LogLevel.Debug) ?? false)
                    logger?.LogDebug("[OAuth] {Provider} login superseded by a newer attempt", name);
                return null;
            }

            if (!string.IsNullOrEmpty(cb.Error)) {
                logger?.LogWarning("[OAuth] {Provider} callback returned an error: {Error}", name, cb.Error);
                return null;
            }

            if (string.IsNullOrEmpty(cb.AccessToken)) {
                logger?.LogWarning("[OAuth] {Provider} callback had no tokens and no error", name);
                return null;
            }

            if (logger?.IsEnabled(LogLevel.Information) ?? false)
                logger?.LogInformation("[OAuth] {Provider} callback received", name);
            return cb;
        }
        catch (TimeoutException) {
            logger?.LogWarning("[OAuth] {Provider} callback timed out", name);
            return null;
        }
        catch (OperationCanceledException) {
            if (logger?.IsEnabled(LogLevel.Debug) ?? false)
                logger?.LogDebug("[OAuth] {Provider} login cancelled", name);
            return null;
        }
        finally {
            _pending.TryRemove(new KeyValuePair<string, TaskCompletionSource<OAuthCallback?>>(name, tcs));
        }
    }

    public bool HandleCallback(string url) {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
            !uri.Host.Equals("oauth", StringComparison.OrdinalIgnoreCase))
            return false;

        NameValueCollection query = HttpUtility.ParseQueryString(uri.Query);
        var cb = new OAuthCallback {
            Provider = uri.AbsolutePath.Trim('/'),
            AccessToken = query["access_token"] ?? "",
            RefreshToken = query["refresh_token"] ?? "",
            Code = query["code"] ?? "",
            Error = query["error"] ?? "",
            ExpiresIn = query["expires_in"] ?? "",
            ClientId = query["client_id"] ?? ""
        };

        if (_pending.TryRemove(cb.Provider, out TaskCompletionSource<OAuthCallback?>? tcs)) {
            tcs.TrySetResult(cb);
            return true;
        }

        logger?.LogWarning("[OAuth] Received {Provider} callback with no login in progress", cb.Provider);
        return false;
    }

    public async Task<bool> RefreshAsync(OAuthProvider provider, CancellationToken ct = default) {
        if (GetRefreshToken(provider) == null) return false;

        SemaphoreSlim gate = _refreshLocks.GetOrAdd(provider.Name, _ => new SemaphoreSlim(1, 1));
        try {
            await gate.WaitAsync(ct);
        }
        catch (OperationCanceledException) {
            return false;
        }

        try {
            string? refreshToken = GetRefreshToken(provider);
            if (refreshToken == null) return false;

            if (logger?.IsEnabled(LogLevel.Debug) ?? false)
                logger?.LogDebug("[OAuth] Refreshing {Provider} tokens...", provider.Name);

            using HttpClient client = httpClientFactory.CreateClient(nameof(OAuthService));
            using var body = new FormUrlEncodedContent([
                new KeyValuePair<string, string>("refresh_token", refreshToken)
            ]);

            using HttpResponseMessage response = await client.PostAsync(RefreshUrl(provider.Name), body, ct);
            string json = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode) {
                logger?.LogWarning("[OAuth] {Provider} token refresh failed ({Status}): {Body}", provider.Name,
                    response.StatusCode, json);
                return false;
            }

            var tokens = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);

            string? Field(string key) {
                return tokens != null && tokens.TryGetValue(key, out JsonElement el) &&
                       el.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                    ? el.ToString()
                    : null;
            }

            string? access = Field("access_token");
            if (string.IsNullOrWhiteSpace(access)) {
                logger?.LogWarning("[OAuth] {Provider} token refresh returned no access_token", provider.Name);
                return false;
            }

            StoreTokens(provider, new OAuthCallback {
                Provider = provider.Name,
                AccessToken = access,
                RefreshToken = Field("refresh_token"),
                ExpiresIn = Field("expires_in"),
                ClientId = Field("client_id")
            });
            if (logger?.IsEnabled(LogLevel.Debug) ?? false)
                logger?.LogDebug("[OAuth] {Provider} tokens refreshed successfully", provider.Name);
            return true;
        }
        catch (Exception ex) {
            logger?.LogWarning(ex, "[OAuth] {Provider} token refresh error", provider.Name);
            return false;
        }
        finally {
            gate.Release();
        }
    }

    private string? Read(string? key) {
        if (key == null) return null;
        string? value = secureStorage.GetOrDefault(key, string.Empty);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}