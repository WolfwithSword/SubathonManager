using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using TwitchLib.Api;
using TwitchLib.Api.Core.Enums;
using TwitchLib.Client;
using TwitchLib.Client.Models;
using TwitchLib.EventSub.Core.EventArgs.Channel;
using TwitchLib.EventSub.Websockets.Core.Models;
using TwitchLib.EventSub.Websockets;
using SubathonManager.Core;
using SubathonManager.Core.Models;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Services;
using TwitchLib.Client.Events;
using TwitchLib.EventSub.Core.EventArgs.Stream;
using TwitchLib.EventSub.Websockets.Core.EventArgs;

namespace SubathonManager.Integration;

public class TwitchService
{
    private readonly string _callbackUrl;

    private TwitchAPI? _api = null!;
    private TwitchClient? _chat = null!;
    private EventSubWebsocketClient? _eventSub = null!;
    private static int _hypeTrainLevel = 0;

    private readonly ILogger? _logger;
    private readonly IConfig _config;

    private readonly string _tokenFile = Path.GetFullPath(Path.Combine(string.Empty
        , "data/twitch_token.json"));

    private string? AccessToken { get; set; }
    public string? UserName { get; private set; } = string.Empty;
    private string? UserId { get; set; }

    public TwitchService(ILogger<TwitchService>? logger, IConfig config)
    {
        int port = 14041; // hardcode cause of app callback url
        _callbackUrl = $"http://localhost:{port}/auth/twitch/callback/";
        _logger = logger;
        _config = config;
    }

    public bool HasTokenFile()
    {
        return File.Exists(_tokenFile);
    }

    public void RevokeTokenFile()
    {
        if (HasTokenFile()) File.Delete(_tokenFile);
        AccessToken = string.Empty;
    }

    public async Task<bool> ValidateTokenAsync()
    {
        if (!HasTokenFile())
            return false;

        var json = await File.ReadAllTextAsync(_tokenFile);
        var data = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        var token = data?["access_token"];

        var api = new TwitchAPI();
        try
        {
            var validation = await api.Auth.ValidateAccessTokenAsync(token);

            if (validation.ClientId != Config.TwitchClientId)
                return false;

            _logger?.LogInformation($"Twitch Token Valid for Scopes: {string.Join(',', validation.Scopes)}");
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Twitch Token Validation Error");
            ErrorMessageEvents.RaiseErrorEvent("ERROR", nameof(SubathonEventSource.Twitch),
                "Twitch Token could not be validated", DateTime.Now.ToLocalTime());
            return false;
        }
    }

    
    [ExcludeFromCodeCoverage]
    public async Task InitializeAsync()
    {
        if (HasTokenFile())
        {
            var json = await File.ReadAllTextAsync(_tokenFile);
            var data = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            AccessToken = data?["access_token"];
        }

        if (string.IsNullOrEmpty(AccessToken))
        {
            await StartOAuthFlowAsync();
        }

        try
        {
            await InitializeApiAsync();
            _logger?.LogDebug("Twitch Initialized API");
            await InitializeChatAsync();
            _logger?.LogDebug("Twitch Initialized Chat");
            await InitializeEventSubAsync();
            _logger?.LogDebug("Twitch Initialized EventSub");
            TwitchEvents.RaiseTwitchConnected();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "TwitchService Initialization Error");
            ErrorMessageEvents.RaiseErrorEvent("ERROR", nameof(SubathonEventSource.Twitch),
                $"Error initializing Twitch Service: {ex.Message}. " +
                $"Please try reconnecting twitch or restarting the application", DateTime.Now);
        }
    }

    
    [ExcludeFromCodeCoverage]
    private async Task StartOAuthFlowAsync()
    {
        string scopes = string.Join('+', new[]
        {
            "user:read:email",
            "bits:read",
            "channel:read:subscriptions",
            "channel:read:redemptions",
            "moderator:read:followers",
            "channel:read:charity",
            "chat:read",
            "chat:edit",
            "channel:read:hype_train"
        });

        var oauthUrl = $"https://id.twitch.tv/oauth2/authorize" +
                       $"?client_id={Config.TwitchClientId}" +
                       $"&redirect_uri={_callbackUrl}" +
                       $"&response_type=token" +
                       $"&scope={scopes}";

        _logger?.LogDebug("Opening Twitch OAuth...");
        Process.Start(new ProcessStartInfo
        {
            FileName = oauthUrl,
            UseShellExecute = true
        });


        if (!HttpListener.IsSupported)
        {
            _logger?.LogError("HTTP Listener not supported. Cannot start Twitch OAuth flow.");
            return;
        }

        // temp listener for callback
        var listener = new HttpListener();

        try
        {
            listener.Prefixes.Add(_callbackUrl.EndsWith("/") ? _callbackUrl : _callbackUrl + "/");
            var tokenUrl = _callbackUrl.Replace("auth/twitch/callback", "token");
            listener.Prefixes.Add(tokenUrl.EndsWith("/") ? tokenUrl : tokenUrl + "/");
            listener.Start();
            _logger?.LogDebug("Waiting for Twitch auth...");

            var context = await listener.GetContextAsync();
            var response = context.Response;

            string html = $"""
                               <html>
                                   <body>
                                       <h2>Waiting for authorization to complete...</h2>
                                       <script>
                                           const hash = window.location.hash.substring(1);
                                           fetch('/token?' + hash)
                                               .then(() => document.body.innerHTML = 'Authorization complete. You can close this window.');
                                       </script>
                                   </body>
                               </html>
                           """;
            var buffer = System.Text.Encoding.UTF8.GetBytes(html);
            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            response.Close();

            var tokenContext = await listener.GetContextAsync();
            var query = tokenContext.Request.Url!.Query.TrimStart('?');
            var parts = System.Web.HttpUtility.ParseQueryString(query);

            AccessToken = parts["access_token"];
            var tokenResponse = tokenContext.Response;
            tokenResponse.StatusCode = 200;
            await tokenResponse.OutputStream.WriteAsync(System.Text.Encoding.UTF8.GetBytes("OK"));
            tokenResponse.Close();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, $"TwitchService Authentication Failure: {ex.Message}");
        }

        try
        {
            listener.Stop();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "TwitchService Authentication Listener Shutdown Error");
        }

        if (!string.IsNullOrEmpty(AccessToken))
        {
            await File.WriteAllTextAsync(_tokenFile,
                JsonSerializer.Serialize(new { access_token = AccessToken }));
        }
    }

    
    [ExcludeFromCodeCoverage]
    private async Task InitializeApiAsync()
    {
        _api = new TwitchAPI();
        _api.Settings.ClientId = Config.TwitchClientId;
        _api.Settings.AccessToken = AccessToken;

        var user = (await _api.Helix.Users.GetUsersAsync()).Users.FirstOrDefault();
        if (user != null)
        {
            UserName = user.DisplayName;
            UserId = user.Id;
            _logger?.LogDebug($"Authenticated as {UserName}");
        }
    }

    
    [ExcludeFromCodeCoverage]
    private async Task InitializeChatAsync()
    {
        var credentials = new ConnectionCredentials(UserName, $"oauth:{AccessToken}");
        _chat = new TwitchClient();
        try
        {
            _chat.Initialize(credentials, channel: UserName);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, ex.Message);
        }

        _chat.OnMessageReceived += HandleMessageCmdReceived;
        await Task.Run(() => _chat.Connect());
    }

    private void HandleMessageCmdReceived(object? s, OnMessageReceivedArgs e)
    {
        string message = e.ChatMessage.Message;
        bool isMod = e.ChatMessage.IsModerator;
        bool isBroadcaster = e.ChatMessage.IsBroadcaster;
        bool isVip = e.ChatMessage.IsVip;

        if (!string.IsNullOrWhiteSpace(message) && message.StartsWith('!'))
        {
            CommandService.ChatCommandRequest(SubathonEventSource.Twitch, message,
                e.ChatMessage.Username, // DisplayName
                isBroadcaster, isMod, isVip, DateTime.Now);
        }
        else if (e.ChatMessage.DisplayName.Equals("blerp", StringComparison.InvariantCultureIgnoreCase))
        {
            BlerpChatService.ParseMessage(e.ChatMessage.Message, SubathonEventSource.Twitch);
        }
    }

    
    [ExcludeFromCodeCoverage]
    private async Task InitializeEventSubAsync()
    {
        _eventSub = new EventSubWebsocketClient();

        _eventSub.WebsocketConnected += HandleEventSubConnect;
        _eventSub.WebsocketReconnected += HandleEventSubReconnect;
        _eventSub.WebsocketDisconnected += HandleEventSubDisconnect;

        _eventSub.StreamOnline += HandleChannelOnline;
        _eventSub.StreamOffline += HandleChannelOffline;
        _eventSub.ChannelFollow += HandleChannelFollow;
        _eventSub.ChannelSubscriptionGift += HandleSubGift;
        _eventSub.ChannelSubscribe += HandleChannelSubscribe;
        _eventSub.ChannelSubscriptionMessage += HandleSubscriptionMsg;
        _eventSub.ChannelBitsUse += HandleBitsUse;
        _eventSub.ChannelRaid += HandleChannelRaid;
        _eventSub.ChannelHypeTrainBeginV2 += HandleHypeTrainBeginV2;
        _eventSub.ChannelHypeTrainProgressV2 += HandleHypeTrainProgressV2;
        _eventSub.ChannelHypeTrainEndV2 += HandleHypeTrainEndV2;
        _eventSub.ChannelCharityCampaignDonate += HandleCharityEvent;

        await _eventSub.ConnectAsync();
    }

    
    [ExcludeFromCodeCoverage]
    private async Task HandleEventSubConnect(object? s, WebsocketConnectedArgs e)
    {
        _logger?.LogInformation("Connected to EventSub WebSocket, session ID: " + _eventSub?.SessionId);
        if (!e.IsRequestedReconnect)
        {
            var eventTypes = new[]
            {
                "stream.offline",
                "stream.online",
                "channel.follow",
                "channel.subscribe",
                "channel.cheer",
                "channel.bits.use",
                "channel.raid",
                "channel.subscription.gift",
                "channel.subscription.message",
                "channel.hype_train.begin",
                "channel.hype_train.progress",
                "channel.hype_train.end",
                "channel.charity_campaign.donate"
            };

            foreach (var type in eventTypes)
            {
                try
                {

                    var condition = new Dictionary<string, string>
                    {
                        { "broadcaster_user_id", UserId! }, { "to_broadcaster_user_id", UserId! },
                        { "moderator_user_id", UserId! }, { "user_id", UserId! }
                    };
                    var x = await _api!.Helix.EventSub.CreateEventSubSubscriptionAsync(type,
                        type.Contains("follow") || type.Contains("hype_train") ? "2" : "1", condition,
                        EventSubTransportMethod.Websocket, _eventSub?.SessionId,
                        clientId: Config.TwitchClientId,
                        accessToken: AccessToken);

                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, $"Failed to subscribe to {type}: {ex.Message}");
                    RevokeTokenFile();
                }
            }
        }
    }


    private Task HandleEventSubReconnect(object? s, WebsocketReconnectedArgs e)
    {
        _logger?.LogInformation("Reconnected EventSub WebSocket.");
        return Task.CompletedTask;
    }
    
    private Task HandleEventSubDisconnect(object? s, WebsocketDisconnectedArgs e)
    {
        // todo try ReconnectAsync and exponential backoff task delay
        _logger?.LogWarning("Disconnected EventSub WebSocket.");
        return Task.CompletedTask;
    }

    private Task HandleChannelOnline(object? s, StreamOnlineArgs e)
    {
        if (bool.TryParse(_config.Get("Twitch", "ResumeOnStart", "false"),
                out var resumeOnStart) && resumeOnStart)
        {
            SubathonEvent subathonEvent = new SubathonEvent
            {
                EventTimestamp = DateTime.Now - TimeSpan.FromSeconds(1),
                Command = SubathonCommandType.Resume,
                Value = $"{SubathonCommandType.Resume}",
                SecondsValue = 0,
                PointsValue = 0,
                Source = SubathonEventSource.Command,
                EventType = SubathonEventType.Command,
                User = "AUTO"
            };
            SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
        }

        if (bool.TryParse(_config.Get("Twitch", "UnlockOnStart", "false"),
                out var unlockOnStart) && unlockOnStart)
        {
            SubathonEvent subathonEvent = new SubathonEvent
            {
                EventTimestamp = DateTime.Now - TimeSpan.FromSeconds(1),
                Command = SubathonCommandType.Unlock,
                Value = $"{SubathonCommandType.Unlock}",
                SecondsValue = 0,
                PointsValue = 0,
                Source = SubathonEventSource.Command,
                EventType = SubathonEventType.Command,
                User = "AUTO"
            };
            SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
        }

        return Task.CompletedTask;
    }

    private Task HandleChannelOffline(object? s, StreamOfflineArgs e)
    {
        if (bool.TryParse(_config.Get("Twitch", "PauseOnEnd", "false"),
                out var pauseOnEnd) && pauseOnEnd)
        {
            SubathonEvent subathonEvent = new SubathonEvent
            {
                EventTimestamp = DateTime.Now - TimeSpan.FromSeconds(1),
                Command = SubathonCommandType.Pause,
                Value = $"{SubathonCommandType.Pause}",
                SecondsValue = 0,
                PointsValue = 0,
                Source = SubathonEventSource.Command,
                EventType = SubathonEventType.Command,
                User = "AUTO"
            };
            SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
        }

        if (bool.TryParse(_config.Get("Twitch", "LockOnEnd", "false"),
                out var lockOnEnd) && lockOnEnd)
        {
            SubathonEvent subathonEvent = new SubathonEvent
            {
                EventTimestamp = DateTime.Now - TimeSpan.FromSeconds(1),
                Command = SubathonCommandType.Lock,
                Value = $"{SubathonCommandType.Lock}",
                SecondsValue = 0,
                PointsValue = 0,
                Source = SubathonEventSource.Command,
                EventType = SubathonEventType.Command,
                User = "AUTO"
            };
            SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
        }

        return Task.CompletedTask;
    }
    
    private Task HandleChannelFollow(object? s, ChannelFollowArgs e)
    {
        // Console.WriteLine($"New follow from {e.Payload.Event.UserName}");

        var eventMeta = e.Metadata as WebsocketEventSubMetadata;
        Guid.TryParse(eventMeta!.MessageId, out var mId);
        if (mId == Guid.Empty) mId = Guid.NewGuid();
        SubathonEvent subathonEvent = new SubathonEvent
        {
            Id = mId,
            Source = SubathonEventSource.Twitch,
            EventType = SubathonEventType.TwitchFollow,
            User = e.Payload.Event.UserName,
            EventTimestamp =
                eventMeta.MessageTimestamp.ToLocalTime() // or e.Payload.Event.FollowedAt and change type
        };
        SubathonEvents.RaiseSubathonEventCreated(subathonEvent);

        return Task.CompletedTask;
    }
    
    private Task HandleSubGift(object? s, ChannelSubscriptionGiftArgs e)
    {
        var eventMeta = e.Metadata as WebsocketEventSubMetadata;
        Guid.TryParse(eventMeta!.MessageId, out var mId);
        if (mId == Guid.Empty) mId = Guid.NewGuid();
        SubathonEvent subathonEvent = new SubathonEvent
        {
            Id = mId,
            Source = SubathonEventSource.Twitch,
            Currency = "sub",
            EventType = SubathonEventType.TwitchGiftSub,
            Value = e.Payload.Event.Tier,
            User = e.Payload.Event.UserName,
            Amount = e.Payload.Event.Total,
            EventTimestamp = eventMeta.MessageTimestamp.ToLocalTime()
        };
        SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
        return Task.CompletedTask;
    }

    private Task HandleChannelSubscribe(object? s, ChannelSubscribeArgs e)
    {
        if (e.Payload.Event.IsGift)
            return Task.CompletedTask;

        var eventMeta = e.Metadata as WebsocketEventSubMetadata;
        Guid.TryParse(eventMeta!.MessageId, out var mId);
        if (mId == Guid.Empty) mId = Guid.NewGuid();
        SubathonEvent subathonEvent = new SubathonEvent
        {
            Id = mId,
            Source = SubathonEventSource.Twitch,
            Currency = "sub",
            EventType = SubathonEventType.TwitchSub,
            Value = e.Payload.Event.Tier,
            User = e.Payload.Event.UserName,
            EventTimestamp = eventMeta.MessageTimestamp.ToLocalTime()
        };
        SubathonEvents.RaiseSubathonEventCreated(subathonEvent);

        return Task.CompletedTask;
    }
    
    private Task HandleSubscriptionMsg(object? s, ChannelSubscriptionMessageArgs e) 
    {
        int duration = e.Payload.Event.DurationMonths; // TODO Do we want to take this into account and multiply?

        var eventMeta = e.Metadata as WebsocketEventSubMetadata;
        Guid.TryParse(eventMeta!.MessageId, out var mId);
        if (mId == Guid.Empty) mId = Guid.NewGuid();
        SubathonEvent subathonEvent = new SubathonEvent
        {
            Id = mId,
            Source = SubathonEventSource.Twitch,
            Currency = "sub",
            EventType = SubathonEventType.TwitchSub,
            Value = e.Payload.Event.Tier,
            User = e.Payload.Event.UserName,
            EventTimestamp = eventMeta.MessageTimestamp.ToLocalTime()
        };
        SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
        return Task.CompletedTask;
    }
    
    private Task HandleBitsUse(object? s, ChannelBitsUseArgs e)  
    {
        var eventMeta = e.Metadata as WebsocketEventSubMetadata;
        Guid.TryParse(eventMeta!.MessageId, out var mId);
        if (mId == Guid.Empty) mId = Guid.NewGuid();
        SubathonEvent subathonEvent = new SubathonEvent
        {
            Id = mId,
            Source = SubathonEventSource.Twitch,
            EventType = SubathonEventType.TwitchCheer,
            User = e.Payload.Event.UserName,
            Currency = "bits",
            Value = e.Payload.Event.Bits.ToString(),
            EventTimestamp = eventMeta.MessageTimestamp.ToLocalTime()
        };
        SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
        if (e.Payload.Event.Type.ToLower() != "cheer")
        {
            _logger?.LogInformation($"TwitchCheer Event {subathonEvent.Id} " +
                                    $"source was: {e.Payload.Event.Type} {e.Payload.Event.PowerUp?.Type}");
        }

        return Task.CompletedTask;
    }

    private Task HandleChannelRaid(object? s, ChannelRaidArgs e)
    {
        var eventMeta = e.Metadata as WebsocketEventSubMetadata;
        Guid.TryParse(eventMeta!.MessageId, out var mId);
        if (mId == Guid.Empty) mId = Guid.NewGuid();
        SubathonEvent subathonEvent = new SubathonEvent
        {
            Id = mId,
            Source = SubathonEventSource.Twitch,
            EventType = SubathonEventType.TwitchRaid,
            User = e.Payload.Event.FromBroadcasterUserName,
            Value = e.Payload.Event.Viewers.ToString(),
            EventTimestamp = eventMeta.MessageTimestamp.ToLocalTime()
        };
        SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
        return Task.CompletedTask;
    }

    private Task HandleHypeTrainBeginV2(object? s, ChannelHypeTrainBeginV2Args e)
    {
        var eventMeta = e.Metadata as WebsocketEventSubMetadata;
        Guid.TryParse(eventMeta!.MessageId, out var mId);
        if (mId == Guid.Empty) mId = Guid.NewGuid();
        SubathonEvent subathonEvent = new SubathonEvent
        {
            Id = mId,
            Source = SubathonEventSource.Twitch,
            EventType = SubathonEventType.TwitchHypeTrain,
            User = e.Payload.Event.BroadcasterUserName,
            Amount = e.Payload.Event.Level,
            Value = "start",
            EventTimestamp = eventMeta.MessageTimestamp.ToLocalTime()
        };
        SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
        _hypeTrainLevel = 1;
        return Task.CompletedTask;
    }
    
    private Task HandleHypeTrainProgressV2(object? s, ChannelHypeTrainProgressV2Args e)
    {
        if (e.Payload.Event.Level <= _hypeTrainLevel) return Task.CompletedTask;

        var eventMeta = e.Metadata as WebsocketEventSubMetadata;
        Guid.TryParse(eventMeta!.MessageId, out var mId);
        if (mId == Guid.Empty) mId = Guid.NewGuid();
        SubathonEvent subathonEvent = new SubathonEvent
        {
            Id = mId,
            Source = SubathonEventSource.Twitch,
            EventType = SubathonEventType.TwitchHypeTrain,
            User = e.Payload.Event.BroadcasterUserName,
            Amount = e.Payload.Event.Level,
            Value = "progress",
            EventTimestamp = eventMeta.MessageTimestamp.ToLocalTime()
        };
        SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
        _hypeTrainLevel = subathonEvent.Amount;
        return Task.CompletedTask;
    }
    
    private Task HandleHypeTrainEndV2(object? s, ChannelHypeTrainEndV2Args e)
    {
        var eventMeta = e.Metadata as WebsocketEventSubMetadata;
        Guid.TryParse(eventMeta!.MessageId, out var mId);
        if (mId == Guid.Empty) mId = Guid.NewGuid();
        SubathonEvent subathonEvent = new SubathonEvent
        {
            Id = mId,
            Source = SubathonEventSource.Twitch,
            EventType = SubathonEventType.TwitchHypeTrain,
            User = e.Payload.Event.BroadcasterUserName,
            Amount = e.Payload.Event.Level,
            Value = "end",
            EventTimestamp = eventMeta.MessageTimestamp.ToLocalTime()
        };
        SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
        _hypeTrainLevel = 0;
        return Task.CompletedTask;
    }
    
    private Task HandleCharityEvent(object? s, ChannelCharityCampaignDonateArgs e )
    {
        var eventMeta = e.Metadata as WebsocketEventSubMetadata;
        Guid.TryParse(eventMeta!.MessageId, out var mId);
        if (mId == Guid.Empty) mId = Guid.NewGuid();
        SubathonEvent subathonEvent = new SubathonEvent
        {
            Id = mId,
            Source = SubathonEventSource.Twitch,
            EventType = SubathonEventType.TwitchCharityDonation,
            User = e.Payload.Event.UserName,
            Value = Math.Round(
                e.Payload.Event.Amount.Value 
                / (decimal)Math.Pow(10, e.Payload.Event.Amount.DecimalPlaces),
                2
            ).ToString("0.00"),
            Currency = e.Payload.Event.Amount.Currency,
            EventTimestamp = eventMeta.MessageTimestamp.ToLocalTime()
        };
        SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // api has no disconnect? 
        OnTeardown();
        if (_chat != null) _chat.Disconnect();
        if (_eventSub != null) await _eventSub.DisconnectAsync();
    }

    public static void SimulateRaid(int viewers=50)
    {
        SubathonEvent subathonEvent = new SubathonEvent
        {
            Source = SubathonEventSource.Simulated,
            EventType = SubathonEventType.TwitchRaid,
            User = "SYSTEM",
            Value = $"{viewers}"
        };
        SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
    }
    
    public static void SimulateCheer(int bitsCount=100)
    {
        SubathonEvent subathonEvent = new SubathonEvent
        {
            Source = SubathonEventSource.Simulated,
            EventType = SubathonEventType.TwitchCheer,
            User = "SYSTEM",
            Currency = "bits",
            Value = $"{bitsCount}"
        };
        SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
    }

    public static void SimulateSubscription(string tier)
    {
        if (tier != "1000" && tier != "2000" && tier != "3000") return;
        
        SubathonEvent subathonEvent = new SubathonEvent
        {
            Source = SubathonEventSource.Simulated,
            Currency = "sub",
            EventType = SubathonEventType.TwitchSub,
            Value = tier,
            User = "SYSTEM"
        };
        SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
    }

    public static void SimulateGiftSubscriptions(string tier, int amount)
    {
        if (tier != "1000" && tier != "2000" && tier != "3000") return;

        SubathonEvent subathonEvent = new SubathonEvent
        {
            Source = SubathonEventSource.Simulated,
            Currency = "sub",
            EventType = SubathonEventType.TwitchGiftSub,
            Value = tier,
            User = "SYSTEM",
            Amount = amount
        };
        SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
    }    
    public static void SimulateFollow()
    {
        SubathonEvent subathonEvent = new SubathonEvent
        {
            Source = SubathonEventSource.Simulated,
            EventType = SubathonEventType.TwitchFollow,
            User = "SYSTEM",
        };
        SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
    }
    public static void SimulateCharityDonation(string value = "10.00", string currency = "USD")
    {
        SubathonEvent subathonEvent = new SubathonEvent
        {
            Source = SubathonEventSource.Simulated,
            EventType = SubathonEventType.TwitchCharityDonation,
            Value = value,
            Currency = currency,
            User = "SYSTEM",
        };
        SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
    }
    
    public static void SimulateHypeTrainStart()
    {
        SubathonEvent subathonEvent = new SubathonEvent
        {
            Source = SubathonEventSource.Simulated,
            EventType = SubathonEventType.TwitchHypeTrain,
            Value = "start",
            Amount = 1,
            User = "SYSTEM",
        };
        _hypeTrainLevel = 1;
        SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
    }
    
    public static void SimulateHypeTrainProgress(int level = 7)
    {
        if (level <= _hypeTrainLevel) return;
        SubathonEvent subathonEvent = new SubathonEvent
        {
            Source = SubathonEventSource.Simulated,
            EventType = SubathonEventType.TwitchHypeTrain,
            Value = "progress",
            Amount = level,
            User = "SYSTEM",
        };
        _hypeTrainLevel = level;
        SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
    }
    
    public static void SimulateHypeTrainEnd(int level = 10)
    {
        SubathonEvent subathonEvent = new SubathonEvent
        {
            Source = SubathonEventSource.Simulated,
            EventType = SubathonEventType.TwitchHypeTrain,
            Value = "end",
            Amount = level,
            User = "SYSTEM",
        };
        _hypeTrainLevel = 0;
        SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
    }

    private void OnTeardown()
    {
        if (_chat != null)
        {
            _chat.OnMessageReceived -= HandleMessageCmdReceived;
        }

        if (_eventSub != null)
        {
            _eventSub.WebsocketConnected -= HandleEventSubConnect;
            _eventSub.WebsocketReconnected -= HandleEventSubReconnect;
            _eventSub.WebsocketDisconnected -= HandleEventSubDisconnect;

            _eventSub.StreamOnline -= HandleChannelOnline;
            _eventSub.StreamOffline -= HandleChannelOffline;
            _eventSub.ChannelFollow -= HandleChannelFollow;
            _eventSub.ChannelSubscriptionGift -= HandleSubGift;
            _eventSub.ChannelSubscribe -= HandleChannelSubscribe;
            _eventSub.ChannelSubscriptionMessage -= HandleSubscriptionMsg;
            _eventSub.ChannelBitsUse -= HandleBitsUse;
            _eventSub.ChannelRaid -= HandleChannelRaid;
            _eventSub.ChannelHypeTrainBeginV2 -= HandleHypeTrainBeginV2;
            _eventSub.ChannelHypeTrainProgressV2 -= HandleHypeTrainProgressV2;
            _eventSub.ChannelHypeTrainEndV2 -= HandleHypeTrainEndV2;
            _eventSub.ChannelCharityCampaignDonate -= HandleCharityEvent;
        }
    }
}

