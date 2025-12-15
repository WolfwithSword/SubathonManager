using Moq;
using Microsoft.Extensions.Logging;
using SubathonManager.Integration;
using SubathonManager.Core.Events;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Models;
using YTLiveChat.Contracts.Models;
using YTLiveChat.Contracts.Services;
using System.Reflection;

namespace SubathonManager.Tests.IntegrationUnitTests
{
    public class YouTubeServiceTests
    {
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
            Action<bool, string?> handler = (running, handle) =>
            {
                eventRaised = true;
                Assert.True(running);
            };
            YouTubeEvents.YouTubeConnectionUpdated += handler;

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
                YouTubeEvents.YouTubeConnectionUpdated -= handler;
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
            Action<bool, string?> handler = (running, handle) =>
            {
                eventRaised = true;
                Assert.False(running);
            };

            YouTubeEvents.YouTubeConnectionUpdated += handler;

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
                YouTubeEvents.YouTubeConnectionUpdated -= handler;
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
        public void OnChatStopped_ShouldSetRunningFalseAndRaiseEvent()
        {
            var logger = new Mock<ILogger<YouTubeService>>();
            var chatLogger = new Mock<ILogger<YTLiveChat.Services.YTLiveChat>>();
            var httpLogger = new Mock<ILogger<YTLiveChat.Services.YTHttpClient>>();
            var config = new Mock<Config>();
            var service = new YouTubeService(logger.Object, config.Object, httpLogger.Object, chatLogger.Object);

            bool eventRaised = false;
            Action<bool, string?> handler = (running, handle) =>
            {
                eventRaised = true;
                Assert.False(running);
            };
            YouTubeEvents.YouTubeConnectionUpdated += handler;

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
                YouTubeEvents.YouTubeConnectionUpdated -= handler;
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
            Action<bool, string?> handler = (running, handle) =>
            {
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
            YouTubeEvents.YouTubeConnectionUpdated += handler;

            try
            {
                bool result = service.Start(null);

                Assert.True(result);
                Assert.False(service.Running); // happens during events
                await Task.Delay(100);
                Assert.True(eventRaised);
            }
            finally
            {
                YouTubeEvents.YouTubeConnectionUpdated -= handler;
            }
        }
    }
}
