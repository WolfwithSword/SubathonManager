using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;
using SubathonManager.Core.Security;
using SubathonManager.Core.Security.Interfaces;

namespace SubathonManager.Integration;

public class PallyService : IAppService, IDisposable
{
    private const string ConfigSection = "PallyGG";
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(60);

    internal string BaseUrl = "wss://events.pally.gg";

    private readonly ILogger? _logger;
    private readonly IConfig _config;
    private readonly ISecureStorage _secureStorage;

    private ClientWebSocket? _socket;
    private CancellationTokenSource? _cts;
    private Task? _runTask;

    public bool Connected { get; private set; }

    private string? ApiKey => _secureStorage.GetOrDefault(StorageKeys.PallyApiKey, string.Empty);
    private string Room => (_config.Get(ConfigSection, "Room", string.Empty) ?? string.Empty).Trim();
    private bool Enabled => _config.GetBool(ConfigSection, "Enabled", false);

    public PallyService(ILogger<PallyService>? logger, IConfig config, ISecureStorage secureStorage)
    {
        _logger = logger;
        _config = config;
        _secureStorage = secureStorage;
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        BroadcastStatus(false);
        if (!Enabled || string.IsNullOrWhiteSpace(ApiKey))
        {
            _logger?.LogInformation("[PallyGG] Not configured or disabled. Integration inactive.");
            return Task.CompletedTask;
        }

        _cts = new CancellationTokenSource();
        _runTask = Task.Run(() => RunAsync(_cts.Token), ct);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        try
        {
            if (_cts != null) await _cts.CancelAsync();
            if (_socket is { State: WebSocketState.Open })
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Stopping", CancellationToken.None);
        }
        catch { /**/ }
        finally
        {
            _socket?.Dispose();
            _socket = null;
            Connected = false;
            BroadcastStatus(false);
        }
    }

    public async Task RestartAsync()
    {
        await StopAsync();
        await StartAsync();
    }

    public bool IsKeyEmpty() => string.IsNullOrWhiteSpace(ApiKey);

    public bool SaveConfig(string apiKey, string room)
    {
        bool updated = _secureStorage.Set(StorageKeys.PallyApiKey, apiKey.Trim());
        updated |= _config.Set(ConfigSection, "Room", room.Trim());
        return updated;
    }

    internal Uri BuildUri()
    {
        string room = Room;
        string channel = string.IsNullOrWhiteSpace(room)
            ? "channel=firehose"
            : $"channel=activity-feed&room={Uri.EscapeDataString(room)}";
        return new Uri($"{BaseUrl}?auth={Uri.EscapeDataString(ApiKey ?? "")}&{channel}");
    }

    private async Task RunAsync(CancellationToken token)
    {
        var reconnect = new Utils.ServiceReconnectState(TimeSpan.FromSeconds(2), 100, TimeSpan.FromMinutes(5));
        while (!token.IsCancellationRequested)
        {
            try
            {
                _socket?.Dispose();
                _socket = new ClientWebSocket();
                await _socket.ConnectAsync(BuildUri(), token);

                Connected = true;
                reconnect.Reset();
                BroadcastStatus(true);
                _logger?.LogInformation("[PallyGG] Connected ({Channel})",
                    string.IsNullOrWhiteSpace(Room) ? "firehose - all pages" : $"room {Room}");

                await ListenAsync(_socket, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning("[PallyGG] Connection error: {Message}", ex.Message);
            }

            if (Connected)
            {
                Connected = false;
                BroadcastStatus(false);
            }

            if (token.IsCancellationRequested) break;

            reconnect.Retries++;
            if (reconnect.Retries >= reconnect.MaxRetries)
            {
                _logger?.LogError("[PallyGG] Max reconnect attempts reached. Please reconnect manually.");
                ErrorMessageEvents.RaiseErrorEvent("ERROR", nameof(SubathonEventSource.PallyGG),
                    "PallyGG.gg reconnect failed after maximum retries.", DateTime.Now);
                break;
            }

            _logger?.LogDebug("[PallyGG] Reconnecting in {Delay}s", reconnect.Backoff.TotalSeconds);
            try { await Task.Delay(reconnect.Backoff, token); }
            catch (OperationCanceledException) { break; }
            reconnect.Backoff = TimeSpan.FromMilliseconds(Math.Min(
                reconnect.Backoff.TotalMilliseconds * 2, reconnect.MaxBackoff.TotalMilliseconds));
        }

        Connected = false;
        BroadcastStatus(false);
    }

    private async Task ListenAsync(ClientWebSocket socket, CancellationToken token)
    {
        using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var pingTask = Task.Run(async () =>
        {
            // keepalive 
            while (!pingCts.Token.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                await Task.Delay(PingInterval, pingCts.Token);
                if (socket.State != WebSocketState.Open) break;
                var ping = Encoding.UTF8.GetBytes("ping");
                await socket.SendAsync(ping, WebSocketMessageType.Text, true, pingCts.Token);
            }
        }, pingCts.Token);

        var buffer = new byte[16 * 1024];
        var messageBuilder = new StringBuilder();
        try
        {
            while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server closed", CancellationToken.None);
                    break;
                }

                messageBuilder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                if (!result.EndOfMessage) continue;

                string message = messageBuilder.ToString();
                messageBuilder.Clear();
                ProcessMessage(message);
            }
        }
        finally
        {
            await pingCts.CancelAsync();
            try { await pingTask; } catch { /**/ }
        }
    }

    public bool ProcessMessage(string message, bool simulated = false)
    {
        if (string.IsNullOrWhiteSpace(message) || message == "pong") return false;
        try
        {
            using var json = JsonDocument.Parse(message);
            var root = json.RootElement;
            if (!root.TryGetProperty("type", out var typeElem)
                || typeElem.GetString() != "campaigntip.notify") return false;
            if (!root.TryGetProperty("payload", out var payload)
                || !payload.TryGetProperty("campaignTip", out var tip)) return false;

            if (!tip.TryGetProperty("grossAmountInCents", out var grossElem)
                || grossElem.ValueKind != JsonValueKind.Number) return false;
            double amount = grossElem.GetInt64() / 100.0;
            if (amount <= 0) return false;

            string user = tip.TryGetProperty("displayName", out var nameElem)
                          && !string.IsNullOrWhiteSpace(nameElem.GetString())
                ? nameElem.GetString()!
                : "Anonymous";

            var subathonEvent = new SubathonEvent
            {
                User = simulated ? "SYSTEM" : user,
                Currency = "USD", // USD only
                Value = amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                Source = simulated ? SubathonEventSource.Simulated : SubathonEventSource.PallyGG,
                EventType = SubathonEventType.PallyGGDonation
            };

            if (payload.TryGetProperty("page", out var page)
                && page.TryGetProperty("slug", out var slugElem))
                subathonEvent.EventTypeMeta = slugElem.GetString();

            if (!simulated && tip.TryGetProperty("id", out var idElem)
                           && !string.IsNullOrWhiteSpace(idElem.GetString()))
                subathonEvent.Id = GuidFromString($"pallygg|{idElem.GetString()}");

            SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
            return true;
        }
        catch (JsonException)
        {
            _logger?.LogDebug("[PallyGG] Ignored non-json message");
            return false;
        }
    }

    internal static Guid GuidFromString(string input)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(input));
        return new Guid(hash);
    }

    public static void SimulateTip(string value = "10.00")
    {
        if (!double.TryParse(value, out var val) || val <= 0) return;
        SubathonEvents.RaiseSubathonEventCreated(new SubathonEvent
        {
            User = "SYSTEM",
            Currency = "USD",
            Value = val.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            Source = SubathonEventSource.Simulated,
            EventType = SubathonEventType.PallyGGDonation
        });
    }

    private static void BroadcastStatus(bool status)
    {
        IntegrationEvents.RaiseConnectionUpdate(new IntegrationConnection
        {
            Source = SubathonEventSource.PallyGG,
            Service = "Socket",
            Name = "PallyGG",
            Status = status
        });
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _socket?.Dispose();
        GC.SuppressFinalize(this);
    }
}
