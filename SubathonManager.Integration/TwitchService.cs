using System.Diagnostics;
using System.Net;
using System.Text.Json;
using TwitchLib.Api;
using TwitchLib.Api.Core.Enums;
using TwitchLib.Client;
using TwitchLib.Client.Models;
using TwitchLib.EventSub.Websockets.Core.Models;
using TwitchLib.EventSub.Websockets;
using SubathonManager.Core;
using SubathonManager.Core.Models;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;

namespace SubathonManager.Integration;

public class TwitchService
{
    private readonly string _callbackUrl;

    private TwitchAPI? _api = null!;
    private TwitchClient? _chat = null!;
    private EventSubWebsocketClient? _eventSub = null!;

    private readonly string _tokenFile = Path.GetFullPath(Path.Combine(string.Empty
        , "data/twitch_token.json"));
    // Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "twitch_token.json");

    public string? AccessToken { get; private set; }
    public string? UserName { get; private set; } = string.Empty;
    public string? UserId { get; private set; }

    public TwitchService()
    {
        //int port = int.Parse(Config.Data["Server"]["Port"]);
        //port += 1;
        int port = 14041; // hardcode cause of app callback url
        _callbackUrl = $"http://localhost:{port}/auth/twitch/callback/";
    }

    public bool HasTokenFile()
    {
        return File.Exists(_tokenFile);
    }

    public void RevokeTokenFile()
    {
        if (HasTokenFile()) File.Delete(_tokenFile);
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
            {
                return false;
            }

            Console.WriteLine($"Twitch Token Valid for Scopes: {string.Join(',', validation.Scopes)}");
            return true;
        }
        catch
        {
            return false;
        }
    }

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
            Console.WriteLine("Twitch Initialized API");
            await InitializeChatAsync();
            Console.WriteLine("Twitch Initialized Chat");
            await InitializeEventSubAsync();
            Console.WriteLine("Twitch Initialized EventSub");
            TwitchEvents.RaiseTwitchConnected();
        }
        catch
        {
            // 
        }
    }

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
            "chat:edit"
        });

        var oauthUrl = $"https://id.twitch.tv/oauth2/authorize" +
                       $"?client_id={Config.TwitchClientId}" +
                       $"&redirect_uri={_callbackUrl}" +
                       $"&response_type=token" +
                       $"&scope={scopes}";

        Console.WriteLine("Opening Twitch OAuth...");
        Process.Start(new ProcessStartInfo
        {
            FileName = oauthUrl,
            UseShellExecute = true
        });

        // temp listener for callback
        var listener = new HttpListener();
        listener.Prefixes.Add(_callbackUrl.EndsWith("/") ? _callbackUrl : _callbackUrl + "/");
        var tokenUrl = _callbackUrl.Replace("auth/twitch/callback", "token");
        listener.Prefixes.Add(tokenUrl.EndsWith("/") ? tokenUrl : tokenUrl + "/");
        listener.Start();
        Console.WriteLine("Waiting for Twitch auth...");

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

        try
        {
            listener.Stop();
        }
        catch
        {
            //
        }

        if (!string.IsNullOrEmpty(AccessToken))
        {
            await File.WriteAllTextAsync(_tokenFile,
                JsonSerializer.Serialize(new { access_token = AccessToken }));
        }
    }

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
            Console.WriteLine($"Authenticated as {UserName}");
        }
    }

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
            Console.WriteLine($"Exception: {ex}, {ex.Message}");
        }

        _chat.OnMessageReceived += (s, e) =>
        {
            string message = e.ChatMessage.Message;
            bool isMod = e.ChatMessage.IsModerator;
            bool isBroadcaster = e.ChatMessage.IsBroadcaster;
            bool isVip = e.ChatMessage.IsVip;
            // todo whitelist usernames ignore case

            if (!string.IsNullOrWhiteSpace(message) && message.StartsWith("!"))
            {
                var parts = message.Substring(1).Split(' '); // remove ! and split
                var command = parts[0].ToLower();
                var args = parts.Skip(1).ToArray();

                switch (command)
                {
                    case "addtime":
                        break;
                    case "pause":
                        break;
                    // TODO SubathonEvent for Command, Value will be the msg,
                    // if addTime and all that or subtract time, also do secondstoadd (+-)
                    // add more commands and param reading
                    // regex yay
                }

                Console.WriteLine(message);
            }
        };
        await Task.Run(() => _chat.Connect());
    }

    private async Task InitializeEventSubAsync()
    {
        // todo store message id's so we know we dont get dupes
        // do same for command messages above
        // probably want a prune button for it too.
        // will also add an export for logs purposes, and also use as base for webhook logging

        _eventSub = new EventSubWebsocketClient();

        _eventSub.WebsocketConnected += async (s, e) =>
        {
            Console.WriteLine("Connected to EventSub WebSocket, session ID: " + _eventSub.SessionId);
            if (!e.IsRequestedReconnect)
            {
                // TODO listen to community gift sub, but do not add time as counts as channel.subscribe
                // but handy for showing "X gifted 50x subs" ? or not needed? 
                var eventTypes = new[]
                {
                    "stream.offline",
                    "stream.online",
                    "channel.follow",
                    "channel.subscribe", // This does not include resubscribes, channel.subscription.message does
                    "channel.cheer",
                    "channel.raid",
                    "channel.subscription.gift",
                    "channel.subscription.message" // TODO verify this does not dupe channel.subscribe. This does include duration_months for adv
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
                            type.Contains("follow") ? "2" : "1", condition,
                            EventSubTransportMethod.Websocket, _eventSub.SessionId,
                            clientId: Config.TwitchClientId,
                            accessToken: AccessToken);

                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Failed to subscribe to {type}: {ex.Message}");
                    }
                }
            }
        };

        _eventSub.WebsocketReconnected += (s, e) =>
        {
            Console.WriteLine("Reconnected EventSub WebSocket.");
            return Task.CompletedTask;
        };

        _eventSub.WebsocketDisconnected += (s, e) =>
        {
            // todo try ReconnectAsync and exponential backoff task delay
            Console.WriteLine("Disconnected EventSub WebSocket.");
            return Task.CompletedTask;
        };

        _eventSub.StreamOnline += (s, e) =>
        {

            if (bool.TryParse(Config.Data["Twitch"]["ResumeOnStart"] ?? "false", out var resumeOnStart) && resumeOnStart)
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

            if (bool.TryParse(Config.Data["Twitch"]["UnlockOnStart"] ?? "false", out var unlockOnStart) && unlockOnStart)
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
        };

        _eventSub.StreamOffline += (s, e) =>
        {

            if (bool.TryParse(Config.Data["Twitch"]["PauseOnEnd"] ?? "false", out var pauseOnEnd) && pauseOnEnd)
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

            if (bool.TryParse(Config.Data["Twitch"]["LockOnEnd"] ?? "false", out var lockOnEnd) && lockOnEnd)
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
        };
        
        _eventSub.ChannelFollow += (s, e) =>
        {
            Console.WriteLine($"New follow from {e.Payload.Event.UserName}");

            var eventMeta = e.Metadata as WebsocketEventSubMetadata;
            Guid.TryParse(eventMeta.MessageId, out var _id);
            if (_id == Guid.Empty) _id = Guid.NewGuid();
            SubathonEvent subathonEvent = new SubathonEvent
            {
                Id = _id,
                Source = SubathonEventSource.Twitch,
                EventType = SubathonEventType.TwitchFollow,
                User = e.Payload.Event.UserName,
                EventTimestamp = eventMeta.MessageTimestamp // or e.Payload.Event.FollowedAt and change type
            };
            SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
            // TODO 

            return Task.CompletedTask;
        };

        _eventSub.ChannelSubscriptionGift += (s, e) =>
        {
            var eventMeta = e.Metadata as WebsocketEventSubMetadata;
            Guid.TryParse(eventMeta.MessageId, out var _id);
            if (_id == Guid.Empty) _id = Guid.NewGuid();
            SubathonEvent subathonEvent = new SubathonEvent
            {
                Id = _id,
                Source = SubathonEventSource.Twitch,
                Currency = "sub",
                EventType = SubathonEventType.TwitchGiftSub,
                Value = e.Payload.Event.Tier,
                User = e.Payload.Event.UserName,
                Amount = e.Payload.Event.Total,
                EventTimestamp = eventMeta.MessageTimestamp
            };
            SubathonEvents.RaiseSubathonEventCreated(subathonEvent);

            Console.WriteLine(
                $"GiftSubs from {e.Payload.Event.UserName} {e.Payload.Event.Total} {e.Payload.Event.Tier}");
            return Task.CompletedTask;
        };

        _eventSub.ChannelSubscribe += (s, e) =>
        {
            ///////////// Maybe we IGNORE if it's gifted, and rely on diff pubsub for gifts
            ///
            /// 
            // this appears to only be new subs, not resubs

            // value can be equiv to tier
            if (e.Payload.Event.IsGift)
            {
                return Task.CompletedTask;
            }

            var eventMeta = e.Metadata as WebsocketEventSubMetadata;
            Guid.TryParse(eventMeta.MessageId, out var _id);
            if (_id == Guid.Empty) _id = Guid.NewGuid();
            SubathonEvent subathonEvent = new SubathonEvent
            {
                Id = _id,
                Source = SubathonEventSource.Twitch,
                Currency = "sub",
                EventType = SubathonEventType.TwitchSub,
                Value = e.Payload.Event.Tier,
                User = e.Payload.Event.UserName,
                EventTimestamp = eventMeta.MessageTimestamp
            };
            SubathonEvents.RaiseSubathonEventCreated(subathonEvent);

            // TODO VERY IMPORTANT
            // TODO Fire Event with _event, have it save to DB, and add to time.
            // TODO There is where we fetch currentTime and Multiplier and set SecondsToAdd based on data


            Console.WriteLine($"Sub from {e.Payload.Event.UserName} {e.Payload.Event.IsGift} {e.Payload.Event.Tier}");
            return Task.CompletedTask;
        };

        _eventSub.ChannelSubscriptionMessage += (s, e) =>
        {
            // resubs only it seems

            int duration = e.Payload.Event.DurationMonths; // TODO Do we want to take this into account and multiply?

            var eventMeta = e.Metadata as WebsocketEventSubMetadata;
            Guid.TryParse(eventMeta.MessageId, out var _id);
            if (_id == Guid.Empty) _id = Guid.NewGuid();
            SubathonEvent subathonEvent = new SubathonEvent
            {
                Id = _id,
                Source = SubathonEventSource.Twitch,
                Currency = "sub",
                EventType = SubathonEventType.TwitchSub,
                Value = e.Payload.Event.Tier,
                User = e.Payload.Event.UserName,
                EventTimestamp = eventMeta.MessageTimestamp
            };
            SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
            // TODO

            Console.WriteLine($"ReSub from {e.Payload.Event.UserName} {e.Payload.Event.Tier}");
            return Task.CompletedTask;
        };

        _eventSub.ChannelCheer += (s, e) =>
        {
            var eventMeta = e.Metadata as WebsocketEventSubMetadata;
            Guid.TryParse(eventMeta.MessageId, out var _id);
            if (_id == Guid.Empty) _id = Guid.NewGuid();
            SubathonEvent subathonEvent = new SubathonEvent
            {
                Id = _id,
                Source = SubathonEventSource.Twitch,
                EventType = SubathonEventType.TwitchCheer,
                User = e.Payload.Event.UserName,
                Currency = "bits",
                Value = e.Payload.Event.Bits.ToString(),
                EventTimestamp = eventMeta.MessageTimestamp // or e.Payload.Event.FollowedAt and change type
            };
            SubathonEvents.RaiseSubathonEventCreated(subathonEvent);
            // TODO

            Console.WriteLine($"Bits from {e.Payload.Event.UserName}: {e.Payload.Event.Bits}");
            return Task.CompletedTask;
        };

        _eventSub.ChannelRaid += (s, e) =>
        {
            var eventMeta = e.Metadata as WebsocketEventSubMetadata;
            Guid.TryParse(eventMeta.MessageId, out var _id);
            if (_id == Guid.Empty) _id = Guid.NewGuid();
            SubathonEvent subathonEvent = new SubathonEvent
            {
                Id = _id,
                Source = SubathonEventSource.Twitch,
                EventType = SubathonEventType.TwitchRaid,
                User = e.Payload.Event.FromBroadcasterUserName,
                Value = e.Payload.Event.Viewers.ToString(),
                EventTimestamp = eventMeta.MessageTimestamp
            };
            SubathonEvents.RaiseSubathonEventCreated(subathonEvent);

            Console.WriteLine(
                $"Raid from {e.Payload.Event.FromBroadcasterUserName} ({e.Payload.Event.Viewers})");
            return Task.CompletedTask;
        };

        await _eventSub.ConnectAsync();

    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // api has no disconnect? 
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
}

