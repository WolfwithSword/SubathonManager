using SubathonManager.Integration;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Models;
using System.Reflection;
using Moq;
using StreamElements.WebSocket.Models.Tip;
using StreamElements.WebSocket.Models.Internal;
using Microsoft.Extensions.Logging;
namespace SubathonManager.Tests.IntegrationUnitTests;

public class StreamElementsServiceTests
{
    [Fact]
    public void InitClient_ShouldReturnFalse_WhenJwtIsEmpty()
    {
        var logger = new Mock<ILogger<StreamElementsService>>();
        var config = new Mock<Config>();
        config.Setup(c => c.Get("StreamElements", "JWT", "")).Returns("");

        var service = new StreamElementsService(logger.Object, config.Object);

        var result = service.InitClient();

        Assert.False(result);
        Assert.False(service.Connected);
        Assert.True(service.IsTokenEmpty());
    }
    
    [Fact]
    public void InitClient_ShouldReturnTrue_WhenJwtIsSet()
    {
        var logger = new Mock<ILogger<StreamElementsService>>();
        var config = new Mock<Config>();
        config.Setup(c => c.Get("StreamElements", "JWT", "")).Returns("TEST_JWT");

        var service = new StreamElementsService(logger.Object, config.Object);

        var result = service.InitClient();

        Assert.True(result); 
        Assert.False(service.Connected);
        Assert.False(service.IsTokenEmpty());
    }
    
    [Fact]
    public void SetJwtToken_ShouldUpdateConfigAndSave()
    {
        var logger = new Mock<ILogger<StreamElementsService>>();
        var config = new Mock<Config>();

        var service = new StreamElementsService(logger.Object, config.Object);

        service.SetJwtToken("NEW_JWT");

        config.Verify(c => c.Set("StreamElements", "JWT", "NEW_JWT"), Times.Once);
        config.Verify(c => c.Save(), Times.Once);
    }
    
    [Fact]
    public void SimulateTip_ShouldRaiseSubathonEvent()
    {
        SubathonEvent? capturedEvent = null;
        Action<SubathonEvent> handler = ev => capturedEvent = ev;
        SubathonEvents.SubathonEventCreated += handler;

        StreamElementsService.SimulateTip("15.5", "USD");

        Assert.NotNull(capturedEvent);
        Assert.Equal("15.5", capturedEvent!.Value);
        Assert.Equal("USD", capturedEvent.Currency);
        Assert.Equal(SubathonEventSource.Simulated, capturedEvent.Source);
        Assert.Equal(SubathonEventType.StreamElementsDonation, capturedEvent.EventType);

        SubathonEvents.SubathonEventCreated -= handler;
    }
    
    [Fact]
    public void OnTip_ShouldRaiseSubathonEvent()
    {
        var logger = new Mock<ILogger<StreamElementsService>>();
        var config = new Mock<Config>();
        var service = new StreamElementsService(logger.Object, config.Object);

        SubathonEvent? capturedEvent = null;
        Action<SubathonEvent> handler = ev => capturedEvent = ev;
        SubathonEvents.SubathonEventCreated += handler;

        var tip = new Tip(
            tipId: Guid.NewGuid().ToString(),
            username: "Test",
            currency: "USD",
            amount: 12.5,
            avatar: string.Empty,
            message: "Test message"
        );

        var method = typeof(StreamElementsService)
            .GetMethod("_OnTip", BindingFlags.NonPublic | BindingFlags.Instance);

        method?.Invoke(service, new object?[] { null, tip });

        Assert.NotNull(capturedEvent);
        Assert.Equal("Test", capturedEvent!.User);
        Assert.Equal("USD", capturedEvent.Currency);
        Assert.Equal("12.5", capturedEvent.Value);
        Assert.Equal(SubathonEventSource.StreamElements, capturedEvent.Source);
        Assert.Equal(SubathonEventType.StreamElementsDonation, capturedEvent.EventType);

        Assert.Equal(Guid.Parse(tip.TipId), capturedEvent.Id);
        SubathonEvents.SubathonEventCreated -= handler;
    }
    
    [Fact]
    public void OnConnected_ShouldSetReconnectingFalse_AndLogInformation()
    {
        var logger = new Mock<ILogger<StreamElementsService>>();
        var config = new Mock<Config>();
        var service = new StreamElementsService(logger.Object, config.Object);

        var method = typeof(StreamElementsService)
            .GetMethod("_OnConnected", BindingFlags.NonPublic | BindingFlags.Instance);

        method?.Invoke(service, new object?[] { null, EventArgs.Empty });

        logger.Verify(l => l.Log(
            LogLevel.Information,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Connected")),
            null,
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()
        ), Times.Once);
    }
    
    [Fact]
    public void OnDisconnected_ShouldSetConnectedFalse_AndRaiseEvent()
    {
        var logger = new Mock<ILogger<StreamElementsService>>();
        var config = new Mock<Config>();
        var service = new StreamElementsService(logger.Object, config.Object);

        bool eventRaised = false;
        
        StreamElementsEvents.StreamElementsConnectionChanged += connected =>
        {
            eventRaised = true;
            Assert.False(connected);
        };

        var method = typeof(StreamElementsService)
            .GetMethod("_OnDisconnected", BindingFlags.NonPublic | BindingFlags.Instance);

        method?.Invoke(service, new object?[] { null, EventArgs.Empty });

        Assert.False(service.Connected);
        Assert.True(eventRaised);

        logger.Verify(l => l.Log(
            LogLevel.Warning,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Disconnected")),
            null,
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()
        ), Times.Once);
    }
    
    [Fact]
    public void OnAuthenticated_ShouldSetConnectedTrue_AndRaiseEvent()
    {
        var logger = new Mock<ILogger<StreamElementsService>>();
        var config = new Mock<Config>();
        var service = new StreamElementsService(logger.Object, config.Object);

        bool eventRaised = false;
        
        typeof(StreamElementsEvents)
            .GetField("StreamElementsConnectionChanged", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);

        Action<bool> handler = connected =>
        {
            eventRaised = true;
            Assert.True(connected);
        };

        StreamElementsEvents.StreamElementsConnectionChanged += handler;

        try
        {
            var method = typeof(StreamElementsService)
                .GetMethod("_OnAuthenticated", BindingFlags.NonPublic | BindingFlags.Instance);

            method?.Invoke(service, new object?[] { null, new Authenticated("testId", "testId2") });

            Assert.True(service.Connected);
            Assert.True(eventRaised);

            logger.Verify(l => l.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Authenticated")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()
            ), Times.Once);
        }
        finally
        {
            StreamElementsEvents.StreamElementsConnectionChanged -= handler;
        }
    }

    [Fact]
    public void OnAuthenticateError_ShouldSetConnectedFalse_AndRaiseErrorEvent()
    {
        var logger = new Mock<ILogger<StreamElementsService>>();
        var config = new Mock<Config>();
        var service = new StreamElementsService(logger.Object, config.Object);

        
        typeof(StreamElementsEvents)
            .GetField("StreamElementsConnectionChanged", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
        
        typeof(StreamElementsEvents)
            .GetField("ErrorEventOccured", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);

        bool connectionChangedRaised = false;
        Action<bool> connectionHandler = connected =>
        {
            connectionChangedRaised = true;
            Assert.False(connected);
        };
        StreamElementsEvents.StreamElementsConnectionChanged += connectionHandler;

        bool errorEventRaised = false;
        Action<string, string, string, DateTime> errorHandler = (level, source, message, time) =>
        {
            errorEventRaised = true;
            Assert.Equal(nameof(SubathonEventSource.StreamElements), source);
        };
        ErrorMessageEvents.ErrorEventOccured += errorHandler;

        try
        {
            var method = typeof(StreamElementsService)
                .GetMethod("_OnAuthenticateError", BindingFlags.NonPublic | BindingFlags.Instance);

            method?.Invoke(service, new object?[] { null, EventArgs.Empty });

            Assert.False(service.Connected);
            Assert.True(connectionChangedRaised);
            Assert.True(errorEventRaised);

            logger.Verify(l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Authentication Error")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()
            ), Times.Once);
        }
        finally
        {
            StreamElementsEvents.StreamElementsConnectionChanged -= connectionHandler;
            ErrorMessageEvents.ErrorEventOccured -= errorHandler;
        }
    }

}
