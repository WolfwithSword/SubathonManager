using Streamlabs.SocketClient;
using Streamlabs.SocketClient.Messages;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Models;

namespace SubathonManager.Integration;

public class StreamLabsService
{
    private StreamlabsClient? _client;
    private string _secretToken = "";
    public bool Connected { get; private set; } = false;

    private readonly ILogger? _logger;
    private readonly IConfig _config;

    public StreamLabsService(ILogger<StreamLabsService>? logger, IConfig config)
    {
        _logger = logger;
        _config = config;
    }
    
    public async Task<bool> InitClientAsync()
    {
        Connected = false;
        GetTokenFromConfig();
        StreamLabsEvents.RaiseStreamLabsConnectionChanged(Connected);
        if (_secretToken.Equals(string.Empty)) return false;

        OptionsWrapper<StreamlabsOptions> options = new OptionsWrapper<StreamlabsOptions>(
            new StreamlabsOptions { Token = _secretToken }
        );

        if (_client != null) await DisconnectAsync();

        _client = new StreamlabsClient(AppServices.Provider.GetRequiredService<ILogger<StreamlabsClient>>(), options);
        _client.OnDonation += OnDonation;
        try
        {
            await _client.ConnectAsync();
            Connected = true;
        }
        catch (Exception ex)
        {
            string message = $"StreamLabs Service failed to connect: {ex.Message}";
            _logger?.LogError(message);
            ErrorMessageEvents.RaiseErrorEvent("ERROR", nameof(SubathonEventSource.Twitch), 
                message, DateTime.Now);
        }
        
        StreamLabsEvents.RaiseStreamLabsConnectionChanged(Connected);
        return Connected;
    }
    
    private void GetTokenFromConfig()
    {
        _secretToken = _config.Get("StreamLabs", "SocketToken")!;
    }  
    
    public bool IsTokenEmpty()
    {
        return string.IsNullOrEmpty(_secretToken);
    }
    
    public void SetSocketToken(string token)
    {
        _secretToken = token;
        _config.Set("StreamLabs", "SocketToken", token);
        _config.Save();
    }
    
    private void OnDonation(object? o, DonationMessage message)
    {
        SubathonEvent subathonEvent = new();
        subathonEvent.User = message.Name;
        subathonEvent.Currency = $"{message.Currency}".ToUpper();
        subathonEvent.Value = $"{message.Amount}";
        subathonEvent.Source = SubathonEventSource.StreamLabs;
        subathonEvent.EventType = SubathonEventType.StreamLabsDonation;
        if (Guid.TryParse(message.MessageId, out var tipGuid))
            subathonEvent.Id = tipGuid;
        
        SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
    }
    
    public static void SimulateTip(string value = "10.00", string currency = "USD")
    {
        if (!double.TryParse(value, out var val))
            return;

        SubathonEvent subathonEvent = new SubathonEvent
        {
            User = "SYSTEM",
            Currency = currency,
            Value = value,
            Source = SubathonEventSource.Simulated,
            EventType = SubathonEventType.StreamLabsDonation
        };
        
        SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
    }
    
    public async Task DisconnectAsync()
    {
        Connected = false;
        if (_client == null) return;
        
        _client.OnDonation -= OnDonation;
        await _client.DisconnectAsync();
        StreamLabsEvents.RaiseStreamLabsConnectionChanged(Connected);
        _logger?.LogInformation("StreamLabsService Disconnected");
    }
}