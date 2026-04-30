using SubathonManager.Integration;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Models;
using System.Reflection;
using Moq;
using StreamElements.WebSocket.Models.Tip;
using StreamElements.WebSocket.Models.Internal;
using Microsoft.Extensions.Logging;
using SubathonManager.Core.Objects;
using SubathonManager.Core.Security;
using SubathonManager.Tests.Utility;

namespace SubathonManager.Tests.IntegrationUnitTests;

[Collection("SharedEventBusTests")]
public class StreamElementsServiceTests
{
    
    private static SubathonEvent? CaptureEvent(Action trigger) =>
        EventUtil.SubathonEventCapture.CaptureRequired(trigger);
    
    [Fact]
    public async Task InitClient_ShouldReturnFalse_WhenJwtIsEmpty()
    {
        var logger = new Mock<ILogger<StreamElementsService>>();
        var config = new Mock<Config>();
        //config.Setup(c => c.Get("StreamElements", "JWT", "")).Returns("");
        
        var storage = new InMemorySecureStorage(new()
        {
            [StorageKeys.StreamElementsJwt] = "",
        });

        var service = new StreamElementsService(logger.Object, config.Object, storage);

        await service.StartAsync(TestContext.Current.CancellationToken);
        
        Assert.False(service.Connected);
        Assert.True(service.IsTokenEmpty());
        
        await service.StopAsync(TestContext.Current.CancellationToken);
    }
    
    [Fact]
    public void InitClient_ShouldReturnTrue_WhenJwtIsSet()
    {
        var logger = new Mock<ILogger<StreamElementsService>>();
        var config = new Mock<Config>();
        
        var storage = new InMemorySecureStorage(new()
        {
            [StorageKeys.StreamElementsJwt] = "TEST_JWT",
        });

        var service = new StreamElementsService(logger.Object, config.Object, storage);

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
        var storage = new InMemorySecureStorage(new()
        {
            [StorageKeys.StreamElementsJwt] = "OLD_JWT",
        });

        var service = new StreamElementsService(logger.Object, config.Object, storage);

        var result =  service.SetJwtToken("NEW_JWT");

        // config.Verify(c => c.Set("StreamElements", "JWT", "NEW_JWT"), Times.Once);
        // config.Verify(c => c.Save(), Times.Once);
        Assert.True(result);
        Assert.Equal(1, storage.SetSuccessCount);
    }
    
    [Fact]
    public void SetJwtToken_ShouldNotUpdateConfigAndSave()
    {
        var logger = new Mock<ILogger<StreamElementsService>>();
        var config = new Mock<Config>();
        
        var storage = new InMemorySecureStorage(new()
        {
            [StorageKeys.StreamElementsJwt] = "OLD_JWT",
        });

        var service = new StreamElementsService(logger.Object, config.Object, storage);

        var result = service.SetJwtToken("OLD_JWT");

        // config.Verify(c => c.Set("StreamElements", "JWT", "OLD_JWT"), Times.Once);
        // config.Verify(c => c.Save(), Times.Never);
        Assert.False(result);
        Assert.Equal(0, storage.SetSuccessCount);
        Assert.Equal(1, storage.SetCount);
    }
    
    [Fact]
    public void SimulateTip_ShouldRaiseSubathonEvent()
    {
        typeof(SubathonEvents)
            .GetField("SubathonEventCreated", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);

        
        SubathonEvent? capturedEvent = CaptureEvent( () => StreamElementsService.SimulateTip("15.5", "USD"));

        Assert.NotNull(capturedEvent);
        Assert.Equal("15.5", capturedEvent.Value);
        Assert.Equal("USD", capturedEvent.Currency);
        Assert.Equal(SubathonEventSource.Simulated, capturedEvent.Source);
        Assert.Equal(SubathonEventType.StreamElementsDonation, capturedEvent.EventType);
    }
    
    [Fact]
    public void OnTip_ShouldRaiseSubathonEvent()
    {
        var logger = new Mock<ILogger<StreamElementsService>>();
        var config = new Mock<Config>();
        var service = new StreamElementsService(logger.Object, config.Object, new InMemorySecureStorage());
        
        typeof(SubathonEvents)
            .GetField("SubathonEventCreated", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);

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

        
        SubathonEvent? capturedEvent = CaptureEvent( () => method?.Invoke(service, new object?[] { null, tip }));

        Assert.NotNull(capturedEvent);
        Assert.Equal("Test", capturedEvent.User);
        Assert.Equal("USD", capturedEvent.Currency);
        Assert.Equal("12.5", capturedEvent.Value);
        Assert.Equal(SubathonEventSource.StreamElements, capturedEvent.Source);
        Assert.Equal(SubathonEventType.StreamElementsDonation, capturedEvent.EventType);

        Assert.Equal(Guid.Parse(tip.TipId), capturedEvent.Id);
    }
    
    [Fact]
    public void OnConnected_ShouldSetReconnectingFalse_AndLogInformation()
    {
        var logger = new Mock<ILogger<StreamElementsService>>();
        var config = new Mock<Config>();
        var service = new StreamElementsService(logger.Object, config.Object, new InMemorySecureStorage());

        var method = typeof(StreamElementsService)
            .GetMethod("_OnConnected", BindingFlags.NonPublic | BindingFlags.Instance);

        method?.Invoke(service, [null, EventArgs.Empty]);

        logger.Verify(l => l.Log(
            LogLevel.Information,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Connected")),
            null,
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()
        ), Times.Once);
    }
    
    [Fact]
    public async Task OnDisconnected_ShouldSetConnectedFalse_AndRaiseEvent()
    {
        var logger = new Mock<ILogger<StreamElementsService>>();
        var config = new Mock<Config>();
        var service = new StreamElementsService(logger.Object, config.Object, new InMemorySecureStorage());

        bool eventRaised = false;
        
        Action<IntegrationConnection> handler = (conn) =>
        {
            if (conn.Source == SubathonEventSource.StreamElements)
            {
                eventRaised = true;
                Assert.False(conn.Status);
            }
        };
        
        IntegrationEvents.ConnectionUpdated += handler;

        var method = typeof(StreamElementsService)
            .GetMethod("_OnDisconnected", BindingFlags.NonPublic | BindingFlags.Instance);
        
        method?.Invoke(service, [null, EventArgs.Empty]);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(service.Connected);
        Assert.True(eventRaised);

        logger.Verify(l => l.Log(
            LogLevel.Warning,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Disconnected")),
            null,
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()
        ), Times.Once);
        
        IntegrationEvents.ConnectionUpdated -= handler;
    }
    
    [Fact]
    public void OnAuthenticated_ShouldSetConnectedTrue_AndRaiseEvent()
    {
        var logger = new Mock<ILogger<StreamElementsService>>();
        var config = new Mock<Config>();
        var service = new StreamElementsService(logger.Object, config.Object, new InMemorySecureStorage());

        bool eventRaised = false;
        
        typeof(IntegrationEvents)
            .GetField("ConnectionUpdated", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
        
        Action<IntegrationConnection> handler = (conn) =>
        {
            if (conn.Source == SubathonEventSource.StreamElements)
            {
                eventRaised = true;
                Assert.True(conn.Status);
            }
        };

        IntegrationEvents.ConnectionUpdated += handler;

        try
        {
            var method = typeof(StreamElementsService)
                .GetMethod("_OnAuthenticated", BindingFlags.NonPublic | BindingFlags.Instance);

            method?.Invoke(service, [null, new Authenticated("testId", "testId2")]);

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
            IntegrationEvents.ConnectionUpdated -= handler;
        }
    }

    [Fact]
    public void OnAuthenticateError_ShouldSetConnectedFalse_AndRaiseErrorEvent()
    {
        var logger = new Mock<ILogger<StreamElementsService>>();
        var config = new Mock<Config>();
        var service = new StreamElementsService(logger.Object, config.Object, new InMemorySecureStorage());
        
        typeof(IntegrationEvents)
            .GetField("ConnectionUpdated", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
        
        typeof(ErrorMessageEvents)
            .GetField("ErrorEventOccured", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);

        bool eventRaised = false;
        Action<IntegrationConnection> handler = (conn) =>
        {
            if (conn.Source == SubathonEventSource.StreamElements)
            {
                eventRaised = true;
                Assert.False(conn.Status);
            }
        };
        IntegrationEvents.ConnectionUpdated += handler;

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

            method?.Invoke(service, [null, EventArgs.Empty]);

            Assert.False(service.Connected);
            Assert.True(eventRaised);
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
            IntegrationEvents.ConnectionUpdated -= handler;
            ErrorMessageEvents.ErrorEventOccured -= errorHandler;
        }
    }

}
