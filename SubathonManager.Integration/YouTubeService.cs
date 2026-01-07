using YTLiveChat.Contracts;
using YTLiveChat.Contracts.Models;
using YTLiveChat.Contracts.Services;
using YTLiveChat.Services; 
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
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
    
    private CancellationTokenSource? _reconnectCts;
    private Task? _reconnectTask;
    private int _reconnectDelay = 5000;
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
        YouTubeEvents.RaiseYouTubeConnectionUpdate(Running, "None");

        _ytHandle = handle ?? _config.Get("YouTube", "Handle")!;
        if (string.IsNullOrEmpty(_ytHandle) || _ytHandle.Trim() == "@")
            return Running;
        if (!_ytHandle.StartsWith("@")) _ytHandle =  "@" + _ytHandle;
        _logger?.LogInformation("Youtube Service Starting for " + _ytHandle);
        
        _ytLiveChat.Start(handle: _ytHandle, overwrite: true);
        return true;
    }

    private void OnInitialPageLoaded(object? sender, InitialPageLoadedEventArgs e)
    {
        Running = true;
        YouTubeEvents.RaiseYouTubeConnectionUpdate(Running, _ytHandle!);
        _logger?.LogInformation($"Successfully loaded YouTube Live ID: {e.LiveId}");
        _reconnectCts?.Cancel();
        _reconnectDelay = 5000;
    }
    private void OnChatStopped(object? sender, ChatStoppedEventArgs e)
    {
        Running = false;
        _logger?.LogWarning("YT Chat stopped");
        YouTubeEvents.RaiseYouTubeConnectionUpdate(Running, _ytHandle!);
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

        YouTubeEvents.RaiseYouTubeConnectionUpdate(Running, _ytHandle!);
        _reconnectDelay = 60 * 1000;
    }
    
    private void OnChatReceived(object? sender, ChatReceivedEventArgs e)
    {
        if (!Running)
            YouTubeEvents.RaiseYouTubeConnectionUpdate(true, _ytHandle!);
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
                item.Timestamp.DateTime.ToLocalTime());
        else if (user.Equals("blerp", StringComparison.OrdinalIgnoreCase))
        {
            BlerpChatService.ParseMessage(messagePreview, SubathonEventSource.YouTube);
        }
    }
    
    private void TryReconnectLoop()
    {
        if (string.IsNullOrEmpty(_ytHandle) || _ytHandle.Trim() == "@")
            return;

        _reconnectCts?.Cancel();
        _reconnectCts = new CancellationTokenSource();
        var token = _reconnectCts.Token;

        _reconnectTask = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested && !Running)
            {
                try
                {
                    _logger?.LogDebug($"[YT] Attempting reconnect in {_reconnectDelay / 1000}s...");
                    await Task.Delay(_reconnectDelay, token);

                    if (string.IsNullOrEmpty(_ytHandle))
                        break;

                    if (!Running)
                        Start(_ytHandle);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning($"Reconnect attempt failed: {ex.Message}");
                }
            }

            _logger?.LogDebug("Reconnect loop ended.");
        }, token);
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
                _reconnectCts?.Cancel();      
                try
                {
                    _reconnectTask?.Wait(1000);
                }
                catch { /**/ }
                try
                {
                    _reconnectCts?.Dispose();
                }
                catch { /**/ }
                _reconnectCts = null;
                _reconnectTask = null;
                
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