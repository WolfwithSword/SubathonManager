using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using SocketIOClient;
using SocketIO.Core;
using SocketIOType = SocketIOClient.SocketIO;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;
using SubathonManager.Core.Security;
using SubathonManager.Core.Security.Interfaces;

namespace SubathonManager.Integration;

public class TipeeeStreamService(ILogger<TipeeeStreamService>? logger, IHttpClientFactory httpClientFactory,
    ISecureStorage secureStorage, ITimerService timerService): IAppService
{
    private const string ApiBase = "https://api.tipeeestream.com";

    internal readonly string _oAuthUrl = "https://oauth.subathonmanager.app/auth/tipeeestream/login";
    internal readonly string _refreshUrl = "https://oauth.subathonmanager.app/auth/tipeeestream/refresh";

    internal Action<string> OpenBrowser =
        url => Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });

    private string? AccessToken => secureStorage.GetOrDefault(StorageKeys.TipeeeStreamAccessToken, string.Empty);
    private string? RefreshToken => secureStorage.GetOrDefault(StorageKeys.TipeeeStreamRefreshToken, string.Empty);
    private string? ApiKey => secureStorage.GetOrDefault(StorageKeys.TipeeeStreamApiKey, string.Empty);
    private string? _username = string.Empty;

    private SocketIOType? _socket;
    private bool _connected;
    private bool _hasAuthError;
    private bool _apiKeyRetried;
    private CancellationTokenSource? _socketCts;
    private IDisposable? _refreshTimerHandle;

    private readonly Utils.ServiceReconnectState _reconnectState =
        new(TimeSpan.FromSeconds(3), maxRetries: 50, maxBackoff: TimeSpan.FromMinutes(5));

    public async Task StartAsync(CancellationToken ct = default)
    {
        Utils.PendingOAuthCallback = null;

        if (!HasTokens())
        {
            logger?.LogInformation("[TipeeeStream] Not configured. Integration disabled.");
            BroadcastStatus(false);
            return;
        }

        await InitializeAsync(ct);
    }

    [ExcludeFromCodeCoverage]
    private async Task InitializeAsync(CancellationToken ct = default)
    {
        if (CheckExpiry())
        {
            var refreshed = await RefreshAccessTokenAsync(ct);
            if (!refreshed)
            {
                RevokeTokens();
                await StartOAuthFlowAsync(ct);
                if (!HasTokens())
                {
                    BroadcastStatus(false);
                    return;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(_username))
        {
            var username = await FetchUsernameAsync(ct);
            if (string.IsNullOrWhiteSpace(username))
            {
                logger?.LogWarning("[TipeeeStream] Could not fetch username");
                BroadcastStatus(false);
                return;
            }
            _username = username;
        }

        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            var apiKey = await FetchApiKeyAsync(ct);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                logger?.LogWarning("[TipeeeStream] Could not fetch API key");
                BroadcastStatus(false);
                return;
            }
            secureStorage.Set(StorageKeys.TipeeeStreamApiKey, apiKey);
        }

        _apiKeyRetried = false;

        // refresh check every 30 minutes
        _refreshTimerHandle?.Dispose();
        _refreshTimerHandle = timerService.Register(
            $"{nameof(TipeeeStreamService)}.TokenRefresh",
            TimeSpan.FromMinutes(30),
            async (token) =>
            {
                if (!CheckExpiry()) return;
                var ok = await RefreshAccessTokenAsync(token);
                if (!ok)
                {
                    logger?.LogWarning("[TipeeeStream] Periodic token refresh failed - disconnecting");
                    BroadcastStatus(false);
                    await DisconnectAsync();
                }
            });

        await ConnectSocketAsync(ct);
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        _refreshTimerHandle?.Dispose();
        _refreshTimerHandle = null;
        await DisconnectAsync();
        BroadcastStatus(false);
    }

    [ExcludeFromCodeCoverage]
    private async Task ConnectSocketAsync(CancellationToken ct = default)
    {
        string? socketUrl = await GetSocketUrlAsync(ct);
        if (string.IsNullOrWhiteSpace(socketUrl))
        {
            logger?.LogWarning("[TipeeeStream] Could not retrieve socket URL");
            BroadcastStatus(false);
            return;
        }

        await DisconnectAsync();

        _socketCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _reconnectState.Reset();
        _hasAuthError = false;
        _connected = false;

        _socket = new SocketIOType(socketUrl, new SocketIOOptions
        {
            Query = new List<KeyValuePair<string, string>>
            {
                new("access_token", ApiKey!)
            },
            EIO = EngineIO.V3,
            Transport = SocketIOClient.Transport.TransportProtocol.WebSocket
        });

        _socket.OnConnected += OnSocketConnected;
        _socket.OnDisconnected += OnSocketDisconnected;
        _socket.On("new-event", OnNewEvent);

        try
        {
            await _socket.ConnectAsync(ct);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "[TipeeeStream] Initial socket connection failed");
            _ = Task.Run(() => ReconnectWithBackoffAsync(_socketCts.Token), ct);
        }
    }

    [ExcludeFromCodeCoverage]
    private async void OnSocketConnected(object? sender, EventArgs e)
    {
        logger?.LogInformation("[TipeeeStream] Socket connected");
        _connected = true;
        _hasAuthError = false;
        _reconnectState.Reset();
        _reconnectState.Cts?.Cancel();

        try
        {
            await _socket!.EmitAsync("join-room", ApiKey, _username);
            logger?.LogDebug("[TipeeeStream] Joined room for {User}", _username);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "[TipeeeStream] Failed to emit join-room");
        }

        BroadcastStatus(true);
    }

    [ExcludeFromCodeCoverage]
    private void OnSocketDisconnected(object? sender, string reason)
    {
        logger?.LogWarning("[TipeeeStream] Socket disconnected: {Reason}", reason);
        _connected = false;
        BroadcastStatus(false);

        if (_hasAuthError || _socketCts?.IsCancellationRequested == true) return;

        if (string.Equals(reason, "io server disconnect", StringComparison.OrdinalIgnoreCase))
        {
            _ = Task.Run(() => HandleApiKeyFailureAsync(_socketCts?.Token ?? CancellationToken.None));
            return;
        }

        _ = Task.Run(() => ReconnectWithBackoffAsync(_socketCts?.Token ?? CancellationToken.None));
    }

    [ExcludeFromCodeCoverage]
    private void OnNewEvent(SocketIOResponse response)
    {
        try
        {
            HandleParsedEvent(response.GetValue<TipeeeWrapper>(0)?.Event);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "[TipeeeStream] Failed to parse new-event");
        }
    }

    internal void ProcessEventJson(string json)
    {
        try
        {
            var wrappers = JsonSerializer.Deserialize<List<TipeeeWrapper>>(json);
            HandleParsedEvent(wrappers?.Count > 0 ? wrappers[0].Event : null);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "[TipeeeStream] Failed to parse event JSON");
        }
    }

    private void HandleParsedEvent(TipeeeEvent? ev)
    {
        if (ev?.Type is not ("donation" or "tipeee")) return;

        var p = ev.Parameters;
        if (p == null) return;

        var refStr = ev.Ref.ValueKind == JsonValueKind.String
            ? ev.Ref.GetString() ?? ""
            : ev.Ref.ValueKind == JsonValueKind.Number
                ? ev.Ref.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "";
        if (refStr.StartsWith("TWITCH_", StringComparison.OrdinalIgnoreCase) ||
            refStr.StartsWith("YOUTUBE_", StringComparison.OrdinalIgnoreCase))
            return;
        
        // docs mention this but in practice it doesn't so, :/
        var isSimulation = refStr.StartsWith("simulation", StringComparison.OrdinalIgnoreCase);

        var username = isSimulation
            ? "SYSTEM"
            : !string.IsNullOrWhiteSpace(p.Username)
                ? p.Username
                : ev.User?.Providers
                      ?.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.Username))
                      ?.Username
                  ?? "TipeeeStream";

        var amount = p.Amount.ValueKind == JsonValueKind.String
            ? double.TryParse(p.Amount.GetString(), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : 0.0
            : p.Amount.ValueKind == JsonValueKind.Number
                ? p.Amount.GetDouble()
                : 0.0;

        var subathonEvent = new SubathonEvent
        {
            Id = Utils.CreateGuidFromUniqueString(refStr),
            Source = SubathonEventSource.TipeeeStream,
            EventType = SubathonEventType.TipeeeStreamDonation,
            User = username,
            Value = amount.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
            Currency = !string.IsNullOrWhiteSpace(p.Currency) ? p.Currency : "EUR",
            EventTimestamp = DateTimeOffset.TryParse(ev.CreatedAt, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var ts)
                ? ts.LocalDateTime
                : DateTime.Now.ToLocalTime()
        };

        SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
    }

    [ExcludeFromCodeCoverage]
    private async Task ReconnectWithBackoffAsync(CancellationToken ct = default)
    {
        if (!await _reconnectState.Lock.WaitAsync(0, ct)) return;

        try
        {
            _reconnectState.Cts?.Cancel();
            _reconnectState.Cts = new CancellationTokenSource();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _reconnectState.Cts.Token);
            var token = linked.Token;

            while (!token.IsCancellationRequested && !_connected && !_hasAuthError)
            {
                if (_reconnectState.Retries >= _reconnectState.MaxRetries)
                {
                    var msg = "TipeeeStream disconnected and max reconnect retries were reached.";
                    ErrorMessageEvents.RaiseErrorEvent(
                        "ERROR", nameof(SubathonEventSource.TipeeeStream), msg, DateTime.Now.ToLocalTime());
                    logger?.LogError(msg);
                    return;
                }

                _reconnectState.Retries++;
                var delay = _reconnectState.Backoff;

                logger?.LogWarning("[TipeeeStream] Reconnect attempt {Attempt}/{Max} in {Delay}s",
                    _reconnectState.Retries, _reconnectState.MaxRetries, delay.TotalSeconds);

                try
                {
                    await Task.Delay(delay, token);
                    if (!_connected && _socket != null)
                        await _socket.ConnectAsync(token);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "[TipeeeStream] Reconnect attempt failed");
                }

                _reconnectState.Backoff = TimeSpan.FromMilliseconds(
                    Math.Min(
                        _reconnectState.Backoff.TotalMilliseconds * 2,
                        _reconnectState.MaxBackoff.TotalMilliseconds));
            }
        }
        finally
        {
            _reconnectState.Lock.Release();
        }
    }

    private async Task DisconnectAsync()
    {
        _socketCts?.Cancel();
        _connected = false;
        _reconnectState.Cts?.Cancel();

        var s = _socket;
        _socket = null;
        if (s == null) return;

        try
        {
            s.OnConnected -= OnSocketConnected;
            s.OnDisconnected -= OnSocketDisconnected;
            s.Off("new-event");
            await s.DisconnectAsync();
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "[TipeeeStream] Disconnect error");
        }
        finally
        {
            s.Dispose();
        }
    }

    [ExcludeFromCodeCoverage]
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await StopAsync(ct);
        await StartOAuthFlowAsync(ct);
        if (!HasTokens())
        {
            BroadcastStatus(false);
            return;
        }
        await InitializeAsync(ct);
    }

    public static void SimulateDonation(string amount, string currency)
    {
        if (!double.TryParse(amount, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var amt)) return;
        var ev = new SubathonEvent
        {
            Source = SubathonEventSource.Simulated,
            EventType = SubathonEventType.TipeeeStreamDonation,
            User = "SYSTEM",
            Value = amt.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
            Currency = !string.IsNullOrWhiteSpace(currency) ? currency : "EUR",
            EventTimestamp = DateTime.Now.ToLocalTime()
        };
        SubathonEvents.RaiseSubathonEventCreated(ev);
    }

    [ExcludeFromCodeCoverage]
    private bool CheckExpiry()
    {
        if (string.IsNullOrWhiteSpace(AccessToken)) return false;
        var expires = Utils.GetAccessTokenExpiry(AccessToken);
        if (expires == null) return false;
        return DateTime.UtcNow >= expires.Value.AddSeconds(-60);
    }

    [ExcludeFromCodeCoverage]
    private async Task<bool> RefreshAccessTokenAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(RefreshToken)) return false;
        logger?.LogDebug("[TipeeeStream] Refreshing access token...");

        try
        {
            using var client = httpClientFactory.CreateClient(nameof(TipeeeStreamService));
            using var body = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("refresh_token", RefreshToken)
            ]);

            var response = await client.PostAsync(_refreshUrl, body, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger?.LogWarning("[TipeeeStream] Token refresh failed ({Status})", response.StatusCode);
                return false;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var tokens = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            var newAccess = tokens?.GetValueOrDefault("access_token").GetString();
            var newRefresh = tokens?.GetValueOrDefault("refresh_token").GetString() ?? RefreshToken;

            if (string.IsNullOrWhiteSpace(newAccess)) return false;

            secureStorage.Set(StorageKeys.TipeeeStreamAccessToken, newAccess);
            secureStorage.Set(StorageKeys.TipeeeStreamRefreshToken, newRefresh);
            logger?.LogDebug("[TipeeeStream] Tokens refreshed successfully");
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "[TipeeeStream] Token refresh error");
            return false;
        }
    }

    private async Task StartOAuthFlowAsync(CancellationToken ct = default)
    {
        RevokeTokens();
        Utils.PendingOAuthCallback = null;
        logger?.LogDebug("[TipeeeStream] Opening OAuth...");
        OpenBrowser(_oAuthUrl);
        var (newAccess, newRefresh) = await WaitForProtocolCallbackAsync(ct);
        if (!string.IsNullOrEmpty(newAccess) && !string.IsNullOrEmpty(newRefresh))
        {
            secureStorage.Set(StorageKeys.TipeeeStreamAccessToken, newAccess);
            secureStorage.Set(StorageKeys.TipeeeStreamRefreshToken, newRefresh);
            logger?.LogInformation("[TipeeeStream] OAuth tokens stored");
        }
        else
        {
            logger?.LogWarning("[TipeeeStream] OAuth flow did not produce tokens");
        }
    }

    private async Task<(string?, string?)> WaitForProtocolCallbackAsync(CancellationToken ct = default)
    {
        var timeout = DateTime.Now.AddMinutes(15);
        while (DateTime.Now < timeout && !ct.IsCancellationRequested)
        {
            var cb = Utils.PendingOAuthCallback;
            if (cb?.Provider == "tipeeestream")
            {
                Utils.PendingOAuthCallback = null;
                if (!string.IsNullOrEmpty(cb.Error))
                {
                    logger?.LogWarning("[TipeeeStream] OAuth error from callback: {Error}", cb.Error);
                    return (null, null);
                }
                if (!string.IsNullOrEmpty(cb.AccessToken) && !string.IsNullOrEmpty(cb.RefreshToken))
                {
                    logger?.LogInformation("[TipeeeStream] OAuth callback received");
                    return (cb.AccessToken, cb.RefreshToken);
                }
                logger?.LogWarning("[TipeeeStream] OAuth callback had no tokens and no error");
                return (null, null);
            }
            await Task.Delay(250, ct);
        }
        logger?.LogWarning("[TipeeeStream] OAuth callback timed out");
        return (null, null);
    }

    private bool HasTokens() =>
        secureStorage.Exists(StorageKeys.TipeeeStreamAccessToken)
        && secureStorage.Exists(StorageKeys.TipeeeStreamRefreshToken)
        && !string.IsNullOrWhiteSpace(AccessToken)
        && !string.IsNullOrWhiteSpace(RefreshToken);

    public void RevokeTokens()
    {
        secureStorage.Delete(StorageKeys.TipeeeStreamAccessToken);
        secureStorage.Delete(StorageKeys.TipeeeStreamRefreshToken);
        secureStorage.Delete(StorageKeys.TipeeeStreamApiKey);
    }

    [ExcludeFromCodeCoverage]
    private async Task<string?> FetchUsernameAsync(CancellationToken ct = default)
    {
        try
        {
            using var client = httpClientFactory.CreateClient(nameof(TipeeeStreamService));
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", AccessToken);
            var resp = await client.GetAsync($"{ApiBase}/v1.0/me", ct);
            if (!resp.IsSuccessStatusCode)
            {
                logger?.LogWarning("[TipeeeStream] FetchUsername HTTP {Status}", resp.StatusCode);
                return null;
            }
            var json = await resp.Content.ReadAsStringAsync(ct);
            logger?.LogDebug("[TipeeeStream] FetchUsername response: {Body}", json);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("datas", out var datas)
                && datas.TryGetProperty("username", out var un))
                return un.GetString();
            if (doc.RootElement.TryGetProperty("username", out var rootUn))
                return rootUn.GetString();
            return null;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "[TipeeeStream] Failed to fetch username");
            return null;
        }
    }

    [ExcludeFromCodeCoverage]
    private async Task<string?> FetchApiKeyAsync(CancellationToken ct = default)
    {
        try
        {
            using var client = httpClientFactory.CreateClient(nameof(TipeeeStreamService));
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", AccessToken);
            var resp = await client.GetAsync($"{ApiBase}/v1.0/me/api", ct);
            if (!resp.IsSuccessStatusCode)
            {
                logger?.LogWarning("[TipeeeStream] FetchApiKey HTTP {Status}", resp.StatusCode);
                return null;
            }
            var json = await resp.Content.ReadAsStringAsync(ct);
            logger?.LogDebug("[TipeeeStream] FetchApiKey response: {Body}", json);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("datas", out var datas)
                && datas.TryGetProperty("apiKey", out var key))
                return key.GetString();
            if (doc.RootElement.TryGetProperty("apiKey", out var rootKey))
                return rootKey.GetString();
            return null;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "[TipeeeStream] Failed to fetch API key");
            return null;
        }
    }

    [ExcludeFromCodeCoverage]
    private async Task<string?> GetSocketUrlAsync(CancellationToken ct = default)
    {
        try
        {
            using var client = httpClientFactory.CreateClient(nameof(TipeeeStreamService));
            var resp = await client.GetAsync($"{ApiBase}/v2.0/site/socket", ct);
            if (!resp.IsSuccessStatusCode)
            {
                logger?.LogWarning("[TipeeeStream] GetSocketUrl HTTP {Status}", resp.StatusCode);
                return null;
            }
            var json = await resp.Content.ReadAsStringAsync(ct);
            logger?.LogDebug("[TipeeeStream] GetSocketUrl response: {Body}", json);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var dataEl = root.TryGetProperty("datas", out var d) ? d : root;
            {
                var host = dataEl.TryGetProperty("host", out var h) ? h.GetString() : null;
                var port = dataEl.TryGetProperty("port", out var p) ? p.GetString() : null;
                if (!string.IsNullOrWhiteSpace(host))
                {
                    host = host.TrimEnd('/', ':');
                    return string.IsNullOrWhiteSpace(port) ? host : $"{host}:{port}";
                }
            }
            return null;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "[TipeeeStream] Failed to get socket URL");
            return null;
        }
    }

    [ExcludeFromCodeCoverage]
    private async Task HandleApiKeyFailureAsync(CancellationToken ct = default)
    {
        if (_apiKeyRetried)
        {
            logger?.LogError("[TipeeeStream] API key retry already attempted - giving up");
            _hasAuthError = true;
            BroadcastStatus(false);
            ErrorMessageEvents.RaiseErrorEvent(
                "ERROR", nameof(SubathonEventSource.TipeeeStream),
                "TipeeeStream API key is invalid and could not be refreshed.", DateTime.Now.ToLocalTime());
            return;
        }

        _apiKeyRetried = true;
        logger?.LogWarning("[TipeeeStream] API key may be stale - re-fetching...");
        secureStorage.Delete(StorageKeys.TipeeeStreamApiKey);

        var newKey = await FetchApiKeyAsync(ct);
        if (string.IsNullOrWhiteSpace(newKey))
        {
            logger?.LogWarning("[TipeeeStream] Re-fetch of API key failed");
            _hasAuthError = true;
            BroadcastStatus(false);
            return;
        }

        secureStorage.Set(StorageKeys.TipeeeStreamApiKey, newKey);
        logger?.LogInformation("[TipeeeStream] API key refreshed - reconnecting socket");
        await ConnectSocketAsync(ct);
    }

    private void BroadcastStatus(bool connected)
    {
        IntegrationEvents.RaiseConnectionUpdate(new IntegrationConnection
        {
            Source = SubathonEventSource.TipeeeStream,
            Service = nameof(SubathonEventSource.TipeeeStream),
            Name = connected ? _username ?? "User" : "",
            Status = connected
        });
    }


    private class TipeeeWrapper
    {
        [JsonPropertyName("event")]
        public TipeeeEvent? Event { get; set; }
    }

    private class TipeeeEvent
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        // "ref" can be a string or a float depending on the event source
        [JsonPropertyName("ref")]
        public JsonElement Ref { get; set; }

        // why is it diff between v1/v2 events...
        [JsonPropertyName("created_at")]
        public string? CreatedAt { get; set; }

        [JsonPropertyName("parameters")]
        public TipeeeEventParameters? Parameters { get; set; }

        [JsonPropertyName("user")]
        public TipeeeEventUser? User { get; set; }
    }

    private class TipeeeEventParameters
    {
        [JsonPropertyName("username")]
        public string? Username { get; set; }

        // "amount" can be a string or number depending on the event source
        [JsonPropertyName("amount")]
        public JsonElement Amount { get; set; }

        [JsonPropertyName("currency")]
        public string? Currency { get; set; }

    }

    private class TipeeeEventUser
    {
        [JsonPropertyName("username")]
        public string? Username { get; set; }

        [JsonPropertyName("providers")]
        public List<TipeeeEventProvider>? Providers { get; set; }
    }

    private class TipeeeEventProvider
    {
        [JsonPropertyName("code")]
        public string? Code { get; set; }

        [JsonPropertyName("username")]
        public string? Username { get; set; }
    }
}
