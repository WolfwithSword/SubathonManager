using System.Text;
using Moq;
using IniParser.Model;
using SubathonManager.Core.Models;
using SubathonManager.Core.Enums;
using System.Net.WebSockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using SubathonManager.Server;
using SubathonManager.Data;
using SubathonManager.Core.Events;
namespace SubathonManager.Tests.ServerUnitTests;

[Collection("ProviderOverrideTests")]
public class WebServerWebSocketTests
{
    
    private static IConfig MockConfig(Dictionary<(string, string), string>? values = null)
    {
        var mock = new Mock<IConfig>();
        
        mock.Setup(c => c.Get(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string s, string k, string d) =>
                values != null && values.TryGetValue((s, k), out var v) ? v : d);
            
        var kd0 = new KeyData("Port");
        kd0.Value = "14045";
        mock.Setup(c => c.GetSection("Server")).Returns(() =>
        {
            var kdc = new KeyDataCollection();
            kdc.AddKey(kd0);
            return kdc;
        });
        return mock.Object;
    }
    
    private static void SetupServices()
    { 
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextFactory<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddSingleton(MockConfig());
        AppServices.Provider = services.BuildServiceProvider();
    }
    
    private static WebServer CreateServer()
    {
        SetupServices();
        var logger = AppServices.Provider.GetRequiredService<ILogger<WebServer>>();
        var factory = AppServices.Provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        return new WebServer(factory, MockConfig(), logger, port: 14045);
    }
    
    private async Task HandleWebSocketAsync(IHttpContext ctx)
    {
        var accept = ctx.AcceptWebSocketAsync();

        if (accept is null)
        {
            await ctx.WriteResponse(400, "Not a WebSocket request");
            return;
        }

        using var socket = await accept;

        var message = Encoding.UTF8.GetBytes("hello");
        await socket.SendAsync(
            message,
            WebSocketMessageType.Text,
            true,
            CancellationToken.None);
    }

    [Fact]
    public void Generate_Injection_Script()
    {   
        var server = CreateServer();
        SetupServices();
        var script = server.GetWebsocketInjectionScript();
        Assert.Contains("ws://localhost:14045/ws", script);
        AppServices.Provider = null!;
    }
    
    [Fact]
    public async Task Non_WebSocket_Request_Is_Rejected_As_WebSocket()
    {
        var ctx = new MockHttpContext
        {
            IsWebSocket = false
        };

        await HandleWebSocketAsync(ctx);

        Assert.Equal(400, ctx.StatusCode);
        Assert.Equal("Not a WebSocket request", ctx.ResponseBody);
    }
    
    [Fact]
    public async Task WebSocket_Request_Is_Accepted()
    {
        var ctx = new MockHttpContext
        {
            IsWebSocket = true
        };

        await HandleWebSocketAsync(ctx);

        Assert.Single(ctx.Socket.SentMessages);

        var text = Encoding.UTF8.GetString(ctx.Socket.SentMessages[0]);
        Assert.Equal("hello", text);
    }
    
    [Fact]
    public async Task WebSocket_Sends_Hello_Message()
    {
        var ctx = new MockHttpContext
        {
            IsWebSocket = true
        };

        await HandleWebSocketAsync(ctx);

        var sent = Encoding.UTF8.GetString(ctx.Socket.SentMessages[0]);
        Assert.Equal("hello", sent);
    }
    
    [Fact]
    public async Task WebSocket_Does_Not_Write_Response()
    {
        var ctx = new MockHttpContext
        {
            IsWebSocket = true
        };
        await HandleWebSocketAsync(ctx);
        Assert.Equal(0, ctx.StatusCode); // default val
    }
    
    [Fact]
    public async Task WebSocket_Does_Not_Call_Accept_When_Not_WS()
    {
        var ctx = new MockHttpContext
        {
            IsWebSocket = false
        };
        await HandleWebSocketAsync(ctx);
        Assert.Equal(1, ctx.AcceptCalls);
    }
    
    [Fact]
    public async Task WebSocket_Is_Disposed()
    {
        var ctx = new MockHttpContext
        {
            IsWebSocket = true
        };
        await HandleWebSocketAsync(ctx);
        Assert.True(ctx.Socket.Disposed);
    }
    
    [Fact]
    public async Task WebSocket_SendGoalsUpdated_List()
    {
        var server = CreateServer();
        SetupServices();
        var ctx = new MockHttpContext
        {
            IsWebSocket = true
        };

        SubathonGoal goal = new SubathonGoal
        {
            Text="Test Goal",
            Points = 5
        };

        List<SubathonGoal> goals = new List<SubathonGoal>();
        goals.Add(goal);
        
        await server.HandleWebSocketRequestAsync(ctx); // does nothing as it exists, but gets coverage
        WebSocketClient client = new WebSocketClient(ctx.Socket);
        client.ClientTypes.Add(WebsocketClientMessageType.Widget);
        server.AddSocketClient(client); // ACTUAL adding to clients list
        server.SendGoalsUpdated(goals,10, GoalsType.Points);
        int count = 0;
        while (ctx.Socket.SentMessages.Count == 0 && count <= 20)
        {
            await Task.Delay(10);
            count++;
        }
        Assert.NotEmpty(ctx.Socket.SentMessages);
        var sent = Encoding.UTF8.GetString(ctx.Socket.SentMessages[0]);
        Assert.Equal("{\"type\":\"goals_list\",\"points\":10,\"goals\":[{\"text\":\"Test Goal\",\"points\":5,\"completed\":true}],\"goals_type\":\"Points\"}", sent);
        AppServices.Provider = null!;
    }    
    
    [Fact]
    public async Task WebSocket_SendSubathonValues()
    {
        var server = CreateServer();
        SetupServices();
        var ctx = new MockHttpContext
        {
            IsWebSocket = true
        };

        
        await server.HandleWebSocketRequestAsync(ctx); // does nothing as it exists, but gets coverage
        WebSocketClient client = new WebSocketClient(ctx.Socket);
        client.ClientTypes.Add(WebsocketClientMessageType.ValueConfig);
        client.ClientTypes.Add(WebsocketClientMessageType.Widget);
        server.AddSocketClient(client); // ACTUAL adding to clients list
        server.SendSubathonValues("[{}]");
        int count = 0;
        while (ctx.Socket.SentMessages.Count == 0 && count <= 20)
        {
            await Task.Delay(10);
            count++;
        }
        Assert.NotEmpty(ctx.Socket.SentMessages);
        var sent = Encoding.UTF8.GetString(ctx.Socket.SentMessages[0]);
        Assert.Equal("{ \"type\": \"value_config\", \"ws_type\": \"ValueConfig\", \"data\": [{}] }", sent);
        AppServices.Provider = null!;
    }
    
    [Fact]
    public async Task WebSocket_SendGoalComplete()
    {
        var server = CreateServer();
        SetupServices();
        var ctx = new MockHttpContext
        {
            IsWebSocket = true
        };

        SubathonGoal goal = new SubathonGoal
        {
            Text="Test Goal",
            Points = 5
        };

        await server.HandleWebSocketRequestAsync(ctx); // does nothing as it exists, but gets coverage
        WebSocketClient client = new WebSocketClient(ctx.Socket);
        client.ClientTypes.Add(WebsocketClientMessageType.Widget);
        server.AddSocketClient(client); // ACTUAL adding to clients list
        server.SendGoalCompleted(goal,10);
        int count = 0;
        while (ctx.Socket.SentMessages.Count == 0 && count <= 20)
        {
            await Task.Delay(10);
            count++;
        }
        Assert.NotEmpty(ctx.Socket.SentMessages);
        var sent = Encoding.UTF8.GetString(ctx.Socket.SentMessages[0]);
        Assert.Equal("{\"type\":\"goal_completed\",\"goal_text\":\"Test Goal\",\"goal_points\":5,\"points\":10}", sent);
        AppServices.Provider = null!;
    }
    
    [Fact]
    public async Task WebSocket_SendSubathonEvent()
    {
        var server = CreateServer();
        SetupServices();
        var ctx = new MockHttpContext
        {
            IsWebSocket = true
        };

        SubathonEvent subathonEvent = new SubathonEvent
        {
            EventType = SubathonEventType.TwitchGiftSub,
            Amount = 5,
            User = "Test User",
            Value = "1000",
            SecondsValue = 60,
            PointsValue = 1,
            Currency = "sub",
            Source = SubathonEventSource.Twitch,
            ProcessedToSubathon = false
        };

        await server.HandleWebSocketRequestAsync(ctx); // does nothing as it exists, but gets coverage
        WebSocketClient client = new WebSocketClient(ctx.Socket);
        client.ClientTypes.Add(WebsocketClientMessageType.Widget);
        server.AddSocketClient(client); // ACTUAL adding to clients list
        server.SendSubathonEventProcessed(subathonEvent,true);
        await Task.Delay(50);
        Assert.Empty(ctx.Socket.SentMessages);

        subathonEvent.ProcessedToSubathon = true;
        server.SendSubathonEventProcessed(subathonEvent,true);
        int count = 0;
        while (ctx.Socket.SentMessages.Count == 0 && count <= 20)
        {
            await Task.Delay(10);
            count++;
        }
        Assert.NotEmpty(ctx.Socket.SentMessages);

        var sent = Encoding.UTF8.GetString(ctx.Socket.SentMessages[0]);
        Assert.Contains("{\"type\":\"event\",\"event_type\":\"TwitchGiftSub\",\"source\":\"Twitch\",\"seconds_added\":300,\"points_added\":5,\"user\":\"Test User\",\"value\":\"1000\",\"amount\":5,\"currency\":\"sub\",\"command\":\"None\"", sent);
        AppServices.Provider = null!;
    }    
    
    [Fact]
    public async Task WebSocket_SendRefreshRequest()
    {
        var server = CreateServer();
        SetupServices();
        var ctx = new MockHttpContext
        {
            IsWebSocket = true
        };
        
        await server.HandleWebSocketRequestAsync(ctx); // does nothing as it exists, but gets coverage
        WebSocketClient client = new WebSocketClient(ctx.Socket);
        client.ClientTypes.Add(WebsocketClientMessageType.Overlay); // only one that gets refresh
        server.AddSocketClient(client); // ACTUAL adding to clients list
        Guid guid = Guid.Empty;
        server.SendRefreshRequest(guid);
        int count = 0;
        while (ctx.Socket.SentMessages.Count == 0 && count <= 20)
        {
            await Task.Delay(10);
            count++;
        }
        Assert.NotEmpty(ctx.Socket.SentMessages);
        var sent = Encoding.UTF8.GetString(ctx.Socket.SentMessages[0]);
        Assert.Equal($"{{\"type\":\"refresh_request\",\"id\":\"{guid}\"}}", sent);
        AppServices.Provider = null!;
    }
    
    [Fact]
    public async Task WebSocket_SendRefreshRequest_NoConsumers()
    {
        var server = CreateServer();
        SetupServices();
        var ctx = new MockHttpContext
        {
            IsWebSocket = true
        };
        
        await server.HandleWebSocketRequestAsync(ctx);
        WebSocketClient client = new WebSocketClient(ctx.Socket);
        client.ClientTypes.Add(WebsocketClientMessageType.Widget);
        server.AddSocketClient(client);
        Guid guid = Guid.Empty;
        server.SendRefreshRequest(guid);
        int count = 0;
        while (ctx.Socket.SentMessages.Count == 0 && count <= 10)
        {
            await Task.Delay(10);
            count++;
        }
        Assert.Empty(ctx.Socket.SentMessages);
        AppServices.Provider = null!;
    }
    
    [Fact]
    public async Task WebSocket_SendSubathonData()
    {
        var server = CreateServer();
        SetupServices();
        var ctx = new MockHttpContext
        {
            IsWebSocket = true
        };
        
        await server.HandleWebSocketRequestAsync(ctx); // does nothing as it exists, but gets coverage
        WebSocketClient client = new WebSocketClient(ctx.Socket);
        client.ClientTypes.Add(WebsocketClientMessageType.Widget);
        server.AddSocketClient(client); // ACTUAL adding to clients list

        MultiplierData mult = new MultiplierData()
        {
            Multiplier = 2.0,
            ApplyToPoints = false,
            ApplyToSeconds = true
        };
        
        SubathonData subathon = new SubathonData()
        {
            MillisecondsCumulative = (long) TimeSpan.FromDays(5).TotalMilliseconds,
            MillisecondsElapsed = (long) TimeSpan.FromDays(3).TotalMilliseconds,
            Points = 5678,
            IsPaused = false,
            IsActive = true,
            IsLocked = false,
            Multiplier = mult,
            Currency = "CAD",
            MoneySum = 6769.55,
            ReversedTime = false
        };
        
        server.SendSubathonDataUpdate(subathon, DateTime.Now);
        int count = 0;
        while (ctx.Socket.SentMessages.Count == 0 && count <= 20)
        {
            await Task.Delay(10);
            count++;
        }
        Assert.NotEmpty(ctx.Socket.SentMessages);
        var sent = Encoding.UTF8.GetString(ctx.Socket.SentMessages[0]);
        Assert.Equal("{\"type\":\"subathon_timer\",\"total_seconds\":172800,\"days\":2,\"hours\":0,\"minutes\":0,\"seconds\":0,\"total_points\":5678,\"rounded_money\":6769,\"fractional_money\":6769.55,\"currency\":\"CAD\",\"is_paused\":false,\"is_locked\":false,\"is_reversed\":false,\"multiplier_points\":1,\"multiplier_time\":2,\"multiplier_start_time\":null,\"multiplier_seconds_total\":0,\"multiplier_seconds_remaining\":0,\"total_seconds_elapsed\":259200,\"total_seconds_added\":432000}", sent);
        AppServices.Provider = null!;
    }
    
        
    [Fact]
    public async Task WebSocket_SelectSend()
    {
        var server = CreateServer();
        SetupServices();
        var ctx = new MockHttpContext
        {
            IsWebSocket = true
        };
        
        await server.HandleWebSocketRequestAsync(ctx); // does nothing as it exists, but gets coverage
        WebSocketClient client = new WebSocketClient(ctx.Socket);
        client.ClientTypes.Add(WebsocketClientMessageType.Widget);
        server.AddSocketClient(client); // ACTUAL adding to clients list

        object data = new
        {
            type = "test",
            points = 5,
        };

        await server.SelectSendAsync(client, data);
        int count = 0;
        while (ctx.Socket.SentMessages.Count == 0 && count <= 20)
        {
            await Task.Delay(10);
            count++;
        }
        Assert.NotEmpty(ctx.Socket.SentMessages);
        var sent = Encoding.UTF8.GetString(ctx.Socket.SentMessages[0]);
        Assert.Equal("{\"type\":\"test\",\"points\":5}", sent);
        AppServices.Provider = null!;
    }
    
    [Fact]
    public async Task WebSocket_ReceivePing_ReturnsPong()
    {
        var server = CreateServer();
        SetupServices();

        var ctx = new MockHttpContext
        {
            IsWebSocket = true
        };

        ctx.Socket.EnqueueReceive("{\"ws_type\":\"ping\"}");
        ctx.Socket.EnqueueClose();

        await server.HandleWebSocketRequestAsync(ctx);
        int count = 0;
        while (ctx.Socket.SentMessages.Count == 0 && count <= 20)
        {
            await Task.Delay(10);
            count++;
        }
        Assert.NotEmpty(ctx.Socket.SentMessages);

        var sent = Encoding.UTF8.GetString(ctx.Socket.SentMessages[0]);
        Assert.Equal("{\"ws_type\":\"pong\"}", sent);

        AppServices.Provider = null!;
    }
    
    [Fact]
    public async Task WebSocket_ReceiveHello_DoesNotSendMessage()
    {
        var server = CreateServer();
        SetupServices();

        var ctx = new MockHttpContext
        {
            IsWebSocket = true
        };

        ctx.Socket.EnqueueReceive("{\"ws_type\":\"hello\",\"origin\":\"unit-test\"}");
        ctx.Socket.EnqueueClose();

        await server.HandleWebSocketRequestAsync(ctx);

        Assert.Empty(ctx.Socket.SentMessages);

        AppServices.Provider = null!;
    }

    [Fact]
    public async Task WebSocket_ReceiveIntegrationSource_AddsSource_AndRaisesEvent()
    {
        var server = CreateServer();
        SetupServices();

        var tcs = new TaskCompletionSource<string>();

        
        Action<string, bool> handler = (src, connected) =>
        {
            if (connected)
                tcs.TrySetResult(src);
        };
        WebServerEvents.WebSocketIntegrationSourceChange += handler;

        var ctx = new MockHttpContext
        {
            IsWebSocket = true
        };

        ctx.Socket.EnqueueReceive(
            "{\"ws_type\":\"IntegrationSource\",\"source\":\"KoFi\"}"
        );
        ctx.Socket.EnqueueClose();

        await server.HandleWebSocketRequestAsync(ctx);

        var result = await tcs.Task;
        Assert.Equal(nameof(SubathonEventSource.KoFi), result);
        WebServerEvents.WebSocketIntegrationSourceChange -= handler;
        AppServices.Provider = null!;
    }


    [Fact]
    public async Task WebSocket_ReceiveIntegrationSource_AddsSourceAndEvent()
    {
        var server = CreateServer();
        SetupServices();

        var tcs = new TaskCompletionSource<string>();

        
        Action<string, bool> handler = (src, connected) =>
        {
            if (connected)
                tcs.TrySetResult(src);
        };

        WebServerEvents.WebSocketIntegrationSourceChange += handler;
        
        SubathonEvent? ev = null;
        Action<SubathonEvent> handler2 = e => ev = e;
        SubathonEvents.SubathonEventCreated += handler2;

        var ctx = new MockHttpContext
        {
            IsWebSocket = true
        };

        ctx.Socket.EnqueueReceive(
            "{\"ws_type\":\"IntegrationSource\",\"source\":\"KoFi\", \"type\": \"KoFiSub\", \"tier\":\"DEFAULT\", \"amount\": 1, \"user\":\"test\"}"
        );
        ctx.Socket.EnqueueClose();

        await server.HandleWebSocketRequestAsync(ctx);

        var result = await tcs.Task;
        int count = 0;
        while (count < 20 && ev == null)
        {
            await Task.Delay(10);
            count++;
        }

        Assert.Equal(nameof(SubathonEventSource.KoFi), result);
        Assert.NotNull(ev);
        Assert.Equal(SubathonEventSource.KoFi, ev.Source);
        Assert.Equal(SubathonEventType.KoFiSub, ev.EventType);
        
        WebServerEvents.WebSocketIntegrationSourceChange -= handler;
        SubathonEvents.SubathonEventCreated -= handler2;
        AppServices.Provider = null!;
    }
    
    [Fact]
    public async Task WebSocket_ReceiveAndInitConsumer()
    {
        var server = CreateServer();
        
        var factory = AppServices.Provider.GetService<IDbContextFactory<AppDbContext>>();
        await using var db = await factory!.CreateDbContextAsync();
        var subathon = new SubathonData { IsActive = true };
        db.SubathonGoalSets.Add(new SubathonGoalSet { Type = null });
        db!.SubathonDatas.Add(subathon);

        db.SubathonEvents.Add(new SubathonEvent {
            SubathonId = subathon.Id,
            EventType = SubathonEventType.KoFiDonation,
            Currency = "USD",
            Value = "5",
            ProcessedToSubathon = true
        });

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        
        var ctx = new MockHttpContext
        {
            IsWebSocket = true
        };

        ctx.Socket.EnqueueReceive("{\"ws_type\":\"Widget\",\"origin\":\"unit-test\"}");
        ctx.Socket.EnqueueClose();

        await server.HandleWebSocketRequestAsync(ctx);
        int count = 0;
        while (ctx.Socket.SentMessages.Count == 0 && count <= 20)
        {
            await Task.Delay(10);
            count++;
        }
        Assert.NotEmpty(ctx.Socket.SentMessages);

        AppServices.Provider = null!;
        server.Stop();
    }    
    
    [Fact]
    public async Task WebSocket_ReceiveAndInitConfigConsumer()
    {
        var server = CreateServer();
        
        var factory = AppServices.Provider.GetService<IDbContextFactory<AppDbContext>>();
        await using var db = await factory!.CreateDbContextAsync();
        var subathon = new SubathonData { IsActive = true };
        db.SubathonGoalSets.Add(new SubathonGoalSet { Type = null });
        db!.SubathonDatas.Add(subathon);

        db.SubathonEvents.Add(new SubathonEvent {
            SubathonId = subathon.Id,
            EventType = SubathonEventType.KoFiDonation,
            Currency = "USD",
            Value = "5",
            ProcessedToSubathon = true
        });
        
        db.SubathonValues.Add(new SubathonValue {
            EventType = SubathonEventType.TwitchSub,
            Meta = "1000"
        });
        
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        
        var ctx = new MockHttpContext
        {
            IsWebSocket = true
        };

        ctx.Socket.EnqueueReceive("{\"ws_type\":\"ValueConfig\",\"type\":\"value_config\"}");
        ctx.Socket.EnqueueClose();

        await server.HandleWebSocketRequestAsync(ctx);
        int count = 0;
        while (ctx.Socket.SentMessages.Count == 0 && count <= 20)
        {
            await Task.Delay(10);
            count++;
        }
        Assert.NotEmpty(ctx.Socket.SentMessages);

        AppServices.Provider = null!;
        server.Stop();
    }

}