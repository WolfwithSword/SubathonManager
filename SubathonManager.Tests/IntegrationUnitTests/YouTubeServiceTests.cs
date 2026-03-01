using Moq;
using Microsoft.Extensions.Logging;
using SubathonManager.Integration;
using SubathonManager.Core.Events;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Models;
using SubathonManager.Services;
using YTLiveChat.Contracts.Models;
using YTLiveChat.Contracts.Services;
using IniParser.Model;
using System.Reflection;
using SubathonManager.Core.Interfaces;

namespace SubathonManager.Tests.IntegrationUnitTests
{
    [Collection("IntegrationEventTests")]
    public class YouTubeServiceTests
    {
        private static IConfig MockConfig(Dictionary<(string, string), string>? values = null)
        {
            var mock = new Mock<IConfig>();
            mock.Setup(c => c.Get(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                .Returns((string s, string k, string d) =>
                    values != null && values.TryGetValue((s, k), out var v) ? v : d);
            
            var kd = new KeyData("Commands.Pause");
            kd.Value = "pause";
            mock.Setup(c => c.GetSection("Chat")).Returns(() =>
            {
                var kdc = new KeyDataCollection();
                kdc.AddKey(kd);
                return kdc;
            });
            
            return mock.Object;
        }
        
        public YouTubeServiceTests()
        {
            typeof(SubathonEvents)
                .GetField("SubathonEventCreated", BindingFlags.Static | BindingFlags.NonPublic)
                ?.SetValue(null, null);
        }
        [Fact]
        public void SimulateSuperChat_ShouldRaiseEvent()
        {
            SubathonEvent? captured = null;
            Action<SubathonEvent> handler = ev => captured = ev;
            SubathonEvents.SubathonEventCreated += handler;
            YouTubeService.SimulateSuperChat("12.5", "USD");

            Assert.NotNull(captured);
            Assert.Equal("12.5", captured!.Value);
            Assert.Equal("USD", captured.Currency);
            Assert.Equal(SubathonEventSource.Simulated, captured.Source);
            Assert.Equal(SubathonEventType.YouTubeSuperChat, captured.EventType);
            SubathonEvents.SubathonEventCreated -= handler;
        }

        [Fact]
        public void SimulateMembership_ShouldRaiseEvent()
        {
            typeof(SubathonEvents)
                .GetField("SubathonEventCreated", BindingFlags.Static | BindingFlags.NonPublic)
                ?.SetValue(null, null);

            SubathonEvent? captured = null;
            Action<SubathonEvent> handler = ev => captured = ev;
            SubathonEvents.SubathonEventCreated += handler;

            YouTubeService.SimulateMembership("Gold");

            Assert.NotNull(captured);
            Assert.Equal("Gold", captured!.Value);
            Assert.Equal("member", captured.Currency);
            Assert.Equal(SubathonEventSource.Simulated, captured.Source);
            Assert.Equal(SubathonEventType.YouTubeMembership, captured.EventType);
            SubathonEvents.SubathonEventCreated -= handler;
        }

        [Fact]
        public void SimulateGiftMemberships_ShouldRaiseEvent()
        {
            SubathonEvent? captured = null;
            Action<SubathonEvent> handler = ev => captured = ev;
            SubathonEvents.SubathonEventCreated += handler;

            YouTubeService.SimulateGiftMemberships(3);

            Assert.NotNull(captured);
            Assert.Equal("member", captured!.Currency);
            Assert.Equal(SubathonEventSource.Simulated, captured.Source);
            Assert.Equal(SubathonEventType.YouTubeGiftMembership, captured.EventType);
            Assert.Equal(3, captured.Amount);
            SubathonEvents.SubathonEventCreated -= handler;
        }

        [Fact]
        public void OnInitialPageLoaded_ShouldSetRunningTrueAndRaiseEvent()
        {
            var logger = new Mock<ILogger<YouTubeService>>();
            var chatLogger = new Mock<ILogger<YTLiveChat.Services.YTLiveChat>>();
            var httpLogger = new Mock<ILogger<YTLiveChat.Services.YTHttpClient>>();
            var config = new Mock<Config>();

            var service = new YouTubeService(logger.Object, config.Object, httpLogger.Object, chatLogger.Object);

            bool eventRaised = false;
            Action<bool, SubathonEventSource, string, string> handler = (running, source, handle, service) =>
            {
                if (source == SubathonEventSource.YouTube)
                {
                    eventRaised = true;
                    Assert.True(running);
                }
            };
            IntegrationEvents.ConnectionUpdated += handler;

            try
            {
                var method = typeof(YouTubeService)
                    .GetMethod("OnInitialPageLoaded", BindingFlags.NonPublic | BindingFlags.Instance);

                var args = new YTLiveChat.Contracts.Services.InitialPageLoadedEventArgs
                {
                    LiveId = "TEST_LIVE_ID"
                };

                method?.Invoke(service, new object?[] { null, args });

                Assert.True(eventRaised);
                Assert.True(service.Running);
            }
            finally
            {
                IntegrationEvents.ConnectionUpdated -= handler;
            }
        }


        [Fact]
        public void OnErrorOccurred_ShouldSetRunningFalseAndRaiseError()
        {
            var logger = new Mock<ILogger<YouTubeService>>();
            var chatLogger = new Mock<ILogger<YTLiveChat.Services.YTLiveChat>>();
            var httpLogger = new Mock<ILogger<YTLiveChat.Services.YTHttpClient>>();
            var config = new Mock<Config>();
            var service = new YouTubeService(logger.Object, config.Object, httpLogger.Object, chatLogger.Object);

            bool eventRaised = false;
            Action<bool, SubathonEventSource, string, string> handler = (running, source, handle, service) =>
            {
                if (source == SubathonEventSource.YouTube)
                {
                    eventRaised = true;
                    Assert.False(running);
                }
            };

            IntegrationEvents.ConnectionUpdated += handler;

            try
            {
                var method = typeof(YouTubeService)
                    .GetMethod("OnErrorOccurred", BindingFlags.NonPublic | BindingFlags.Instance);

                var errorArgs = new YTLiveChat.Contracts.Services.ErrorOccurredEventArgs(new Exception("Test error"));
                method?.Invoke(service, new object?[] { null, errorArgs });

                Assert.True(eventRaised);
                Assert.False(service.Running);
            }
            finally
            {
                IntegrationEvents.ConnectionUpdated -= handler;
            }
        }

        [Fact]
        public void OnChatReceived_ShouldRaiseSuperChatEvent()
        {
            var logger = new Mock<ILogger<YouTubeService>>();
            var chatLogger = new Mock<ILogger<YTLiveChat.Services.YTLiveChat>>();
            var httpLogger = new Mock<ILogger<YTLiveChat.Services.YTHttpClient>>();
            var config = new Mock<Config>();
            var service = new YouTubeService(logger.Object, config.Object, httpLogger.Object, chatLogger.Object);
            
            SubathonEvent? capturedEvent = null;
            Action<SubathonEvent> handler = e => capturedEvent = e;
            SubathonEvents.SubathonEventCreated += handler;

            try
            {
                var field = typeof(YouTubeService)
                    .GetField("_ytHandle", BindingFlags.NonPublic | BindingFlags.Instance);
                field!.SetValue(service, "@TestChannel");

                service.Running = true;
                
                var method = typeof(YouTubeService)
                    .GetMethod("OnChatReceived", BindingFlags.NonPublic | BindingFlags.Instance);

                var chatItem = new ChatItem
                {
                    Id = Guid.NewGuid().ToString(),
                    Timestamp = DateTimeOffset.UtcNow,
                    Author = new Author { Name = "TestUser", ChannelId = "TestChannelId" },
                    Message = Array.Empty<MessagePart>(),
                    Superchat = new Superchat
                    {
                        AmountString = "$5.00", AmountValue = (decimal)5.00, Currency = "CA$",
                        BodyBackgroundColor = "",
                    }
                };

                var eventArgs = new ChatReceivedEventArgs { ChatItem = chatItem };
                method?.Invoke(service, new object?[] { null, eventArgs });

                Assert.NotNull(capturedEvent);
                Assert.Equal("TestUser", capturedEvent!.User);
                Assert.Equal("CA$", capturedEvent.Currency);
                Assert.Equal("5", capturedEvent.Value);
                Assert.Equal(SubathonEventSource.YouTube, capturedEvent.Source);
                Assert.Equal(SubathonEventType.YouTubeSuperChat, capturedEvent.EventType);
            }
            finally
            {
                SubathonEvents.SubathonEventCreated -= handler;
            }
        }
        
        [Fact]
        public void OnChatReceived_Membership_New_RaisesEvent()
        {
            var logger = new Mock<ILogger<YouTubeService>>();
            var chatLogger = new Mock<ILogger<YTLiveChat.Services.YTLiveChat>>();
            var httpLogger = new Mock<ILogger<YTLiveChat.Services.YTHttpClient>>();
            var config = new Mock<Config>();
            var service = new YouTubeService(logger.Object, config.Object, httpLogger.Object, chatLogger.Object);
            SubathonEvent? captured = null;
            Action<SubathonEvent> handler = e => captured = e;
            SubathonEvents.SubathonEventCreated += handler;

            service.Running = true;
            typeof(YouTubeService)
                .GetField("_ytHandle", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(service, "@test");

            var chatItem = new ChatItem
            {
                Id = Guid.NewGuid().ToString(),
                Message = Array.Empty<MessagePart>(),
                Timestamp = DateTimeOffset.UtcNow,
                Author = new Author { Name = "MemberUser", ChannelId = "MemberChannelId" },
                MembershipDetails = new MembershipDetails
                {
                    EventType = MembershipEventType.New,
                    HeaderSubtext = "New Member",
                    LevelName = "DEFAULT"
                }
            };

            var args = new ChatReceivedEventArgs { ChatItem = chatItem };
            typeof(YouTubeService)
                .GetMethod("OnChatReceived", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(service, new object?[] { null, args });

            Assert.NotNull(captured);
            Assert.Equal(SubathonEventSource.YouTube, captured!.Source);
            Assert.Equal(SubathonEventType.YouTubeMembership, captured!.EventType);
            Assert.Equal("MemberUser", captured.User);

            SubathonEvents.SubathonEventCreated -= handler;
        }
        
        [Fact]
        public void OnChatReceived_Membership_Gift_RaisesEvent()
        {
            var logger = new Mock<ILogger<YouTubeService>>();
            var chatLogger = new Mock<ILogger<YTLiveChat.Services.YTLiveChat>>();
            var httpLogger = new Mock<ILogger<YTLiveChat.Services.YTHttpClient>>();
            var config = new Mock<Config>();
            var service = new YouTubeService(logger.Object, config.Object, httpLogger.Object, chatLogger.Object);
            SubathonEvent? captured = null;
            Action<SubathonEvent> handler = e => captured = e;
            SubathonEvents.SubathonEventCreated += handler;
            
            service.Running = true;
            typeof(YouTubeService)
                .GetField("_ytHandle", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(service, "@test");

            var chatItem = new ChatItem
            {
                Id = Guid.NewGuid().ToString(),
                Message = Array.Empty<MessagePart>(),
                Timestamp = DateTimeOffset.UtcNow,
                Author = new Author { Name = "MemberUser", ChannelId = "MemberChannelId" },
                MembershipDetails = new MembershipDetails
                {
                    EventType = MembershipEventType.GiftPurchase,
                    GifterUsername = "Gifter",
                    GiftCount = 3
                }
            };

            var args = new ChatReceivedEventArgs { ChatItem = chatItem };
            typeof(YouTubeService)
                .GetMethod("OnChatReceived", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(service, new object?[] { null, args });

            Assert.NotNull(captured);
            Assert.Equal(SubathonEventSource.YouTube, captured!.Source);
            Assert.Equal(SubathonEventType.YouTubeGiftMembership, captured!.EventType);
            Assert.Equal("Gifter", captured.User);
            Assert.Equal(3, captured.Amount);

            SubathonEvents.SubathonEventCreated -= handler;
        }
        
        [Fact]
        public void OnChatReceived_Membership_GiftRedemption_DoesNotRaiseEvent()
        {
            var logger = new Mock<ILogger<YouTubeService>>();
            var chatLogger = new Mock<ILogger<YTLiveChat.Services.YTLiveChat>>();
            var httpLogger = new Mock<ILogger<YTLiveChat.Services.YTHttpClient>>();
            var config = new Mock<Config>();
            var service = new YouTubeService(logger.Object, config.Object, httpLogger.Object, chatLogger.Object); 
            bool eventRaised = false;
            Action<SubathonEvent> handler = e => eventRaised = true;
            SubathonEvents.SubathonEventCreated += handler;

            service.Running = true;
            typeof(YouTubeService)
                .GetField("_ytHandle", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(service, "@test");

            var chatItem = new ChatItem
            {
                Id = Guid.NewGuid().ToString(),
                Message = Array.Empty<MessagePart>(),
                Timestamp = DateTimeOffset.UtcNow,
                Author = new Author { Name = "Gifter",  ChannelId = "MemberChannelId" },
                MembershipDetails = new MembershipDetails
                {
                    EventType = MembershipEventType.GiftRedemption
                }
            };

            var args = new ChatReceivedEventArgs { ChatItem = chatItem };
            typeof(YouTubeService)
                .GetMethod("OnChatReceived", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(service, new object?[] { null, args });

            Assert.False(eventRaised);
            SubathonEvents.SubathonEventCreated -= handler;
        }
        
        [Fact]
        public void OnChatReceived_ChatCommand_InvokesCommandService()
        {
            var logger = new Mock<ILogger<YouTubeService>>();
            var chatLogger = new Mock<ILogger<YTLiveChat.Services.YTLiveChat>>();
            var httpLogger = new Mock<ILogger<YTLiveChat.Services.YTHttpClient>>();
            var config = new Mock<Config>();
            var service = new YouTubeService(logger.Object, config.Object, httpLogger.Object, chatLogger.Object);
            SubathonEvent? captured = null;
            Action<SubathonEvent> handler = e => captured = e;
            SubathonEvents.SubathonEventCreated += handler;
            
            var configCs = MockConfig(new()
            {
                { ("Chat", "Commands.Pause"), "pause" },
                { ("Chat", "Commands.Pause.permissions.Mods"), "true" },
                { ("Chat", "Commands.Pause.permissions.VIPs"), "false" },
                { ("Chat", "Commands.Pause.permissions.Whitelist"), "specialguy" }
            });
            CommandService.SetConfig(configCs);
            
            service.Running = true;
            typeof(YouTubeService)
                .GetField("_ytHandle", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(service, "@test");

            MessagePart parts = new TextPart{Text="!pause"}; 
            var chatItem = new ChatItem
            {
                Id = Guid.NewGuid().ToString(),
                Timestamp = DateTimeOffset.UtcNow,
                IsModerator = true,
                Author = new Author { Name = "User", ChannelId = "12345"},
                Message = new[] { parts }
            };

            var args = new ChatReceivedEventArgs { ChatItem = chatItem };
            typeof(YouTubeService)
                .GetMethod("OnChatReceived", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(service, new object?[] { null, args });
            
            Assert.NotNull(captured);
            Assert.Equal(SubathonEventSource.YouTube, captured!.Source);
            Assert.Equal(SubathonEventType.Command, captured!.EventType);
            Assert.Equal(SubathonCommandType.Pause, captured!.Command);
            Assert.Equal("User", captured.User);
            SubathonEvents.SubathonEventCreated -= handler;
        }
        
        [Fact]
        public void OnChatStopped_ShouldSetRunningFalseAndRaiseEvent()
        {
            var logger = new Mock<ILogger<YouTubeService>>();
            var chatLogger = new Mock<ILogger<YTLiveChat.Services.YTLiveChat>>();
            var httpLogger = new Mock<ILogger<YTLiveChat.Services.YTHttpClient>>();
            var config = new Mock<Config>();
            var service = new YouTubeService(logger.Object, config.Object, httpLogger.Object, chatLogger.Object);

            bool eventRaised = false;
            Action<bool, SubathonEventSource, string, string> handler = (running, source, handle, service) =>
            {
                if (source == SubathonEventSource.YouTube)
                {
                    eventRaised = true;
                    Assert.False(running);
                }
            };
            IntegrationEvents.ConnectionUpdated += handler;

            try
            {
                var method = typeof(YouTubeService)
                    .GetMethod("OnChatStopped", BindingFlags.NonPublic | BindingFlags.Instance);

                var eventArgs = new ChatStoppedEventArgs();
                method?.Invoke(service, new object?[] { null, eventArgs });

                Assert.False(service.Running);
                Assert.True(eventRaised);
            }
            finally
            {
                IntegrationEvents.ConnectionUpdated -= handler;
            }
        }

        [Fact]
        public async Task Start_ShouldReturnTrueAndSetHandle()
        {
            var logger = new Mock<ILogger<YouTubeService>>();
            var chatLogger = new Mock<ILogger<YTLiveChat.Services.YTLiveChat>>();
            var httpLogger = new Mock<ILogger<YTLiveChat.Services.YTHttpClient>>();
            var config = new Mock<Config>();
            config.Setup(c => c.Get("YouTube", "Handle", "")).Returns("@TestChannel");

            var service = new YouTubeService(logger.Object, config.Object, httpLogger.Object, chatLogger.Object);

            bool eventRaised = false;
            bool ranNone = false;
            Action<bool, SubathonEventSource, string, string> handler = (running, source, handle, serviceType) =>
            {
                if (source != SubathonEventSource.YouTube) return;
                if (!ranNone)
                {
                    Assert.Equal("None", handle);
                    ranNone = true;
                    eventRaised = true;
                    return;
                }

                Assert.True(ranNone);
                eventRaised = true;
                Assert.False(running);
                Assert.Equal("@TestChannel", handle);
                Assert.True(service.Running);
            };
            IntegrationEvents.ConnectionUpdated += handler;

            try
            {
                //bool result = service.Start(null);
                await service.StartAsync(CancellationToken.None);
                Assert.False(service.Running); // happens during events
                await Task.Delay(100);
                Assert.True(eventRaised);
                await service.StopAsync(CancellationToken.None);
            }
            finally
            {
                IntegrationEvents.ConnectionUpdated -= handler;
            }
        }
        
        [Fact]
        public void OnChatReceived_BlerpMessage()
        {
            var logger = new Mock<ILogger<YouTubeService>>();
            var chatLogger = new Mock<ILogger<YTLiveChat.Services.YTLiveChat>>();
            var httpLogger = new Mock<ILogger<YTLiveChat.Services.YTHttpClient>>();
            var config = new Mock<Config>();
            var service = new YouTubeService(logger.Object, config.Object, httpLogger.Object, chatLogger.Object);
            SubathonEvent? captured = null;
            Action<SubathonEvent> handler = e => captured = e;
            SubathonEvents.SubathonEventCreated += handler;
            
            service.Running = true;
            typeof(YouTubeService)
                .GetField("_ytHandle", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(service, "@test");

            MessagePart parts = new TextPart{Text="SomeGuy used 500 beets to play FunnyHaHa"}; 
            var chatItem = new ChatItem
            {
                Id = Guid.NewGuid().ToString(),
                Timestamp = DateTimeOffset.UtcNow,
                IsModerator = true,
                Author = new Author { Name = "blerp", ChannelId = "12345"},
                Message = new[] { parts }
            };

            var args = new ChatReceivedEventArgs { ChatItem = chatItem };
            typeof(YouTubeService)
                .GetMethod("OnChatReceived", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(service, new object?[] { null, args });
            
            Assert.NotNull(captured);
            Assert.Equal(SubathonEventSource.Blerp, captured!.Source);
            Assert.Equal(SubathonEventType.BlerpBeets, captured!.EventType);
            Assert.Equal("SomeGuy", captured.User);
            SubathonEvents.SubathonEventCreated -= handler;
        }
        
    }
}
