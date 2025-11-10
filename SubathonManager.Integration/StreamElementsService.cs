using StreamElementsNET;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Models;

namespace SubathonManager.Integration;

public class StreamElementsService
{

    private Client? _client;
    public bool Connected { get; private set; } = false;
    private string _jwtToken = "";
    private bool _hasAuthError = false;
    
    private readonly ILogger? _logger = AppServices.Provider.GetRequiredService<ILogger<StreamElementsService>>();
    
    public bool InitClient()
    {
        _hasAuthError = false;
        Connected = false;
        GetJwtFromConfig();
        if (_jwtToken.Equals(String.Empty)) return false;
        
        if (_client != null)
        {
            Disconnect();
        }

        _client = new Client(_jwtToken);
        
        _client.OnConnected += _OnConnected;
        _client.OnAuthenticated += _OnAuthenticated;
        _client.OnTip += _OnTip;
        _client.OnDisconnected += _OnDisconnected;
        _client.OnAuthenticationFailure += _OnAuthenticateError;
        
        _client.Connect();
        return true;
    }

    public bool IsTokenEmpty()
    {
        return string.IsNullOrEmpty(_jwtToken);
    }

    public void SetJwtToken(string token)
    {
        _jwtToken = token;
        Config.Data["StreamElements"]["JWT"] = token;
        Config.Save();
    }

    private void GetJwtFromConfig()
    {
        _jwtToken = Config.Data["StreamElements"]["JWT"] ?? "";
    }

    private void _OnConnected(object? sender, EventArgs e)
    {
        _logger?.LogInformation("StreamElementsService Connected");
    }
    private void _OnDisconnected(object? sender, EventArgs e)
    {
        _logger?.LogWarning($"StreamElementsService Disconnected");
        Connected = false;
        StreamElementsEvents.RaiseStreamElementsConnectionChanged(Connected);
        if (!_hasAuthError)
        {
            Task.Run(() =>
            {
                Task.Delay(200);
                if (!_hasAuthError && !Connected && _client != null)
                {
                    _client.Connect();
                } 
            });
        }
    }

    private void _OnAuthenticated(object? sender,  StreamElementsNET.Models.Internal.Authenticated e)
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
    }

    private void _OnTip(object? sender, StreamElementsNET.Models.Tip.Tip e)
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