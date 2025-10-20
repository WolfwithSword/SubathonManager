using StreamElementsNET;
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
        Console.WriteLine($"StreamElementsService Connected");
        // StreamElementsEvents.RaiseStreamElementsConnectionChanged(Connected);
    }
    private void _OnDisconnected(object? sender, EventArgs e)
    {
        Console.WriteLine($"StreamElementsService Disconnected");
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
        Console.WriteLine($"StreamElementsService Authenticated");
        Connected = true;
        _hasAuthError = false;
        StreamElementsEvents.RaiseStreamElementsConnectionChanged(Connected);
    }

    private void _OnAuthenticateError(object? sender, EventArgs e)
    {
        Console.WriteLine($"StreamElementsService Authentication Error");
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
        // Console.WriteLine($"SE Tip: {e.Amount} {e.Currency} {e.Username} {e.TipId}");
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
            //
        }
    }

    public static void SimulateTip(string value = "10.00", string currency = "USD")
    {
        if (!double.TryParse(value, out var val))
        {
            Console.WriteLine($"Invalid value for simulated tip. {value}");
            return;
        }

        SubathonEvent subathonEvent = new();
        subathonEvent.User = "SYSTEM";
        subathonEvent.Currency = currency;
        
        subathonEvent.Value = value; // TODO verify format, must be parsable as a double
        subathonEvent.Source = SubathonEventSource.Simulated;
        subathonEvent.EventType = SubathonEventType.StreamElementsDonation;
        
        SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
    }
}