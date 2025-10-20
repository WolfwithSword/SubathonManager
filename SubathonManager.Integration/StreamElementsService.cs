using StreamElementsNET;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Models;
using TwitchLib.Api.Helix.Models.Streams.GetStreamKey;

namespace SubathonManager.Integration;

public class StreamElementsService
{

    private Client? client;
    public bool Connected { get; private set; } = false;
    private string JwtToken = "";
    private bool HasAuthError = false;
    
    public bool InitClient()
    {
        HasAuthError = false;
        Connected = false;
        GetJwtFromConfig();
        if (JwtToken.Equals(String.Empty)) return false;
        
        if (client != null)
        {
            Disconnect();
        }

        client = new Client(JwtToken);
        
        client.OnConnected += _OnConnected;
        client.OnAuthenticated += _OnAuthenticated;
        client.OnTip += _OnTip;
        client.OnDisconnected += _OnDisconnected;
        client.OnAuthenticationFailure += _OnAuthenticateError;
        
        client.Connect();
        return true;
    }

    public bool IsTokenEmpty()
    {
        return string.IsNullOrEmpty(JwtToken);
    }

    public void SetJwtToken(string token)
    {
        JwtToken = token;
        Config.Data["StreamElements"]["JWT"] = token;
        Config.Save();
    }

    private void GetJwtFromConfig()
    {
        JwtToken = Config.Data["StreamElements"]["JWT"] ?? "";
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
        if (!HasAuthError)
        {
            Task.Run(() =>
            {
                Task.Delay(200);
                if (!HasAuthError && !Connected && client != null)
                {
                    client.Connect();
                } 
            });
        }
    }

    private void _OnAuthenticated(object? sender,  StreamElementsNET.Models.Internal.Authenticated e)
    {
        Console.WriteLine($"StreamElementsService Authenticated");
        Connected = true;
        HasAuthError = false;
        StreamElementsEvents.RaiseStreamElementsConnectionChanged(Connected);
    }

    private void _OnAuthenticateError(object? sender, EventArgs e)
    {
        Console.WriteLine($"StreamElementsService Authentication Error");
        Connected = false;
        HasAuthError = true;
        StreamElementsEvents.RaiseStreamElementsConnectionChanged(Connected);
    }

    private void _OnTip(object? sender, StreamElementsNET.Models.Tip.Tip e)
    {
        SubathonEvent _event = new();
        _event.User = e.Username;
        _event.Currency = e.Currency;
        _event.Value = $"{e.Amount}";
        _event.Source = SubathonEventSource.StreamElements;
        _event.EventType = SubathonEventType.StreamElementsDonation;
        if (Guid.TryParse(e.TipId, out var tipGuid))
            _event.Id = tipGuid;
        
        SubathonEvents.RaiseSubathonEventCreated(_event);
        // Console.WriteLine($"SE Tip: {e.Amount} {e.Currency} {e.Username} {e.TipId}");
    }

    public void Disconnect()
    {
        if (client == null) return;
        try
        {
            client.Disconnect();
            client.OnConnected -= _OnConnected;
            client.OnAuthenticated -= _OnAuthenticated;
            client.OnTip -= _OnTip;
            client.OnDisconnected -= _OnDisconnected;
            client.OnAuthenticationFailure -= _OnAuthenticateError;
            client = null;
        }
        catch (Exception ex)
        {
            //
        }
    }

    public static void SimulateTip(string value = "10.00", string currency = "USD")
    {
        if (!double.TryParse(value, out var _value))
        {
            Console.WriteLine($"Invalid value for simulated tip. {value}");
            return;
        }

        SubathonEvent _event = new();
        _event.User = "SYSTEM";
        _event.Currency = currency;
        
        _event.Value = value; // TODO verify format, must be parsable as a double
        _event.Source = SubathonEventSource.Simulated;
        _event.EventType = SubathonEventType.StreamElementsDonation;
        
        SubathonEvents.RaiseSubathonEventCreated(_event);
    }
}