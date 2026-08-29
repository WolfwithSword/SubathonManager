using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;
using SubathonManager.Core.Security;
using SubathonManager.Core.Security.Interfaces;
using SubathonManager.Services;
using TwitchLib.Api;
using TwitchLib.Api.Auth;
using TwitchLib.Api.Core.Enums;
using TwitchLib.Api.Helix.Models.EventSub;
using TwitchLib.Api.Helix.Models.Users.GetUsers;
using TwitchLib.Client;
using TwitchLib.Client.Events;
using TwitchLib.Client.Models;
using TwitchLib.EventSub.Core.EventArgs.Channel;
using TwitchLib.EventSub.Core.EventArgs.Stream;
using TwitchLib.EventSub.Websockets;
using TwitchLib.EventSub.Websockets.Core.EventArgs;
using TwitchLib.EventSub.Websockets.Core.Models;

// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.Integration;

public class TwitchService(
    ILogger<TwitchService>? logger,
    IConfig config,
    ISecureStorage secureStorage,
    ITimerService? timerService = null)
    : IDisposable, IAppService {
    private static int _hypeTrainLevel;

    private readonly Utils.ServiceReconnectState _chatReconnect =
        new(TimeSpan.FromSeconds(5), 200, TimeSpan.FromMinutes(2));

    private readonly Utils.ServiceReconnectState _eventSubReconnect =
        new(TimeSpan.FromSeconds(2.5), 200, TimeSpan.FromMinutes(5));

    private readonly TimeSpan _hypeTrainLevelDuration = TimeSpan.FromSeconds(5 * 60 + 15); // 5m + buffer time
    internal readonly string _oAuthURl = "https://oauth.subathonmanager.app/auth/twitch/login";
    private TwitchAPI? _api;
    private TwitchClient? _chat;
    private bool _disposed;
    private EventSubWebsocketClient? _eventSub;
    private volatile bool _isConnected;

    private DateTime _lastChatDisconnectLog = DateTime.MinValue;

    internal Uri? EventSubUrl = null;
    internal string? Login = string.Empty;

    internal Action<string> OpenBrowser =
        url => Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });

    public string? UserName { get; private set; } = string.Empty;
    private string? UserId { get; set; }

    private string? AccessToken => secureStorage.GetOrDefault(StorageKeys.TwitchAccessToken, string.Empty);

    [ExcludeFromCodeCoverage]
    public async Task StartAsync(CancellationToken ct = default) {
        if (HasTokenFile()) {
            bool tokenValid = await ValidateTokenAsync();
            if (!tokenValid) {
                RevokeTokenFile();
                logger?.LogWarning("Twitch token expired - deleting token file");
            }
            else {
                logger?.LogInformation("Twitch Service starting up...");
                await InitializeAsync(ct);
            }
        }
    }

    public async Task StopAsync(CancellationToken ct = default) {
        // api has no disconnect? 
        OnTeardown();
        if (_chat != null) await _chat.DisconnectAsync();
        if (_eventSub != null) await _eventSub.DisconnectAsync();

        IntegrationEvents.RaiseConnectionUpdate(new IntegrationConnection {
            Source = SubathonEventSource.Twitch,
            Service = "API",
            Name = UserName ?? "",
            Status = false,
            Configured = HasTokenFile()
        });
        IntegrationEvents.RaiseConnectionUpdate(new IntegrationConnection {
            Source = SubathonEventSource.Twitch,
            Service = "EventSub",
            Name = UserName ?? "",
            Status = false,
            Configured = HasTokenFile()
        });
        IntegrationEvents.RaiseConnectionUpdate(new IntegrationConnection {
            Source = SubathonEventSource.Twitch,
            Service = "Chat",
            Name = UserName ?? "",
            Status = false,
            Configured = HasTokenFile()
        });
    }

    public void Dispose() {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    public bool HasTokenFile() {
        return secureStorage.Exists(StorageKeys.TwitchAccessToken) &&
               !string.IsNullOrWhiteSpace(AccessToken);
    }

    public void RevokeTokenFile() {
        secureStorage.Delete(StorageKeys.TwitchAccessToken);
    }

    public async Task<bool> ValidateTokenAsync() {
        if (!HasTokenFile())
            return false;
        var api = new TwitchAPI();
        try {
            ValidateAccessTokenResponse? validation = await api.Auth.ValidateAccessTokenAsync(AccessToken);

            if (validation.ClientId != Config.TwitchClientId)
                return false;

            logger?.LogInformation($"Twitch Token Valid for Scopes: {string.Join(',', validation.Scopes)}");
            return true;
        }
        catch (HttpRequestException ex) {
            logger?.LogError(ex, "Could not validate token. Internet connection may be down.");
            return true;
        }
        catch (Exception ex) {
            logger?.LogError(ex, "Twitch Token Validation Error");
            ErrorMessageEvents.RaiseErrorEvent("ERROR", nameof(SubathonEventSource.Twitch),
                "Twitch Token could not be validated", DateTime.Now.ToLocalTime());
            return false;
        }
    }

    [ExcludeFromCodeCoverage]
    public async Task InitializeAsync(CancellationToken ct = default) {
        if (string.IsNullOrEmpty(AccessToken)) await StartOAuthFlowAsync();

        try {
            await InitializeApiAsync();
            logger?.LogDebug("Twitch Initialized API");
            await InitializeChatAsync();
            logger?.LogDebug("Twitch Initialized Chat");
            await InitializeEventSubAsync();
            logger?.LogDebug("Twitch Initialized EventSub");
        }
        catch (Exception ex) {
            logger?.LogError(ex, "TwitchService Initialization Error");
            ErrorMessageEvents.RaiseErrorEvent("ERROR", nameof(SubathonEventSource.Twitch),
                $"Error initializing Twitch Service: {ex.Message}. " +
                $"Please try reconnecting twitch or restarting the application", DateTime.Now);
        }
    }

    private async Task StartOAuthFlowAsync() {
        Utils.PendingOAuthCallback = null;
        logger?.LogDebug("Opening Twitch OAuth...");
        OpenBrowser(_oAuthURl);
        string? token = await WaitForProtocolCallbackAsync();
        if (!string.IsNullOrWhiteSpace(token)) secureStorage.Set(StorageKeys.TwitchAccessToken, token);
    }

    private async Task<string?> WaitForProtocolCallbackAsync(CancellationToken ct = default) {
        DateTime timeout = DateTime.Now.AddMinutes(15);
        while (DateTime.Now < timeout && !ct.IsCancellationRequested) {
            OAuthCallback? cb = Utils.PendingOAuthCallback;
            if (cb?.Provider == "twitch" && !string.IsNullOrEmpty(cb.AccessToken)) {
                logger?.LogInformation("Twitch OAuth Callback received");
                string? token = cb.AccessToken;
                Utils.PendingOAuthCallback = null;
                return token;
            }

            await Task.Delay(250, ct);
        }

        return null;
    }

    [ExcludeFromCodeCoverage]
    private async Task InitializeApiAsync() {
        _api = new TwitchAPI {
            Settings = {
                ClientId = Config.TwitchClientId,
                AccessToken = AccessToken
            }
        };

        User? user = (await _api.Helix.Users.GetUsersAsync()).Users.FirstOrDefault();
        if (user != null) {
            UserName = user.DisplayName;
            Login = user.Login;
            UserId = user.Id;
            logger?.LogDebug($"Authenticated as {UserName}");

            IntegrationEvents.RaiseConnectionUpdate(new IntegrationConnection {
                Source = SubathonEventSource.Twitch,
                Service = "API",
                Name = UserName!,
                Status = true
            });
        }
        else {
            Login = string.Empty;
            IntegrationEvents.RaiseConnectionUpdate(new IntegrationConnection {
                Source = SubathonEventSource.Twitch,
                Service = "API",
                Name = "",
                Status = false,
                Configured = HasTokenFile()
            });
        }
    }


    [ExcludeFromCodeCoverage]
    private async Task InitializeChatAsync() {
        _chatReconnect.Reset();
        var credentials = new ConnectionCredentials(UserName!, $"oauth:{AccessToken}", disableUsernameCheck: true);
        _chat = new TwitchClient();

        _chat.OnMessageReceived += HandleMessageCmdReceived;
        _chat.OnDisconnected += HandleChatDisconnect;
        _chat.OnReconnected += HandleChatReconnect;
        _chat.OnConnected += HandleChatConnect;

        try {
            _chat.Initialize(credentials, UserName);
            logger?.LogDebug("[Twitch] Authenticated Chat as {UserName}", UserName);
        }
        catch (Exception ex) {
            logger?.LogError(ex, ex.Message);
            IntegrationEvents.RaiseConnectionUpdate(new IntegrationConnection {
                Source = SubathonEventSource.Twitch,
                Service = "Chat",
                Name = UserName!,
                Status = false,
                Configured = HasTokenFile()
            });
        }

        await _chat.ConnectAsync();
    }

    private Task HandleChatConnect(object? sender, OnConnectedEventArgs e) {
        IntegrationEvents.RaiseConnectionUpdate(new IntegrationConnection {
            Source = SubathonEventSource.Twitch,
            Service = "Chat",
            Name = UserName!,
            Status = true
        });
        return Task.CompletedTask;
    }

    [ExcludeFromCodeCoverage]
    private Task HandleChatDisconnect(object? sender, OnDisconnectedArgs onDisconnectedArgs) {
        if ((DateTime.Now - _lastChatDisconnectLog).TotalSeconds > 60) {
            logger?.LogWarning("Twitch Chat Disconnected. Attempting Reconnect...");
            _lastChatDisconnectLog = DateTime.Now;
            IntegrationEvents.RaiseConnectionUpdate(new IntegrationConnection {
                Source = SubathonEventSource.Twitch,
                Service = "Chat",
                Name = UserName!,
                Status = false,
                Configured = HasTokenFile()
            });
        }

        _ = Task.Run(TryReconnectChatAsync);
        return Task.CompletedTask;
    }


    [ExcludeFromCodeCoverage]
    private async Task TryReconnectChatAsync() {
        if (_chat == null)
            return;

        if (!await _chatReconnect.Lock.WaitAsync(0))
            return;

        try {
            _chatReconnect.Cts?.Cancel();
            _chatReconnect.Cts = new CancellationTokenSource();
            CancellationToken token = _chatReconnect.Cts.Token;


            while (!token.IsCancellationRequested && !_chat.IsConnected) {
                _chatReconnect.Retries++;
                TimeSpan delay = _chatReconnect.Backoff;

                logger?.LogDebug(
                    "[Twitch Chat] Reconnect attempt {Attempt} in {Delay}s",
                    _chatReconnect.Retries,
                    delay.TotalSeconds);

                try {
                    await Task.Delay(delay, token);

                    if (_chat.IsConnected) {
                        logger?.LogDebug("Twitch Chat reconnect successful.");
                        IntegrationEvents.RaiseConnectionUpdate(new IntegrationConnection {
                            Source = SubathonEventSource.Twitch,
                            Service = "Chat",
                            Name = UserName!,
                            Status = true
                        });
                        return;
                    }

                    await _chat.ReconnectAsync();
                }
                catch (OperationCanceledException) {
                    return;
                }
                catch (Exception ex) {
                    logger?.LogWarning(ex, "Twitch Chat reconnect failed");
                }

                _chatReconnect.Backoff = TimeSpan.FromMilliseconds(
                    Math.Min(
                        _chatReconnect.Backoff.TotalMilliseconds * 2,
                        _chatReconnect.MaxBackoff.TotalMilliseconds));
            }
        }
        finally {
            _chatReconnect.Lock.Release();
        }
    }


    [ExcludeFromCodeCoverage]
    private Task HandleChatReconnect(object? _, OnConnectedEventArgs onConnectedEventArgs) {
        logger?.LogInformation("Twitch Chat Reconnected");
        _chatReconnect.Cts?.Cancel();
        _chatReconnect.Reset();
        IntegrationEvents.RaiseConnectionUpdate(new IntegrationConnection {
            Source = SubathonEventSource.Twitch,
            Service = "Chat",
            Name = UserName!,
            Status = true
        });
        return Task.CompletedTask;
    }


    private Task HandleMessageCmdReceived(object? s, OnMessageReceivedArgs e) {
        if (!e.ChatMessage.Channel.Equals(Login, StringComparison.InvariantCultureIgnoreCase) &&
            !e.ChatMessage.Channel.Equals(UserName, StringComparison.InvariantCultureIgnoreCase))
            return Task.CompletedTask;

        string message = e.ChatMessage.Message;
        bool isMod = e.ChatMessage.UserDetail.IsModerator;
        bool isBroadcaster = e.ChatMessage.IsBroadcaster;
        bool isVip = e.ChatMessage.UserDetail.IsVip;

        if (!string.IsNullOrWhiteSpace(message) && message.StartsWith('!'))
            CommandService.ChatCommandRequest(SubathonEventSource.Twitch, message,
                e.ChatMessage.Username, // DisplayName
                isBroadcaster, isMod, isVip, DateTime.Now);
        else if (e.ChatMessage.DisplayName.Equals("blerp", StringComparison.InvariantCultureIgnoreCase)
                 && config.GetBool("Extensions", "Blerp.Enabled", true))
            BlerpChatService.ParseMessage(e.ChatMessage.Message, SubathonEventSource.Twitch);

        return Task.CompletedTask;
    }

    private async Task InitializeEventSubAsync() {
        _eventSubReconnect.Reset();
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

        await _eventSub.ConnectAsync(EventSubUrl);
    }

    private bool IsEventSubConnected() {
        return _eventSub != null && !string.IsNullOrEmpty(_eventSub.SessionId) && _isConnected;
    }


    private async Task HandleEventSubConnect(object? s, WebsocketConnectedArgs e) {
        var hasError = false;
        logger?.LogInformation("Connected to EventSub WebSocket, session ID: "
                               + _eventSub?.SessionId + ", isReconnect: " + e.IsRequestedReconnect);
        if (!e.IsRequestedReconnect) {
            // todo allow override from local in case of deprecation, thanks twitch
            var eventTypes = new[] {
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

            foreach (string type in eventTypes)
                try {
                    var condition = new Dictionary<string, string> {
                        { "broadcaster_user_id", UserId! }, { "to_broadcaster_user_id", UserId! },
                        { "moderator_user_id", UserId! }, { "user_id", UserId! }
                    };
                    if (_api == null) continue;
                    CreateEventSubSubscriptionResponse? x = await _api.Helix.EventSub.CreateEventSubSubscriptionAsync(
                        type,
                        type.Contains("follow") || type.Contains("hype_train") ? "2" : "1", condition,
                        EventSubTransportMethod.Websocket, _eventSub?.SessionId,
                        clientId: Config.TwitchClientId,
                        accessToken: AccessToken);
                }
                catch (Exception ex) {
                    logger?.LogError(ex, $"Failed to subscribe to {type}: {ex.Message}");
                    ErrorMessageEvents.RaiseErrorEvent(
                        "ERROR",
                        nameof(SubathonEventSource.Twitch),
                        $"Failed to subscribe to {type} EventSub. Please report this issue at https://github.com/WolfwithSword/SubathonManager/issues",
                        DateTime.Now.ToLocalTime());
                    RevokeTokenFile();
                    hasError = true;
                }
        }

        _isConnected = !hasError;

        if (_isConnected) {
            _eventSubReconnect.Cts?.Cancel();
            _eventSubReconnect.Reset();
        }

        IntegrationEvents.RaiseConnectionUpdate(new IntegrationConnection {
            Source = SubathonEventSource.Twitch,
            Service = "EventSub",
            Name = UserName!,
            Status = IsEventSubConnected(),
            Configured = HasTokenFile()
        });
    }


    [ExcludeFromCodeCoverage]
    private Task HandleEventSubReconnect(object? s, WebsocketReconnectedArgs e) {
        logger?.LogInformation("Reconnected EventSub WebSocket.");
        if (_eventSubReconnect.Retries >= 1)
            ErrorMessageEvents.RaiseErrorEvent("INFO", nameof(SubathonEventSource.Twitch),
                "Twitch EventSub has reconnected", DateTime.Now.ToLocalTime());
        _eventSubReconnect.Cts?.Cancel();
        _eventSubReconnect.Reset();
        _isConnected = true;
        if (_chat is { IsConnected: true })
            // eventsub disconnect can false-disconnect chat sometimes.
            IntegrationEvents.RaiseConnectionUpdate(new IntegrationConnection {
                Source = SubathonEventSource.Twitch,
                Service = "Chat",
                Name = UserName!,
                Status = true
            });
        return Task.CompletedTask;
    }

    [ExcludeFromCodeCoverage]
    private Task HandleEventSubDisconnect(object? s, WebsocketDisconnectedArgs e) {
        logger?.LogWarning("Disconnected EventSub WebSocket.");

        ErrorMessageEvents.RaiseErrorEvent("WARN", nameof(SubathonEventSource.Twitch),
            "Twitch EventSub has disconnected", DateTime.Now.ToLocalTime());

        _isConnected = false;
        IntegrationEvents.RaiseConnectionUpdate(new IntegrationConnection {
            Source = SubathonEventSource.Twitch,
            Service = "EventSub",
            Name = UserName!,
            Status = _isConnected,
            Configured = HasTokenFile()
        });
        _ = Task.Run(TryReconnectEventSubAsync);
        return Task.CompletedTask;
    }

    [ExcludeFromCodeCoverage]
    private async Task TryReconnectEventSubAsync() {
        if (_eventSub == null)
            return;

        if (!await _eventSubReconnect.Lock.WaitAsync(0))
            return;

        try {
            _eventSubReconnect.Cts?.Cancel();
            _eventSubReconnect.Cts = new CancellationTokenSource();
            CancellationToken token = _eventSubReconnect.Cts.Token;

            while (!token.IsCancellationRequested && !IsEventSubConnected()) {
                if (_eventSubReconnect.MaxRetries > 0 &&
                    _eventSubReconnect.Retries >= _eventSubReconnect.MaxRetries) {
                    ErrorMessageEvents.RaiseErrorEvent(
                        "ERROR",
                        nameof(SubathonEventSource.Twitch),
                        "Twitch EventSub reconnect failed after maximum retries.",
                        DateTime.Now.ToLocalTime());

                    logger?.LogError("EventSub reconnect aborted: max retries reached. Please investigate.");
                    return;
                }

                if (!await ValidateTokenAsync()) {
                    logger?.LogError("EventSub reconnect aborted: Twitch token invalid.");
                    ErrorMessageEvents.RaiseErrorEvent("ERROR", nameof(SubathonEventSource.Twitch),
                        "Twitch EventSub could not be reconnected - Twitch Token is invalid",
                        DateTime.Now.ToLocalTime());
                    RevokeTokenFile();
                    return;
                }

                _eventSubReconnect.Retries++;

                TimeSpan delay = _eventSubReconnect.Backoff;

                logger?.LogWarning(
                    "[Twitch EventSub] Reconnect attempt {Attempt} in {Delay}s",
                    _eventSubReconnect.Retries,
                    delay.TotalSeconds);

                try {
                    await Task.Delay(delay, token);

                    if (IsEventSubConnected())
                        return;

                    await _eventSub.ReconnectAsync();
                }
                catch (OperationCanceledException) {
                    return;
                }
                catch (Exception ex) {
                    logger?.LogWarning(ex, "EventSub reconnect failed");
                }

                _eventSubReconnect.Backoff = TimeSpan.FromMilliseconds(
                    Math.Min(
                        _eventSubReconnect.Backoff.TotalMilliseconds * 2,
                        _eventSubReconnect.MaxBackoff.TotalMilliseconds));
            }
        }
        finally {
            _eventSubReconnect.Lock.Release();
        }
    }


    private Task HandleChannelOnline(object? s, StreamOnlineArgs e) {
        if (config.GetBool("Twitch", "ResumeOnStart")) {
            var subathonEvent = new SubathonEvent {
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

        if (config.GetBool("Twitch", "UnlockOnStart")) {
            var subathonEvent = new SubathonEvent {
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

    private Task HandleChannelOffline(object? s, StreamOfflineArgs e) {
        if (config.GetBool("Twitch", "PauseOnEnd")) {
            var subathonEvent = new SubathonEvent {
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

        if (config.GetBool("Twitch", "LockOnEnd")) {
            var subathonEvent = new SubathonEvent {
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

    private Task HandleChannelFollow(object? s, ChannelFollowArgs e) {
        var eventMeta = e.Metadata as WebsocketEventSubMetadata;
        Guid.TryParse(eventMeta!.MessageId, out Guid mId);
        if (mId == Guid.Empty) mId = Guid.NewGuid();
        var subathonEvent = new SubathonEvent {
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

    private Task HandleSubGift(object? s, ChannelSubscriptionGiftArgs e) {
        var eventMeta = e.Metadata as WebsocketEventSubMetadata;
        Guid.TryParse(eventMeta!.MessageId, out Guid mId);
        if (mId == Guid.Empty) mId = Guid.NewGuid();
        string? user = e.Payload.Event.UserName;
        if (e.Payload.Event.IsAnonymous || string.IsNullOrWhiteSpace(user))
            user = "Anonymous";
        var subathonEvent = new SubathonEvent {
            Id = mId,
            Source = SubathonEventSource.Twitch,
            Currency = "sub",
            EventType = SubathonEventType.TwitchGiftSub,
            Value = e.Payload.Event.Tier,
            User = user,
            Amount = e.Payload.Event.Total,
            EventTimestamp = eventMeta.MessageTimestamp.ToLocalTime()
        };
        SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
        return Task.CompletedTask;
    }

    private Task HandleChannelSubscribe(object? s, ChannelSubscribeArgs e) {
        if (e.Payload.Event.IsGift)
            return Task.CompletedTask;

        var eventMeta = e.Metadata as WebsocketEventSubMetadata;
        Guid.TryParse(eventMeta!.MessageId, out Guid mId);
        if (mId == Guid.Empty) mId = Guid.NewGuid();
        var subathonEvent = new SubathonEvent {
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

    private Task HandleSubscriptionMsg(object? s, ChannelSubscriptionMessageArgs e) {
        // int duration = e.Payload.Event.DurationMonths; // Do we want to take this into account and multiply? - no, people can reshare and it is read in

        var eventMeta = e.Metadata as WebsocketEventSubMetadata;
        Guid.TryParse(eventMeta!.MessageId, out Guid mId);
        if (mId == Guid.Empty) mId = Guid.NewGuid();
        var subathonEvent = new SubathonEvent {
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

    private Task HandleBitsUse(object? s, ChannelBitsUseArgs e) {
        var eventMeta = e.Metadata as WebsocketEventSubMetadata;
        Guid.TryParse(eventMeta!.MessageId, out Guid mId);
        string user = e.Payload.Event.UserName;
        if (string.IsNullOrWhiteSpace(user)) user = "Anonymous";
        if (mId == Guid.Empty) mId = Guid.NewGuid();
        var subathonEvent = new SubathonEvent {
            Id = mId,
            Source = SubathonEventSource.Twitch,
            EventType = SubathonEventType.TwitchCheer,
            User = user,
            Currency = "bits",
            Value = e.Payload.Event.Bits.ToString(),
            EventTimestamp = eventMeta.MessageTimestamp.ToLocalTime()
        };
        SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
        if (e.Payload.Event.Type.ToLower() != "cheer")
            logger?.LogInformation($"TwitchCheer Event {subathonEvent.Id} " +
                                   $"source was: {e.Payload.Event.Type} {e.Payload.Event.PowerUp?.Type}");

        return Task.CompletedTask;
    }

    private Task HandleChannelRaid(object? s, ChannelRaidArgs e) {
        var eventMeta = e.Metadata as WebsocketEventSubMetadata;
        Guid.TryParse(eventMeta!.MessageId, out Guid mId);
        if (mId == Guid.Empty) mId = Guid.NewGuid();
        var subathonEvent = new SubathonEvent {
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

    private void ClearHypeTrainTimeout() {
        SimulateHypeTrainEnd(_hypeTrainLevel);
    }

    private Task HandleHypeTrainBeginV2(object? s, ChannelHypeTrainBeginV2Args e) {
        var eventMeta = e.Metadata as WebsocketEventSubMetadata;
        Guid.TryParse(eventMeta!.MessageId, out Guid mId);
        if (mId == Guid.Empty) mId = Guid.NewGuid();
        var subathonEvent = new SubathonEvent {
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
        timerService?.Register("twitch-hype-train", _hypeTrainLevelDuration, ClearHypeTrainTimeout);
        return Task.CompletedTask;
    }

    private Task HandleHypeTrainProgressV2(object? s, ChannelHypeTrainProgressV2Args e) {
        if (e.Payload.Event.Level <= _hypeTrainLevel) return Task.CompletedTask;

        var eventMeta = e.Metadata as WebsocketEventSubMetadata;
        Guid.TryParse(eventMeta!.MessageId, out Guid mId);
        if (mId == Guid.Empty) mId = Guid.NewGuid();
        var subathonEvent = new SubathonEvent {
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
        timerService?.Register("twitch-hype-train", _hypeTrainLevelDuration, ClearHypeTrainTimeout);
        return Task.CompletedTask;
    }

    private Task HandleHypeTrainEndV2(object? s, ChannelHypeTrainEndV2Args e) {
        var eventMeta = e.Metadata as WebsocketEventSubMetadata;
        Guid.TryParse(eventMeta!.MessageId, out Guid mId);
        if (mId == Guid.Empty) mId = Guid.NewGuid();
        var subathonEvent = new SubathonEvent {
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

        timerService?.Unregister("twitch-hype-train");
        return Task.CompletedTask;
    }

    private Task HandleCharityEvent(object? s, ChannelCharityCampaignDonateArgs e) {
        var eventMeta = e.Metadata as WebsocketEventSubMetadata;
        Guid.TryParse(eventMeta!.MessageId, out Guid mId);
        if (mId == Guid.Empty) mId = Guid.NewGuid();
        var subathonEvent = new SubathonEvent {
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

    public static void SimulateRaid(int viewers = 50) {
        var subathonEvent = new SubathonEvent {
            Source = SubathonEventSource.Simulated,
            EventType = SubathonEventType.TwitchRaid,
            User = "SYSTEM",
            Value = $"{viewers}"
        };
        SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
    }

    public static void SimulateCheer(int bitsCount = 100) {
        var subathonEvent = new SubathonEvent {
            Source = SubathonEventSource.Simulated,
            EventType = SubathonEventType.TwitchCheer,
            User = "SYSTEM",
            Currency = "bits",
            Value = $"{bitsCount}"
        };
        SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
    }

    public static void SimulateSubscription(string tier) {
        if (tier != "1000" && tier != "2000" && tier != "3000") return;

        var subathonEvent = new SubathonEvent {
            Source = SubathonEventSource.Simulated,
            Currency = "sub",
            EventType = SubathonEventType.TwitchSub,
            Value = tier,
            EventTypeMeta = tier,
            User = "SYSTEM"
        };
        SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
    }

    public static void SimulateGiftSubscriptions(string tier, int amount) {
        if (tier != "1000" && tier != "2000" && tier != "3000") return;

        var subathonEvent = new SubathonEvent {
            Source = SubathonEventSource.Simulated,
            Currency = "sub",
            EventType = SubathonEventType.TwitchGiftSub,
            Value = tier,
            EventTypeMeta = tier,
            User = "SYSTEM",
            Amount = amount
        };
        SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
    }

    public static void SimulateFollow() {
        var subathonEvent = new SubathonEvent {
            Source = SubathonEventSource.Simulated,
            EventType = SubathonEventType.TwitchFollow,
            User = "SYSTEM"
        };
        SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
    }

    public static void SimulateCharityDonation(string value = "10.00", string currency = "USD") {
        var subathonEvent = new SubathonEvent {
            Source = SubathonEventSource.Simulated,
            EventType = SubathonEventType.TwitchCharityDonation,
            Value = value,
            Currency = currency,
            User = "SYSTEM"
        };
        SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
    }

    public static void SimulateHypeTrainStart() {
        var subathonEvent = new SubathonEvent {
            Source = SubathonEventSource.Simulated,
            EventType = SubathonEventType.TwitchHypeTrain,
            Value = "start",
            Amount = 1,
            User = "SYSTEM"
        };
        _hypeTrainLevel = 1;
        SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
    }

    public static void SimulateHypeTrainProgress(int level = 7) {
        if (level <= _hypeTrainLevel) return;
        var subathonEvent = new SubathonEvent {
            Source = SubathonEventSource.Simulated,
            EventType = SubathonEventType.TwitchHypeTrain,
            Value = "progress",
            Amount = level,
            User = "SYSTEM"
        };
        _hypeTrainLevel = level;
        SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
    }

    public static void SimulateHypeTrainEnd(int level = 10) {
        var subathonEvent = new SubathonEvent {
            Source = SubathonEventSource.Simulated,
            EventType = SubathonEventType.TwitchHypeTrain,
            Value = "end",
            Amount = level,
            User = "SYSTEM"
        };
        _hypeTrainLevel = 0;
        SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
    }

    protected virtual void Dispose(bool disposing) {
        if (!_disposed) {
            if (disposing) {
                _chatReconnect.Dispose();
                _eventSubReconnect.Dispose();
                OnTeardown();
            }

            _disposed = true;
        }
    }

    private void OnTeardown() {
        if (_chat != null) {
            _chat.OnMessageReceived -= HandleMessageCmdReceived;
            _chat.OnDisconnected -= HandleChatDisconnect;
            _chat.OnReconnected -= HandleChatReconnect;
            _chat.OnConnected -= HandleChatConnect;
        }

        if (_eventSub != null) {
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