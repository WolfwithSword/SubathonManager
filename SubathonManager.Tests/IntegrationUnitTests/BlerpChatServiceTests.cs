using SubathonManager.Core.Enums;
using SubathonManager.Core.Models;
using SubathonManager.Integration;
using SubathonManager.Tests.Utility;

namespace SubathonManager.Tests.IntegrationUnitTests;

[Collection("SharedEventBusTests")]
public class BlerpChatServiceTests
{     
    private static SubathonEvent? CaptureEvent(Action trigger) =>
        EventUtil.SubathonEventCapture.CaptureRequired(trigger);
    
    [Fact]
    public void SimulateBlerpBits_RaiseEvent()
    {
        
        var ev = CaptureEvent(() => BlerpChatService.SimulateBlerpMessage(500, "bits"));

        Assert.NotNull(ev);
        Assert.Equal(SubathonEventType.BlerpBits, ev.EventType);
        Assert.Equal("bits", ev.Currency);
        Assert.Equal("500", ev.Value);
        Assert.Equal("SYSTEM", ev.User);
    }
    
    [Fact]
    public void SimulateBlerpBits_NoEvent()
    {
        
        var ev = CaptureEvent(() => BlerpChatService.SimulateBlerpMessage(500, "something"));
        Assert.Null(ev);
    }
        
    [Fact]
    public void SimulateBlerpBeets_RaiseEvent()
    {
        
        var ev = CaptureEvent(() => BlerpChatService.SimulateBlerpMessage(500, "beets"));

        Assert.NotNull(ev);
        Assert.Equal(SubathonEventType.BlerpBeets, ev.EventType);
        Assert.Equal("beets", ev.Currency);
        Assert.Equal("500", ev.Value);
        Assert.Equal("SYSTEM", ev.User);
    }  
    
    [Fact]
    public void BlerpBeets_RaiseEvent()
    {
        
        var ev = CaptureEvent(() => BlerpChatService.ParseMessage("SomeGuy used 500 beets to play XYZ", SubathonEventSource.Twitch));

        Assert.NotNull(ev);
        Assert.Equal(SubathonEventType.BlerpBeets, ev.EventType);
        Assert.Equal("beets", ev.Currency);
        Assert.Equal("500", ev.Value);
        Assert.Equal("SomeGuy", ev.User);
    }    
    
    [Fact]
    public void BlerpBits_RaiseEvent()
    {
        
        var ev = CaptureEvent(() => BlerpChatService.ParseMessage("SomeGuy used 500 bits to play XYZ", SubathonEventSource.Twitch));

        Assert.NotNull(ev);
        Assert.Equal(SubathonEventType.BlerpBits, ev.EventType);
        Assert.Equal("bits", ev.Currency);
        Assert.Equal("500", ev.Value);
        Assert.Equal("SomeGuy", ev.User);
    }   
    
    [Fact]
    public void BlerpBits_DoesNotRaiseEvent()
    {
        
        var ev = CaptureEvent(() => BlerpChatService.ParseMessage("SomeGuy used 400 currency to play XYZ", SubathonEventSource.Twitch));

        Assert.Null(ev);
    }
}