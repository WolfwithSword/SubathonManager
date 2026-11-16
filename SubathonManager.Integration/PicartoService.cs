using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using PicartoEventsLib.Abstractions.Models;
using PicartoEventsLib.Clients;
using PicartoEventsLib.Internal;
using PicartoEventsLib.Options;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Services;

namespace SubathonManager.Integration;

public class PicartoService : IDisposable, IAppService {
    private readonly ILogger<PicartoChatClient>? _chatLogger;

    private readonly Utils.ServiceReconnectState _chatReconnect = new();
    private readonly ILogger<PicartoEventsClient>? _clientLogger;
    private readonly IConfig _config;
    private readonly Utils.ServiceReconnectState _eventsReconnect = new();

    private readonly ILogger? _logger;
    internal PicartoChatClient? _chatClient;
    private bool _chatConnected;

    internal bool _disposed;
    internal PicartoEventsClient? _eventClient;

    private bool _eventsConnected;
    private string? _picartoUsername;

    internal PicartoClientOptions Opts = new();
    public bool Running;

    public PicartoService(ILogger<PicartoService>? logger, IConfig config, ILogger<PicartoEventsClient>? clientLogger,
        ILogger<PicartoChatClient>? chatLogger) {
        _logger = logger;
        _config = config;
        _clientLogger = clientLogger;
        _chatLogger = chatLogger;
    }

    public async Task StartAsync(CancellationToken ct = default) {
        await StartupServiceAsync(ct);
    }

    public Task StopAsync(CancellationToken ct = default) {
        Running = false;
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose() {
        if (_disposed) return;

        _chatReconnect.Dispose();
        _eventsReconnect.Dispose();
        if (_eventClient != null) {
            _eventClient.Disconnected -= OnDisconnect;
            _eventClient.Connected -= OnConnect;
            _eventClient.AlertReceived -= OnAlert;
            _ = Task.Run(_eventClient.DisconnectAsync);
        }

        if (_chatClient != null) {
            _chatClient.Disconnected -= OnDisconnect;
            _chatClient.Connected -= OnConnect;
            _chatClient.MessageReceived -= OnChatMessage;
            _ = Task.Run(_chatClient.DisconnectAsync);
        }

        _disposed = true;
    }

    public async Task StartupServiceAsync(CancellationToken ct = default) {
        Running = false;
        Opts.Channel = string.Empty;

        _picartoUsername = _config.Get("Picarto", "Username", string.Empty)!;
        bool configured = !string.IsNullOrWhiteSpace(_picartoUsername);

        IntegrationEvents.RaiseConnectionUpdate(
            SubathonEventSource.Picarto,
            "Chat",
            false,
            _picartoUsername,
            configured);
        IntegrationEvents.RaiseConnectionUpdate(
            SubathonEventSource.Picarto,
            "Alerts",
            false,
            _picartoUsername,
            configured);

        if (!configured)
            return;

        _logger?.LogInformation("Picarto Service Starting for " + _picartoUsername);
        Opts.Channel = _picartoUsername;

        await Opts.InitAsync();
        _eventClient ??= new PicartoEventsClient(Opts, _clientLogger);
        _chatClient ??= new PicartoChatClient(Opts, _chatLogger);

        _eventClient.Disconnected += OnDisconnect;
        _chatClient.Disconnected += OnDisconnect;
        _eventClient.Connected += OnConnect;
        _chatClient.Connected += OnConnect;

        _chatClient.MessageReceived += OnChatMessage;
        _eventClient.AlertReceived += OnAlert;

        await _eventClient.ConnectAsync();
        await _chatClient.ConnectAsync();
    }

    [ExcludeFromCodeCoverage]
    private void OnAlert(object? sender, PicartoAlert alert) {
        ProcessAlert(alert);
    }

    public static void ProcessAlert(PicartoAlert alert) {
        var ev = new SubathonEvent {
            Source = alert.Username == "SYSTEM" ? SubathonEventSource.Simulated : SubathonEventSource.Picarto,
            EventTimestamp = alert.Timestamp.ToLocalTime(),
            User = alert.Username
        };

        if (alert is PicartoFollow follow) {
            ev.EventType = SubathonEventType.PicartoFollow;
        }
        else if (alert is PicartoSubscription sub) {
            ev.EventType = sub.IsGift ? SubathonEventType.PicartoGiftSub : SubathonEventType.PicartoSub;
            ev.Currency = "sub";
            ev.Value = $"T{sub.Tier}";
            ev.EventTypeMeta = $"T{sub.Tier}";
            ev.Amount = sub.GetMonths() * sub.Quantity;
        }
        else if (alert is PicartoTip tip) {
            ev.Currency = "kudos";
            ev.Value = $"{tip.Amount}";
            ev.EventType = SubathonEventType.PicartoTip;
        }
        else {
            return;
        }

        SubathonEvents.RaiseSubathonEventCreated(ev);
    }

    [ExcludeFromCodeCoverage]
    private void OnChatMessage(object? sender, PicartoChatMessage chatMessage) {
        ProcessChatMessage(chatMessage);
    }

    internal static void ProcessChatMessage(PicartoChatMessage chatMessage) {
        if (string.IsNullOrWhiteSpace(chatMessage.Message) || !chatMessage.Message.StartsWith('!'))
            return;
        CommandService.ChatCommandRequest(SubathonEventSource.Picarto, chatMessage.Message,
            chatMessage.User.Username,
            chatMessage.User.IsBroadcaster,
            false, false,
            chatMessage.Timestamp.ToLocalTime(), chatMessage.MsgId);
    }

    public async Task UpdateChannel() {
        // call for connect/reconnect button
        if (_eventClient == null && _chatClient == null) {
            await StartupServiceAsync();
            return;
        }

        string picartoUsername = _config.Get("Picarto", "Username")!;
        // if (string.Equals(picartoUsername, _picartoUsername))
        //     return;

        _picartoUsername = picartoUsername;

        if (string.IsNullOrWhiteSpace(_picartoUsername)) {
            if (_eventClient != null)
                await _eventClient.DisconnectAsync();
            if (_chatClient != null)
                await _chatClient.DisconnectAsync();
            return;
        }

        if (_eventClient != null)
            await _eventClient.ChangeChannel(_picartoUsername);
        if (_chatClient != null)
            await _chatClient.ChangeChannel(_picartoUsername);
    }

    private void OnConnect(object? sender, PicartoWebSocketConnectedEventArgs args) {
        if (sender is PicartoChatClient) {
            _chatConnected = true;
            _chatReconnect.Reset();
            _chatReconnect.Cts?.Cancel();

            IntegrationEvents.RaiseConnectionUpdate(
                SubathonEventSource.Picarto,
                "Chat",
                _chatConnected,
                _picartoUsername ?? "");
        }
        else if (sender is PicartoEventsClient) {
            _eventsConnected = true;
            _eventsReconnect.Reset();
            _eventsReconnect.Cts?.Cancel();
            IntegrationEvents.RaiseConnectionUpdate(
                SubathonEventSource.Picarto,
                "Alerts",
                _eventsConnected,
                _picartoUsername ?? "");
        }
    }

    [ExcludeFromCodeCoverage]
    private async Task ReconnectWithBackoffAsync(
        PicartoWebSocketConnection client,
        Func<bool> isConnected,
        Utils.ServiceReconnectState state) {
        await state.RunAsync($"Picarto {client.GetType().Name}", SubathonEventSource.Picarto,
            () => !isConnected() && !string.IsNullOrWhiteSpace(_picartoUsername),
            async _ => await client.ReconnectAsync(),
            _logger, "Picarto reconnect failed after maximum retries.", LogLevel.Debug);
    }

    private void OnDisconnect(object? sender, PicartoWebSocketDisconnectedEventArgs args) {
        _logger?.LogWarning(
            "Picarto disconnected: {Sender} {Status} - {Description}",
            sender?.GetType().Name,
            args.CloseStatus,
            args.CloseDescription);

        string description = args.CloseDescription ?? string.Empty;
        bool shouldReconnect =
            args.Exception != null ||
            (description != "Disconnect" && description != "Closing");

        if (sender is PicartoChatClient) {
            _chatConnected = false;

            IntegrationEvents.RaiseConnectionUpdate(
                SubathonEventSource.Picarto,
                "Chat",
                _chatConnected,
                _picartoUsername ?? "");

            if (shouldReconnect && sender is PicartoWebSocketConnection ws)
                _ = Task.Run(() =>
                    ReconnectWithBackoffAsync(
                        ws,
                        () => _chatConnected,
                        _chatReconnect));
        }
        else if (sender is PicartoEventsClient) {
            _eventsConnected = false;
            IntegrationEvents.RaiseConnectionUpdate(
                SubathonEventSource.Picarto,
                "Alerts",
                _eventsConnected,
                _picartoUsername ?? "");

            if (shouldReconnect && sender is PicartoWebSocketConnection ws)
                _ = Task.Run(() =>
                    ReconnectWithBackoffAsync(
                        ws,
                        () => _eventsConnected,
                        _eventsReconnect));
        }
    }
}