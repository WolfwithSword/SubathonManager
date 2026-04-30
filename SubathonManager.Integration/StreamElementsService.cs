using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using StreamElements.WebSocket;
using StreamElements.WebSocket.Models.Tip;
using StreamElements.WebSocket.Models.Internal;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;
using SubathonManager.Core.Security;
using SubathonManager.Core.Security.Interfaces;

namespace SubathonManager.Integration;

public class StreamElementsService(ILogger<StreamElementsService>? logger, IConfig config, ISecureStorage secureStorage)
    : IAppService
{

    private Func<StreamElementsClient> ClientFactory { get; set; } 
        = () => new StreamElementsClient();
    
    private StreamElementsClient? _client;
    public bool Connected { get; private set; } = false;
    private string? JwtToken => secureStorage.GetOrDefault(StorageKeys.StreamElementsJwt, string.Empty);
    private bool _hasAuthError = false;
    private readonly Utils.ServiceReconnectState _reconnectState =
        new(TimeSpan.FromSeconds(2), maxRetries: 50, maxBackoff: TimeSpan.FromMinutes(5));
    

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        InitClient();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        Disconnect();
        return Task.CompletedTask;
    }
    
    public bool InitClient()
    {
        _client = ClientFactory();
        _hasAuthError = false;
        Connected = false;
        if (string.IsNullOrWhiteSpace(JwtToken)) return false;
        
        if (_client != null)
        {
            Disconnect();
        }

        _client = new StreamElementsClient();
        
        _reconnectState.Reset();
        _client.OnConnected += _OnConnected;
        _client.OnAuthenticated += _OnAuthenticated;
        _client.OnTip += _OnTip;
        _client.OnDisconnected += _OnDisconnected;
        _client.OnAuthenticationFailure += _OnAuthenticateError;
        _client.Connect(JwtToken);
        return true;
    }

    public bool IsTokenEmpty()
    {
        return !secureStorage.Exists(StorageKeys.StreamElementsJwt) || string.IsNullOrWhiteSpace(JwtToken);
    }

    public bool SetJwtToken(string token)
    {
        return secureStorage.Set(StorageKeys.StreamElementsJwt, token);
    }
    
    private void _OnConnected(object? sender, EventArgs e)
    {
        logger?.LogInformation("[StreamElementsService] Connected");
        _reconnectState.Reset();
        _reconnectState.Cts?.Cancel();
    }
    private void _OnDisconnected(object? sender, EventArgs e)
    {
        logger?.LogWarning("[StreamElementsService] Disconnected");
        Connected = false;
         
        IntegrationEvents.RaiseConnectionUpdate(new IntegrationConnection
        {
            Source = SubathonEventSource.StreamElements,
            Service = "Socket",
            Name = "User",
            Status = Connected
        });
        if (_hasAuthError) return;
        
        _ = Task.Run(ReconnectWithBackoffAsync);
    }

    [ExcludeFromCodeCoverage]
    private async Task ReconnectWithBackoffAsync()
    {
        if (_client == null)
            return;

        if (!await _reconnectState.Lock.WaitAsync(0))
            return; // already reconnecting

        try
        {
            _reconnectState.Cts?.Cancel();
            _reconnectState.Cts = new CancellationTokenSource();
            var token = _reconnectState.Cts.Token;

            while (!token.IsCancellationRequested && !Connected && !_hasAuthError)
            {
                if (_reconnectState.Retries >= _reconnectState.MaxRetries)
                {
                    string message =
                        "StreamElements disconnected and max reconnect retries were reached.";

                    ErrorMessageEvents.RaiseErrorEvent(
                        "ERROR",
                        nameof(SubathonEventSource.StreamElements),
                        message,
                        DateTime.Now.ToLocalTime());

                    logger?.LogError(message);
                    return;
                }

                _reconnectState.Retries++;

                var delay = _reconnectState.Backoff;

                logger?.LogWarning(
                    "[StreamElementsService] Reconnect attempt {Attempt}/{Max} in {Delay}s",
                    _reconnectState.Retries,
                    _reconnectState.MaxRetries,
                    delay.TotalSeconds);

                try
                {
                    await Task.Delay(delay, token);

                    if (!Connected && !_hasAuthError && _client != null)
                    {
                        _client.Connect(JwtToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    string message = $"StreamElements Disconnected with an error. Could not auto-reconnect. {ex.Message}";
                    logger?.LogWarning(ex, message);
                    ErrorMessageEvents.RaiseErrorEvent("ERROR", nameof(SubathonEventSource.StreamElements), 
                                       message, DateTime.Now.ToLocalTime());
                }

                // exponential backoff
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

    private void _OnAuthenticated(object? sender, Authenticated e)
    {
        logger?.LogDebug($"[StreamElementsService] Authenticated");
        Connected = true;
        _hasAuthError = false;

        _reconnectState.Cts?.Cancel();
        _reconnectState.Reset(); 
        IntegrationEvents.RaiseConnectionUpdate(new IntegrationConnection
        {
            Source = SubathonEventSource.StreamElements,
            Service = "Socket",
            Name = "User",
            Status = Connected
        });
    }

    private void _OnAuthenticateError(object? sender, EventArgs e)
    {
        logger?.LogError("[StreamElementsService] Authentication Error");
        Connected = false;
        _hasAuthError = true;
        _reconnectState.Cts?.Cancel();   
        IntegrationEvents.RaiseConnectionUpdate(new IntegrationConnection
        {
            Source = SubathonEventSource.StreamElements,
            Service = "Socket",
            Name = "User",
            Status = Connected
        });
        ErrorMessageEvents.RaiseErrorEvent("ERROR", nameof(SubathonEventSource.StreamElements), 
            "StreamElements Token could not be validated", DateTime.Now.ToLocalTime());
    }

    private void _OnTip(object? sender, Tip e)
    {
        SubathonEvent subathonEvent = new()
        {
            User = e.Username,
            Currency = e.Currency,
            Value = $"{e.Amount}",
            Source = SubathonEventSource.StreamElements,
            EventType = SubathonEventType.StreamElementsDonation
        };
        if (Guid.TryParse(e.TipId, out var tipGuid))
            subathonEvent.Id = tipGuid;
        
        SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
    }

    public void Disconnect()
    {
        _reconnectState.Cts?.Cancel();
        if (_client == null) return;
        try
        {
            _client.Disconnect();
            _client.OnConnected -= _OnConnected;
            _client.OnAuthenticated -= _OnAuthenticated;
            _client.OnTip -= _OnTip;
            _client.OnDisconnected -= _OnDisconnected;
            _client.OnAuthenticationFailure -= _OnAuthenticateError;
            _client = null;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "[StreamElementsService] Disconnection Error");
        }
        finally
        {
            IntegrationEvents.RaiseConnectionUpdate(new IntegrationConnection
            {
                Source = SubathonEventSource.StreamElements,
                Service = "Socket",
                Name = "User",
                Status = false
            });
        }
    }

    public static void SimulateTip(string value = "10.00", string currency = "USD")
    {
        if (!double.TryParse(value, out var val))
            return;

        SubathonEvent subathonEvent = new()
        {
            User = "SYSTEM",
            Currency = currency,
            Value = value,
            Source = SubathonEventSource.Simulated,
            EventType = SubathonEventType.StreamElementsDonation
        };

        SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
    }
}