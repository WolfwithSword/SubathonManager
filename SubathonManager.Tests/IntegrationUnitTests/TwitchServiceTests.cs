using TwitchLib.Client.Events;
using TwitchLib.EventSub.Core.EventArgs.Stream;
using TwitchLib.EventSub.Core.EventArgs.Channel;
using TwitchLib.EventSub.Websockets.Core.Models;
using TwitchLib.Client.Enums;
using System.Drawing;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Models;
using SubathonManager.Services;
using SubathonManager.Integration;
using System.Reflection;
using System.Text;
using TwitchLib.Client.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using SubathonManager.Tests.Utility;
using UserType = TwitchLib.Client.Enums.UserType;
// ReSharper disable NullableWarningSuppressionIsUsed
// ReSharper disable SuggestVarOrType_SimpleTypes

namespace SubathonManager.Tests.IntegrationUnitTests
{
    [Collection("SharedEventBusTests")]
    public class TwitchServiceTests
    {
        public TwitchServiceTests()
        {
            var path = Path.GetFullPath(Path.Combine(string.Empty
                , "data"));
            Directory.CreateDirectory(path);
        }

        private static SubathonEvent? CaptureEvent(Action trigger) =>
            EventUtil.SubathonEventCapture.CaptureRequired(trigger);

        public class MockEventSubServer : IAsyncDisposable
        {
            private readonly WebApplication _app;
            private WebSocket? _currentSocket;
            private readonly CancellationTokenSource _cts = new();

            public string SessionId { get; } = Guid.NewGuid().ToString("N")[..16];
            public Uri Uri { get; }

            public MockEventSubServer()
            {
                int port = GetFreePort();
                Uri = new Uri($"ws://127.0.0.1:{port}/ws");
                string httpBase = $"http://127.0.0.1:{port}";

                var builder = WebApplication.CreateBuilder();
                builder.WebHost.UseUrls(httpBase);
                builder.Logging.ClearProviders();
                _app = builder.Build();
                _app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

                _app.Map("/ws", async context =>
                {
                    if (!context.WebSockets.IsWebSocketRequest)
                    {
                        context.Response.StatusCode = 400;
                        return;
                    }

                    var socket = await context.WebSockets.AcceptWebSocketAsync();
                    _currentSocket = socket;

                    var welcome = new
                    {
                        metadata = new
                        {
                            message_id = Guid.NewGuid().ToString(),
                            message_type = "session_welcome",
                            message_timestamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ")
                        },
                        payload = new
                        {
                            session = new
                            {
                                id = SessionId,
                                status = "connected",
                                keepalive_timeout_seconds = 10,
                                reconnect_url = (string?)null,
                                connected_at = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ")
                            }
                        }
                    };

                    await SendRawAsync(socket, JsonSerializer.Serialize(welcome));

                    var buffer = new byte[4096];
                    while (socket.State == WebSocketState.Open && !_cts.Token.IsCancellationRequested)
                    {
                        try
                        {
                            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                            readCts.CancelAfter(TimeSpan.FromSeconds(9));
                            var result = await socket.ReceiveAsync(buffer, readCts.Token);
                            if (result.MessageType == WebSocketMessageType.Close) break;
                        }
                        catch (OperationCanceledException)
                        {
                            if (socket.State == WebSocketState.Open)
                            {
                                var keepalive = new
                                {
                                    metadata = new
                                    {
                                        message_id = Guid.NewGuid().ToString(),
                                        message_type = "session_keepalive",
                                        message_timestamp =
                                            DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ")
                                    },
                                    payload = new { }
                                };
                                await SendRawAsync(socket, JsonSerializer.Serialize(keepalive));
                            }
                        }
                        catch
                        {
                            break;
                        }
                    }
                });

                _app.StartAsync().GetAwaiter().GetResult();
            }

            public async Task SendNotificationAsync(string subscriptionType, string subscriptionVersion,
                object eventPayload)
            {
                if (_currentSocket?.State != WebSocketState.Open) return;

                var notification = new
                {
                    metadata = new
                    {
                        message_id = Guid.NewGuid().ToString(),
                        message_type = "notification",
                        message_timestamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ"),
                        subscription_type = subscriptionType,
                        subscription_version = subscriptionVersion
                    },
                    payload = new
                    {
                        subscription = new
                        {
                            id = Guid.NewGuid().ToString(),
                            type = subscriptionType,
                            version = subscriptionVersion,
                            status = "enabled",
                            condition = new { broadcaster_user_id = "123456" },
                            transport = new { method = "websocket", session_id = SessionId },
                            created_at = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ")
                        },
                        @event = eventPayload
                    }
                };

                await SendRawAsync(_currentSocket, JsonSerializer.Serialize(notification));
            }

            private static async Task SendRawAsync(WebSocket socket, string json)
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
            }

            public static int GetFreePort()
            {
                var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
                l.Start();
                int port = ((IPEndPoint)l.LocalEndpoint).Port;
                l.Stop();
                return port;
            }

            public async ValueTask DisposeAsync()
            {
                _cts.Cancel();
                if (_currentSocket?.State == WebSocketState.Open)
                {
                    try
                    {
                        await _currentSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                    }
                    catch {/**/}
                }

                await _app.StopAsync();
                await _app.DisposeAsync();
                _cts.Dispose();
            }
        }


        [Fact]
        public void SimulateRaid_RaisesRaidEvent()
        {
            var ev = CaptureEvent(() => TwitchService.SimulateRaid(123));

            Assert.NotNull(ev);
            Assert.Equal(SubathonEventSource.Simulated, ev.Source);
            Assert.Equal(SubathonEventType.TwitchRaid, ev.EventType);
            Assert.Equal("123", ev.Value);
            Assert.Equal("SYSTEM", ev.User);
        }


        [Fact]
        public void SimulateCheer_RaisesCheerEvent()
        {
            var ev = CaptureEvent(() => TwitchService.SimulateCheer(500));

            Assert.NotNull(ev);
            Assert.Equal(SubathonEventType.TwitchCheer, ev.EventType);
            Assert.Equal("bits", ev.Currency);
            Assert.Equal("500", ev.Value);
        }

        [Fact]
        public void SimulateSubscription_InvalidTier_DoesNotRaise()
        {
            SubathonEvent? captured = CaptureEvent(() => TwitchService.SimulateSubscription("9000"));
            Assert.Null(captured);
        }


        [Theory]
        [InlineData("1000")]
        [InlineData("2000")]
        [InlineData("3000")]
        public void SimulateSubscription_ShouldRaiseSubEvent(string tier)
        {
            SubathonEvent? ev = CaptureEvent(() => TwitchService.SimulateSubscription(tier));
            TwitchService.SimulateSubscription(tier);

            Assert.NotNull(ev);
            Assert.Equal(SubathonEventType.TwitchSub, ev.EventType);
            Assert.Equal(SubathonEventSource.Simulated, ev.Source);
            Assert.Equal("sub", ev.Currency);
            Assert.Equal(1, ev.Amount);
            Assert.Equal(tier, ev.Value);

        }

        [Fact]
        public void SimulateGiftSubscriptions_ShouldRaiseGiftSubEvent()
        {
            SubathonEvent? ev = CaptureEvent(() => TwitchService.SimulateGiftSubscriptions("1000", 5));
            Assert.NotNull(ev);
            Assert.Equal(SubathonEventType.TwitchGiftSub, ev.EventType);
            Assert.Equal(SubathonEventSource.Simulated, ev.Source);
            Assert.Equal("sub", ev.Currency);
            Assert.Equal("1000", ev.Value);
            Assert.Equal(5, ev.Amount);
        }

        [Fact]
        public void SimulateFollow_ShouldRaiseFollowEvent()
        {
            SubathonEvent? ev = CaptureEvent(TwitchService.SimulateFollow);
            Assert.NotNull(ev);
            Assert.Equal(SubathonEventType.TwitchFollow, ev.EventType);
            Assert.Equal(SubathonEventSource.Simulated, ev.Source);
        }

        [Fact]
        public void SimulateCharityDonation_ShouldRaiseDonationEvent()
        {
            SubathonEvent? ev = CaptureEvent(() => TwitchService.SimulateCharityDonation("25.50", "CAD"));

            Assert.NotNull(ev);
            Assert.Equal(SubathonEventType.TwitchCharityDonation, ev.EventType);
            Assert.Equal(SubathonEventSource.Simulated, ev.Source);
            Assert.Equal("25.50", ev.Value);
            Assert.Equal("CAD", ev.Currency);
        }

        [Fact]
        public void SimulateHypeTrainStart_ShouldRaiseStartEvent()
        {
            SubathonEvent? ev = CaptureEvent(TwitchService.SimulateHypeTrainStart);

            Assert.NotNull(ev);
            Assert.Equal(SubathonEventSource.Simulated, ev.Source);
            Assert.NotNull(ev);
            Assert.Equal(SubathonEventType.TwitchHypeTrain, ev.EventType);
            Assert.Equal("start", ev.Value);
            Assert.Equal(1, ev.Amount);
        }

        [Fact]
        public void SimulateHypeTrainProgress_ShouldOnlyRaiseIfLevelIncreases()
        {
            SubathonEvent? ev = CaptureEvent(TwitchService.SimulateHypeTrainStart);
            SubathonEvent? ev2 = CaptureEvent(() => TwitchService.SimulateHypeTrainProgress(5));

            Assert.NotNull(ev2);
            Assert.Equal(SubathonEventSource.Simulated, ev2.Source);
            Assert.Equal(SubathonEventType.TwitchHypeTrain, ev2.EventType);
            Assert.NotNull(ev2);
            Assert.Equal("progress", ev2.Value);
            Assert.Equal(5, ev2.Amount);
        }

        [Fact]
        public void SimulateHypeTrainEnd_ShouldRun()
        {
            SubathonEvent? ev = CaptureEvent(() => TwitchService.SimulateHypeTrainEnd(10));
            Assert.NotNull(ev);
            Assert.Equal(SubathonEventSource.Simulated, ev.Source);
            Assert.Equal(SubathonEventType.TwitchHypeTrain, ev.EventType);
            Assert.NotNull(ev);
            Assert.Equal("end", ev.Value);
            Assert.Equal(10, ev.Amount);
        }

        [Fact]
        public async Task HandleChannelOnline_ResumeOnStart_RaisesResumeCommand()
        {
            var config = MockConfig.MakeMockConfig(new()
            {
                { ("Twitch", "ResumeOnStart"), "true" }
            });

            var service = new TwitchService(null, config);

            var ev = CaptureEvent(() =>
                service
                    .GetType()
                    .GetMethod("HandleChannelOnline", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(service, [null, new StreamOnlineArgs()])
            );

            Assert.NotNull(ev);
            Assert.Equal(SubathonCommandType.Resume, ev.Command);
            Assert.Equal(SubathonEventType.Command, ev.EventType);
            Assert.Equal("AUTO", ev.User);
            await service.StopAsync();
        }

        [Fact]
        public async Task HandleChannelOnline_UnlockOnStart_RaisesResumeCommand()
        {
            var config =MockConfig.MakeMockConfig(new()
            {
                { ("Twitch", "UnlockOnStart"), "true" }
            });

            var service = new TwitchService(null, config);

            var ev = CaptureEvent(() =>
                service
                    .GetType()
                    .GetMethod("HandleChannelOnline", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(service, [null, new StreamOnlineArgs()])
            );

            Assert.NotNull(ev);
            Assert.Equal(SubathonCommandType.Unlock, ev.Command);
            Assert.Equal(SubathonEventType.Command, ev.EventType);
            Assert.Equal("AUTO", ev.User);
            await service.StopAsync();
        }

        [Fact]
        public async Task HandleChannelOffline_PauseOnEnd_RaisesPauseCommand()
        {
            var config =MockConfig.MakeMockConfig(new()
            {
                { ("Twitch", "PauseOnEnd"), "true" }
            });

            var service = new TwitchService(null, config);

            var ev = CaptureEvent(() =>
                service
                    .GetType()
                    .GetMethod("HandleChannelOffline", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(service, [null, new StreamOfflineArgs()])
            );

            Assert.NotNull(ev);
            Assert.Equal(SubathonCommandType.Pause, ev.Command);
            Assert.Equal(SubathonEventType.Command, ev.EventType);
            Assert.Equal("AUTO", ev.User);
            await service.StopAsync();
        }

        [Fact]
        public async Task HandleChannelOffline_LockOnEnd_RaisesPauseCommand()
        {
            var config =MockConfig.MakeMockConfig(new()
            {
                { ("Twitch", "LockOnEnd"), "true" }
            });

            var service = new TwitchService(null, config);

            var ev = CaptureEvent(() =>
                service
                    .GetType()
                    .GetMethod("HandleChannelOffline", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(service, [null, new StreamOfflineArgs()])
            );

            Assert.NotNull(ev);
            Assert.Equal(SubathonCommandType.Lock, ev.Command);
            Assert.Equal(SubathonEventType.Command, ev.EventType);
            Assert.Equal("AUTO", ev.User);
            await service.StopAsync();
        }

        [Fact]
        public async Task HandleChannelFollow_RaisesFollowEvent()
        {
            var service = new TwitchService(null,MockConfig.MakeMockConfig());

            var meta = new WebsocketEventSubMetadata
            {
                MessageId = Guid.NewGuid().ToString(),
                MessageTimestamp = DateTime.UtcNow,
            };

            var args = new ChannelFollowArgs
            {
                Metadata = meta,
                Payload = new()
                {
                    Event = new()
                    {
                        UserName = "Follower123"
                    }
                }
            };

            var ev = CaptureEvent(() =>
                service
                    .GetType()
                    .GetMethod("HandleChannelFollow", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(service, [null, args])
            );

            Assert.NotNull(ev);
            Assert.Equal(SubathonEventType.TwitchFollow, ev.EventType);
            Assert.Equal("Follower123", ev.User);
            Assert.Equal(SubathonEventSource.Twitch, ev.Source);
            await service.StopAsync();
        }

        [Fact]
        public void HandleHypeTrainProgress_IgnoresSameOrLowerLevel()
        {
            TwitchService.SimulateHypeTrainStart();

            SubathonEvent? capturedEvent = CaptureEvent(() =>
                TwitchService.SimulateHypeTrainProgress(1));
            Assert.Null(capturedEvent);
        }

        [Fact]
        public async Task HandleChatMessage_Command_RaisesCommandEvent()
        {
            var config =MockConfig.MakeMockConfig(new()
            {
                { ("Chat", "Commands.Pause"), "pause" },

                { ("Chat", "Commands.Pause.permissions.Mods"), "true" },
                { ("Chat", "Commands.Pause.permissions.VIPs"), "false" },
                { ("Chat", "Commands.Pause.permissions.Whitelist"), "specialguy" }
            });
            CommandService.SetConfig(config);
            var service = new TwitchService(null, config);
            service.Login = "teststreamer";

            ChatMessage MakeMessage(string message, bool isVip, bool isMod, bool isBroadcaster, string userName,
                string displayName)
            {
                TwitchLib.Client.Enums.UserType type = UserType.Viewer;
                if (isMod) type = UserType.Moderator;
                if (isBroadcaster) type = UserType.Broadcaster;

                return new ChatMessage(
                    botUsername: "",
                    userId: "123456789",
                    userName: userName,
                    displayName: displayName,
                    colorHex: "#ffffff",
                    color: Color.Black,
                    emoteSet: new("", ""),
                    message: message,
                    userType: type,
                    channel: "teststreamer",
                    id: Guid.NewGuid().ToString(),
                    isSubscriber: true,
                    subscribedMonthCount: 4,
                    roomId: "098765432",
                    isTurbo: false,
                    isModerator: isMod,
                    isMe: false,
                    isBroadcaster: isBroadcaster,
                    isVip: isVip,
                    isPartner: false,
                    isStaff: false,
                    noisy: Noisy.NotSet,
                    rawIrcMessage: "",
                    emoteReplacedMessage: "",
                    badges: [],
                    cheerBadge: new CheerBadge(0),
                    bits: 0,
                    bitsInDollars: 0
                );

            }

            var chatMsg = MakeMessage("!pause", false, false,
                true, "test", "Test");
            var args = new OnMessageReceivedArgs
            {
                ChatMessage = chatMsg
            };


            var ev = CaptureEvent(() =>
                service
                    .GetType()
                    .GetMethod("HandleMessageCmdReceived", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(service, [null, args])
            );

            Assert.NotNull(ev);
            Assert.Equal(SubathonEventType.Command, ev.EventType);
            Assert.Equal(SubathonCommandType.Pause, ev.Command);
            Assert.Equal(SubathonEventSource.Twitch, ev.Source);
            Assert.Equal("test", ev.User);


            chatMsg = MakeMessage("!pause", false, false,
                false, "specialguy", "specialguy");
            args = new OnMessageReceivedArgs
            {
                ChatMessage = chatMsg
            };

            ev = CaptureEvent(() =>
                service
                    .GetType()
                    .GetMethod("HandleMessageCmdReceived", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(service, [null, args])
            );

            Assert.NotNull(ev);
            Assert.Equal(SubathonEventType.Command, ev.EventType);
            Assert.Equal(SubathonCommandType.Pause, ev.Command);
            Assert.Equal(SubathonEventSource.Twitch, ev.Source);
            Assert.Equal("specialguy", ev.User);

            chatMsg = MakeMessage("!pause", false, true,
                false, "testuser", "TestUser");
            args = new OnMessageReceivedArgs
            {
                ChatMessage = chatMsg
            };

            ev = CaptureEvent(() =>
                service
                    .GetType()
                    .GetMethod("HandleMessageCmdReceived", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(service, [null, args])
            );

            Assert.NotNull(ev);
            Assert.Equal(SubathonEventType.Command, ev.EventType);
            Assert.Equal(SubathonCommandType.Pause, ev.Command);
            Assert.Equal(SubathonEventSource.Twitch, ev.Source);
            Assert.Equal("testuser", ev.User);

            chatMsg = MakeMessage("!pause", true, false,
                false, "testuser", "TestUser");
            args = new OnMessageReceivedArgs
            {
                ChatMessage = chatMsg
            };

            ev = CaptureEvent(() =>
                service
                    .GetType()
                    .GetMethod("HandleMessageCmdReceived", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(service, [null, args])
            );

            Assert.Null(ev);

            chatMsg = MakeMessage("!resume", true, true,
                false, "testuser", "TestUser");
            args = new OnMessageReceivedArgs
            {
                ChatMessage = chatMsg
            };

            ev = CaptureEvent(() =>
                service
                    .GetType()
                    .GetMethod("HandleMessageCmdReceived", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(service, [null, args])
            );

            Assert.Null(ev);
            await service.StopAsync();
        }

        [Fact]
        public async Task HasTokenFile_ReturnsCorrectValue()
        {
            var filePath = Path.GetFullPath(Path.Combine(string.Empty
                , "data/twitch_token.json"));
            var service = new TwitchService(null,MockConfig.MakeMockConfig());
            await File.WriteAllTextAsync(filePath, "{}");
            Assert.True(service.HasTokenFile());
            File.Delete(filePath);
            Assert.False(service.HasTokenFile());
            await service.StopAsync();
        }

        [Fact]
        public async Task RevokeTokenFile_DeletesFileAndClearsAccessToken()
        {
            var filePath = Path.GetFullPath(Path.Combine(string.Empty
                , "data/twitch_token.json"));
            await File.WriteAllTextAsync(filePath, "{}");
            var service = new TwitchService(null,MockConfig.MakeMockConfig());
            service.RevokeTokenFile();
            Assert.False(File.Exists(filePath));
            await service.StopAsync();
        }

        [Fact]
        public async Task ValidateTokenAsync_ReturnsFalse_WhenFileMissing()
        {
            var filePath = Path.GetFullPath(Path.Combine(string.Empty
                , "data/twitch_token.json"));
            var service = new TwitchService(null,MockConfig.MakeMockConfig());
            File.Delete(filePath);

            bool result = await service.ValidateTokenAsync();

            Assert.False(result);
            await service.StopAsync();
        }

        [Fact]
        public async Task ValidateTokenAsync_ReturnsFalse_WhenTokenInvalid()
        {
            var filePath = Path.GetFullPath(Path.Combine(string.Empty
                , "data/twitch_token.json"));
            var service = new TwitchService(null,MockConfig.MakeMockConfig());
            await File.WriteAllTextAsync(filePath, JsonSerializer.Serialize(new { access_token = "badtoken" }));

            bool result = await service.ValidateTokenAsync();

            Assert.False(result);
            File.Delete(filePath);
            await service.StopAsync();
        }

        [Fact]
        public async Task HandleSubGift_RaisesGiftSubEvent()
        {
            var service = new TwitchService(null,MockConfig.MakeMockConfig());

            var meta = new WebsocketEventSubMetadata
                { MessageId = Guid.NewGuid().ToString(), MessageTimestamp = DateTime.UtcNow };
            var args = new ChannelSubscriptionGiftArgs
            {
                Metadata = meta,
                Payload = new() { Event = new() { Tier = "1000", Total = 3, UserName = "gifter" } }
            };

            var ev = CaptureEvent(() =>
                service.InvokePrivate("HandleSubGift", null, args).Wait()
            );

            Assert.NotNull(ev);
            Assert.Equal(SubathonEventType.TwitchGiftSub, ev.EventType);
            Assert.Equal(SubathonEventSource.Twitch, ev.Source);
            Assert.Equal(3, ev.Amount);
            Assert.Equal("gifter", ev.User);
            await service.StopAsync();
        }

        [Fact]
        public async Task HandleBitsUse_RaisesCheerEvent()
        {
            var service = new TwitchService(null,MockConfig.MakeMockConfig());

            var meta = new WebsocketEventSubMetadata
                { MessageId = Guid.NewGuid().ToString(), MessageTimestamp = DateTime.UtcNow };
            var args = new ChannelBitsUseArgs
            {
                Metadata = meta,
                Payload = new() { Event = new() { Bits = 500, UserName = "cheerer", Type = "cheer" } }
            };

            var ev = CaptureEvent(() =>
                service.InvokePrivate("HandleBitsUse", null, args).Wait()
            );

            Assert.NotNull(ev);
            Assert.Equal(SubathonEventType.TwitchCheer, ev.EventType);
            Assert.Equal(SubathonEventSource.Twitch, ev.Source);
            Assert.Equal("cheerer", ev.User);
            Assert.Equal("500", ev.Value);
            await service.StopAsync();
        }

        [Fact]
        public async Task HandleChannelSubscribe_RaisesSubEvent()
        {
            var service = new TwitchService(null,MockConfig.MakeMockConfig());

            var args = new ChannelSubscribeArgs
            {
                Metadata = new WebsocketEventSubMetadata
                {
                    MessageId = Guid.NewGuid().ToString(),
                    MessageTimestamp = DateTime.UtcNow
                },
                Payload = new()
                {
                    Event = new()
                    {
                        Tier = "1000",
                        UserName = "subscriber",
                        IsGift = false
                    }
                }
            };

            var ev = CaptureEvent(() => service.InvokePrivate("HandleChannelSubscribe",
                null, args).Wait());

            Assert.NotNull(ev);
            Assert.Equal(SubathonEventType.TwitchSub, ev.EventType);
            Assert.Equal(SubathonEventSource.Twitch, ev.Source);
            Assert.Equal("subscriber", ev.User);
            Assert.Equal("1000", ev.Value);
            await service.StopAsync();
        }


        [Fact]
        public async Task HandleSubscriptionMsg_RaisesSubEvent()
        {
            var service = new TwitchService(null,MockConfig.MakeMockConfig());

            var args = new ChannelSubscriptionMessageArgs
            {
                Metadata = new WebsocketEventSubMetadata
                {
                    MessageId = Guid.NewGuid().ToString(),
                    MessageTimestamp = DateTime.UtcNow
                },
                Payload = new()
                {
                    Event = new()
                    {
                        Tier = "2000",
                        UserName = "subscriber"
                    }
                }
            };

            var ev = CaptureEvent(() => service.InvokePrivate("HandleSubscriptionMsg", null, args).Wait());

            Assert.NotNull(ev);
            Assert.Equal(SubathonEventType.TwitchSub, ev.EventType);
            Assert.Equal(SubathonEventSource.Twitch, ev.Source);
            Assert.Equal("subscriber", ev.User);
            Assert.Equal("2000", ev.Value);
            await service.StopAsync();
        }

        [Fact]
        public async Task HandleChannelRaid_RaisesRaidEvent()
        {
            var service = new TwitchService(null,MockConfig.MakeMockConfig());

            var args = new ChannelRaidArgs
            {
                Metadata = new WebsocketEventSubMetadata
                {
                    MessageId = Guid.NewGuid().ToString(),
                    MessageTimestamp = DateTime.UtcNow
                },
                Payload = new()
                {
                    Event = new()
                    {
                        FromBroadcasterUserName = "raider",
                        Viewers = 42
                    }
                }
            };

            var ev = CaptureEvent(() => service.InvokePrivate("HandleChannelRaid", null, args).Wait());

            Assert.NotNull(ev);
            Assert.Equal(SubathonEventType.TwitchRaid, ev.EventType);
            Assert.Equal(SubathonEventSource.Twitch, ev.Source);
            Assert.Equal("raider", ev.User);
            Assert.Equal("42", ev.Value);
            await service.StopAsync();
        }

        [Fact]
        public async Task HandleHypeTrainBeginV2_RaisesStartEvent()
        {
            var service = new TwitchService(null,MockConfig.MakeMockConfig());

            var args = new ChannelHypeTrainBeginV2Args
            {
                Metadata = new WebsocketEventSubMetadata
                {
                    MessageId = Guid.NewGuid().ToString(),
                    MessageTimestamp = DateTime.UtcNow
                },
                Payload = new()
                {
                    Event = new()
                    {
                        BroadcasterUserName = "broadcaster",
                        Level = 1
                    }
                }
            };

            var ev = CaptureEvent(() => service.InvokePrivate("HandleHypeTrainBeginV2", null, args).Wait());

            Assert.NotNull(ev);
            Assert.Equal(SubathonEventType.TwitchHypeTrain, ev.EventType);
            Assert.Equal(SubathonEventSource.Twitch, ev.Source);
            Assert.Equal("start", ev.Value);
            Assert.Equal("broadcaster", ev.User);
            Assert.Equal(1, ev.Amount);
            await service.StopAsync();
        }

        [Fact]
        public async Task HandleHypeTrainProgressV2_RaisesProgressEvent()
        {
            var service = new TwitchService(null,MockConfig.MakeMockConfig());
            await service.InvokePrivate("HandleHypeTrainBeginV2", null, new ChannelHypeTrainBeginV2Args
            {
                Metadata = new WebsocketEventSubMetadata
                {
                    MessageId = Guid.NewGuid().ToString(),
                    MessageTimestamp = DateTime.UtcNow
                },
                Payload = new()
                {
                    Event = new()
                    {
                        BroadcasterUserName = "broadcaster",
                        Level = 1
                    }
                }
            });

            var args = new ChannelHypeTrainProgressV2Args
            {
                Metadata = new WebsocketEventSubMetadata
                {
                    MessageId = Guid.NewGuid().ToString(),
                    MessageTimestamp = DateTime.UtcNow
                },
                Payload = new()
                {
                    Event = new()
                    {
                        BroadcasterUserName = "broadcaster",
                        Level = 2
                    }
                }
            };

            var ev = CaptureEvent(() => service.InvokePrivate("HandleHypeTrainProgressV2", null, args).Wait());

            Assert.NotNull(ev);
            Assert.Equal(SubathonEventType.TwitchHypeTrain, ev.EventType);
            Assert.Equal(SubathonEventSource.Twitch, ev.Source);
            Assert.Equal("progress", ev.Value);
            Assert.Equal("broadcaster", ev.User);
            Assert.Equal(2, ev.Amount);
            await service.StopAsync();
        }

        [Fact]
        public async Task HandleHypeTrainEndV2_RaisesEndEvent()
        {
            var service = new TwitchService(null,MockConfig.MakeMockConfig());

            var args = new ChannelHypeTrainEndV2Args
            {
                Metadata = new WebsocketEventSubMetadata
                {
                    MessageId = Guid.NewGuid().ToString(),
                    MessageTimestamp = DateTime.UtcNow
                },
                Payload = new()
                {
                    Event = new()
                    {
                        BroadcasterUserName = "broadcaster",
                        Level = 5
                    }
                }
            };

            var ev = CaptureEvent(() => service.InvokePrivate("HandleHypeTrainEndV2", null, args).Wait());

            Assert.NotNull(ev);
            Assert.Equal(SubathonEventType.TwitchHypeTrain, ev.EventType);
            Assert.Equal(SubathonEventSource.Twitch, ev.Source);
            Assert.Equal("end", ev.Value);
            Assert.Equal("broadcaster", ev.User);
            Assert.Equal(5, ev.Amount);
            await service.StopAsync();
        }

        [Fact]
        public async Task HandleCharityEvent_RaisesDonationEvent()
        {
            var service = new TwitchService(null,MockConfig.MakeMockConfig());

            var args = new ChannelCharityCampaignDonateArgs
            {
                Metadata = new WebsocketEventSubMetadata
                {
                    MessageId = Guid.NewGuid().ToString(),
                    MessageTimestamp = DateTime.UtcNow
                },
                Payload = new()
                {
                    Event = new()
                    {
                        UserName = "donor",
                        Amount = new()
                        {
                            Value = 2550,
                            DecimalPlaces = 2,
                            Currency = "CAD"
                        }
                    }
                }
            };

            var ev = CaptureEvent(() => service.InvokePrivate("HandleCharityEvent", null, args).Wait());

            Assert.NotNull(ev);
            Assert.Equal(SubathonEventType.TwitchCharityDonation, ev.EventType);
            Assert.Equal(SubathonEventSource.Twitch, ev.Source);
            Assert.Equal("donor", ev.User);
            Assert.Equal("25.50", ev.Value);
            Assert.Equal("CAD", ev.Currency);
            await service.StopAsync();
        }

        [Fact]
        public async Task StopAsync_CanBeCalledTwice_Safely()
        {
            // in case any listeners still exist
            var service = new TwitchService(null,MockConfig.MakeMockConfig());
            await service.StartAsync(CancellationToken.None);

            await service.StopAsync(CancellationToken.None);
            await service.StopAsync(CancellationToken.None);
        }

        [Fact]
        public async Task HandleChatMessage_BlerpNotification()
        {
            var config =MockConfig.MakeMockConfig();
            var service = new TwitchService(null, config);

            service.Login = "teststreamer";

            ChatMessage MakeMessage(string message, bool isVip, bool isMod, bool isBroadcaster, string userName,
                string displayName)
            {
                TwitchLib.Client.Enums.UserType type = UserType.Viewer;
                if (isMod) type = UserType.Moderator;
                if (isBroadcaster) type = UserType.Broadcaster;

                return new ChatMessage(
                    botUsername: "",
                    userId: "123456789",
                    userName: userName,
                    displayName: displayName,
                    colorHex: "#ffffff",
                    color: Color.Black,
                    emoteSet: new("", ""),
                    message: message,
                    userType: type,
                    channel: "teststreamer",
                    id: Guid.NewGuid().ToString(),
                    isSubscriber: true,
                    subscribedMonthCount: 4,
                    roomId: "098765432",
                    isTurbo: false,
                    isModerator: isMod,
                    isMe: false,
                    isBroadcaster: isBroadcaster,
                    isVip: isVip,
                    isPartner: false,
                    isStaff: false,
                    noisy: Noisy.NotSet,
                    rawIrcMessage: "",
                    emoteReplacedMessage: "",
                    badges: [],
                    cheerBadge: new CheerBadge(0),
                    bits: 0,
                    bitsInDollars: 0
                );

            }

            var chatMsg = MakeMessage("SomeGuy used 500 bits to play XYZ", false, false,
                false, "blerp", "blerp");
            var args = new OnMessageReceivedArgs
            {
                ChatMessage = chatMsg
            };


            var ev = CaptureEvent(() =>
                service
                    .GetType()
                    .GetMethod("HandleMessageCmdReceived", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(service, [null, args])
            );

            Assert.NotNull(ev);
            Assert.Equal(SubathonEventType.BlerpBits, ev.EventType);
            Assert.Equal(SubathonEventSource.Blerp, ev.Source);
            Assert.Equal("SomeGuy", ev.User);
            await service.StopAsync();
        }

        [Fact]
        public async Task HandleChatMessage_BlerpNotificationWrongChat()
        {
            var config =MockConfig.MakeMockConfig();
            var service = new TwitchService(null, config);

            service.Login = "teststreamer2";

            ChatMessage MakeMessage(string message, bool isVip, bool isMod, bool isBroadcaster, string userName,
                string displayName)
            {
                TwitchLib.Client.Enums.UserType type = UserType.Viewer;
                if (isMod) type = UserType.Moderator;
                if (isBroadcaster) type = UserType.Broadcaster;

                return new ChatMessage(
                    botUsername: "",
                    userId: "123456789",
                    userName: userName,
                    displayName: displayName,
                    colorHex: "#ffffff",
                    color: Color.Black,
                    emoteSet: new("", ""),
                    message: message,
                    userType: type,
                    channel: "teststreamer",
                    id: Guid.NewGuid().ToString(),
                    isSubscriber: true,
                    subscribedMonthCount: 4,
                    roomId: "098765432",
                    isTurbo: false,
                    isModerator: isMod,
                    isMe: false,
                    isBroadcaster: isBroadcaster,
                    isVip: isVip,
                    isPartner: false,
                    isStaff: false,
                    noisy: Noisy.NotSet,
                    rawIrcMessage: "",
                    emoteReplacedMessage: "",
                    badges: [],
                    cheerBadge: new CheerBadge(0),
                    bits: 0,
                    bitsInDollars: 0
                );

            }

            var chatMsg = MakeMessage("SomeGuy used 500 bits to play XYZ", false, false,
                false, "blerp", "blerp");
            var args = new OnMessageReceivedArgs
            {
                ChatMessage = chatMsg
            };


            var ev = CaptureEvent(() =>
                service
                    .GetType()
                    .GetMethod("HandleMessageCmdReceived", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(service, [null, args])
            );

            Assert.Null(ev);
            await service.StopAsync();
        }

        [Fact]
        public async Task StartOAuthFlow_WritesTokenFile()
        {
            var tokenFilePath = Path.GetFullPath("data/twitch_token.json");
            if (File.Exists(tokenFilePath)) File.Delete(tokenFilePath);

            var service = new TwitchService(null,MockConfig.MakeMockConfig());
            service.TwitchOAuthUrl = "http://localhost/fake";
            service.OpenBrowser = _ => { };
            service.CallbackPort = MockEventSubServer.GetFreePort();

            var fakeToken = "test_access_token_abc123";

            var browserSim = Task.Run(async () =>
            {
                await Task.Delay(200);

                using (var tcp = new System.Net.Sockets.TcpClient())
                {
                    await tcp.ConnectAsync(IPAddress.Loopback, service.CallbackPort);
                    var stream = tcp.GetStream();
                    var req = "GET /auth/twitch/callback/ HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(req));
                    var buf = new byte[4096];
                    while (await stream.ReadAsync(buf) > 0)
                    {
                    }
                }

                await Task.Delay(50);

                using (var tcp = new System.Net.Sockets.TcpClient())
                {
                    await tcp.ConnectAsync(IPAddress.Loopback, service.CallbackPort);
                    var stream = tcp.GetStream();
                    var req =
                        $"GET /token?access_token={fakeToken}&token_type=bearer HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(req));
                    var buf = new byte[4096];
                    while (await stream.ReadAsync(buf) > 0)
                    {
                    }
                }
            });

            var oauthMethod = typeof(TwitchService)
                .GetMethod("StartOAuthFlowAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

            await Task.WhenAll(
                browserSim,
                (Task)oauthMethod.Invoke(service, null)!
            );

            Assert.True(File.Exists(tokenFilePath));
            var json = await File.ReadAllTextAsync(tokenFilePath);
            var data = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            Assert.Equal(fakeToken, data!["access_token"]);

            File.Delete(tokenFilePath);
            await service.StopAsync();
        }
        
        [Fact]
        public async Task EventSub_ConnectsAndFiresConnectionUpdate()
        {
            await using var wsServer = new MockEventSubServer();

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            void Handler(bool b, SubathonEventSource source, string name, string svc)
            {
                if (svc == "EventSub") tcs.TrySetResult(b);
            }

            IntegrationEvents.ConnectionUpdated += Handler;
            try
            {
                var service = new TwitchService(null,MockConfig.MakeMockConfig());
                service.EventSubUrl = wsServer.Uri;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await (Task)typeof(TwitchService)
                            .GetMethod("InitializeEventSubAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                            .Invoke(service, null)!;
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetException(ex);
                    }
                });

                var result = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
                Assert.True(result == tcs.Task, "Timed out — EventSub ConnectionUpdated never fired");
                Assert.True(await tcs.Task, "EventSub connected=false, expected true");
                service.Dispose();
            }
            finally
            {
                IntegrationEvents.ConnectionUpdated -= Handler;
            }
        }
    }
}