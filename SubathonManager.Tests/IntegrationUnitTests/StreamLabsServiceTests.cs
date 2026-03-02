using Moq;
using Microsoft.Extensions.Logging;
using SubathonManager.Integration;
using SubathonManager.Core.Events;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Models;
using Streamlabs.SocketClient.Messages;
using System.Reflection;
using Streamlabs.SocketClient;
using Streamlabs.SocketClient.Messages.DataTypes;
using SubathonManager.Core.Interfaces;

namespace SubathonManager.Tests.IntegrationUnitTests
{
    [Collection("IntegrationEventTests")]
    public class StreamLabsServiceTests
    {
        public StreamLabsServiceTests()
        {
            typeof(SubathonEvents)
                .GetField("SubathonEventCreated", BindingFlags.Static | BindingFlags.NonPublic)
                ?.SetValue(null, null);
            typeof(IntegrationEvents)
                .GetField("RaiseConnectionUpdate", BindingFlags.Static | BindingFlags.NonPublic)
                ?.SetValue(null, null);
        }
        
        private static (StreamLabsService service, Mock<IStreamlabsClient> mockClient) MakeService(
            string token = "valid_token")
        {
            var logger = new Mock<ILogger<StreamLabsService>>();
            var config = new Mock<IConfig>();
            config.Setup(c => c.Get("StreamLabs", "SocketToken", ""))
                .Returns(token);

            var mockClient = new Mock<IStreamlabsClient>();
            mockClient.Setup(c => c.ConnectAsync()).Returns(Task.CompletedTask);
            mockClient.Setup(c => c.DisconnectAsync()).Returns(Task.CompletedTask);

            var service = new StreamLabsService(logger.Object, config.Object);
            service.ClientFactory = _ => mockClient.Object;

            return (service, mockClient);
        }
        
        [Fact]
        public void IsTokenEmpty_ShouldReturnTrue_WhenTokenNotSet()
        {
            var logger = new Mock<ILogger<StreamLabsService>>();
            var config = new Mock<IConfig>();
            config.Setup(c => c.Get("StreamLabs", "SocketToken", "")).Returns("");
            var service = new StreamLabsService(logger.Object, config.Object);
            Assert.True(service.IsTokenEmpty());
        }

        [Fact]
        public void SetSocketToken_ShouldUpdateConfigAndSave()
        {
            var logger = new Mock<ILogger<StreamLabsService>>();
            var config = new Mock<IConfig>();
            config.Setup(c => c.Set("StreamLabs", "SocketToken", "NEW_TOKEN")).Returns(true);
            var service = new StreamLabsService(logger.Object, config.Object);

            service.SetSocketToken("NEW_TOKEN");

            config.Verify(c => c.Set("StreamLabs", "SocketToken", "NEW_TOKEN"), Times.Once);
            config.Verify(c => c.Save(), Times.Once);
        }
        
        [Fact]
        public void SetSocketToken_ShouldNotSave_WhenConfigSetReturnsFalse()
        {
            var logger = new Mock<ILogger<StreamLabsService>>();
            var config = new Mock<IConfig>();
            config.Setup(c => c.Set("StreamLabs", "SocketToken", "OLD_TOKEN")).Returns(false);
            var service = new StreamLabsService(logger.Object, config.Object);

            service.SetSocketToken("OLD_TOKEN");

            config.Verify(c => c.Set("StreamLabs", "SocketToken", "OLD_TOKEN"), Times.Once);
            config.Verify(c => c.Save(), Times.Never);
        }

        [Fact]
        public async Task InitClientAsync_ReturnsFalse_WhenTokenEmpty()
        {
            var logger = new Mock<ILogger<StreamLabsService>>();
            var config = new Mock<IConfig>();
            config.Setup(c => c.Get("StreamLabs", "SocketToken", "")).Returns("");
            var service = new StreamLabsService(logger.Object, config.Object);

            var result = await service.InitClientAsync();

            Assert.False(result);
            Assert.False(service.Connected);
        }
        
        [Fact]
        public async Task InitClientAsync_ReturnsTrue_AndSetsConnected_WhenTokenValid()
        {
            var (service, mockClient) = MakeService("valid_token");

            var result = await service.InitClientAsync();

            Assert.True(result);
            Assert.True(service.Connected);
            mockClient.Verify(c => c.ConnectAsync(), Times.Once);
        }
        
        [Fact]
        public async Task InitClientAsync_ReturnsFalse_WhenConnectThrows()
        {
            var (service, mockClient) = MakeService("valid_token");
            mockClient.Setup(c => c.ConnectAsync())
                .ThrowsAsync(new Exception("connection refused"));

            var result = await service.InitClientAsync();

            Assert.False(result);
            Assert.False(service.Connected);
        }
        
        [Fact]
        public async Task InitClientAsync_RaisesConnectionUpdate_TrueOnSuccess()
        {
            var (service, _) = MakeService("valid_token");

            bool? lastStatus = null;
            IntegrationEvents.ConnectionUpdated += (b, _, _, svc) =>
            {
                if (svc == "Socket") lastStatus = b;
            };

            await service.InitClientAsync();

            Assert.True(lastStatus);
        }
        
        [Fact]
        public async Task StartAsync_Works()
        {
            var logger = new Mock<ILogger<StreamLabsService>>();
            var config = new Mock<Config>();
            config.Setup(c => c.Get("StreamLabs", "SocketToken", "")).Returns("");

            var service = new StreamLabsService(logger.Object, config.Object);

            await service.StartAsync();
            Assert.False(service.Connected);
            await service.StopAsync();
        }

        [Fact]
        public async Task InitClientAsync_RaisesConnectionUpdate_FalseOnFailure()
        {
            var (service, mockClient) = MakeService("valid_token");
            mockClient.Setup(c => c.ConnectAsync())
                .ThrowsAsync(new Exception("fail"));

            bool? lastStatus = null;
            IntegrationEvents.ConnectionUpdated += (b, _, _, svc) =>
            {
                if (svc == "Socket") lastStatus = b;
            };

            await service.InitClientAsync();

            Assert.False(lastStatus);
        }
        
        [Fact]
        public async Task InitClientAsync_DisconnectsExistingClient_BeforeCreatingNew()
        {
            var (service, mockClient) = MakeService("valid_token");
            mockClient.Setup(c => c.ConnectAsync()).Returns(Task.CompletedTask);
            mockClient.Setup(c => c.DisconnectAsync()).Returns(Task.CompletedTask);

            await service.InitClientAsync();
            Assert.True(service.Connected);

            // disconnects old client first
            await service.InitClientAsync();

            mockClient.Verify(c => c.DisconnectAsync(), Times.Once);
            mockClient.Verify(c => c.ConnectAsync(), Times.Exactly(2));
        }
        
        [Fact]
        public async Task DisconnectAsync_SetsConnectedFalse_AndRaisesUpdate()
        {
            var (service, mockClient) = MakeService();
            await service.InitClientAsync();
            Assert.True(service.Connected);

            bool? lastStatus = null;
            IntegrationEvents.ConnectionUpdated += (b, _, _, svc) =>
            {
                if (svc == "Socket") lastStatus = b;
            };

            await service.DisconnectAsync();

            Assert.False(service.Connected);
            Assert.False(lastStatus);
            mockClient.Verify(c => c.DisconnectAsync(), Times.Once);
        }

        [Fact]
        public async Task DisconnectAsync_DoesNothing_WhenNotConnected()
        {
            var (service, mockClient) = MakeService();

            await service.DisconnectAsync();

            mockClient.Verify(c => c.DisconnectAsync(), Times.Never);
        }
        
        [Fact]
        public async Task StopAsync_Works_WhenConnected()
        {
            var (service, mockClient) = MakeService();
            await service.InitClientAsync();

            await service.StopAsync();

            Assert.False(service.Connected);
            mockClient.Verify(c => c.DisconnectAsync(), Times.Once);
        }
        
        [Fact]
        public void SimulateTip_ShouldRaiseSubathonEvent()
        {
            typeof(SubathonEvents)
                .GetField("SubathonEventCreated", BindingFlags.Static | BindingFlags.NonPublic)
                ?.SetValue(null, null);
            SubathonEvent? capturedEvent = null;
            void Handler(SubathonEvent ev) => capturedEvent = ev;

            SubathonEvents.SubathonEventCreated += Handler;
            try
            {
                StreamLabsService.SimulateTip("25.5", "USD");

                Assert.NotNull(capturedEvent);
                Assert.Equal("25.5", capturedEvent!.Value);
                Assert.Equal("USD", capturedEvent.Currency);
                Assert.Equal(SubathonEventSource.Simulated, capturedEvent.Source);
                Assert.Equal(SubathonEventType.StreamLabsDonation, capturedEvent.EventType);
            }
            finally
            {
                SubathonEvents.SubathonEventCreated -= Handler;
            }
        }
        
        [Fact]
        public async Task OnDonation_SubscribedAfterInit_UnsubscribedAfterDisconnect()
        {
            var (service, mockClient) = MakeService();
            await service.InitClientAsync();

            mockClient.VerifyAdd(c => c.OnDonation += It.IsAny<EventHandler<DonationMessage>>(), Times.Once);

            await service.DisconnectAsync();

            mockClient.VerifyRemove(c => c.OnDonation -= It.IsAny<EventHandler<DonationMessage>>(), Times.Once);
        }
        
        [Fact]
        public async Task OnDonation_ShouldRaiseSubathonEvent()
        {
            var logger = new Mock<ILogger<StreamLabsService>>();
            var config = new Mock<Config>();
            var service = new StreamLabsService(logger.Object, config.Object);

            typeof(SubathonEvents)
                .GetField("SubathonEventCreated", BindingFlags.Static | BindingFlags.NonPublic)
                ?.SetValue(null, null);
            
            SubathonEvent? capturedEvent = null;
            Action<SubathonEvent> handler = ev => capturedEvent = ev;
            SubathonEvents.SubathonEventCreated += handler;

            var donation = new DonationMessage
            {
                Id = long.MaxValue,
                FormattedAmount = "$5.00",
                Emotes = "",
                IconClassName = "",
                To = new Recipient{Name = "TestTo"},
                From = "TestFrom",
                MessageId = Guid.NewGuid().ToString(),
                FromUserId = "",
                Source = "TestSource",
                Priority = long.MinValue,
                Name = "Donor",
                Amount = (decimal)5.0,
                Currency = Currency.Usd,
                Message = "Test message"
            };

            var method = typeof(StreamLabsService)
                .GetMethod("OnDonation", BindingFlags.NonPublic | BindingFlags.Instance);

            method?.Invoke(service, new object?[] { null, donation });

            Assert.NotNull(capturedEvent);
            Assert.Equal("Donor", capturedEvent!.User);
            Assert.Equal("USD", capturedEvent.Currency); // should be uppercased
            Assert.Equal("5", capturedEvent.Value);
            Assert.Equal(SubathonEventSource.StreamLabs, capturedEvent.Source);
            Assert.Equal(SubathonEventType.StreamLabsDonation, capturedEvent.EventType);
            Assert.Equal(Guid.Parse(donation.MessageId), capturedEvent.Id);
            
            SubathonEvents.SubathonEventCreated -= handler;
            await service.DisconnectAsync();
        }
        
        [Fact]
        public async Task OnDonation_RaisesSubathonEvent_WithCorrectFields()
        {
            var (service, _) = MakeService();
            await service.InitClientAsync();

            SubathonEvent? captured = null;
            void Handler(SubathonEvent ev) => captured = ev;
            SubathonEvents.SubathonEventCreated += Handler;

            var msgId = Guid.NewGuid().ToString();
            var donation = new DonationMessage
            {
                Id = 1,
                Name = "Donor",
                Amount = 5.0m,
                Currency = Currency.Usd,
                Message = "Test",
                MessageId = msgId,
                FormattedAmount = "$5.00",
                Emotes = "",
                IconClassName = "",
                To = new Recipient { Name = "TestTo" },
                From = "TestFrom",
                FromUserId = "",
                Source = "TestSource",
                Priority = 0
            };

            typeof(StreamLabsService)
                .GetMethod("OnDonation", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(service, [null, donation]);

            Assert.NotNull(captured);
            Assert.Equal("Donor", captured!.User);
            Assert.Equal("USD", captured.Currency);
            Assert.Equal("5.0", captured.Value);
            Assert.Equal(SubathonEventSource.StreamLabs, captured.Source);
            Assert.Equal(SubathonEventType.StreamLabsDonation, captured.EventType);
            Assert.Equal(Guid.Parse(msgId), captured.Id);
            SubathonEvents.SubathonEventCreated -= Handler;
        }
    }
}
