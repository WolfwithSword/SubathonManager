using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SocketIO.Core;
using SocketIOClient;
using SocketIOClient.Transport;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;
using SubathonManager.Core.Security;
using SubathonManager.Core.Security.Interfaces;
using SocketIOType = SocketIOClient.SocketIO;

namespace SubathonManager.Integration;

public class TreatStreamService(
    ILogger<TreatStreamService>? logger,
    IHttpClientFactory httpClientFactory,
    ISecureStorage secureStorage,
    ITimerService timerService) : IAppService {
    internal readonly string _oAuthUrl = "https://oauth.subathonmanager.app/auth/treatstream/login";

    private readonly Utils.ServiceReconnectState _reconnectState =
        new(TimeSpan.FromSeconds(3), 50, TimeSpan.FromMinutes(5));

    internal readonly string _refreshUrl = "https://oauth.subathonmanager.app/auth/treatstream/refresh";

    internal readonly string _socketTokenUrl = "https://treatstream.com/Oauth2/Authorize/socketToken";
    private bool _connected;
    private bool _hasAuthError;
    private IDisposable? _refreshTimerHandle;

    private SocketIOType? _socket;
    private CancellationTokenSource? _socketCts;

    internal Action<string> OpenBrowser =
        url => Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });

    internal string SocketUrl = "https://nodeapi.treatstream.com/";

    private string? AccessToken => secureStorage.GetOrDefault(StorageKeys.TreatStreamAccessToken, string.Empty);
    private string? RefreshToken => secureStorage.GetOrDefault(StorageKeys.TreatStreamRefreshToken, string.Empty);
    private string? ClientId => secureStorage.GetOrDefault(StorageKeys.TreatStreamClientId, string.Empty);

    private DateTime? TokenExpiry {
        get {
            string? raw = secureStorage.GetOrDefault(StorageKeys.TreatStreamTokenExpiry, string.Empty);
            return DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime dt)
                ? dt
                : null;
        }
    }

    public async Task StartAsync(CancellationToken ct = default) {
        Utils.PendingOAuthCallback = null;

        if (!HasTokens()) {
            logger?.LogInformation("[TreatStream] Not configured. Integration disabled.");
            BroadcastStatus(false);
            return;
        }

        await InitializeAsync(ct);
    }

    public async Task StopAsync(CancellationToken ct = default) {
        _refreshTimerHandle?.Dispose();
        _refreshTimerHandle = null;
        await DisconnectAsync();
        BroadcastStatus(false);
    }

    [ExcludeFromCodeCoverage]
    private async Task InitializeAsync(CancellationToken ct = default) {
        if (CheckExpiry()) {
            bool refreshed = await RefreshAccessTokenAsync(ct);
            if (!refreshed) {
                RevokeTokens();
                await StartOAuthFlowAsync(ct);
                if (!HasTokens()) {
                    BroadcastStatus(false);
                    return;
                }
            }
        }

        RegisterRefreshTimer();
        await ConnectSocketAsync(ct);
    }

    [ExcludeFromCodeCoverage]
    public async Task ConnectAsync(CancellationToken ct = default) {
        await StopAsync(ct);
        await StartOAuthFlowAsync(ct);
        if (!HasTokens()) {
            BroadcastStatus(false);
            return;
        }

        await InitializeAsync(ct);
    }

    internal void StoreExpiry(string? expiresIn) {
        double seconds = double.TryParse(expiresIn, NumberStyles.Any, CultureInfo.InvariantCulture, out double s) &&
                         s > 0
            ? s
            : TimeSpan.FromDays(15).TotalSeconds; // docs say 30, but if it is unknown, be safe with 15
        secureStorage.Set(StorageKeys.TreatStreamTokenExpiry,
            DateTime.UtcNow.AddSeconds(seconds).ToString("O", CultureInfo.InvariantCulture));
    }

    private bool CheckExpiry() {
        if (string.IsNullOrWhiteSpace(AccessToken)) return false;
        DateTime? expires = TokenExpiry;
        if (expires == null) return false;
        return DateTime.UtcNow >= expires.Value.AddHours(-1);
    }

    [ExcludeFromCodeCoverage]
    private void RegisterRefreshTimer() {
        _refreshTimerHandle?.Dispose();
        TimeSpan untilRefresh = (TokenExpiry ?? DateTime.UtcNow.AddDays(30)) - DateTime.UtcNow - TimeSpan.FromHours(1);
        TimeSpan interval = TimeSpan.FromTicks(Math.Clamp(untilRefresh.Ticks,
            TimeSpan.FromMinutes(5).Ticks, TimeSpan.FromHours(24).Ticks));

        _refreshTimerHandle = timerService.Register(
            $"{nameof(TreatStreamService)}.TokenRefresh",
            interval,
            async token => {
                if (!CheckExpiry()) return;
                bool ok = await RefreshAccessTokenAsync(token);
                if (ok) {
                    RegisterRefreshTimer();
                }
                else {
                    logger?.LogWarning("[TreatStream] Scheduled token refresh failed - disconnecting");
                    BroadcastStatus(false);
                    await DisconnectAsync();
                }
            });
    }

    [ExcludeFromCodeCoverage]
    internal async Task<bool> RefreshAccessTokenAsync(CancellationToken ct = default) {
        if (string.IsNullOrWhiteSpace(RefreshToken)) return false;
        logger?.LogDebug("[TreatStream] Refreshing access token...");

        try {
            using HttpClient client = httpClientFactory.CreateClient(nameof(TreatStreamService));
            using var body = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("refresh_token", RefreshToken)
            ]);

            HttpResponseMessage response = await client.PostAsync(_refreshUrl, body, ct);
            if (!response.IsSuccessStatusCode) {
                logger?.LogWarning("[TreatStream] Token refresh failed ({Status})", response.StatusCode);
                return false;
            }

            string json = await response.Content.ReadAsStringAsync(ct);
            var tokens = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            var newAccess = tokens?.GetValueOrDefault("access_token").ToString();
            string? newRefresh = tokens != null && tokens.TryGetValue("refresh_token", out JsonElement r)
                ? r.ToString()
                : RefreshToken;
            string? expiresIn = tokens != null && tokens.TryGetValue("expires_in", out JsonElement e)
                ? e.ToString()
                : null;
            string? clientId = tokens != null && tokens.TryGetValue("client_id", out JsonElement c)
                ? c.ToString()
                : null;

            if (string.IsNullOrWhiteSpace(newAccess)) return false;

            secureStorage.Set(StorageKeys.TreatStreamAccessToken, newAccess);
            secureStorage.Set(StorageKeys.TreatStreamRefreshToken, newRefresh ?? RefreshToken!);
            if (!string.IsNullOrWhiteSpace(clientId))
                secureStorage.Set(StorageKeys.TreatStreamClientId, clientId);
            StoreExpiry(expiresIn);
            logger?.LogDebug("[TreatStream] Tokens refreshed successfully");
            return true;
        }
        catch (Exception ex) {
            logger?.LogWarning(ex, "[TreatStream] Token refresh error");
            return false;
        }
    }

    [ExcludeFromCodeCoverage]
    private async Task StartOAuthFlowAsync(CancellationToken ct = default) {
        RevokeTokens();
        Utils.PendingOAuthCallback = null;
        logger?.LogDebug("[TreatStream] Opening OAuth...");
        OpenBrowser(_oAuthUrl);
        OAuthCallback? cb = await WaitForProtocolCallbackAsync(ct);
        if (cb != null && !string.IsNullOrEmpty(cb.AccessToken) && !string.IsNullOrEmpty(cb.RefreshToken)) {
            secureStorage.Set(StorageKeys.TreatStreamAccessToken, cb.AccessToken);
            secureStorage.Set(StorageKeys.TreatStreamRefreshToken, cb.RefreshToken);
            if (!string.IsNullOrWhiteSpace(cb.ClientId))
                secureStorage.Set(StorageKeys.TreatStreamClientId, cb.ClientId);
            StoreExpiry(cb.ExpiresIn);
            logger?.LogInformation("[TreatStream] OAuth tokens stored");
        }
        else {
            logger?.LogWarning("[TreatStream] OAuth flow did not produce tokens");
        }
    }

    [ExcludeFromCodeCoverage]
    private async Task<OAuthCallback?> WaitForProtocolCallbackAsync(CancellationToken ct = default) {
        DateTime timeout = DateTime.Now.AddMinutes(15);
        while (DateTime.Now < timeout && !ct.IsCancellationRequested) {
            OAuthCallback? cb = Utils.PendingOAuthCallback;
            if (cb?.Provider == "treatstream") {
                Utils.PendingOAuthCallback = null;
                if (!string.IsNullOrEmpty(cb.Error)) {
                    logger?.LogWarning("[TreatStream] OAuth error from callback: {Error}", cb.Error);
                    return null;
                }

                return cb;
            }

            await Task.Delay(250, ct);
        }

        logger?.LogWarning("[TreatStream] OAuth callback timed out");
        return null;
    }

    public bool HasTokens() {
        return secureStorage.Exists(StorageKeys.TreatStreamAccessToken)
               && secureStorage.Exists(StorageKeys.TreatStreamRefreshToken)
               && !string.IsNullOrWhiteSpace(AccessToken)
               && !string.IsNullOrWhiteSpace(RefreshToken);
    }

    [ExcludeFromCodeCoverage]
    public void RevokeTokens() {
        secureStorage.Delete(StorageKeys.TreatStreamAccessToken);
        secureStorage.Delete(StorageKeys.TreatStreamRefreshToken);
        secureStorage.Delete(StorageKeys.TreatStreamTokenExpiry);
        secureStorage.Delete(StorageKeys.TreatStreamClientId);
    }

    [ExcludeFromCodeCoverage]
    internal async Task<string?> FetchSocketTokenAsync(CancellationToken ct = default) {
        if (string.IsNullOrWhiteSpace(ClientId)) {
            logger?.LogWarning("[TreatStream] No stored client_id - reconnect via OAuth to fetch it");
            return null;
        }

        try {
            using HttpClient client = httpClientFactory.CreateClient(nameof(TreatStreamService));
            using var body = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("client_id", ClientId!),
                new KeyValuePair<string, string>("access_token", AccessToken ?? "")
            ]);
            HttpResponseMessage resp = await client.PostAsync(_socketTokenUrl, body, ct);
            if (!resp.IsSuccessStatusCode) {
                logger?.LogWarning("[TreatStream] FetchSocketToken HTTP {Status}", resp.StatusCode);
                return null;
            }

            string json = await resp.Content.ReadAsStringAsync(ct);
            using JsonDocument doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("socket_token", out JsonElement tok)
                ? tok.ToString()
                : null;
        }
        catch (Exception ex) {
            logger?.LogWarning(ex, "[TreatStream] Failed to fetch socket token");
            return null;
        }
    }

    [ExcludeFromCodeCoverage]
    private async Task ConnectSocketAsync(CancellationToken ct = default) {
        string? socketToken = await FetchSocketTokenAsync(ct);
        if (string.IsNullOrWhiteSpace(socketToken)) {
            logger?.LogWarning("[TreatStream] Could not retrieve socket token");
            BroadcastStatus(false);
            _ = Task.Run(() => ReconnectWithBackoffAsync(_socketCts?.Token ?? ct), ct);
            return;
        }

        await DisconnectAsync();

        _socketCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _hasAuthError = false;
        _connected = false;

        _socket = new SocketIOType(SocketUrl, new SocketIOOptions {
            Query = new List<KeyValuePair<string, string>> {
                new("token", socketToken)
            },
            EIO = EngineIO.V3,
            Transport = TransportProtocol.WebSocket
        });

        _socket.OnConnected += OnSocketConnected;
        _socket.OnDisconnected += OnSocketDisconnected;
        _socket.On("realTimeTreat", OnRealTimeTreat);

        try {
            await _socket.ConnectAsync(ct);
        }
        catch (Exception ex) {
            logger?.LogWarning(ex, "[TreatStream] Initial socket connection failed");
            _ = Task.Run(() => ReconnectWithBackoffAsync(_socketCts.Token), ct);
        }
    }

    [ExcludeFromCodeCoverage]
    private void OnSocketConnected(object? sender, EventArgs e) {
        logger?.LogInformation("[TreatStream] Socket connected");
        _connected = true;
        _hasAuthError = false;
        _reconnectState.Reset();
        _reconnectState.Cts?.Cancel();
        BroadcastStatus(true);
    }

    [ExcludeFromCodeCoverage]
    private void OnSocketDisconnected(object? sender, string reason) {
        logger?.LogWarning("[TreatStream] Socket disconnected: {Reason}", reason);
        _connected = false;
        BroadcastStatus(false);

        if (_hasAuthError || _socketCts?.IsCancellationRequested == true) return;
        _ = Task.Run(() => ReconnectWithBackoffAsync(_socketCts?.Token ?? CancellationToken.None));
    }

    [ExcludeFromCodeCoverage]
    private async Task ReconnectWithBackoffAsync(CancellationToken ct = default) {
        if (!await _reconnectState.Lock.WaitAsync(0, ct)) return;

        try {
            _reconnectState.Cts?.Cancel();
            _reconnectState.Cts = new CancellationTokenSource();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _reconnectState.Cts.Token);
            CancellationToken token = linked.Token;

            while (!token.IsCancellationRequested && !_connected && !_hasAuthError) {
                if (_reconnectState.Retries >= _reconnectState.MaxRetries) {
                    var msg = "TreatStream disconnected and max reconnect retries were reached.";
                    ErrorMessageEvents.RaiseErrorEvent(
                        "ERROR", nameof(SubathonEventSource.TreatStream), msg, DateTime.Now.ToLocalTime());
                    logger?.LogError(msg);
                    return;
                }

                _reconnectState.Retries++;
                TimeSpan delay = _reconnectState.Backoff;
                logger?.LogWarning("[TreatStream] Reconnect attempt {Attempt}/{Max} in {Delay}s",
                    _reconnectState.Retries, _reconnectState.MaxRetries, delay.TotalSeconds);

                try {
                    await Task.Delay(delay, token);

                    if (CheckExpiry()) await RefreshAccessTokenAsync(token);
                    string? socketToken = await FetchSocketTokenAsync(token);
                    if (!string.IsNullOrWhiteSpace(socketToken) && !_connected && _socket != null) {
                        await DisconnectSocketOnlyAsync();
                        _socket = new SocketIOType(SocketUrl, new SocketIOOptions {
                            Query = new List<KeyValuePair<string, string>> { new("token", socketToken) },
                            EIO = EngineIO.V3,
                            Transport = TransportProtocol.WebSocket
                        });
                        _socket.OnConnected += OnSocketConnected;
                        _socket.OnDisconnected += OnSocketDisconnected;
                        _socket.On("realTimeTreat", OnRealTimeTreat);
                        await _socket.ConnectAsync(token);
                    }
                }
                catch (OperationCanceledException) {
                    return;
                }
                catch (Exception ex) {
                    logger?.LogWarning(ex, "[TreatStream] Reconnect attempt failed");
                }

                _reconnectState.Backoff = TimeSpan.FromMilliseconds(
                    Math.Min(
                        _reconnectState.Backoff.TotalMilliseconds * 2,
                        _reconnectState.MaxBackoff.TotalMilliseconds));
            }
        }
        finally {
            _reconnectState.Lock.Release();
        }
    }

    [ExcludeFromCodeCoverage]
    private async Task DisconnectSocketOnlyAsync() {
        SocketIOType? s = _socket;
        _socket = null;
        if (s == null) return;
        try {
            s.OnConnected -= OnSocketConnected;
            s.OnDisconnected -= OnSocketDisconnected;
            s.Off("realTimeTreat");
            await s.DisconnectAsync();
        }
        catch (Exception ex) {
            logger?.LogWarning(ex, "[TreatStream] Disconnect error");
        }
        finally {
            s.Dispose();
        }
    }

    private async Task DisconnectAsync() {
        _socketCts?.Cancel();
        _connected = false;
        _reconnectState.Cts?.Cancel();
        await DisconnectSocketOnlyAsync();
    }

    [ExcludeFromCodeCoverage]
    private void OnRealTimeTreat(SocketIOResponse response) {
        try {
            ProcessTreatJson(response.GetValue<JsonElement>().GetRawText());
        }
        catch (Exception ex) {
            logger?.LogWarning(ex, "[TreatStream] Failed to parse realTimeTreat");
        }
    }

    internal bool ProcessTreatJson(string json, bool simulated = false) {
        try {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            string? title = root.TryGetProperty("title", out JsonElement titleEl) ? titleEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(title)) return false;

            string sender = root.TryGetProperty("sender", out JsonElement senderEl)
                            && !string.IsNullOrWhiteSpace(senderEl.GetString())
                ? senderEl.GetString()!
                : "TreatStream";

            string dateCreated = root.TryGetProperty("date_created", out JsonElement dateEl)
                ? dateEl.GetString() ?? ""
                : "";

            bool isSystem = simulated || sender == "SYSTEM";

            var subathonEvent = new SubathonEvent {
                Id = Utils.CreateGuidFromUniqueString($"treatstream|{title}|{dateCreated}"),
                Source = isSystem ? SubathonEventSource.Simulated : SubathonEventSource.TreatStream,
                EventType = SubathonEventType.TreatStreamOrder,
                User = isSystem ? "SYSTEM" : sender,
                Currency = "item",
                Amount = 1,
                Value = title,
                TertiaryValue = title,
                EventTimestamp = DateTimeOffset.TryParse(dateCreated, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out DateTimeOffset ts)
                    ? ts.LocalDateTime
                    : DateTime.Now.ToLocalTime()
            };

            SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
            return true;
        }
        catch (JsonException ex) {
            logger?.LogWarning(ex, "[TreatStream] Invalid treat JSON");
            return false;
        }
    }

    public static void SimulateTreat(string title = "Fancy Treat") {
        if (string.IsNullOrWhiteSpace(title)) title = "Fancy Treat";
        SubathonEvents.RaiseSubathonEventCreated(new SubathonEvent {
            Source = SubathonEventSource.Simulated,
            EventType = SubathonEventType.TreatStreamOrder,
            User = "SYSTEM",
            Currency = "item",
            Amount = 1,
            Value = title,
            TertiaryValue = title,
            EventTimestamp = DateTime.Now.ToLocalTime()
        });
    }

    private void BroadcastStatus(bool connected) {
        IntegrationEvents.RaiseConnectionUpdate(new IntegrationConnection {
            Source = SubathonEventSource.TreatStream,
            Service = "Socket",
            Name = connected ? "TreatStream" : "",
            Status = connected,
            Configured = HasTokens()
        });
    }
}