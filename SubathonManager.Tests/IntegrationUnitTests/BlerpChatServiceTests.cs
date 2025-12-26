using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Models;
using SubathonManager.Integration;
using System.Reflection;
namespace SubathonManager.Tests.IntegrationUnitTests;

[Collection("IntegrationEventTests")]
public class BlerpChatServiceTests
{     
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
    
    [Fact]
    public void SimulateBlerpBits_RaiseEvent()
    {
        typeof(SubathonEvents)
            .GetField("SubathonEventCreated", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
        var ev = CaptureEvent(() => BlerpChatService.SimulateBlerpMessage(500, "bits"));

        Assert.Equal(SubathonEventType.BlerpBits, ev.EventType);
        Assert.Equal("bits", ev.Currency);
        Assert.Equal("500", ev.Value);
        Assert.Equal("SYSTEM", ev.User);
    }
    
    [Fact]
    public void SimulateBlerpBits_NoEvent()
    {
        typeof(SubathonEvents)
            .GetField("SubathonEventCreated", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
        var ev = CaptureEvent(() => BlerpChatService.SimulateBlerpMessage(500, "something"));
        Assert.Null(ev);
    }
        
    [Fact]
    public void SimulateBlerpBeets_RaiseEvent()
    {
        typeof(SubathonEvents)
            .GetField("SubathonEventCreated", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
        var ev = CaptureEvent(() => BlerpChatService.SimulateBlerpMessage(500, "beets"));

        Assert.Equal(SubathonEventType.BlerpBeets, ev.EventType);
        Assert.Equal("beets", ev.Currency);
        Assert.Equal("500", ev.Value);
        Assert.Equal("SYSTEM", ev.User);
    }  
    
    [Fact]
    public void BlerpBeets_RaiseEvent()
    {
        typeof(SubathonEvents)
            .GetField("SubathonEventCreated", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
        var ev = CaptureEvent(() => BlerpChatService.ParseMessage("SomeGuy used 500 beets to play XYZ", SubathonEventSource.Twitch));

        Assert.Equal(SubathonEventType.BlerpBeets, ev.EventType);
        Assert.Equal("beets", ev.Currency);
        Assert.Equal("500", ev.Value);
        Assert.Equal("SomeGuy", ev.User);
    }    
    
    [Fact]
    public void BlerpBits_RaiseEvent()
    {
        typeof(SubathonEvents)
            .GetField("SubathonEventCreated", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
        var ev = CaptureEvent(() => BlerpChatService.ParseMessage("SomeGuy used 500 bits to play XYZ", SubathonEventSource.Twitch));

        Assert.Equal(SubathonEventType.BlerpBits, ev.EventType);
        Assert.Equal("bits", ev.Currency);
        Assert.Equal("500", ev.Value);
        Assert.Equal("SomeGuy", ev.User);
    }   
    
    [Fact]
    public void BlerpBits_DoesNotRaiseEvent()
    {
        typeof(SubathonEvents)
            .GetField("SubathonEventCreated", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
        var ev = CaptureEvent(() => BlerpChatService.ParseMessage("SomeGuy used 400 currency to play XYZ", SubathonEventSource.Twitch));

        Assert.Null(ev);
    }
}