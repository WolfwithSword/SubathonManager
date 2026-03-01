using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
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
using SubathonManager.Core.Interfaces;
using SubathonManager.Services;
using TwitchLib.Client.Events;
using TwitchLib.EventSub.Core.EventArgs.Stream;
using TwitchLib.EventSub.Websockets.Core.EventArgs;

namespace SubathonManager.Integration;

public class TwitchService : IDisposable, IAppService
{
    private readonly string _callbackUrl;

    private TwitchAPI? _api;
    private TwitchClient? _chat;
    private EventSubWebsocketClient? _eventSub;
    private static int _hypeTrainLevel = 0;
    private bool _disposed = false;

    private readonly ILogger? _logger;
    private readonly IConfig _config;

    private readonly string _tokenFile = Path.GetFullPath(Path.Combine(string.Empty
        , "data/twitch_token.json"));
    
    private DateTime _lastChatDisconnectLog = DateTime.MinValue;
    private volatile bool _isConnected = false;
    
    private readonly Utils.ServiceReconnectState _chatReconnect =
        new(TimeSpan.FromSeconds(5), maxRetries: 200, maxBackoff: TimeSpan.FromMinutes(2));

    private readonly Utils.ServiceReconnectState _eventSubReconnect =
        new(TimeSpan.FromSeconds(2.5), maxRetries: 200, maxBackoff: TimeSpan.FromMinutes(5));

    private string? AccessToken { get; set; }
    public string? UserName { get; private set; } = string.Empty;
    internal string? Login = string.Empty;
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
    
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (HasTokenFile())
        {
            var tokenValid = await ValidateTokenAsync();
            if (!tokenValid)
            {
                RevokeTokenFile();
                _logger?.LogWarning("Twitch token expired - deleting token file");
            }
            else
            {
                _logger?.LogInformation("Twitch Service starting up...");
                await InitializeAsync(ct);
            }
        }
    }
    
    [ExcludeFromCodeCoverage]
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (HasTokenFile())
        {
            var json = await File.ReadAllTextAsync(_tokenFile, ct);
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
        // todo allow scopes to load from file for emergency in case of deprecation, thanks twitch
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
        
        var uri = new Uri(_callbackUrl);
        int port = uri.Port;

        var tcpListener = new TcpListener(IPAddress.Loopback, port);

        try
        {
            tcpListener.Start();
            _logger?.LogDebug("Waiting for Twitch auth...");

            // serve the HTML page
            AccessToken = await HandleOAuthExchangeAsync(tcpListener, uri.AbsolutePath);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "TwitchService Authentication Failure: {ExMessage}", ex.Message);
            ErrorMessageEvents.RaiseErrorEvent(
                "ERROR",
                nameof(SubathonEventSource.Twitch),"Failed to do Twitch OAuth setup",
                DateTime.Now.ToLocalTime());
        }
        finally
        {
            tcpListener.Stop();
        }

        if (!string.IsNullOrEmpty(AccessToken))
        {
            await File.WriteAllTextAsync(_tokenFile,
                JsonSerializer.Serialize(new { access_token = AccessToken }));
            await Task.Delay(100);
        }
    }
    
    [ExcludeFromCodeCoverage]
    private async Task<string?> HandleOAuthExchangeAsync(TcpListener tcpListener, string callbackPath)
    {
        using (var client = await tcpListener.AcceptTcpClientAsync())
        {
            var stream = client.GetStream();
            await ReadHttpRequestAsync(stream); // consume

            string html = """
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

            await SendHttpResponseAsync(stream, 200, "text/html", html);
        }

        // fetch token
        using (var client = await tcpListener.AcceptTcpClientAsync())
        {
            var stream = client.GetStream();
            string requestLine = await ReadHttpRequestAsync(stream);
            
            var match = System.Text.RegularExpressions.Regex.Match(requestLine, @"GET (/token\?[^ ]+)");
            string? accessToken = null;

            if (match.Success)
            {
                var query = match.Groups[1].Value.Split('?', 2).ElementAtOrDefault(1) ?? "";
                var parts = System.Web.HttpUtility.ParseQueryString(query);
                accessToken = parts["access_token"];
            }

            await SendHttpResponseAsync(stream, 200, "text/plain", "OK");
            return accessToken;
        }
    }
    
    [ExcludeFromCodeCoverage]
    private async Task<string> ReadHttpRequestAsync(NetworkStream stream)
    {
        var buffer = new byte[4096];
        var sb = new System.Text.StringBuilder();

        while (true)
        {
            int bytesRead = await stream.ReadAsync(buffer);
            if (bytesRead == 0) break;
            sb.Append(System.Text.Encoding.UTF8.GetString(buffer, 0, bytesRead));
            if (sb.ToString().Contains("\r\n\r\n")) break; // end headers
        }

        return sb.ToString().Split("\r\n")[0];
    }

    private async Task SendHttpResponseAsync(NetworkStream stream, int statusCode, string contentType, string body)
    {
        var bodyBytes = System.Text.Encoding.UTF8.GetBytes(body);
        var headers = $"HTTP/1.1 {statusCode} OK\r\n" +
                      $"Content-Type: {contentType}; charset=utf-8\r\n" +
                      $"Content-Length: {bodyBytes.Length}\r\n" +
                      $"Connection: close\r\n\r\n";

        var headerBytes = System.Text.Encoding.UTF8.GetBytes(headers);
        await stream.WriteAsync(headerBytes);
        await stream.WriteAsync(bodyBytes);
        await stream.FlushAsync();
    }

    
    [ExcludeFromCodeCoverage]
    private async Task InitializeApiAsync()
    {
        _api = new TwitchAPI
        {
            Settings =
            {
                ClientId = Config.TwitchClientId,
                AccessToken = AccessToken
            }
        };

        var user = (await _api.Helix.Users.GetUsersAsync()).Users.FirstOrDefault();
        if (user != null)
        {
            UserName = user.DisplayName;
            Login = user.Login;
            UserId = user.Id;
            _logger?.LogDebug($"Authenticated as {UserName}");
            
            IntegrationEvents.RaiseConnectionUpdate(true, SubathonEventSource.Twitch, UserName!, "API");
        }
        else
        {
            Login = string.Empty;
            IntegrationEvents.RaiseConnectionUpdate(false, SubathonEventSource.Twitch, "", "API");
        }
    }

    
    [ExcludeFromCodeCoverage]
    private async Task InitializeChatAsync()
    {
        _chatReconnect.Reset();
        var credentials = new ConnectionCredentials(UserName, $"oauth:{AccessToken}");
        _chat = new TwitchClient();
        
        try
        {
            _chat.Initialize(credentials, channel: UserName);
            IntegrationEvents.RaiseConnectionUpdate(true, SubathonEventSource.Twitch, UserName!, "Chat");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, ex.Message);
            IntegrationEvents.RaiseConnectionUpdate(false, SubathonEventSource.Twitch, UserName!, "Chat");
        }

        _chat.OnMessageReceived += HandleMessageCmdReceived;
        _chat.OnDisconnected += HandleChatDisconnect;
        _chat.OnReconnected += HandleChatReconnect;
        await Task.Run(() => _chat.Connect());
    }

    [ExcludeFromCodeCoverage]
    private void HandleChatDisconnect(object? _, TwitchLib.Communication.Events.OnDisconnectedEventArgs args)
    {
        if ((DateTime.Now - _lastChatDisconnectLog).TotalSeconds > 60)
        {
            _logger?.LogWarning("Twitch Chat Disconnected. Attempting Reconnect...");
            _lastChatDisconnectLog = DateTime.Now;
            IntegrationEvents.RaiseConnectionUpdate(false, SubathonEventSource.Twitch, UserName!, "Chat");
        }
        Task.Run(TryReconnectChatAsync);
    }
    
    
    [ExcludeFromCodeCoverage]
    private async Task TryReconnectChatAsync()
    {
        if (_chat == null)
            return;

        if (!await _chatReconnect.Lock.WaitAsync(0))
            return;
        
        try
        {
            _chatReconnect.Cts?.Cancel();
            _chatReconnect.Cts = new CancellationTokenSource();
            var token = _chatReconnect.Cts.Token;
            

            while (!token.IsCancellationRequested && _chat.IsConnected == false)
            {
                _chatReconnect.Retries++;
                var delay = _chatReconnect.Backoff;

                _logger?.LogDebug(
                    "[Twitch Chat] Reconnect attempt {Attempt} in {Delay}s",
                    _chatReconnect.Retries,
                    delay.TotalSeconds);
                
                try
                {
                    await Task.Delay(delay, token);

                    if (_chat.IsConnected)
                    {
                        _logger?.LogDebug("Twitch Chat reconnect successful.");
                        IntegrationEvents.RaiseConnectionUpdate(true, SubathonEventSource.Twitch, UserName!, "Chat");
                        return;
                    }

                    _chat.Reconnect();
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Twitch Chat reconnect failed");
                }

                _chatReconnect.Backoff = TimeSpan.FromMilliseconds(
                    Math.Min(
                        _chatReconnect.Backoff.TotalMilliseconds * 2,
                        _chatReconnect.MaxBackoff.TotalMilliseconds));
            }
        }
        finally
        {
            _chatReconnect.Lock.Release();
        }
    }


    [ExcludeFromCodeCoverage]
    private void HandleChatReconnect(object? _, TwitchLib.Communication.Events.OnReconnectedEventArgs args)
    {
        _logger?.LogInformation("Twitch Chat Reconnected");
        _chatReconnect.Cts?.Cancel();
        _chatReconnect.Reset();
    }
    
    
    private void HandleMessageCmdReceived(object? s, OnMessageReceivedArgs e)
    {
        if (!e.ChatMessage.Channel.Equals(Login, StringComparison.InvariantCultureIgnoreCase) && 
            !e.ChatMessage.Channel.Equals(UserName, StringComparison.InvariantCultureIgnoreCase))
            return;
        
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
        _chatReconnect.Reset();
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
    
    private bool IsEventSubConnected()
    {
        return _eventSub != null && !string.IsNullOrEmpty(_eventSub.SessionId) && _isConnected;
    }

    
    [ExcludeFromCodeCoverage]
    private async Task HandleEventSubConnect(object? s, WebsocketConnectedArgs e)
    {
        bool hasError = false;
        _logger?.LogInformation("Connected to EventSub WebSocket, session ID: "
                                + _eventSub?.SessionId + ", isReconnect: " + e.IsRequestedReconnect);
        if (!e.IsRequestedReconnect)
        {
            // todo allow override from local in case of deprecation, thanks twitch
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
                    ErrorMessageEvents.RaiseErrorEvent(
                        "ERROR",
                        nameof(SubathonEventSource.Twitch),
                        $"Failed to subscribe to {type} EventSub. Please report this issue at https://github.com/WolfwithSword/SubathonManager/issues",
                        DateTime.Now.ToLocalTime());
                    RevokeTokenFile();
                    hasError = true;
                }
            }
        }
        _isConnected = !hasError;

        if (_isConnected)
        {
            _eventSubReconnect.Cts?.Cancel();
            _eventSubReconnect.Reset();
        }
        IntegrationEvents.RaiseConnectionUpdate(_isConnected, SubathonEventSource.Twitch, UserName!, "EventSub");
    }


    [ExcludeFromCodeCoverage]
    private Task HandleEventSubReconnect(object? s, WebsocketReconnectedArgs e)
    {
        _logger?.LogInformation("Reconnected EventSub WebSocket.");
        ErrorMessageEvents.RaiseErrorEvent("INFO", nameof(SubathonEventSource.Twitch),
            "Twitch EventSub has reconnected", DateTime.Now.ToLocalTime());
        _eventSubReconnect.Cts?.Cancel();
        _eventSubReconnect.Reset();
        _isConnected = true;
        return Task.CompletedTask;
    }
    
    [ExcludeFromCodeCoverage]
    private Task HandleEventSubDisconnect(object? s, WebsocketDisconnectedArgs e)
    {
        _logger?.LogWarning("Disconnected EventSub WebSocket.");
        
        ErrorMessageEvents.RaiseErrorEvent("WARN", nameof(SubathonEventSource.Twitch),
            "Twitch EventSub has disconnected", DateTime.Now.ToLocalTime());
        
        _isConnected = false;
        IntegrationEvents.RaiseConnectionUpdate(_isConnected, SubathonEventSource.Twitch, UserName!, "EventSub");
        _ = Task.Run(TryReconnectEventSubAsync);
        return Task.CompletedTask;
    }
    
    [ExcludeFromCodeCoverage]
    private async Task TryReconnectEventSubAsync()
    {
        if (_eventSub == null)
            return;

        if (!await _eventSubReconnect.Lock.WaitAsync(0))
            return;
        
        try
        {
            _eventSubReconnect.Cts?.Cancel();
            _eventSubReconnect.Cts = new CancellationTokenSource();
            var token = _eventSubReconnect.Cts.Token;

            while (!token.IsCancellationRequested && !IsEventSubConnected())
            {
                if (_eventSubReconnect.MaxRetries > 0 &&
                    _eventSubReconnect.Retries >= _eventSubReconnect.MaxRetries)
                {
                    ErrorMessageEvents.RaiseErrorEvent(
                        "ERROR",
                        nameof(SubathonEventSource.Twitch),
                        "Twitch EventSub reconnect failed after maximum retries.",
                        DateTime.Now.ToLocalTime());

                    _logger?.LogError("EventSub reconnect aborted: max retries reached. Please investigate.");
                    return;
                }

                if (!await ValidateTokenAsync())
                {
                    _logger?.LogError("EventSub reconnect aborted: Twitch token invalid.");
                    ErrorMessageEvents.RaiseErrorEvent("ERROR", nameof(SubathonEventSource.Twitch),
                        "Twitch EventSub could not be reconnected - Twitch Token is invalid", DateTime.Now.ToLocalTime());
                    RevokeTokenFile();
                    return;
                }

                _eventSubReconnect.Retries++;

                var delay = _eventSubReconnect.Backoff;

                _logger?.LogWarning(
                    "[Twitch EventSub] Reconnect attempt {Attempt} in {Delay}s",
                    _eventSubReconnect.Retries,
                    delay.TotalSeconds);

                try
                {
                    await Task.Delay(delay, token);

                    if (IsEventSubConnected())
                        return;

                    await _eventSub.ReconnectAsync();
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "EventSub reconnect failed");
                }

                _eventSubReconnect.Backoff = TimeSpan.FromMilliseconds(
                    Math.Min(
                        _eventSubReconnect.Backoff.TotalMilliseconds * 2,
                        _eventSubReconnect.MaxBackoff.TotalMilliseconds));
            }
        }
        finally
        {
            _eventSubReconnect.Lock.Release();
        }
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
        // int duration = e.Payload.Event.DurationMonths; // Do we want to take this into account and multiply? - no, people can reshare and it is read in

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

    public async Task StopAsync(CancellationToken ct = default)
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
                _chatReconnect.Dispose();
                _eventSubReconnect.Dispose();
                OnTeardown();
            }
            _disposed = true;
        }
    }
    
    private void OnTeardown()
    {
        if (_chat != null)
        {
            _chat.OnMessageReceived -= HandleMessageCmdReceived;
            _chat.OnDisconnected -= HandleChatDisconnect;
            _chat.OnReconnected -= HandleChatReconnect;
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

