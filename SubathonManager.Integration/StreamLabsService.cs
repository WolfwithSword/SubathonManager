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
using SubathonManager.Core.Objects;
using SubathonManager.Core.Security;
using SubathonManager.Core.Security.Interfaces;

namespace SubathonManager.Integration;

public class StreamLabsService : IAppService
{
    internal Func<string, IStreamlabsClient> ClientFactory;
    private IStreamlabsClient? _client;
    public bool Connected { get; private set; } = false;

    private readonly ILogger? _logger;
    private readonly ISecureStorage _secureStorage;

    internal string BaseUrl = "https://sockets.streamlabs.com";
    private string? SecretToken => _secureStorage.GetOrDefault(StorageKeys.StreamLabsSocketToken, string.Empty);

    public StreamLabsService(ILogger<StreamLabsService>? logger, ISecureStorage secureStorage)
    {
        _logger = logger;
        _secureStorage = secureStorage;

        ClientFactory = token =>
        {
            var options = new OptionsWrapper<StreamlabsOptions>(
                new StreamlabsOptions { Token = token, Url = BaseUrl });
            return new StreamlabsClient(
                AppServices.Provider.GetRequiredService<ILogger<StreamlabsClient>>(), options);
        };
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
        IntegrationEvents.RaiseConnectionUpdate(new IntegrationConnection
        {
            Source = SubathonEventSource.StreamLabs,
            Service = "Socket",
            Name = "User",
            Status = false
        });
        
        if (string.IsNullOrWhiteSpace(SecretToken))
        {
            Connected = false; 
            return false;
        }

        if (_client != null) await DisconnectAsync();
        
        _client = ClientFactory(SecretToken);
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
        IntegrationEvents.RaiseConnectionUpdate(new IntegrationConnection
        {
            Source = SubathonEventSource.StreamLabs,
            Service = "Socket",
            Name = "User",
            Status = Connected
        });
        _logger?.LogInformation("StreamLabs Service Connected");
        return Connected;
    }
    
    public bool IsTokenEmpty()
    {
        return !_secureStorage.Exists(StorageKeys.StreamLabsSocketToken) || string.IsNullOrWhiteSpace(SecretToken);
    }

    public bool SetSocketToken(string token)
    {
       return _secureStorage.Set(StorageKeys.StreamLabsSocketToken, token);
    }
    
    private void OnDonation(object? o, DonationMessage message)
    {
        SubathonEvent subathonEvent = new()
        {
            User = message.Name,
            Currency = $"{message.Currency}".ToUpper(),
            Value = $"{message.Amount}",
            Source = SubathonEventSource.StreamLabs,
            EventType = SubathonEventType.StreamLabsDonation
        };
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
        IntegrationEvents.RaiseConnectionUpdate(new IntegrationConnection
        {
            Source = SubathonEventSource.StreamLabs,
            Service = "Socket",
            Name = "User",
            Status = Connected
        });
        _logger?.LogInformation("StreamLabsService Disconnected");
    }
}