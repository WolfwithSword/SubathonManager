using Moq;
using TwitchLib.Client.Events;
using TwitchLib.EventSub.Core.EventArgs.Stream;
using TwitchLib.EventSub.Core.EventArgs.Channel;
using TwitchLib.EventSub.Websockets.Core.Models;
using TwitchLib.Client.Enums;
using System.Drawing;
using System.Text.Json;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Models;
using SubathonManager.Services;
using SubathonManager.Integration;
using System.Reflection;
using TwitchLib.Client.Models;
using IniParser.Model;
using UserType = TwitchLib.Client.Enums.UserType;

namespace SubathonManager.Tests.IntegrationUnitTests
{
    public class TwitchServiceTests
    {
        public TwitchServiceTests()
        {
            typeof(SubathonEvents)
                .GetField("SubathonEventCreated", BindingFlags.Static | BindingFlags.NonPublic)
                ?.SetValue(null, null);
            
            var path = Path.GetFullPath(Path.Combine(string.Empty
                , "data"));
            Directory.CreateDirectory(path);
        }
        
        private static SubathonEvent CaptureEvent(Action trigger)
        {
            SubathonEvent? captured = null;
            void EventCaptureHandler(SubathonEvent e) => captured = e;

            SubathonEvents.SubathonEventCreated += EventCaptureHandler;
            try
            {
                trigger();
                return captured!;
            }
            finally
            {
                SubathonEvents.SubathonEventCreated -= EventCaptureHandler;
            }
        }
        
        private static IConfig MockConfig(Dictionary<(string, string), string>? values = null)
        {
            var mock = new Mock<IConfig>();
            mock.Setup(c => c.Get(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                .Returns((string s, string k, string d) =>
                    values != null && values.TryGetValue((s, k), out var v) ? v : d);
            
            var kd = new KeyData("Commands.Pause");
            kd.Value = "pause";
            mock.Setup(c => c.GetSection("Twitch")).Returns(() =>
            {
                var kdc = new KeyDataCollection();
                kdc.AddKey(kd);
                return kdc;
            });
            
            return mock.Object;
        }
        
        
        [Fact]
        public void SimulateRaid_RaisesRaidEvent()
        {
            var ev = CaptureEvent(() => TwitchService.SimulateRaid(123));

            Assert.Equal(SubathonEventSource.Simulated, ev.Source);
            Assert.Equal(SubathonEventType.TwitchRaid, ev.EventType);
            Assert.Equal("123", ev.Value);
            Assert.Equal("SYSTEM", ev.User);
        }


        [Fact]
        public void SimulateCheer_RaisesCheerEvent()
        {
            var ev = CaptureEvent(() => TwitchService.SimulateCheer(500));

            Assert.Equal(SubathonEventType.TwitchCheer, ev.EventType);
            Assert.Equal("bits", ev.Currency);
            Assert.Equal("500", ev.Value);
        }

        [Fact]
        public void SimulateSubscription_InvalidTier_DoesNotRaise()
        {
            SubathonEvent? captured = null;
            void Handler(SubathonEvent e) => captured = e;

            SubathonEvents.SubathonEventCreated += Handler;
            try
            {
                TwitchService.SimulateSubscription("9000");
                Assert.Null(captured);
            }
            finally
            {
                SubathonEvents.SubathonEventCreated -= Handler;
            }
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
            Assert.Equal(SubathonEventType.TwitchSub, ev!.EventType);
            Assert.Equal(SubathonEventSource.Simulated, ev!.Source);
            Assert.Equal("sub", ev.Currency);
            Assert.Equal(1, ev.Amount);
            Assert.Equal(tier, ev.Value);
            
        }

        [Fact]
        public void SimulateGiftSubscriptions_ShouldRaiseGiftSubEvent()
        {
            SubathonEvent? ev = CaptureEvent(() => TwitchService.SimulateGiftSubscriptions("1000", 5));
            Assert.NotNull(ev);
            Assert.Equal(SubathonEventType.TwitchGiftSub, ev!.EventType);
            Assert.Equal(SubathonEventSource.Simulated, ev!.Source);
            Assert.Equal("sub", ev.Currency);
            Assert.Equal("1000", ev.Value);
            Assert.Equal(5, ev.Amount);
        }

        [Fact]
        public void SimulateFollow_ShouldRaiseFollowEvent()
        {
            SubathonEvent? ev = CaptureEvent(() => TwitchService.SimulateFollow());
            Assert.NotNull(ev);
            Assert.Equal(SubathonEventType.TwitchFollow, ev!.EventType);
            Assert.Equal(SubathonEventSource.Simulated, ev.Source);
        }

        [Fact]
        public void SimulateCharityDonation_ShouldRaiseDonationEvent()
        {
            SubathonEvent? ev = CaptureEvent(() => TwitchService.SimulateCharityDonation("25.50", "CAD"));

            Assert.NotNull(ev);
            Assert.Equal(SubathonEventType.TwitchCharityDonation, ev!.EventType);
            Assert.Equal(SubathonEventSource.Simulated, ev!.Source);
            Assert.Equal("25.50", ev.Value);
            Assert.Equal("CAD", ev.Currency);
        }

        [Fact]
        public void SimulateHypeTrainStart_ShouldRaiseStartEvent()
        {
            SubathonEvent? ev = CaptureEvent(() => TwitchService.SimulateHypeTrainStart());

            Assert.Equal(SubathonEventSource.Simulated, ev!.Source);
            Assert.NotNull(ev);
            Assert.Equal(SubathonEventType.TwitchHypeTrain, ev!.EventType);
            Assert.Equal("start", ev.Value);
            Assert.Equal(1, ev.Amount);
        }

        [Fact]
        public void SimulateHypeTrainProgress_ShouldOnlyRaiseIfLevelIncreases()
        {
            SubathonEvent? ev = CaptureEvent(() => TwitchService.SimulateHypeTrainStart());
            SubathonEvent? ev2 = CaptureEvent(() => TwitchService.SimulateHypeTrainProgress(5));
            Assert.Equal(SubathonEventSource.Simulated, ev2!.Source);
            Assert.Equal(SubathonEventType.TwitchHypeTrain, ev2!.EventType);
            Assert.NotNull(ev2);
            Assert.Equal("progress", ev2!.Value);
            Assert.Equal(5, ev2.Amount);
        }

        [Fact]
        public void SimulateHypeTrainEnd_ShouldRun()
        {
            SubathonEvent? ev = CaptureEvent(() => TwitchService.SimulateHypeTrainEnd(10));
            Assert.Equal(SubathonEventSource.Simulated, ev!.Source);
            Assert.Equal(SubathonEventType.TwitchHypeTrain, ev!.EventType);
            Assert.NotNull(ev);
            Assert.Equal("end", ev!.Value);
            Assert.Equal(10, ev.Amount);
        }
        
        [Fact]
        public void HandleChannelOnline_ResumeOnStart_RaisesResumeCommand()
        {
            var config = MockConfig(new()
            {
                { ("Twitch", "ResumeOnStart"), "true" }
            });

            var service = new TwitchService(null, config);

            var ev = CaptureEvent(() =>
                service
                    .GetType()
                    .GetMethod("HandleChannelOnline", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(service, new object?[] { null, new StreamOnlineArgs() })
            );

            Assert.Equal(SubathonCommandType.Resume, ev.Command);
            Assert.Equal(SubathonEventType.Command, ev.EventType);
            Assert.Equal("AUTO", ev.User);
        }      
        
        [Fact]
        public void HandleChannelOnline_UnlockOnStart_RaisesResumeCommand()
        {
            var config = MockConfig(new()
            {
                { ("Twitch", "UnlockOnStart"), "true" }
            });

            var service = new TwitchService(null, config);

            var ev = CaptureEvent(() =>
                service
                    .GetType()
                    .GetMethod("HandleChannelOnline", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(service, new object?[] { null, new StreamOnlineArgs() })
            );

            Assert.Equal(SubathonCommandType.Unlock, ev.Command);
            Assert.Equal(SubathonEventType.Command, ev.EventType);
            Assert.Equal("AUTO", ev.User);
        }

        [Fact]
        public void HandleChannelOffline_PauseOnEnd_RaisesPauseCommand()
        {
            var config = MockConfig(new()
            {
                { ("Twitch", "PauseOnEnd"), "true" }
            });

            var service = new TwitchService(null, config);

            var ev = CaptureEvent(() =>
                service
                    .GetType()
                    .GetMethod("HandleChannelOffline", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(service, new object?[] { null, new StreamOfflineArgs() })
            );

            Assert.Equal(SubathonCommandType.Pause, ev.Command);
            Assert.Equal(SubathonEventType.Command, ev.EventType);
            Assert.Equal("AUTO", ev.User);
        }
        
        [Fact]
        public void HandleChannelOffline_LockOnEnd_RaisesPauseCommand()
        {
            var config = MockConfig(new()
            {
                { ("Twitch", "LockOnEnd"), "true" }
            });

            var service = new TwitchService(null, config);

            var ev = CaptureEvent(() =>
                service
                    .GetType()
                    .GetMethod("HandleChannelOffline", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(service, new object?[] { null, new StreamOfflineArgs() })
            );

            Assert.Equal(SubathonCommandType.Lock, ev.Command);
            Assert.Equal(SubathonEventType.Command, ev.EventType);
            Assert.Equal("AUTO", ev.User);
        }
        
        [Fact]
        public void HandleChannelFollow_RaisesFollowEvent()
        {
            var service = new TwitchService(null, MockConfig());

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
                    .Invoke(service, new object?[] { null, args })
            );

            Assert.Equal(SubathonEventType.TwitchFollow, ev.EventType);
            Assert.Equal("Follower123", ev.User);
            Assert.Equal(SubathonEventSource.Twitch, ev.Source);
        }

        [Fact]
        public void HandleHypeTrainProgress_IgnoresSameOrLowerLevel()
        {
            TwitchService.SimulateHypeTrainStart();

            SubathonEvent? captured = null;
            void Handler(SubathonEvent e) => captured = e;

            SubathonEvents.SubathonEventCreated += Handler;
            try
            {
                TwitchService.SimulateHypeTrainProgress(1);
                Assert.Null(captured);
            }
            finally
            {
                SubathonEvents.SubathonEventCreated -= Handler;
            }
        }
        
        [Fact]
        public void HandleChatMessage_Command_RaisesCommandEvent()
        {
            var config = MockConfig(new()
            {
                { ("Twitch", "Commands.Pause"), "pause" },

                { ("Twitch", "Commands.Pause.permissions.Mods"), "true" },
                { ("Twitch", "Commands.Pause.permissions.VIPs"), "false" },
                { ("Twitch", "Commands.Pause.permissions.Whitelist"), "specialguy" }
            });
            CommandService.SetConfig(config);
            var service = new TwitchService(null, config);

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
                    badges: new List<KeyValuePair<string, string>>(),
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
                    .Invoke(service, new object?[] { null, args })
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
                    .Invoke(service, new object?[] { null, args })
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
                    .Invoke(service, new object?[] { null, args })
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
                    .Invoke(service, new object?[] { null, args })
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
                    .Invoke(service, new object?[] { null, args })
            );

            Assert.Null(ev);
        }

        [Fact]
        public void HasTokenFile_ReturnsCorrectValue()
        {
            var filePath = Path.GetFullPath(Path.Combine(string.Empty
                , "data/twitch_token.json"));
            var service = new TwitchService(null, MockConfig());
            File.WriteAllText(filePath, "{}");
            Assert.True(service.HasTokenFile());
            File.Delete(filePath);
            Assert.False(service.HasTokenFile());
        }
        
        [Fact]
        public void RevokeTokenFile_DeletesFileAndClearsAccessToken()
        {
            var filePath = Path.GetFullPath(Path.Combine(string.Empty
                , "data/twitch_token.json"));
            File.WriteAllText(filePath, "{}");
            var service = new TwitchService(null, MockConfig());
            service.RevokeTokenFile();
            Assert.False(File.Exists(filePath));
        }
        
        [Fact]
        public async Task ValidateTokenAsync_ReturnsFalse_WhenFileMissing()
        {
            var filePath = Path.GetFullPath(Path.Combine(string.Empty
                , "data/twitch_token.json"));
            var service = new TwitchService(null, MockConfig());
            File.Delete(filePath);
    
            bool result = await service.ValidateTokenAsync();
    
            Assert.False(result);
        }

        [Fact]
        public async Task ValidateTokenAsync_ReturnsFalse_WhenTokenInvalid()
        {
            var filePath = Path.GetFullPath(Path.Combine(string.Empty
                , "data/twitch_token.json"));
            var service = new TwitchService(null, MockConfig());
            await File.WriteAllTextAsync(filePath, JsonSerializer.Serialize(new { access_token = "badtoken" }));

            bool result = await service.ValidateTokenAsync();

            Assert.False(result);
            File.Delete(filePath);
        }
        
        [Fact]
        public async Task HandleSubGift_RaisesGiftSubEvent()
        {
            var service = new TwitchService(null, MockConfig());
    
            var meta = new WebsocketEventSubMetadata { MessageId = Guid.NewGuid().ToString(), MessageTimestamp = DateTime.UtcNow };
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
        }
        
        [Fact]
        public async Task HandleBitsUse_RaisesCheerEvent()
        {
            var service = new TwitchService(null, MockConfig());

            var meta = new WebsocketEventSubMetadata { MessageId = Guid.NewGuid().ToString(), MessageTimestamp = DateTime.UtcNow };
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
        }

        [Fact]
        public async Task HandleChannelSubscribe_RaisesSubEvent()
        {
            var service = new TwitchService(null, MockConfig());

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

            Assert.Equal(SubathonEventType.TwitchSub, ev.EventType);
            Assert.Equal(SubathonEventSource.Twitch, ev.Source);
            Assert.Equal("subscriber", ev.User);
            Assert.Equal("1000", ev.Value);
        }
        

        [Fact]
        public async Task HandleSubscriptionMsg_RaisesSubEvent()
        {
            var service = new TwitchService(null, MockConfig());

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

            Assert.Equal(SubathonEventType.TwitchSub, ev.EventType);
            Assert.Equal(SubathonEventSource.Twitch, ev.Source);
            Assert.Equal("subscriber", ev.User);
            Assert.Equal("2000", ev.Value);
        }
        
        [Fact]
        public async Task HandleChannelRaid_RaisesRaidEvent()
        {
            var service = new TwitchService(null, MockConfig());

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

            Assert.Equal(SubathonEventType.TwitchRaid, ev.EventType);
            Assert.Equal(SubathonEventSource.Twitch, ev.Source);
            Assert.Equal("raider", ev.User);
            Assert.Equal("42", ev.Value);
        }
        
        [Fact]
        public async Task HandleHypeTrainBeginV2_RaisesStartEvent()
        {
            var service = new TwitchService(null, MockConfig());

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

            Assert.Equal(SubathonEventType.TwitchHypeTrain, ev.EventType);
            Assert.Equal(SubathonEventSource.Twitch, ev.Source);
            Assert.Equal("start", ev.Value);
            Assert.Equal("broadcaster", ev.User);
            Assert.Equal(1, ev.Amount);
        }
        
        [Fact]
        public async Task HandleHypeTrainProgressV2_RaisesProgressEvent()
        {
            var service = new TwitchService(null, MockConfig());
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

            Assert.Equal(SubathonEventType.TwitchHypeTrain, ev.EventType);
            Assert.Equal(SubathonEventSource.Twitch, ev.Source);
            Assert.Equal("progress", ev.Value);
            Assert.Equal("broadcaster", ev.User);
            Assert.Equal(2, ev.Amount);
        }
        
        [Fact]
        public async Task HandleHypeTrainEndV2_RaisesEndEvent()
        {
            var service = new TwitchService(null, MockConfig());

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

            Assert.Equal(SubathonEventType.TwitchHypeTrain, ev.EventType);
            Assert.Equal(SubathonEventSource.Twitch, ev.Source);
            Assert.Equal("end", ev.Value);
            Assert.Equal("broadcaster", ev.User);
            Assert.Equal(5, ev.Amount);
        }
        
        [Fact]
        public async Task HandleCharityEvent_RaisesDonationEvent()
        {
            var service = new TwitchService(null, MockConfig());

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

            Assert.Equal(SubathonEventType.TwitchCharityDonation, ev.EventType);
            Assert.Equal(SubathonEventSource.Twitch, ev.Source);
            Assert.Equal("donor", ev.User);
            Assert.Equal("25.50", ev.Value);
            Assert.Equal("CAD", ev.Currency);
        }
        
        [Fact]
        public async Task StopAsync_CanBeCalledTwice_Safely()
        {
            // in case any listeners still exist
            var service = new TwitchService(null, MockConfig());

            await service.StopAsync(CancellationToken.None);
            await service.StopAsync(CancellationToken.None);
        }

    }
    
}
