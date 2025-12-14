using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StreamElements.WebSocket;
using StreamElements.WebSocket.Models.Tip;
using StreamElements.WebSocket.Models.Internal;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Models;

namespace SubathonManager.Integration;

public class StreamElementsService
{

    public Func<StreamElementsClient> ClientFactory { get; set; } 
        = () => new StreamElementsClient();
    
    private StreamElementsClient? _client;
    public bool Connected { get; private set; } = false;
    private string _jwtToken = "";
    private bool _hasAuthError = false;
    private readonly object _reconnectLock = new();
    private bool _isReconnecting = false;
    
    private readonly ILogger? _logger;
    private readonly IConfig _config;

    public StreamElementsService(ILogger<StreamElementsService>? logger, IConfig config)
    {
        _logger = logger;
        _config = config;
    }
    
    public bool InitClient()
    {
        _client = ClientFactory();
        _hasAuthError = false;
        Connected = false;
        GetJwtFromConfig();
        if (_jwtToken.Equals(string.Empty)) return false;
        
        if (_client != null)
        {
            Disconnect();
        }

        _client = new StreamElementsClient();
        
        _client.OnConnected += _OnConnected;
        _client.OnAuthenticated += _OnAuthenticated;
        _client.OnTip += _OnTip;
        _client.OnDisconnected += _OnDisconnected;
        _client.OnAuthenticationFailure += _OnAuthenticateError;
        
        _client.Connect(_jwtToken);
        return true;
    }

    public bool IsTokenEmpty()
    {
        return string.IsNullOrEmpty(_jwtToken);
    }

    public void SetJwtToken(string token)
    {
        _jwtToken = token;
        _config.Set("StreamElements", "JWT", token);
        _config.Save();
    }

    private void GetJwtFromConfig()
    {
        _jwtToken = _config.Get("StreamElements", "JWT", "")!;
    }

    private void _OnConnected(object? sender, EventArgs e)
    {
        lock (_reconnectLock)
            _isReconnecting = false;
        _logger?.LogInformation("StreamElementsService Connected");
    }
    private void _OnDisconnected(object? sender, EventArgs e)
    {
        _logger?.LogWarning($"StreamElementsService Disconnected");
        Connected = false;
        StreamElementsEvents.RaiseStreamElementsConnectionChanged(Connected);
        if (_hasAuthError) return;
        
        Task.Run(async () =>
        {
            await Task.Delay(500);
            lock (_reconnectLock)
            {
                if (_isReconnecting || Connected || _hasAuthError || _client == null)
                    return;

                _isReconnecting = true;
            }

            try
            {
                _client.Connect(_jwtToken);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "StreamElements Reconnection failed, retrying in 2s");
                await Task.Delay(2000);
                try
                {
                    _client.Connect(_jwtToken);
                }
                catch (Exception ex2)
                {
                    string message = $"StreamElements Disconnected with an error. Could not auto-reconnect. {ex2.Message}";
                    ErrorMessageEvents.RaiseErrorEvent("ERROR", nameof(SubathonEventSource.StreamElements), 
                        message, DateTime.Now.ToLocalTime());
                    _logger?.LogError(ex2, message);
                }
            }
            finally
            {
                lock (_reconnectLock)
                    _isReconnecting = false;
            }
        });
    }

    private void _OnAuthenticated(object? sender, Authenticated e)
    {
        _logger?.LogDebug($"StreamElementsService Authenticated");
        Connected = true;
        _hasAuthError = false;
        StreamElementsEvents.RaiseStreamElementsConnectionChanged(Connected);
    }

    private void _OnAuthenticateError(object? sender, EventArgs e)
    {
        _logger?.LogError($"StreamElementsService Authentication Error");
        Connected = false;
        _hasAuthError = true;
        StreamElementsEvents.RaiseStreamElementsConnectionChanged(Connected);
        ErrorMessageEvents.RaiseErrorEvent("ERROR", nameof(SubathonEventSource.StreamElements), 
            "StreamElements Token could not be validated", DateTime.Now.ToLocalTime());
    }

    private void _OnTip(object? sender, Tip e)
    {
        SubathonEvent subathonEvent = new();
        subathonEvent.User = e.Username;
        subathonEvent.Currency = e.Currency;
        subathonEvent.Value = $"{e.Amount}";
        subathonEvent.Source = SubathonEventSource.StreamElements;
        subathonEvent.EventType = SubathonEventType.StreamElementsDonation;
        if (Guid.TryParse(e.TipId, out var tipGuid))
            subathonEvent.Id = tipGuid;
        
        SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
    }

    public void Disconnect()
    {
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
            _logger?.LogWarning(ex, "StreamElementsService Disconnection Error");
        }
    }

    public static void SimulateTip(string value = "10.00", string currency = "USD")
    {
        if (!double.TryParse(value, out var val))
            return;

        SubathonEvent subathonEvent = new();
        subathonEvent.User = "SYSTEM";
        subathonEvent.Currency = currency;
        
        subathonEvent.Value = value; // TODO verify format, must be parsable as a double
        subathonEvent.Source = SubathonEventSource.Simulated;
        subathonEvent.EventType = SubathonEventType.StreamElementsDonation;
        
        SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
    }
}