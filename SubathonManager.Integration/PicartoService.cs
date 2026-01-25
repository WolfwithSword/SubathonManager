using PicartoEventsLib.Clients;
using PicartoEventsLib.Internal;
using Microsoft.Extensions.Logging;
using PicartoEventsLib.Options;
using PicartoEventsLib.Abstractions.Models;
using SubathonManager.Core.Models;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Services;
using System.Diagnostics.CodeAnalysis;
namespace SubathonManager.Integration;

public class PicartoService : IDisposable
{
    internal PicartoEventsClient? _eventClient;
    internal PicartoChatClient? _chatClient;
    
    internal bool _disposed = false;
    private string? _picartoUsername;
    public bool Running;

    private readonly Utils.ServiceReconnectState _chatReconnect = new();
    private readonly Utils.ServiceReconnectState _eventsReconnect = new();
    
    private bool _eventsConnected;
    private bool _chatConnected;
    
    private readonly ILogger? _logger;
    private readonly ILogger<PicartoEventsClient>? _clientLogger;
    private readonly ILogger<PicartoChatClient>? _chatLogger;
    private readonly IConfig _config;
    
    internal PicartoClientOptions Opts = new();
    
    public PicartoService(ILogger<PicartoService>? logger, IConfig config, ILogger<PicartoEventsClient>? clientLogger,
        ILogger<PicartoChatClient>? chatLogger)
    {
        _logger = logger;
        _config = config;
        _clientLogger = clientLogger;
        _chatLogger = chatLogger;
    }
    
    public async Task<bool> StartAsync()
    {
        Running = false;
        Opts.Channel = string.Empty;
        
        _picartoUsername = _config.Get("Picarto", "Username", string.Empty)!;

        if (string.IsNullOrWhiteSpace(_picartoUsername))
            return Running;
        
        IntegrationEvents.RaiseConnectionUpdate(false, SubathonEventSource.Picarto, _picartoUsername, "Chat");
        IntegrationEvents.RaiseConnectionUpdate(false, SubathonEventSource.Picarto, _picartoUsername, "Alerts");
        
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
        return true;
    }

    [ExcludeFromCodeCoverage]
    private void OnAlert(object? sender, PicartoAlert alert)
    {
        ProcessAlert(alert);
    }

    public static void ProcessAlert(PicartoAlert alert)
    {
        SubathonEvent ev = new SubathonEvent();
        ev.Source = alert.Username == "SYSTEM" ? SubathonEventSource.Simulated : SubathonEventSource.Picarto;
        ev.EventTimestamp = alert.Timestamp.ToLocalTime();
        ev.User = alert.Username;
        
        if (alert is PicartoFollow follow)
        {
            ev.EventType = SubathonEventType.PicartoFollow;
        }
        else if (alert is PicartoSubscription sub)
        {
            ev.EventType = sub.IsGift ? SubathonEventType.PicartoGiftSub : SubathonEventType.PicartoSub;
            ev.Currency = "sub";
            ev.Value = $"T{sub.Tier}";
            ev.Amount = sub.GetMonths() * sub.Quantity;
        }
        else if (alert is PicartoTip tip)
        {
            ev.Currency = "kudos";
            ev.Value = $"{tip.Amount}";
            ev.EventType = SubathonEventType.PicartoTip;
        }
        else
            return;
        SubathonEvents.RaiseSubathonEventCreated(ev);
    }

    [ExcludeFromCodeCoverage]
    private void OnChatMessage(object? sender, PicartoChatMessage chatMessage)
    {
        ProcessChatMessage(chatMessage);
    }

    internal static void ProcessChatMessage(PicartoChatMessage chatMessage)
    {
        if (string.IsNullOrWhiteSpace(chatMessage.Message) || !chatMessage.Message.StartsWith('!'))
            return;
        CommandService.ChatCommandRequest(SubathonEventSource.Picarto, chatMessage.Message,
            chatMessage.User.Username,
            chatMessage.User.IsBroadcaster,
            false, false, 
            chatMessage.Timestamp.ToLocalTime(), chatMessage.MsgId);
    }
    
    public async Task UpdateChannel()
    {
        // call for connect/reconnect button
        if (_eventClient == null && _chatClient == null)
        {
            await StartAsync();
            return;
        }
        
        var picartoUsername = _config.Get("Picarto", "Username")!;
        // if (string.Equals(picartoUsername, _picartoUsername))
        //     return;
        
        _picartoUsername = picartoUsername;

        if (string.IsNullOrWhiteSpace(_picartoUsername))
        {
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

    private void OnConnect(object? sender, PicartoWebSocketConnectedEventArgs args)
    {
        if (sender is PicartoChatClient)
        {
            _chatConnected = true;
            _chatReconnect.Reset();
            _chatReconnect.Cts?.Cancel();
            IntegrationEvents.RaiseConnectionUpdate(
                true, SubathonEventSource.Picarto, _picartoUsername ?? "", "Chat");
            
        }
        else if (sender is PicartoEventsClient)
        {
            _eventsConnected = true;
            _eventsReconnect.Reset();
            _eventsReconnect.Cts?.Cancel();
            IntegrationEvents.RaiseConnectionUpdate(
                _eventsConnected, SubathonEventSource.Picarto, _picartoUsername ?? "", "Alerts");
        }
    }

    [ExcludeFromCodeCoverage]
    private async Task ReconnectWithBackoffAsync(
        PicartoWebSocketConnection client,
        Func<bool> isConnected,
        Utils.ServiceReconnectState state)
    {
        if (!await state.Lock.WaitAsync(0))
            return;
        
        try
        {
            _logger?.LogInformation("Attempting to reconnect {Client}", client.GetType().Name);
            state.Cts?.Cancel();
            state.Cts = new CancellationTokenSource();
            var token = state.Cts.Token;

            while (!isConnected() && !token.IsCancellationRequested && !string.IsNullOrWhiteSpace(_picartoUsername))
            {
                var delay = state.Backoff;
                _logger?.LogDebug(
                    "{Client} reconnect attempt in {Delay}s",
                    client.GetType().Name,
                    delay);

                try
                {
                    await Task.Delay(delay, token);
                    await client.ReconnectAsync();
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, $"{client.GetType().Name} reconnection failed");
                }

                state.Backoff = TimeSpan.FromMilliseconds(
                    Math.Min(
                        state.Backoff.TotalMilliseconds * 2,
                        state.MaxBackoff.TotalMilliseconds));
                state.Retries += 1;
                if (state.Retries >= state.MaxRetries)
                {
                    _logger?.LogError("Max retries exceeded for Picarto connection. Please reconnect");
                    // discord error event TODO 
                    state.Cts?.Cancel();
                    break;
                }
            }
        }
        finally
        {
            state.Lock.Release();
        }
    }

    private void OnDisconnect(object? sender, PicartoWebSocketDisconnectedEventArgs args)
    {
        _logger?.LogWarning(
            "Picarto disconnected: {Sender} {Status} - {Description}",
            sender?.GetType().Name,
            args.CloseStatus,
            args.CloseDescription);
        
        var description = args.CloseDescription ?? string.Empty;
        bool shouldReconnect =
            args.Exception != null ||
            (description != "Disconnect" && description != "Closing");

        if (sender is PicartoChatClient)
        {
            _chatConnected = false;
            IntegrationEvents.RaiseConnectionUpdate(
                _chatConnected, SubathonEventSource.Picarto, _picartoUsername ?? "", "Chat");

            if (shouldReconnect && sender is PicartoWebSocketConnection ws)
            {
                _ = Task.Run(() =>
                    ReconnectWithBackoffAsync(
                        ws,
                        () => _chatConnected,
                        _chatReconnect));
            }
        }
        else if (sender is PicartoEventsClient)
        {
            _eventsConnected = false;
            IntegrationEvents.RaiseConnectionUpdate(
                _eventsConnected, SubathonEventSource.Picarto, _picartoUsername ?? "", "Alerts");

            if (shouldReconnect && sender is PicartoWebSocketConnection ws)
            {
                _ = Task.Run(() =>
                    ReconnectWithBackoffAsync(
                        ws,
                        () => _eventsConnected,
                        _eventsReconnect));
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        
        _chatReconnect.Dispose();
        _eventsReconnect.Dispose();
        if (_eventClient != null)
        {
            _eventClient.Disconnected -= OnDisconnect;
            _eventClient.Connected -= OnConnect;
            _eventClient.AlertReceived -= OnAlert;
            _ = Task.Run(_eventClient.DisconnectAsync);
        }

        if (_chatClient != null)
        {
            _chatClient.Disconnected -= OnDisconnect;
            _chatClient.Connected -= OnConnect;
            _chatClient.MessageReceived -= OnChatMessage;
            _ = Task.Run(_chatClient.DisconnectAsync);
        }

        _disposed = true;
    }
}