using YTLiveChat.Contracts;
using YTLiveChat.Contracts.Models;
using YTLiveChat.Contracts.Services;
using YTLiveChat.Services; 
using Microsoft.Extensions.Logging;
using System.Diagnostics.CodeAnalysis;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Models;
using SubathonManager.Services;

namespace SubathonManager.Integration;

public class YouTubeService : IDisposable
{
    private int _pollTime = 1500;
    private readonly HttpClient _httpClient;
    private readonly IYTLiveChat _ytLiveChat;
    private bool _disposed = false;
    private string? _ytHandle;
    public bool Running;

    // private readonly ILogger<YTLiveChat.Services.YTLiveChat> _chatLogger;
    //NullLogger<YTLiveChat.Services.YTLiveChat>.Instance;
    // private readonly ILogger<YTHttpClient> _httpClientLogger;
    //NullLogger<YTHttpClient>.Instance;

    private readonly Utils.ServiceReconnectState _reconnectState = 
        new(TimeSpan.FromSeconds(5), maxRetries: 100, maxBackoff: TimeSpan.FromMinutes(2));
    
    private readonly ILogger? _logger;
    private readonly IConfig _config;
    
    private int _canonicalLinkErrorCount = 0;
    
    public YouTubeService(ILogger<YouTubeService>? logger, IConfig config, ILogger<YTHttpClient>? httpClientLogger, ILogger<YTLiveChat.Services.YTLiveChat>? chatLogger)
    {
        _logger = logger;
        _config = config;
        var options = new YTLiveChatOptions
        {
            RequestFrequency = _pollTime,
        };
        
        if (_httpClient == null)
            _httpClient = new HttpClient()
            {
                BaseAddress = new Uri(options.YoutubeBaseUrl)
            };
        
        var ytHttpClient = new YTHttpClient(_httpClient, httpClientLogger);
        _ytLiveChat = new YTLiveChat.Services.YTLiveChat(options, ytHttpClient, chatLogger);
        
        _ytLiveChat.InitialPageLoaded += OnInitialPageLoaded;
        _ytLiveChat.ChatReceived += OnChatReceived;
        _ytLiveChat.ChatStopped += OnChatStopped;
        _ytLiveChat.ErrorOccurred += OnErrorOccurred;
    }

    public bool Start(string? handle)
    {
        Running = false;
        _reconnectState.Reset();
        IntegrationEvents.RaiseConnectionUpdate(Running, SubathonEventSource.YouTube, "None", "Chat");

        _ytHandle = handle ?? _config.Get("YouTube", "Handle")!;
        if (string.IsNullOrEmpty(_ytHandle) || _ytHandle.Trim() == "@")
        {
            _logger?.LogInformation("YouTube Service not connected to any channel. Not running.");
            return Running;
        }

        if (!_ytHandle.StartsWith("@")) _ytHandle =  "@" + _ytHandle;
        _logger?.LogInformation("Youtube Service Starting for " + _ytHandle);
        
        _ytLiveChat.Start(handle: _ytHandle, overwrite: true);
        return true;
    }

    private void OnInitialPageLoaded(object? sender, InitialPageLoadedEventArgs e)
    {
        Running = true;
        _reconnectState.Reset();
        _reconnectState.Cts?.Cancel();

        IntegrationEvents.RaiseConnectionUpdate(Running, SubathonEventSource.YouTube, _ytHandle!, "Chat");
        _logger?.LogInformation($"Successfully loaded YouTube Live ID: {e.LiveId}");
    }
    private void OnChatStopped(object? sender, ChatStoppedEventArgs e)
    {
        Running = false;
        _logger?.LogWarning("YT Chat stopped");
        IntegrationEvents.RaiseConnectionUpdate(Running, SubathonEventSource.YouTube, _ytHandle!, "Chat");
        TryReconnectLoop();
    }

    private void OnErrorOccurred(object? sender, ErrorOccurredEventArgs e)
    {
        var ex = e.GetException();
        Running = false;
        
        if (ex?.Message.Contains("canonical link not found", StringComparison.OrdinalIgnoreCase) == true)
        {
            if (_canonicalLinkErrorCount == 0)
            {
                _logger?.LogWarning(
                    "YouTube stream is offline? (canonical link not found)."
                );
            }

            _canonicalLinkErrorCount++;
        }
        else
        {
            _logger?.LogWarning(ex, "YT Error Occurred");
        }

        IntegrationEvents.RaiseConnectionUpdate(Running, SubathonEventSource.YouTube, _ytHandle!, "Chat");
    }
    
    private void OnChatReceived(object? sender, ChatReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_ytHandle) || _ytHandle == "@")
            return;
        
        if (!Running)
        {
            IntegrationEvents.RaiseConnectionUpdate(true, SubathonEventSource.YouTube, _ytHandle!, "Chat");
            _reconnectState.Cts?.Cancel();
            _reconnectState.Reset();
        }

        Running = true;
        _canonicalLinkErrorCount = 0;
        
        ChatItem item = e.ChatItem;

        if (item.Timestamp.DateTime.ToLocalTime() <
            DateTime.Now - TimeSpan.FromMinutes(3))
        {
            // only if it's not older than 3 min,
            // to avoid reparsing old events that had new id's
            return;
        }

        string user = item.Author.Name.Replace("@", "");
        if (item.Superchat != null)
        {
            string currency = item.Superchat.Currency.ToUpper().Trim();
            string raw = item.Superchat.AmountString.Trim();
            if (currency == "USD" && !raw.StartsWith('$')) // Parsed incorrectly
                currency = Utils.TryParseCurrency(raw);

            if (currency == "" || currency == "???")
            {
                string message = $"Unknown currency detected: From: {user},  {item.Superchat.AmountString}";
                ErrorMessageEvents.RaiseErrorEvent("WARN", nameof(SubathonEventType.YouTubeSuperChat), 
                    message, item.Timestamp.DateTime.ToLocalTime());
                _logger?.LogWarning(message);
            }
            
            SubathonEvent subathonEvent = new();
            subathonEvent.User = user;
            subathonEvent.Currency = $"{currency}".Trim().ToUpper();
            subathonEvent.Value = $"{item.Superchat.AmountValue}";
            subathonEvent.Source = SubathonEventSource.YouTube;
            subathonEvent.EventType = SubathonEventType.YouTubeSuperChat;
            subathonEvent.Id = Utils.CreateGuidFromUniqueString(item.Id);
            subathonEvent.EventTimestamp = item.Timestamp.DateTime.ToLocalTime();
            SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
            return;
        }

        if (item.MembershipDetails != null)
        {
            SubathonEvent subathonEvent = new();
            subathonEvent.Source = SubathonEventSource.YouTube;
            subathonEvent.Id = Utils.CreateGuidFromUniqueString(item.Id);
            subathonEvent.EventTimestamp = item.Timestamp.DateTime.ToLocalTime();
            
            // there can be up to 6 unique membership tiers
            // we will check/store as Meta
            // however due to limitation on New Members not showing tier
            // we treat it as if there is only one for now
            
            var details = item.MembershipDetails;
            string tier = details.HeaderSubtext ?? details.LevelName;
            if (string.IsNullOrEmpty(tier))
                tier = details.LevelName;
            subathonEvent.Value = tier;
            subathonEvent.Currency = "member";
            
            if (tier.ToLower().Contains("new") && details.EventType == MembershipEventType.Unknown)
                details.EventType = MembershipEventType.New;
            
            switch (details.EventType)
            {
                case MembershipEventType.Milestone: // essentially a resub
                    subathonEvent.EventType = SubathonEventType.YouTubeMembership;
                    break;
                case MembershipEventType.GiftPurchase:
                    subathonEvent.EventType = SubathonEventType.YouTubeGiftMembership;
                    user = details.GifterUsername?.Replace("@", "") ?? user;
                    subathonEvent.Amount = details.GiftCount ?? 1;
                    break;
                case MembershipEventType.GiftRedemption:
                    // NEVER PROCESS THIS
                    return;
                case MembershipEventType.New:
                    subathonEvent.EventType = SubathonEventType.YouTubeMembership;
                    break;
                default:
                    subathonEvent.EventType = SubathonEventType.YouTubeMembership;
                    break;
            }
            subathonEvent.User = user;
            
            // temp
            subathonEvent.Value = "DEFAULT";
            
            SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
            return;
        }

        string messagePreview = string.Join("", item.Message.Select(p => p.ToString()));
        if (messagePreview.StartsWith('!'))
            CommandService.ChatCommandRequest(SubathonEventSource.YouTube, messagePreview, user,
                item.IsOwner, item.IsModerator, false,
                item.Timestamp.DateTime.ToLocalTime(), Utils.CreateGuidFromUniqueString(item.Id));
        else if (user.Equals("blerp", StringComparison.OrdinalIgnoreCase))
        {
            BlerpChatService.ParseMessage(messagePreview, SubathonEventSource.YouTube);
        }
    }
    
    private void TryReconnectLoop()
    {
        if (string.IsNullOrWhiteSpace(_ytHandle) || _ytHandle.Trim() == "@")
            return;
        
        _ = Task.Run(ReconnectWithBackoffAsync);
    }
    
    private async Task ReconnectWithBackoffAsync()
    {
        if (await _reconnectState.IsReconnecting())
            return; // already reconnecting
        
        if (string.IsNullOrWhiteSpace(_ytHandle) || _ytHandle.Trim() == "@")
            return;
        
        try
        {
            _reconnectState.Cts?.Cancel();
            _reconnectState.Cts = new CancellationTokenSource();
            var token = _reconnectState.Cts.Token;

            while (!token.IsCancellationRequested && !Running && _ytHandle?.Trim() != "@" && !string.IsNullOrWhiteSpace(_ytHandle))
            {
                if (_reconnectState.Retries >= _reconnectState.MaxRetries)
                {
                    _logger?.LogError(
                        "[YT] Max reconnect retries ({Retries}) reached. Giving up.",
                        _reconnectState.MaxRetries);
                    return;
                }

                _reconnectState.Retries++;

                var delay = _reconnectState.Backoff;

                _logger?.LogWarning(
                    "[YT] Reconnect attempt {Attempt}/{Max} in {Delay}s",
                    _reconnectState.Retries,
                    _reconnectState.MaxRetries,
                    delay.TotalSeconds);

                try
                {
                    await Task.Delay(delay, token);

                    if (!Running && !string.IsNullOrEmpty(_ytHandle))
                    {
                        _ytLiveChat.Start(handle: _ytHandle, overwrite: true);
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "[YT] Reconnect attempt failed");
                }

                // Exponential backoff
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
    
    [ExcludeFromCodeCoverage]
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
    
    [ExcludeFromCodeCoverage]
    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _reconnectState.Dispose();
                _ytLiveChat.Stop();
                _ytLiveChat.InitialPageLoaded -= OnInitialPageLoaded;
                _ytLiveChat.ChatReceived -= OnChatReceived;
                _ytLiveChat.ChatStopped -= OnChatStopped;
                _ytLiveChat.ErrorOccurred -= OnErrorOccurred;
                _ytLiveChat.Dispose();
                _httpClient.Dispose();
            }
            _disposed = true;
        }
    }
    
    public static void SimulateSuperChat(string value = "10.00", string currency = "USD")
    {
        if (!double.TryParse(value, out var val))
            return;

        SubathonEvent subathonEvent = new SubathonEvent
        {
            User = "SYSTEM",
            Currency = currency,
            Value = value,
            Source = SubathonEventSource.Simulated,
            EventType = SubathonEventType.YouTubeSuperChat
        };
        SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
    }
    
    public static void SimulateMembership(string tier = "DEFAULT")
    {
        SubathonEvent subathonEvent = new SubathonEvent
        {
            Source = SubathonEventSource.Simulated,
            Currency = "member",
            EventType = SubathonEventType.YouTubeMembership,
            Value = tier,
            User = "SYSTEM"
        };
        SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
    }

    public static void SimulateGiftMemberships(int amount)
    {
        string tier = "DEFAULT";
        SubathonEvent subathonEvent = new SubathonEvent
        {
            Source = SubathonEventSource.Simulated,
            Currency = "member",
            EventType = SubathonEventType.YouTubeGiftMembership,
            Value = tier,
            User = "SYSTEM",
            Amount = amount
        };
        SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
    } 
}