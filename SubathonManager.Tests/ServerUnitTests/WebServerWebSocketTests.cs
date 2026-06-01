using System.Text;
using SubathonManager.Core.Models;
using SubathonManager.Core.Enums;
using System.Net.WebSockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using SubathonManager.Server;
using SubathonManager.Data;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Tests.Utility;
// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.Tests.ServerUnitTests;

[Collection("NonParallel")]
public class WebServerWebSocketEventBusEnforcedSequentialTests
{
    
    [Fact]
    public async Task WebSocket_SendRefreshRequest_NoConsumers()
    {
        /*
         * Fails 1 in like 10 runs due to parallel stuff
         */
        var server = WebServerWebSocketTests.CreateServer();
        WebServerWebSocketTests.SetupServices();
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
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Empty(ctx.Socket.SentMessages);
        AppServices.Provider = null!;
        await server.StopAsync(TestContext.Current.CancellationToken);
    }
}

[Collection("SharedEventBusTests")]
public class WebServerWebSocketEventBusTests
{
    
    private static SubathonEvent? CaptureEvent(Action trigger) =>
        EventUtil.SubathonEventCapture.CaptureRequired(trigger);

    [Fact]
    public async Task WebSocket_ReceiveIntegrationSource_AddsSourceAndEvent()
    {
        WebServerWebSocketTests.SetupServices();
        var server = WebServerWebSocketTests.CreateServer();

        var sourceTcs = new TaskCompletionSource<string>();
        Action<string, bool> handler = (src, connected) =>
        {
            if (connected)
                sourceTcs.TrySetResult(src);
        };

        WebServerEvents.WebSocketIntegrationSourceChange += handler;

        try
        {
            var ctx = new MockHttpContext
            {
                IsWebSocket = true
            };
            ctx.Socket.EnqueueReceive(
                    "{\"ws_type\":\"IntegrationSource\",\"source\":\"KoFi\", \"type\": \"KoFiSub\", \"tier\":\"DEFAULT\", \"amount\": 1, \"user\":\"test\"}"
                );
            ctx.Socket.EnqueueClose();

            SubathonEvent? ev = CaptureEvent( async void () => 
                await server.HandleWebSocketRequestAsync(ctx));

            var result = await sourceTcs.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            Assert.Equal(nameof(SubathonEventSource.KoFi), result);
            Assert.NotNull(ev);
            Assert.Equal(SubathonEventSource.KoFi, ev.Source);
            Assert.Equal(SubathonEventType.KoFiSub, ev.EventType);
        }
        finally
        {
            WebServerEvents.WebSocketIntegrationSourceChange -= handler;
            AppServices.Provider = null!;
            await server.StopAsync(TestContext.Current.CancellationToken);
        }
    }
    
    [Fact]
    public async Task WebSocket_ReceiveCommand()
    {
        WebServerWebSocketTests.SetupServices();
        var server = WebServerWebSocketTests.CreateServer();

        var sourceTcs = new TaskCompletionSource<string>();

        Action<string, bool> handler = (src, connected) =>
        {
            if (connected)
                sourceTcs.TrySetResult(src);
        };


        WebServerEvents.WebSocketIntegrationSourceChange += handler;

        try
        {
            var ctx = new MockHttpContext
            {
                IsWebSocket = true
            };

            ctx.Socket.EnqueueReceive(
                    "{\"ws_type\":\"Command\", \"type\": \"Command\", \"message\":\"\", \"command\": \"pause\", \"user\":\"test\"}");
            ctx.Socket.EnqueueClose();
            

            SubathonEvent? ev = CaptureEvent( async void () =>
            {
                await server.HandleWebSocketRequestAsync(ctx);
            });

            Assert.NotNull(ev);
            Assert.Equal(SubathonEventSource.External, ev.Source);
            Assert.Equal(SubathonEventType.Command, ev.EventType);
        }
        finally
        {
            WebServerEvents.WebSocketIntegrationSourceChange -= handler;
            AppServices.Provider = null!;
            await server.StopAsync(TestContext.Current.CancellationToken);
        }
    }
}

[Collection("ProviderOverrideTests")]
public class WebServerWebSocketTests(ITestOutputHelper testOutputHelper)
{
    private readonly ITestOutputHelper _testOutputHelper = testOutputHelper;

    private static async Task WaitForMessageMatchingAsync(
        MockWebSocket socket,
        Func<string, bool> predicate,
        TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!cts.IsCancellationRequested)
        {
            if (socket.SentMessages.Any(m => predicate(Encoding.UTF8.GetString(m))))
                return;
            await Task.Delay(10, cts.Token);
        }
        throw new TimeoutException("No matching websocket message received within timeout.");
    }
    
    private static async Task WaitForMessageAsync(MockWebSocket socket, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);

        while (!cts.IsCancellationRequested)
        {
            if (socket.SentMessages.Count > 0)
                return;

            await Task.Delay(10, cts.Token);
        }

        throw new TimeoutException("No websocket message received within timeout.");
    }

    private static IConfig MakeMockConfig(Dictionary<(string, string), string>? values = null)
    {
        if (values == null) values = new Dictionary<(string, string), string>();
        if (!values.ContainsKey(("Server", "Port"))) values[("Server", "Port")] = "14045";
        var config = MockConfig.MakeMockConfig(values);
        return config;
    }
    
    internal static void SetupServices()
    { 
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextFactory<AppDbContext>(o => o.UseInMemoryDatabase(dbName)); 
        var mockConfig = MakeMockConfig(new()
        {
            { ("Server", "Port"), "14045" }
        });
        services.AddSingleton(mockConfig);
        AppServices.Provider = services.BuildServiceProvider();
    }
    
    internal static WebServer CreateServer()
    {
        SetupServices();
        var logger = AppServices.Provider.GetRequiredService<ILogger<WebServer>>();
        var factory = AppServices.Provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        var mockConfig = MakeMockConfig(new()
        {
            { ("Server", "Port"), "14045" }
        });
        var webserver = new WebServer(logger, mockConfig, factory);
        webserver.Initialize();
        return webserver;
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
        SetupServices();
        var server = CreateServer();
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
        
        await WaitForMessageMatchingAsync(ctx.Socket, m => m.Contains("goals_list"), TimeSpan.FromSeconds(5));
        var sent = ctx.Socket.SentMessages
            .Select(m => Encoding.UTF8.GetString(m))
            .First(m => m.Contains("goals_list"));
        Assert.Equal("{\"type\":\"goals_list\",\"points\":10,\"goals\":[{\"text\":\"Test Goal\",\"points\":5,\"completed\":true}],\"goals_type\":\"Points\"}", sent);
        AppServices.Provider = null!;
        await server.StopAsync(TestContext.Current.CancellationToken);
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
        
        await WaitForMessageMatchingAsync(ctx.Socket, m => m.Contains("value_config"), TimeSpan.FromSeconds(5));
        var sent = ctx.Socket.SentMessages
            .Select(m => Encoding.UTF8.GetString(m))
            .First(m => m.Contains("value_config"));
        Assert.Equal("{ \"type\": \"value_config\", \"ws_type\": \"ValueConfig\", \"data\": [{}] }", sent);
        AppServices.Provider = null!;
        await server.StopAsync(TestContext.Current.CancellationToken);
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
        
        await WaitForMessageMatchingAsync(ctx.Socket, m => m.Contains("goal_completed"), TimeSpan.FromSeconds(5));
        var sent = ctx.Socket.SentMessages
            .Select(m => Encoding.UTF8.GetString(m))
            .First(m => m.Contains("goal_completed"));
        Assert.Equal("{\"type\":\"goal_completed\",\"goal_text\":\"Test Goal\",\"goal_points\":5,\"points\":10}", sent);
        AppServices.Provider = null!;
        await server.StopAsync(TestContext.Current.CancellationToken);
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
        
        server.SendSubathonEventProcessed(subathonEvent, true);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        var messagesAfterUnprocessed = ctx.Socket.SentMessages
            .Select(m => Encoding.UTF8.GetString(m))
            .ToList();
        Assert.DoesNotContain(messagesAfterUnprocessed,
            m => m.Contains("\"type\":\"event\"") && m.Contains("TwitchGiftSub"));

        ctx.Socket.SentMessages.Clear();
        
        subathonEvent.ProcessedToSubathon = true;
        server.SendSubathonEventProcessed(subathonEvent, true);
        
        await WaitForMessageMatchingAsync(
            ctx.Socket,
            m => m.Contains("\"type\":\"event\"") && m.Contains("TwitchGiftSub"),
            TimeSpan.FromSeconds(5));

        Assert.NotEmpty(ctx.Socket.SentMessages);
        var sent = ctx.Socket.SentMessages
            .Select(m => Encoding.UTF8.GetString(m))
            .First(m => m.Contains("\"type\":\"event\"") && m.Contains("TwitchGiftSub"));

        Assert.Contains(
            "{\"type\":\"event\",\"event_type\":\"TwitchGiftSub\",\"source\":\"Twitch\"",
            sent);
        Assert.Contains("\"user\":\"Test User\"", sent);
        AppServices.Provider = null!;
        await server.StopAsync(TestContext.Current.CancellationToken);
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
        
        await WaitForMessageMatchingAsync(ctx.Socket, m => m.Contains("refresh_request"), TimeSpan.FromSeconds(5));
        var sent = ctx.Socket.SentMessages
            .Select(m => Encoding.UTF8.GetString(m))
            .First(m => m.Contains("refresh_request"));
        Assert.Equal($"{{\"type\":\"refresh_request\",\"id\":\"{guid}\"}}", sent);
        AppServices.Provider = null!;
        await server.StopAsync(TestContext.Current.CancellationToken);
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
        await WaitForMessageMatchingAsync(ctx.Socket, m => m.Contains("subathon_timer"), TimeSpan.FromSeconds(5));
        var sent = ctx.Socket.SentMessages
            .Select(m => Encoding.UTF8.GetString(m))
            .First(m => m.Contains("subathon_timer"));
        Assert.Equal("{\"type\":\"subathon_timer\",\"total_seconds\":172800,\"days\":2,\"hours\":0,\"minutes\":0,\"seconds\":0,\"total_points\":5678,\"rounded_money\":6769,\"fractional_money\":6769.55,\"currency\":\"CAD\",\"is_paused\":false,\"is_locked\":false,\"is_reversed\":false,\"multiplier_points\":1,\"multiplier_time\":2,\"multiplier_start_time\":null,\"multiplier_seconds_total\":0,\"multiplier_seconds_remaining\":0,\"total_seconds_elapsed\":259200,\"total_seconds_added\":432000}", sent);
        AppServices.Provider = null!;
        await server.StopAsync(TestContext.Current.CancellationToken);
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
        await WaitForMessageMatchingAsync(ctx.Socket, m => m.Contains("\"type\":\"test\""), TimeSpan.FromSeconds(5));
        var sent = ctx.Socket.SentMessages
            .Select(m => Encoding.UTF8.GetString(m))
            .First(m => m.Contains("\"type\":\"test\""));
        Assert.Equal("{\"type\":\"test\",\"points\":5}", sent);
        AppServices.Provider = null!;
        await server.StopAsync(TestContext.Current.CancellationToken);
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
        await WaitForMessageMatchingAsync(ctx.Socket, m => m.Contains("pong"), TimeSpan.FromSeconds(5));
        var sent = ctx.Socket.SentMessages
            .Select(m => Encoding.UTF8.GetString(m))
            .First(m => m.Contains("pong"));
        Assert.Equal("{\"ws_type\":\"pong\"}", sent);

        AppServices.Provider = null!;
        await server.StopAsync(TestContext.Current.CancellationToken);
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
        await server.StopAsync(TestContext.Current.CancellationToken);
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
        await server.StopAsync(TestContext.Current.CancellationToken);
    }
    
    
    [Fact]
    public async Task WebSocket_ReceiveAndInitConsumer()
    {
        SetupServices();
        var server = CreateServer();

        try
        {
            var factory = AppServices.Provider.GetService<IDbContextFactory<AppDbContext>>();
            await using var db = await factory!.CreateDbContextAsync(TestContext.Current.CancellationToken);

            var subathon = new SubathonData { IsActive = true };
            db.SubathonGoalSets.Add(new SubathonGoalSet { Type = null });
            db.SubathonDatas.Add(subathon);

            db.SubathonEvents.Add(new SubathonEvent
            {
                SubathonId = subathon.Id,
                EventType = SubathonEventType.KoFiDonation,
                Currency = "USD",
                Value = "5",
                ProcessedToSubathon = true
            });

            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            db.ChangeTracker.Clear();

            var ctx = new MockHttpContext
            {
                IsWebSocket = true
            };

            ctx.Socket.EnqueueReceive("{\"ws_type\":\"Widget\",\"origin\":\"unit-test\"}");
            ctx.Socket.EnqueueClose();

            await server.HandleWebSocketRequestAsync(ctx);

            await WaitForMessageAsync(ctx.Socket, TimeSpan.FromSeconds(5));

            Assert.NotEmpty(ctx.Socket.SentMessages);
        }
        finally
        {
            AppServices.Provider = null!;
            server.Stop();
        }
    }
    
    [Fact]
    public async Task WebSocket_ReceiveAndInitConfigConsumer()
    {
        SetupServices();
        var server = CreateServer();

        try
        {
            var factory = AppServices.Provider.GetService<IDbContextFactory<AppDbContext>>();
            await using var db = await factory!.CreateDbContextAsync(TestContext.Current.CancellationToken);

            var subathon = new SubathonData { IsActive = true };
            db.SubathonGoalSets.Add(new SubathonGoalSet { Type = null });
            db.SubathonDatas.Add(subathon);

            db.SubathonEvents.Add(new SubathonEvent
            {
                SubathonId = subathon.Id,
                EventType = SubathonEventType.KoFiDonation,
                Currency = "USD",
                Value = "5",
                ProcessedToSubathon = true
            });

            db.SubathonValues.Add(new SubathonValue
            {
                EventType = SubathonEventType.TwitchSub,
                Meta = "1000"
            });

            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            db.ChangeTracker.Clear();

            var ctx = new MockHttpContext
            {
                IsWebSocket = true
            };

            ctx.Socket.EnqueueReceive("{\"ws_type\":\"ValueConfig\",\"type\":\"value_config\"}");
            ctx.Socket.EnqueueClose();

            await server.HandleWebSocketRequestAsync(ctx);

            await WaitForMessageAsync(ctx.Socket, TimeSpan.FromSeconds(5));

            Assert.NotEmpty(ctx.Socket.SentMessages);
        }
        finally
        {
            AppServices.Provider = null!;
            server.Stop();
        }
    }

}