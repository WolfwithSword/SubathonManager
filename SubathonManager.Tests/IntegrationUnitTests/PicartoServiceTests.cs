using System.Net.WebSockets;
using Moq;
using Microsoft.Extensions.Logging;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Models;
using SubathonManager.Services;
using SubathonManager.Integration;
using PicartoEventsLib.Abstractions.Models;
using PicartoEventsLib.Internal;
using System.Reflection;
using IniParser.Model;
using PicartoEventsLib.Clients;
using PicartoEventsLib.Options;

namespace SubathonManager.Tests.IntegrationUnitTests;

[Collection("IntegrationEventTests")]
public class PicartoServiceTests
{
    public PicartoServiceTests()
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
        mock.Setup(c => c.GetSection("Chat")).Returns(() =>
        {
            var kdc = new KeyDataCollection();
            kdc.AddKey(kd);
            return kdc;
        });
            
        return mock.Object;
    }

    private class MockPicartoClientOptions : PicartoClientOptions
    {
        public override async Task InitAsync(string? channel = null)
        {
            var tokenProp = typeof(PicartoClientOptions).GetProperty("JwtToken", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            tokenProp!.SetValue(this, "1234567890");
            await Task.Delay(1);
        }
    }
    
    
    private class TestEventClient : PicartoEventsClient
    {
        public TestEventClient(PicartoClientOptions options, ILogger<PicartoEventsClient>? logger)
            : base(options, (ILogger<PicartoEventsClient>?) logger)
        {
        }

        public override async Task ConnectAsync()
        {
            await Task.Delay(1);
            OnConnected(new PicartoWebSocketConnectedEventArgs(new Uri("wss://example.com")));
        }
        
        public override async Task DisconnectAsync()
        {
            await Task.Delay(1);
            OnDisconnected(new PicartoWebSocketDisconnectedEventArgs(WebSocketCloseStatus.NormalClosure,
                "Disconnect", null, true));
        }
    }
    
    private class TestChatClient : PicartoChatClient
    {
        public TestChatClient(PicartoClientOptions options, ILogger<PicartoChatClient>? logger)
            : base(options, (ILogger<PicartoChatClient>?) logger)
        {
        }

        public new async Task ConnectAsync()
        {
            await Task.Delay(1);
        }
    }

    [Fact]
    public void SimulateTip_RaisesTipEvent()
    {
        PicartoTip tip = new PicartoTip
        {
            Username = "TestUser",
            Amount = 562,
            Channel = "TestChannel"
        };
        
        typeof(SubathonEvents)
            .GetField("SubathonEventCreated", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
        var ev = CaptureEvent(() => PicartoService.ProcessAlert(tip));

        Assert.Equal(SubathonEventType.PicartoTip, ev.EventType);
        Assert.Equal(SubathonEventSubType.TokenLike, ev.EventType.GetSubType());
        Assert.Equal("kudos", ev.Currency);
        Assert.Equal("562", ev.Value);
        Assert.Equal("TestUser", ev.User);
    }
    
    [Theory]
    [InlineData(4.99, 1, 1)]
    [InlineData(9.99, 2, 1)]
    [InlineData(14.99, 3, 1)]
    [InlineData(14.97, 1, 3)]
    [InlineData(29.94, 1, 6)]
    [InlineData(59.88, 1, 12)]
    public void SimulateSubscription_ShouldRaiseSubEvent(decimal amount, int tier, int months)
    {
        PicartoSubscription sub = new PicartoSubscription
        {
            Username = "TestUser",
            Amount = amount,
            Channel = "TestChannel"
        };
        typeof(SubathonEvents)
            .GetField("SubathonEventCreated", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
        SubathonEvent? ev = CaptureEvent(() => 
            PicartoService.ProcessAlert(sub));

        Assert.NotNull(ev);
        Assert.Equal(SubathonEventType.PicartoSub, ev!.EventType);
        Assert.Equal(SubathonEventSource.Picarto, ev!.Source);
        Assert.Equal("sub", ev.Currency);
        Assert.Equal(months, ev.Amount);
        Assert.Equal($"T{tier}", ev.Value);
        Assert.Equal("TestUser", ev.User);
        Assert.Equal(SubathonEventSubType.SubLike, ev.EventType.GetSubType());
    }
    
        
    [Theory]
    [InlineData(4.99, 1, 1)]
    [InlineData(4.99, 1, 2)] // two subs
    [InlineData(4.99, 1, 5)] // 5 subs
    [InlineData(14.97, 3, 1)]
    [InlineData(14.97, 3, 2)] // two subs
    public void SimulateGiftSubscription_ShouldRaiseSubEvent(decimal amount, int months, int quantity)
    {
        PicartoSubscription sub = new PicartoSubscription
        {
            Username = "TestUser",
            Amount = amount * quantity,
            Quantity = quantity,
            Channel = "TestChannel",
            IsGift = true
        };
        typeof(SubathonEvents)
            .GetField("SubathonEventCreated", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
        SubathonEvent? ev = CaptureEvent(() => 
            PicartoService.ProcessAlert(sub));

        Assert.NotNull(ev);
        Assert.Equal(SubathonEventType.PicartoGiftSub, ev!.EventType);
        Assert.Equal(SubathonEventSource.Picarto, ev!.Source);
        Assert.Equal("sub", ev.Currency);
        Assert.Equal(months * quantity, ev.Amount);
        Assert.Equal($"T1", ev.Value);
        Assert.Equal("TestUser", ev.User);
        Assert.Equal(SubathonEventSubType.GiftSubLike, ev.EventType.GetSubType());
    }
             
    [Theory]
    [InlineData(SubathonEventSource.Picarto, "TestUser")]
    [InlineData(SubathonEventSource.Simulated, "SYSTEM")]
    public void SimulateFollow_RaisesFollowEvent(SubathonEventSource src, string user)
    {
        PicartoFollow fl = new PicartoFollow
        {
            Username = user,
            Channel = "TestChannel"
        };
        
        typeof(SubathonEvents)
            .GetField("SubathonEventCreated", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
        var ev = CaptureEvent(() => PicartoService.ProcessAlert(fl));

        Assert.Equal(SubathonEventType.PicartoFollow, ev.EventType);
        Assert.Equal(SubathonEventSubType.FollowLike, ev.EventType.GetSubType());
        Assert.Equal(user, ev.User);
        Assert.Equal(src, ev.Source);
    }
    
    [Theory]
    [InlineData("!pause", false, "TestUser", "TestStreamer", false)]
    [InlineData("!pause", true, "TestStreamer", "TestStreamer", true)]
    [InlineData("!pause", false, "specialguy", "TestStreamer", true)]
    [InlineData("hey wassup", false, "specialguy", "TestStreamer", false)]
    public void OnChatReceived_ChatCommand_InvokesCommandService(string cmd, bool isBroadcaster, string user, string channel, bool output)
    {
        SubathonEvent? captured = null;
        Action<SubathonEvent> handler = e => captured = e;
        SubathonEvents.SubathonEventCreated += handler;
        
        var configCs = MockConfig(new()
        {
            { ("Chat", "Commands.Pause"), "pause" },
            { ("Chat", "Commands.Pause.permissions.Mods"), "false" },
            { ("Chat", "Commands.Pause.permissions.VIPs"), "false" },
            { ("Chat", "Commands.Pause.permissions.Whitelist"), "specialguy" }
        });
        
        CommandService.SetConfig(configCs);

        PicartoChatMessage msg = new PicartoChatMessage
        {
            ChannelId = 123456,
            Channel = channel,
            User = new PicartoUser
            {
                Id = isBroadcaster ? 123456 : 987654,
                Username = user,
                Avatar = "",
                Color = "",
                IsBroadcaster = isBroadcaster
            },
            Message = cmd,
            MsgId = Guid.Empty
        };
        
        PicartoService.ProcessChatMessage(msg);

        if (output)
        {
            Assert.NotNull(captured);
            Assert.Equal(SubathonEventSource.Picarto, captured!.Source);
            Assert.Equal(SubathonEventType.Command, captured!.EventType);
            Assert.Equal(SubathonCommandType.Pause, captured!.Command);
            Assert.Equal(user, captured.User);
        }
        else
        {
            Assert.Null(captured);
        }

        SubathonEvents.SubathonEventCreated -= handler;
    }

    [Fact]
    public void OnDisposedProperlyTest()
    {
                
        var configCs = MockConfig(new()
        {
            { ("Chat", "Commands.Pause"), "pause" },
            { ("Chat", "Commands.Pause.permissions.Mods"), "false" },
            { ("Chat", "Commands.Pause.permissions.VIPs"), "false" },
            { ("Chat", "Commands.Pause.permissions.Whitelist"), "specialguy" },
            { ("Picarto", "Username"), "TestChannel"}
        });
        PicartoService service = new PicartoService(null, configCs, null, null);
        
        Assert.False(service._disposed);

        service.Dispose();
        Assert.True(service._disposed);

        service.Dispose();
        Assert.True(service._disposed);
    }

    
    [Fact]
    public async Task StartAsync_Test_RaisesConnections()
    {
                
        var configCs = MockConfig(new()
        {
            { ("Chat", "Commands.Pause"), "pause" },
            { ("Chat", "Commands.Pause.permissions.Mods"), "false" },
            { ("Chat", "Commands.Pause.permissions.VIPs"), "false" },
            { ("Chat", "Commands.Pause.permissions.Whitelist"), "specialguy" },
            { ("Picarto", "Username"), "TestChannel"}
        });
        PicartoService service = new PicartoService(null, configCs, null, null);
        MockPicartoClientOptions mockOpts = new MockPicartoClientOptions()
        {
            Channel = "TestChannel"
        };
        await mockOpts.InitAsync();
        service.Opts = mockOpts;
        await service.Opts.InitAsync();
        service._eventClient = new TestEventClient(mockOpts, null);
        service._chatClient = new TestChatClient(mockOpts, null);
        
        bool eventChatConnectRaised = false;
        bool eventAlertsConnectRaised = false;
        bool eventChatDisconnectRaised = false;
        bool eventAlertsDisconnectRaised = false;
        Action<bool, SubathonEventSource, string, string> handler = (running, source, handle, serviceType) =>
        {
            if (source == SubathonEventSource.Picarto)
            {
                if (serviceType == "Chat")
                    if (running)
                        eventChatConnectRaised = true;
                    else
                        eventChatDisconnectRaised = true;
                else if (serviceType == "Alerts")
                    if (running)
                        eventAlertsConnectRaised = true;
                    else
                        eventAlertsDisconnectRaised = true;
                Assert.Equal("TestChannel", handle);
            }
        };
        IntegrationEvents.ConnectionUpdated += handler;
        
        await service.StartAsync();
        
        await Task.Delay(200);
        
        Assert.True(eventChatConnectRaised);
        Assert.True(eventAlertsConnectRaised);
        
        Assert.False(service._disposed);
        service.Dispose();
        Assert.True(service._disposed);
        service.Dispose();
        Assert.True(service._disposed);
        
        await Task.Delay(200);
        Assert.True(eventChatDisconnectRaised);
        Assert.True(eventAlertsDisconnectRaised);
    }
    
        
    [Fact]
    public async Task Test_ChangeChannel_And_Connect_RaisesConnections()
    {
                
        var configCs = MockConfig(new()
        {
            { ("Chat", "Commands.Pause"), "pause" },
            { ("Chat", "Commands.Pause.permissions.Mods"), "false" },
            { ("Chat", "Commands.Pause.permissions.VIPs"), "false" },
            { ("Chat", "Commands.Pause.permissions.Whitelist"), "specialguy" },
            { ("Picarto", "Username"), "TestChannel"}
        });
        PicartoService service = new PicartoService(null, configCs, null, null);
        MockPicartoClientOptions mockOpts = new MockPicartoClientOptions()
        {
            Channel = "TestChannel"
        };
        await mockOpts.InitAsync();
        service.Opts = mockOpts;
        await service.Opts.InitAsync();
        service._eventClient = new TestEventClient(mockOpts, null);
        service._chatClient = new TestChatClient(mockOpts, null);

        string lastChannelSeen = string.Empty;
        
        bool eventChatConnectRaised = false;
        bool eventAlertsConnectRaised = false;
        bool eventChatDisconnectRaised = false;
        bool eventAlertsDisconnectRaised = false;
        Action<bool, SubathonEventSource, string, string> handler = (running, source, handle, serviceType) =>
        {
            if (source == SubathonEventSource.Picarto)
            {
                if (serviceType == "Chat")
                    if (running)
                        eventChatConnectRaised = true;
                    else
                        eventChatDisconnectRaised = true;
                else if (serviceType == "Alerts")
                    if (running)
                        eventAlertsConnectRaised = true;
                    else
                        eventAlertsDisconnectRaised = true;
            }
        };
        IntegrationEvents.ConnectionUpdated += handler;
        
        await service.StartAsync();
        
        await Task.Delay(200);
        
        Assert.True(eventChatConnectRaised);
        Assert.True(eventAlertsConnectRaised);

        // configCs.Set("Picarto", "Username", "NewChannel");
        await service.UpdateChannel();
        
        await Task.Delay(200);
        Assert.True(eventChatDisconnectRaised);
        Assert.True(eventAlertsDisconnectRaised);
        
        Assert.False(service._disposed);
        service.Dispose();
        Assert.True(service._disposed);
        service.Dispose();
        Assert.True(service._disposed);
    }

}
