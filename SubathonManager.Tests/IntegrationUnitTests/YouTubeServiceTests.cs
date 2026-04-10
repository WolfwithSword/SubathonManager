using Moq;
using Microsoft.Extensions.Logging;
using SubathonManager.Integration;
using SubathonManager.Core.Events;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Models;
using SubathonManager.Services;
using YTLiveChat.Contracts.Models;
using YTLiveChat.Contracts.Services;
using System.Reflection;
using SubathonManager.Core.Objects;
using SubathonManager.Tests.Utility;
// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.Tests.IntegrationUnitTests
{
    [Collection("SharedEventBusTests")]
    public class YouTubeServiceTests
    {
        private static SubathonEvent? CaptureEvent(Action trigger) =>
            EventUtil.SubathonEventCapture.CaptureRequired(trigger);
        
        [Fact]
        public void SimulateSuperChat_ShouldRaiseEvent()
        {
            
            SubathonEvent? captured = CaptureEvent( () => YouTubeService.SimulateSuperChat("12.5", "USD"));

            Assert.NotNull(captured);
            Assert.Equal("12.5", captured!.Value);
            Assert.Equal("USD", captured.Currency);
            Assert.Equal(SubathonEventSource.Simulated, captured.Source);
            Assert.Equal(SubathonEventType.YouTubeSuperChat, captured.EventType);
        }

        [Fact]
        public void SimulateMembership_ShouldRaiseEvent()
        {
            SubathonEvent? captured = CaptureEvent( () => 
                YouTubeService.SimulateMembership("Gold"));

            Assert.NotNull(captured);
            Assert.Equal("Gold", captured!.Value);
            Assert.Equal("member", captured.Currency);
            Assert.Equal(SubathonEventSource.Simulated, captured.Source);
            Assert.Equal(SubathonEventType.YouTubeMembership, captured.EventType);
        }

        [Fact]
        public void SimulateGiftMemberships_ShouldRaiseEvent()
        {
            
            SubathonEvent? captured = CaptureEvent( () => 
                YouTubeService.SimulateGiftMemberships(3));

            Assert.NotNull(captured);
            Assert.Equal("member", captured!.Currency);
            Assert.Equal(SubathonEventSource.Simulated, captured.Source);
            Assert.Equal(SubathonEventType.YouTubeGiftMembership, captured.EventType);
            Assert.Equal(3, captured.Amount);
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
            Action<IntegrationConnection> handler = (conn) =>
            {
                if (conn.Source == SubathonEventSource.YouTube)
                {
                    eventRaised = true;
                    Assert.True(conn.Status);
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

                method?.Invoke(service, [null, args]);

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
            Action<IntegrationConnection> handler = (conn) =>
            {
                if (conn.Source == SubathonEventSource.YouTube)
                {
                    eventRaised = true;
                    Assert.False(conn.Status);
                }
            };

            IntegrationEvents.ConnectionUpdated += handler;

            try
            {
                var method = typeof(YouTubeService)
                    .GetMethod("OnErrorOccurred", BindingFlags.NonPublic | BindingFlags.Instance);

                var errorArgs = new YTLiveChat.Contracts.Services.ErrorOccurredEventArgs(new Exception("Test error"));
                method?.Invoke(service, [null, errorArgs]);

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
                Message = [],
                Superchat = new Superchat
                {
                    AmountString = "$5.00", AmountValue = (decimal)5.00, Currency = "CA$",
                    BodyBackgroundColor = "",
                }
            };

            var eventArgs = new ChatReceivedEventArgs { ChatItem = chatItem };
            
            SubathonEvent? capturedEvent = CaptureEvent( () => method?.Invoke(service, [null, eventArgs]));

            Assert.NotNull(capturedEvent);
            Assert.Equal("TestUser", capturedEvent!.User);
            Assert.Equal("CA$", capturedEvent.Currency);
            Assert.Equal("5", capturedEvent.Value);
            Assert.Equal(SubathonEventSource.YouTube, capturedEvent.Source);
            Assert.Equal(SubathonEventType.YouTubeSuperChat, capturedEvent.EventType);
        }


        [Fact]
        public void OnChatReceived_Membership_Gift_RaisesEvent()
        {
            var logger = new Mock<ILogger<YouTubeService>>();
            var chatLogger = new Mock<ILogger<YTLiveChat.Services.YTLiveChat>>();
            var httpLogger = new Mock<ILogger<YTLiveChat.Services.YTHttpClient>>();
            var config = new Mock<Config>();
            var service = new YouTubeService(logger.Object, config.Object, httpLogger.Object, chatLogger.Object);
            
            service.Running = true;
            typeof(YouTubeService)
                .GetField("_ytHandle", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(service, "@test");

            var chatItem = new ChatItem
            {
                Id = Guid.NewGuid().ToString(),
                Message = [],
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
            
            SubathonEvent? captured = CaptureEvent( () => typeof(YouTubeService)
                .GetMethod("OnChatReceived", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(service, [null, args]));

            Assert.NotNull(captured);
            Assert.Equal(SubathonEventSource.YouTube, captured!.Source);
            Assert.Equal(SubathonEventType.YouTubeGiftMembership, captured!.EventType);
            Assert.Equal("Gifter", captured.User);
            Assert.Equal(3, captured.Amount);

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
                Message = [],
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
                .Invoke(service, [null, args]);

            Assert.False(eventRaised);
        }
        
        [Fact]
        public void OnChatReceived_ChatCommand_InvokesCommandService()
        {
            var logger = new Mock<ILogger<YouTubeService>>();
            var chatLogger = new Mock<ILogger<YTLiveChat.Services.YTLiveChat>>();
            var httpLogger = new Mock<ILogger<YTLiveChat.Services.YTHttpClient>>();
            var config = new Mock<Config>();
            var service = new YouTubeService(logger.Object, config.Object, httpLogger.Object, chatLogger.Object);
            
            var configCs = MockConfig.MakeMockConfig(new()
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
                Message = [parts]
            };

            var args = new ChatReceivedEventArgs { ChatItem = chatItem };
            
            SubathonEvent? captured = CaptureEvent( () => typeof(YouTubeService)
                .GetMethod("OnChatReceived", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(service, [null, args]));
            
            Assert.NotNull(captured);
            Assert.Equal(SubathonEventSource.YouTube, captured!.Source);
            Assert.Equal(SubathonEventType.Command, captured!.EventType);
            Assert.Equal(SubathonCommandType.Pause, captured!.Command);
            Assert.Equal("User", captured.User);
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
            Action<IntegrationConnection> handler = (conn) =>
            {
                if (conn.Source == SubathonEventSource.YouTube)
                {
                    eventRaised = true;
                    Assert.False(conn.Status);
                }
            };
            IntegrationEvents.ConnectionUpdated += handler;

            try
            {
                var method = typeof(YouTubeService)
                    .GetMethod("OnChatStopped", BindingFlags.NonPublic | BindingFlags.Instance);

                var eventArgs = new ChatStoppedEventArgs();
                method?.Invoke(service, [null, eventArgs]);

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
            Action<IntegrationConnection> handler = (conn) =>
            {
                if (conn.Source != SubathonEventSource.YouTube) return;
                if (!ranNone)
                {
                    Assert.Equal("None", conn.Name);
                    ranNone = true;
                    eventRaised = true;
                    return;
                }

                Assert.True(ranNone);
                eventRaised = true;
                Assert.False(conn.Status);
                Assert.Equal("@TestChannel", conn.Name);
                Assert.Equal("@TestChannel", Utils.GetConnection(SubathonEventSource.YouTube, "Chat").Name);
                Assert.False(Utils.GetConnection(SubathonEventSource.YouTube, "Chat").Status);
                Assert.False(service.Running);
            };
            IntegrationEvents.ConnectionUpdated += handler;

            try
            {
                //bool result = service.Start(null);
                await service.StartAsync(CancellationToken.None);
                Assert.False(service.Running); // happens during events
                await Task.Delay(300);
                Assert.True(eventRaised);
                Assert.Equal("@TestChannel", Utils.GetConnection(SubathonEventSource.YouTube, "Chat").Name);
                Assert.False(Utils.GetConnection(SubathonEventSource.YouTube, "Chat").Status);
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
                Message = [parts]
            };

            var args = new ChatReceivedEventArgs { ChatItem = chatItem };
            
            SubathonEvent? captured = CaptureEvent( () => typeof(YouTubeService)
                .GetMethod("OnChatReceived", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(service, [null, args]));
            
            Assert.NotNull(captured);
            Assert.Equal(SubathonEventSource.Blerp, captured!.Source);
            Assert.Equal(SubathonEventType.BlerpBeets, captured!.EventType);
            Assert.Equal("SomeGuy", captured.User);
        }
        
        [Fact]
        public void SimulateSuperChat_ShouldNotRaiseEvent_WhenValueInvalid()
        {
            bool raised = false;
            Action<SubathonEvent> handler = _ => raised = true;
            SubathonEvents.SubathonEventCreated += handler;

            YouTubeService.SimulateSuperChat("notanumber", "USD");

            Assert.False(raised);
        }
        
        [Theory]
        [InlineData("")]
        [InlineData("@")]
        [InlineData("   ")]
        public void Start_ShouldReturnFalse_WhenHandleIsInvalid(string handle)
        {
            var logger = new Mock<ILogger<YouTubeService>>();
            var chatLogger = new Mock<ILogger<YTLiveChat.Services.YTLiveChat>>();
            var httpLogger = new Mock<ILogger<YTLiveChat.Services.YTHttpClient>>();
            var config = new Mock<Config>();
            config.Setup(c => c.Get("YouTube", "Handle", "")).Returns(handle);

            var service = new YouTubeService(logger.Object, config.Object, httpLogger.Object, chatLogger.Object);

            bool result = service.Start(null);

            Assert.False(result);
            Assert.False(service.Running);
        }
        
        [Fact]
        public void Start_ShouldPrependAt_WhenHandleMissingIt()
        {
            var logger = new Mock<ILogger<YouTubeService>>();
            var chatLogger = new Mock<ILogger<YTLiveChat.Services.YTLiveChat>>();
            var httpLogger = new Mock<ILogger<YTLiveChat.Services.YTHttpClient>>();
            var config = new Mock<Config>();
            config.Setup(c => c.Get("YouTube", "Handle", "")).Returns("TestChannel");

            var service = new YouTubeService(logger.Object, config.Object, httpLogger.Object, chatLogger.Object);
            service.Start(null);

            var handle = typeof(YouTubeService)
                .GetField("_ytHandle", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(service) as string;

            Assert.Equal("@TestChannel", handle);
        }
        
        [Fact]
        public void OnChatReceived_ShouldReturnEarly_WhenHandleIsEmpty()
        {
            var logger = new Mock<ILogger<YouTubeService>>();
            var chatLogger = new Mock<ILogger<YTLiveChat.Services.YTLiveChat>>();
            var httpLogger = new Mock<ILogger<YTLiveChat.Services.YTHttpClient>>();
            var config = new Mock<Config>();
            var service = new YouTubeService(logger.Object, config.Object, httpLogger.Object, chatLogger.Object);

            bool raised = false;
            Action<SubathonEvent> handler = _ => raised = true;
            SubathonEvents.SubathonEventCreated += handler;
            service.Running = true;

            var chatItem = new ChatItem
            {
                Id = Guid.NewGuid().ToString(),
                Timestamp = DateTimeOffset.UtcNow,
                Author = new Author { Name = "TestUser", ChannelId = "id" },
                Message = [],
                Superchat = new Superchat { AmountString = "$5.00", AmountValue = 5, Currency = "USD", 
                    BodyBackgroundColor = "" }
            };

            typeof(YouTubeService)
                .GetMethod("OnChatReceived", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(service, [null, new ChatReceivedEventArgs { ChatItem = chatItem }]);

            Assert.False(raised);
        }

        
        [Fact]
        public void OnChatReceived_ShouldRaiseConnectionUpdate_WhenNotRunning()
        {
            var logger = new Mock<ILogger<YouTubeService>>();
            var chatLogger = new Mock<ILogger<YTLiveChat.Services.YTLiveChat>>();
            var httpLogger = new Mock<ILogger<YTLiveChat.Services.YTHttpClient>>();
            var config = new Mock<Config>();
            var service = new YouTubeService(logger.Object, config.Object, httpLogger.Object, chatLogger.Object);

            typeof(YouTubeService)
                .GetField("_ytHandle", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(service, "@test");

            service.Running = false; //

            bool connectionEventRaised = false;
            
            Action<IntegrationConnection> handler = (conn) =>
            {
                if (conn is { Source: SubathonEventSource.YouTube })
                {
                    connectionEventRaised = true;
                }
            };
            IntegrationEvents.ConnectionUpdated += handler;

            var chatItem = new ChatItem
            {
                Id = Guid.NewGuid().ToString(),
                Timestamp = DateTimeOffset.UtcNow,
                Author = new Author { Name = "TestUser", ChannelId = "id" },
                Message = [],
                Superchat = new Superchat
                {
                    AmountString = "$5.00",
                    AmountValue = 5,
                    Currency = "USD",
                    BodyBackgroundColor = ""
                }
            };

            typeof(YouTubeService)
                .GetMethod("OnChatReceived", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(service, [null, new ChatReceivedEventArgs { ChatItem = chatItem }]);

            Assert.True(connectionEventRaised);
            Assert.True(service.Running);
            IntegrationEvents.ConnectionUpdated -= handler;
        }
        
        [Fact]
        public void OnChatReceived_ShouldReturnEarly_WhenMessageIsTooOld()
        {
            var logger = new Mock<ILogger<YouTubeService>>();
            var chatLogger = new Mock<ILogger<YTLiveChat.Services.YTLiveChat>>();
            var httpLogger = new Mock<ILogger<YTLiveChat.Services.YTHttpClient>>();
            var config = new Mock<Config>();
            var service = new YouTubeService(logger.Object, config.Object, httpLogger.Object, chatLogger.Object);

            typeof(YouTubeService)
                .GetField("_ytHandle", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(service, "@test");
            service.Running = true;

            bool raised = false;
            Action<SubathonEvent> handler = _ => raised = true;
            SubathonEvents.SubathonEventCreated += handler;

            var chatItem = new ChatItem
            {
                Id = Guid.NewGuid().ToString(),
                Timestamp = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5), // older than 3 min
                Author = new Author { Name = "OldUser", ChannelId = "id" },
                Message = [],
                Superchat = new Superchat
                {
                    AmountString = "$5.00",
                    AmountValue = 5,
                    Currency = "USD",
                    BodyBackgroundColor = ""
                }
            };

            typeof(YouTubeService)
                .GetMethod("OnChatReceived", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(service, [null, new ChatReceivedEventArgs { ChatItem = chatItem }]);

            Assert.False(raised);
        }

        [Fact]
        public void OnChatReceived_SuperChat_UnknownCurrency_RaisesErrorEvent()
        {
            var logger = new Mock<ILogger<YouTubeService>>();
            var chatLogger = new Mock<ILogger<YTLiveChat.Services.YTLiveChat>>();
            var httpLogger = new Mock<ILogger<YTLiveChat.Services.YTHttpClient>>();
            var config = new Mock<Config>();
            var service = new YouTubeService(logger.Object, config.Object, httpLogger.Object, chatLogger.Object);

            typeof(YouTubeService)
                .GetField("_ytHandle", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(service, "@test");
            service.Running = true;

            bool errorRaised = false;
            Action<string, string, string, DateTime> errorHandler = (level, _, _, _) =>
            {
                if (level == "WARN") errorRaised = true;
            };
            ErrorMessageEvents.ErrorEventOccured += errorHandler;

            // empty currency string will resolve to "???"
            var chatItem = new ChatItem
            {
                Id = Guid.NewGuid().ToString(),
                Timestamp = DateTimeOffset.UtcNow,
                Author = new Author { Name = "TestUser", ChannelId = "id" },
                Message = [],
                Superchat = new Superchat
                {
                    AmountString = "5.00",
                    AmountValue = 5,
                    Currency = "",
                    BodyBackgroundColor = ""
                }
            };

            typeof(YouTubeService)
                .GetMethod("OnChatReceived", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(service, [null, new ChatReceivedEventArgs { ChatItem = chatItem }]);

            Assert.True(errorRaised);
            ErrorMessageEvents.ErrorEventOccured -= errorHandler;
        }

        [Fact]
        public void OnChatReceived_SuperChat_USD_WithoutDollarSign_ParsesCurrency()
        {
            var logger = new Mock<ILogger<YouTubeService>>();
            var chatLogger = new Mock<ILogger<YTLiveChat.Services.YTLiveChat>>();
            var httpLogger = new Mock<ILogger<YTLiveChat.Services.YTHttpClient>>();
            var config = new Mock<Config>();
            var service = new YouTubeService(logger.Object, config.Object, httpLogger.Object, chatLogger.Object);

            typeof(YouTubeService)
                .GetField("_ytHandle", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(service, "@test");
            service.Running = true;

            var chatItem = new ChatItem
            {
                Id = Guid.NewGuid().ToString(),
                Timestamp = DateTimeOffset.UtcNow,
                Author = new Author { Name = "TestUser", ChannelId = "id" },
                Message = [],
                Superchat = new Superchat
                {
                    AmountString = "CA$5.00",
                    AmountValue = 5,
                    Currency = "USD",
                    BodyBackgroundColor = ""
                }
            };
            
            SubathonEvent? captured = CaptureEvent( () => typeof(YouTubeService)
                .GetMethod("OnChatReceived", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(service, [null, new ChatReceivedEventArgs { ChatItem = chatItem }]));

            Assert.NotNull(captured);
            Assert.NotEqual("USD", captured!.Currency);
        }
        
        [Theory]
        [InlineData("superchat")]
        [InlineData("membership")]
        public void OnChatReceived_Ticker_DoesNotRaiseEvent(string itemType)
        {
            var logger = new Mock<ILogger<YouTubeService>>();
            var chatLogger = new Mock<ILogger<YTLiveChat.Services.YTLiveChat>>();
            var httpLogger = new Mock<ILogger<YTLiveChat.Services.YTHttpClient>>();
            var config = new Mock<Config>();
            var service = new YouTubeService(logger.Object, config.Object, httpLogger.Object, chatLogger.Object);

            typeof(YouTubeService)
                .GetField("_ytHandle", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(service, "@test");
            service.Running = true;

            bool raised = false;
            Action<SubathonEvent> handler = _ => raised = true;
            SubathonEvents.SubathonEventCreated += handler;

            var chatItem = new ChatItem
            {
                Id = Guid.NewGuid().ToString(),
                Timestamp = DateTimeOffset.UtcNow,
                IsTicker = true,
                Author = new Author { Name = "TestUser", ChannelId = "id" },
                Message = [],
                Superchat = itemType == "superchat"
                    ? new Superchat { AmountString = "$5.00", AmountValue = 5, Currency = "USD", BodyBackgroundColor = "" }
                    : null,
                MembershipDetails = itemType == "membership"
                    ? new MembershipDetails { EventType = MembershipEventType.New, LevelName = "Gold" }
                    : null
            };

            typeof(YouTubeService)
                .GetMethod("OnChatReceived", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(service, [null, new ChatReceivedEventArgs { ChatItem = chatItem }]);

            Assert.False(raised);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void OnChatReceived_Membership_IdHandling(bool isValidGuid)
        {
            var logger = new Mock<ILogger<YouTubeService>>();
            var chatLogger = new Mock<ILogger<YTLiveChat.Services.YTLiveChat>>();
            var httpLogger = new Mock<ILogger<YTLiveChat.Services.YTHttpClient>>();
            var config = new Mock<Config>();
            var service = new YouTubeService(logger.Object, config.Object, httpLogger.Object, chatLogger.Object);

            typeof(YouTubeService)
                .GetField("_ytHandle", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(service, "@test");
            service.Running = true;

            var knownGuid = Guid.NewGuid();
            string itemId = isValidGuid ? knownGuid.ToString() : "yt-not-a-guid";

            var chatItem = new ChatItem
            {
                Id = itemId,
                Timestamp = DateTimeOffset.UtcNow,
                Author = new Author { Name = "MemberUser", ChannelId = "id" },
                Message = [],
                MembershipDetails = new MembershipDetails { EventType = MembershipEventType.New, LevelName = "Gold" }
            };

            
            SubathonEvent? captured = CaptureEvent( () => typeof(YouTubeService)
                .GetMethod("OnChatReceived", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(service, [null, new ChatReceivedEventArgs { ChatItem = chatItem }]));

            Assert.NotNull(captured);
            Assert.NotEqual(Guid.Empty, captured!.Id);
            if (isValidGuid)
                Assert.Equal(knownGuid, captured.Id);

        }

        [Theory]
        [InlineData(MembershipEventType.New, "Gold", "New Member", SubathonEventType.YouTubeMembership, "Gold")]
        [InlineData(MembershipEventType.Milestone, "DEFAULT", "Special guy", SubathonEventType.YouTubeMembership, "DEFAULT")]
        [InlineData(MembershipEventType.Unknown, "Gold", "Some subtext", SubathonEventType.YouTubeMembership, "Some subtext")] // not true case
        [InlineData(MembershipEventType.Unknown, "Gold", "Welcome new member!", SubathonEventType.YouTubeMembership, "Gold")]
        [InlineData(MembershipEventType.Unknown, "Upgraded membership to MyTier!!", "Upgraded membership to MyTier!!", SubathonEventType.YouTubeMembership, "MyTier!")]
        public void OnChatReceived_Membership_RaisesYouTubeMembership(
            MembershipEventType eventType, string levelName, string headerSubtext, SubathonEventType expectedEventType, string expectedLevel)
        {
            var logger = new Mock<ILogger<YouTubeService>>();
            var chatLogger = new Mock<ILogger<YTLiveChat.Services.YTLiveChat>>();
            var httpLogger = new Mock<ILogger<YTLiveChat.Services.YTHttpClient>>();
            var config = new Mock<Config>();
            var service = new YouTubeService(logger.Object, config.Object, httpLogger.Object, chatLogger.Object);

            typeof(YouTubeService)
                .GetField("_ytHandle", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(service, "@test");
            service.Running = true;
            
            var chatItem = new ChatItem
            {
                Id = Guid.NewGuid().ToString(),
                Timestamp = DateTimeOffset.UtcNow,
                Author = new Author { Name = "MemberUser", ChannelId = "id" },
                Message = [],
                MembershipDetails = new MembershipDetails
                {
                    EventType = eventType,
                    HeaderSubtext = headerSubtext,
                    LevelName = levelName,
                    MilestoneMonths = eventType == MembershipEventType.Milestone ? 6 : 0
                }
            };

            
            SubathonEvent? captured = CaptureEvent( () => typeof(YouTubeService)
                .GetMethod("OnChatReceived", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(service, [null, new ChatReceivedEventArgs { ChatItem = chatItem }]));

            Assert.NotNull(captured);
            Assert.Equal(expectedEventType, captured!.EventType);
            Assert.Equal(SubathonEventSource.YouTube, captured.Source);
            Assert.Equal(expectedLevel, captured.Value);
            Assert.Equal("MemberUser", captured.User);
        }
   
        [Fact]
        public void OnChatReceived_Membership_Milestone_TierIsMember_UsesHeaderSubtext()
        {
            var logger = new Mock<ILogger<YouTubeService>>();
            var chatLogger = new Mock<ILogger<YTLiveChat.Services.YTLiveChat>>();
            var httpLogger = new Mock<ILogger<YTLiveChat.Services.YTHttpClient>>();
            var config = new Mock<Config>();
            var service = new YouTubeService(logger.Object, config.Object, httpLogger.Object, chatLogger.Object);

            typeof(YouTubeService)
                .GetField("_ytHandle", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(service, "@test");
            service.Running = true;

            var chatItem = new ChatItem
            {
                Id = Guid.NewGuid().ToString(),
                Timestamp = DateTimeOffset.UtcNow,
                Author = new Author { Name = "MemberUser", ChannelId = "id" },
                Message = [],
                MembershipDetails = new MembershipDetails
                {
                    EventType = MembershipEventType.Milestone,
                    HeaderSubtext = "Gold",
                    MilestoneMonths = 6,
                    LevelName = "Member" // fallback to HeaderSubtext
                }
            };

            
            SubathonEvent? captured = CaptureEvent( () => typeof(YouTubeService)
                .GetMethod("OnChatReceived", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(service, [null, new ChatReceivedEventArgs { ChatItem = chatItem }]));

            Assert.NotNull(captured);
            Assert.Equal("Gold", captured!.Value);
        }

        [Fact]
        public void OnChatReceived_Membership_GiftPurchase_NullGifterUsername_UsesAuthorName()
        {
            var logger = new Mock<ILogger<YouTubeService>>();
            var chatLogger = new Mock<ILogger<YTLiveChat.Services.YTLiveChat>>();
            var httpLogger = new Mock<ILogger<YTLiveChat.Services.YTHttpClient>>();
            var config = new Mock<Config>();
            var service = new YouTubeService(logger.Object, config.Object, httpLogger.Object, chatLogger.Object);

            typeof(YouTubeService)
                .GetField("_ytHandle", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(service, "@test");
            service.Running = true;

            var chatItem = new ChatItem
            {
                Id = Guid.NewGuid().ToString(),
                Timestamp = DateTimeOffset.UtcNow,
                Author = new Author { Name = "AuthorFallback", ChannelId = "id" },
                Message = [],
                MembershipDetails = new MembershipDetails
                {
                    EventType = MembershipEventType.GiftPurchase,
                    GifterUsername = null,
                    GiftCount = 2
                }
            };

            
            SubathonEvent? captured = CaptureEvent( () => typeof(YouTubeService)
                .GetMethod("OnChatReceived", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(service, [null, new ChatReceivedEventArgs { ChatItem = chatItem }]));

            Assert.NotNull(captured);
            Assert.Equal("AuthorFallback", captured!.User);
            Assert.Equal(2, captured.Amount);
        }
        
        
        
        [Theory]
        [InlineData("   ", "   ")]
        [InlineData("member", "member")]
        [InlineData("Member", "Member")]
        [InlineData("MEMBER", "MEMBER")]
        public void OnChatReceived_Membership_TierNormalizesToDefault(string levelName, string headerSubtext)
        {
            var logger = new Mock<ILogger<YouTubeService>>();
            var chatLogger = new Mock<ILogger<YTLiveChat.Services.YTLiveChat>>();
            var httpLogger = new Mock<ILogger<YTLiveChat.Services.YTHttpClient>>();
            var config = new Mock<Config>();
            var service = new YouTubeService(logger.Object, config.Object, httpLogger.Object, chatLogger.Object);

            typeof(YouTubeService)
                .GetField("_ytHandle", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(service, "@test");
            service.Running = true;

            var chatItem = new ChatItem
            {
                Id = Guid.NewGuid().ToString(),
                Timestamp = DateTimeOffset.UtcNow,
                Author = new Author { Name = "MemberUser", ChannelId = "id" },
                Message = [],
                MembershipDetails = new MembershipDetails
                {
                    EventType = MembershipEventType.New,
                    HeaderSubtext = headerSubtext,
                    LevelName = levelName
                }
            };

            
            SubathonEvent? captured = CaptureEvent( () => typeof(YouTubeService)
                .GetMethod("OnChatReceived", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(service, [null, new ChatReceivedEventArgs { ChatItem = chatItem }]));

            Assert.NotNull(captured);
            Assert.Equal("DEFAULT", captured!.Value);
        }
        
        [Fact]
        public void TryReconnectLoop_ShouldNotStart_WhenHandleIsEmpty()
        {
            var logger = new Mock<ILogger<YouTubeService>>();
            var chatLogger = new Mock<ILogger<YTLiveChat.Services.YTLiveChat>>();
            var httpLogger = new Mock<ILogger<YTLiveChat.Services.YTHttpClient>>();
            var config = new Mock<Config>();
            var service = new YouTubeService(logger.Object, config.Object, httpLogger.Object, chatLogger.Object);

            var method = typeof(YouTubeService)
                .GetMethod("TryReconnectLoop", BindingFlags.NonPublic | BindingFlags.Instance);

            var ex = Record.Exception(() => method!.Invoke(service, null));
            Assert.Null(ex);
        }
        
        [Fact]
        public void OnErrorOccurred_CanonicalLinkError_LogsOnceOnly()
        {
            var logger = new Mock<ILogger<YouTubeService>>();
            var chatLogger = new Mock<ILogger<YTLiveChat.Services.YTLiveChat>>();
            var httpLogger = new Mock<ILogger<YTLiveChat.Services.YTHttpClient>>();
            var config = new Mock<Config>();
            var service = new YouTubeService(logger.Object, config.Object, httpLogger.Object, chatLogger.Object);

            typeof(YouTubeService)
                .GetField("_ytHandle", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(service, "@test");

            var method = typeof(YouTubeService)
                .GetMethod("OnErrorOccurred", BindingFlags.NonPublic | BindingFlags.Instance);

            var errorArgs = new ErrorOccurredEventArgs(new Exception("canonical link not found"));

            // logs only 1x
            method!.Invoke(service, [null, errorArgs]);
            method!.Invoke(service, [null, errorArgs]);

            var counter = typeof(YouTubeService)
                .GetField("_canonicalLinkErrorCount", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(service) as int?;

            Assert.Equal(2, counter);
            Assert.False(service.Running);

            logger.Verify(
                x => x.Log(
                    LogLevel.Warning,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("canonical link")),
                    null,
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }

        [Fact]
        public void OnErrorOccurred_InitialContinuationToken_LogsWarningWithMessage()
        {
            var logger = new Mock<ILogger<YouTubeService>>();
            var chatLogger = new Mock<ILogger<YTLiveChat.Services.YTLiveChat>>();
            var httpLogger = new Mock<ILogger<YTLiveChat.Services.YTHttpClient>>();
            var config = new Mock<Config>();
            var service = new YouTubeService(logger.Object, config.Object, httpLogger.Object, chatLogger.Object);

            typeof(YouTubeService)
                .GetField("_ytHandle", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(service, "@test");

            var method = typeof(YouTubeService)
                .GetMethod("OnErrorOccurred", BindingFlags.NonPublic | BindingFlags.Instance);

            var errorArgs = new ErrorOccurredEventArgs(new Exception("Initial Continuation token not found"));
            method!.Invoke(service, [null, errorArgs]);

            Assert.False(service.Running);
            logger.Verify(
                x => x.Log(
                    LogLevel.Warning,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Initial Continuation token not found")),
                    It.IsAny<Exception?>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }
    }
}
