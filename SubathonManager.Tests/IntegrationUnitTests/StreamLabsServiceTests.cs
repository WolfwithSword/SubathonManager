using Moq;
using Microsoft.Extensions.Logging;
using SubathonManager.Integration;
using SubathonManager.Core.Events;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Models;
using Streamlabs.SocketClient.Messages;
using System.Reflection;
using Streamlabs.SocketClient.Messages.DataTypes;

namespace SubathonManager.Tests.IntegrationUnitTests
{
    public class StreamLabsServiceTests
    {
        [Fact]
        public void IsTokenEmpty_ShouldReturnTrue_WhenTokenNotSet()
        {
            var logger = new Mock<ILogger<StreamLabsService>>();
            var config = new Mock<Config>();
            config.Setup(c => c.Get("StreamLabs", "SocketToken", "")).Returns("");

            var service = new StreamLabsService(logger.Object, config.Object);

            Assert.True(service.IsTokenEmpty());
        }

        [Fact]
        public void SetSocketToken_ShouldUpdateConfigAndSave()
        {
            var logger = new Mock<ILogger<StreamLabsService>>();
            var config = new Mock<Config>();

            var service = new StreamLabsService(logger.Object, config.Object);

            service.SetSocketToken("NEW_TOKEN");

            config.Verify(c => c.Set("StreamLabs", "SocketToken", "NEW_TOKEN"), Times.Once);
            config.Verify(c => c.Save(), Times.Once);
        }

        [Fact]
        public async Task InitClientAsync_ShouldReturnFalse_WhenTokenEmpty()
        {
            var logger = new Mock<ILogger<StreamLabsService>>();
            var config = new Mock<Config>();
            config.Setup(c => c.Get("StreamLabs", "SocketToken", "")).Returns("");

            var service = new StreamLabsService(logger.Object, config.Object);

            var result = await service.InitClientAsync();

            Assert.False(result);
            Assert.False(service.Connected);
        }

        [Fact]
        public void SimulateTip_ShouldRaiseSubathonEvent()
        {
            SubathonEvent? capturedEvent = null;
            Action<SubathonEvent> handler = ev => capturedEvent = ev;
            SubathonEvents.SubathonEventCreated += handler;

            StreamLabsService.SimulateTip("25.5", "USD");

            Assert.NotNull(capturedEvent);
            Assert.Equal("25.5", capturedEvent!.Value);
            Assert.Equal("USD", capturedEvent.Currency);
            Assert.Equal(SubathonEventSource.Simulated, capturedEvent.Source);
            Assert.Equal(SubathonEventType.StreamLabsDonation, capturedEvent.EventType);
            SubathonEvents.SubathonEventCreated -= handler;
        }

        [Fact]
        public void OnDonation_ShouldRaiseSubathonEvent()
        {
            var logger = new Mock<ILogger<StreamLabsService>>();
            var config = new Mock<Config>();
            var service = new StreamLabsService(logger.Object, config.Object);

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
        }
    }
}
