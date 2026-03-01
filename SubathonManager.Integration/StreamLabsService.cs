using Streamlabs.SocketClient;
using Streamlabs.SocketClient.Messages;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;

namespace SubathonManager.Integration;

public class StreamLabsService : IAppService
{
    private StreamlabsClient? _client;
    private string _secretToken = "";
    public bool Connected { get; private set; } = false;

    private readonly ILogger? _logger;
    private readonly IConfig _config;

    internal string BaseUrl = "https://sockets.streamlabs.com";

    public StreamLabsService(ILogger<StreamLabsService>? logger, IConfig config)
    {
        _logger = logger;
        _config = config;
    }
    
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await InitClientAsync();
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await DisconnectAsync();
    }
    
    // todo cleanup all names in test for inits
    public async Task<bool> InitClientAsync()
    {
        Connected = false;
        GetTokenFromConfig();
        
        IntegrationEvents.RaiseConnectionUpdate(Connected, SubathonEventSource.StreamLabs, "User", "Socket");
        if (_secretToken.Equals(string.Empty)) return false;

        // TODO Webserver selfhost mock tests here 
        OptionsWrapper<StreamlabsOptions> options = new OptionsWrapper<StreamlabsOptions>(
            new StreamlabsOptions { Token = _secretToken, Url = BaseUrl}
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
        
        IntegrationEvents.RaiseConnectionUpdate(Connected, SubathonEventSource.StreamLabs, "User", "Socket");
        _logger?.LogInformation("StreamLabs Service Connected");
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
        if (_config.Set("StreamLabs", "SocketToken", token))
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
        if (!Connected) return;
        Connected = false;
        if (_client == null) return;
        
        _client.OnDonation -= OnDonation;
        await _client.DisconnectAsync();
        IntegrationEvents.RaiseConnectionUpdate(Connected, SubathonEventSource.StreamLabs, "User", "Socket");
        _logger?.LogInformation("StreamLabsService Disconnected");
    }
}