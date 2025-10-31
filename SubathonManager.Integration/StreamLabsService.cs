using Streamlabs.SocketClient;
using Streamlabs.SocketClient.Messages;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Models;

namespace SubathonManager.Integration;

public class StreamLabsService
{
    // TODO logger
    private StreamlabsClient? _client;
    private string _secretToken = "";
    public bool Connected { get; private set; } = false;
    
    public async Task<bool> InitClientAsync()
    {   
        Connected = false;
        GetTokenFromConfig();
        StreamLabsEvents.RaiseStreamLabsConnectionChanged(Connected);
        if (_secretToken.Equals(String.Empty)) return false;
        
        OptionsWrapper<StreamlabsOptions> options = new OptionsWrapper<StreamlabsOptions>(
            new StreamlabsOptions { Token = _secretToken }
        );
        
        if (_client != null) await DisconnectAsync();
        
        _client = new StreamlabsClient(NullLogger<StreamlabsClient>.Instance, options);
        _client.OnDonation += OnDonation;
        await _client.ConnectAsync();
        Connected = true;
        StreamLabsEvents.RaiseStreamLabsConnectionChanged(Connected);
        return true;
    }
    
    private void GetTokenFromConfig()
    {
        _secretToken = Config.Data["StreamLabs"]["SocketToken"] ?? "";
    }  
    
    public bool IsTokenEmpty()
    {
        return string.IsNullOrEmpty(_secretToken);
    }
    
    public void SetSocketToken(string token)
    {
        _secretToken = token;
        Config.Data["StreamLabs"]["SocketToken"] = token;
        Config.Save();
    }
    
    private void OnDonation(object? o, DonationMessage message)
    {
        SubathonEvent subathonEvent = new();
        subathonEvent.User = message.Name;
        subathonEvent.Currency = $"{message.Currency}".ToUpper();
        subathonEvent.Value = $"{message.Amount}";
        subathonEvent.Source = SubathonEventSource.StreamLabs;
        subathonEvent.EventType = SubathonEventType.StreamLabsDonation;
        Console.WriteLine(message.MessageId);
        if (Guid.TryParse(message.MessageId, out var tipGuid))
            subathonEvent.Id = tipGuid;
        
        SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
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
        subathonEvent.EventType = SubathonEventType.StreamLabsDonation;
        
        SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
    }
    
    public async Task DisconnectAsync()
    {
        Connected = false;
        if (_client == null) return;
        
        _client.OnDonation -= OnDonation;
        await _client.DisconnectAsync();
        StreamLabsEvents.RaiseStreamLabsConnectionChanged(Connected);
    }
}