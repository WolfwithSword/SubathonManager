using System.Net.WebSockets;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Data;
using SubathonManager.Server;
using SubathonManager.Server.Interfaces;
using SubathonManager.Tests.Utility;

// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.Tests.ServerUnitTests;

[Collection("GlobalState")]
public class WebServerWebSocketEventBusEnforcedSequentialTests {
    [Fact]
    public async Task WebSocket_SendRefreshRequest_NoConsumers() {
        /*
         * Fails 1 in like 10 runs due to parallel stuff
         */
        WebServer server = WebServerWebSocketTests.CreateServer();
        WebServerWebSocketTests.SetupServices();
        var ctx = new MockHttpContext {
            IsWebSocket = true
        };

        await server.HandleWebSocketRequestAsync(ctx);
        var client = new WebSocketClient(ctx.Socket);
        client.ClientTypes.Add(WebsocketClientMessageType.Widget);
        server.AddSocketClient(client);
        var guid = Guid.Empty;
        server.SendRefreshRequest(guid);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Empty(ctx.Socket.SentMessages);
        AppServices.Provider = null!;
        await server.StopAsync(TestContext.Current.CancellationToken);
    }
}

[Collection("GlobalState")]
public class WebServerWebSocketEventBusTests {
    private static Task<SubathonEvent?> CaptureEventAsync(Func<Task> trigger) {
        return EventUtil.SubathonEventCapture.CaptureAsync(trigger);
    }

    [Fact]
    public async Task WebSocket_ReceiveIntegrationSource_AddsSourceAndEvent() {
        WebServerWebSocketTests.SetupServices();
        WebServer server = WebServerWebSocketTests.CreateServer();

        var sourceTcs = new TaskCompletionSource<string>();
        Action<string, bool> handler = (src, connected) => {
            if (connected)
                sourceTcs.TrySetResult(src);
        };

        WebServerEvents.WebSocketIntegrationSourceChange += handler;

        try {
            var ctx = new MockHttpContext {
                IsWebSocket = true
            };
            ctx.Socket.EnqueueReceive(
                "{\"ws_type\":\"IntegrationSource\",\"source\":\"KoFi\", \"type\": \"KoFiSub\", \"tier\":\"DEFAULT\", \"amount\": 1, \"user\":\"test\"}"
            );
            ctx.Socket.EnqueueClose();

            SubathonEvent? ev = await CaptureEventAsync(() => server.HandleWebSocketRequestAsync(ctx));

            string result =
                await sourceTcs.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            Assert.Equal(nameof(SubathonEventSource.KoFi), result);
            Assert.NotNull(ev);
            Assert.Equal(SubathonEventSource.KoFi, ev.Source);
            Assert.Equal(SubathonEventType.KoFiSub, ev.EventType);
        }
        finally {
            WebServerEvents.WebSocketIntegrationSourceChange -= handler;
            AppServices.Provider = null!;
            await server.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task WebSocket_ReceiveCommand() {
        WebServerWebSocketTests.SetupServices();
        WebServer server = WebServerWebSocketTests.CreateServer();

        var sourceTcs = new TaskCompletionSource<string>();

        Action<string, bool> handler = (src, connected) => {
            if (connected)
                sourceTcs.TrySetResult(src);
        };


        WebServerEvents.WebSocketIntegrationSourceChange += handler;

        try {
            var ctx = new MockHttpContext {
                IsWebSocket = true
            };

            ctx.Socket.EnqueueReceive(
                "{\"ws_type\":\"Command\", \"type\": \"Command\", \"message\":\"\", \"command\": \"pause\", \"user\":\"test\"}");
            ctx.Socket.EnqueueClose();


            SubathonEvent? ev = await CaptureEventAsync(() => server.HandleWebSocketRequestAsync(ctx));

            Assert.NotNull(ev);
            Assert.Equal(SubathonEventSource.External, ev.Source);
            Assert.Equal(SubathonEventType.Command, ev.EventType);
        }
        finally {
            WebServerEvents.WebSocketIntegrationSourceChange -= handler;
            AppServices.Provider = null!;
            await server.StopAsync(TestContext.Current.CancellationToken);
        }
    }
}

[Collection("GlobalState")]
public class WebServerWebSocketTests(ITestOutputHelper testOutputHelper) {
    private readonly ITestOutputHelper _testOutputHelper = testOutputHelper;

    private static async Task WaitForMessageMatchingAsync(
        MockWebSocket socket,
        Func<string, bool> predicate,
        TimeSpan timeout) {
        using var cts = new CancellationTokenSource(timeout);
        while (!cts.IsCancellationRequested) {
            if (socket.SentMessages.Any(m => predicate(Encoding.UTF8.GetString(m))))
                return;
            await Task.Delay(10, cts.Token);
        }

        throw new TimeoutException("No matching websocket message received within timeout.");
    }

    private static async Task WaitForMessageAsync(MockWebSocket socket, TimeSpan timeout) {
        using var cts = new CancellationTokenSource(timeout);

        while (!cts.IsCancellationRequested) {
            if (socket.SentMessages.Count > 0)
                return;

            await Task.Delay(10, cts.Token);
        }

        throw new TimeoutException("No websocket message received within timeout.");
    }

    private static IConfig MakeMockConfig(Dictionary<(string, string), string>? values = null) {
        if (values == null) values = new Dictionary<(string, string), string>();
        if (!values.ContainsKey(("Server", "Port"))) values[("Server", "Port")] = "14045";
        IConfig config = MockConfig.MakeMockConfig(values);
        return config;
    }

    internal static void SetupServices() {
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextFactory<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
        IConfig mockConfig = MakeMockConfig(new Dictionary<(string, string), string> {
            { ("Server", "Port"), "14045" }
        });
        services.AddSingleton(mockConfig);
        AppServices.Provider = services.BuildServiceProvider();
    }

    internal static WebServer CreateServer() {
        SetupServices();
        var logger = AppServices.Provider.GetRequiredService<ILogger<WebServer>>();
        var factory = AppServices.Provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        IConfig mockConfig = MakeMockConfig(new Dictionary<(string, string), string> {
            { ("Server", "Port"), "14045" }
        });
        var webserver = new WebServer(logger, mockConfig, factory);
        webserver.Initialize();
        return webserver;
    }

    private async Task HandleWebSocketAsync(IHttpContext ctx) {
        Task<WebSocket>? accept = ctx.AcceptWebSocketAsync();

        if (accept is null) {
            await ctx.WriteResponse(400, "Not a WebSocket request");
            return;
        }

        using WebSocket socket = await accept;

        byte[] message = Encoding.UTF8.GetBytes("hello");
        await socket.SendAsync(
            message,
            WebSocketMessageType.Text,
            true,
            CancellationToken.None);
    }

    [Fact]
    public void Generate_Injection_Script() {
        SetupServices();
        WebServer server = CreateServer();
        string script = server.GetWebsocketInjectionScript();
        Assert.Contains("ws://localhost:14045/ws", script);
        AppServices.Provider = null!;
    }

    [Fact]
    public void Injection_Script_Widget_Mode_Is_Not_An_Overlay_Host() {
        SetupServices();
        WebServer server = CreateServer();
        string script = server.GetWebsocketInjectionScript();

        Assert.Contains("var IS_OVERLAY = false;", script);
        Assert.Contains("var ROUTE_ID = '';", script);
        Assert.DoesNotContain("__IS_OVERLAY__", script);
        Assert.DoesNotContain("__ROUTE_ID__", script);
        Assert.DoesNotContain("__WS_URL__", script);
        Assert.DoesNotContain("__EMPTY_GUID__", script);

        AppServices.Provider = null!;
    }

    [Fact]
    public void Injection_Script_Overlay_Mode_Carries_Route_Id() {
        SetupServices();
        WebServer server = CreateServer();
        var routeId = Guid.NewGuid();
        string script = server.GetWebsocketInjectionScript(routeId.ToString());

        Assert.Contains("var IS_OVERLAY = true;", script);
        Assert.Contains($"var ROUTE_ID = '{routeId}';", script);
        Assert.Contains($"var EMPTY_GUID = '{Guid.Empty}';", script);
        Assert.Contains("ws_type: 'Overlay'", script);
        Assert.Contains("ws_type: 'Widget'", script);

        AppServices.Provider = null!;
    }

    [Fact]
    public void Injection_Script_Still_Dispatches_Every_Legacy_Global() {
        SetupServices();
        WebServer server = CreateServer();
        string script = server.GetWebsocketInjectionScript();
        foreach (string handler in new[] {
                     "handleSubathonUpdate", "handleSubathonEvent", "handlePromptUpdate",
                     "handleGoalsUpdate", "handleGoalCompleted", "handleValueConfig",
                     "handleTotalsUpdate", "handleSubscriptionTotalsUpdate", "handleWheelSpinResult",
                     "handleWheelData", "handleWheelSpinStart", "handleWheelSpinStatus",
                     "handleVarsUpdate", "handleSubathonDisconnect"
                 })
            Assert.Contains(handler, script);

        AppServices.Provider = null!;
    }

    [Fact]
    public void Injection_Script_Replays_Cached_State_To_Late_Legacy_Handlers() {
        SetupServices();
        WebServer server = CreateServer();
        string script = server.GetWebsocketInjectionScript();

        Assert.Contains("DOMContentLoaded", script);
        Assert.Contains("replayStateToLegacy", script);
        Assert.Contains("legacyServed", script);

        foreach (string stateType in new[] {
                     "subathon_timer", "subathon_totals", "subscription_totals",
                     "goals_list", "value_config", "prompt_update", "wheel_data"
                 })
            Assert.Contains(stateType + ":", script);

        AppServices.Provider = null!;
    }

    [Fact]
    public async Task Non_WebSocket_Request_Is_Rejected_As_WebSocket() {
        var ctx = new MockHttpContext {
            IsWebSocket = false
        };

        await HandleWebSocketAsync(ctx);

        Assert.Equal(400, ctx.StatusCode);
        Assert.Equal("Not a WebSocket request", ctx.ResponseBody);
    }

    [Fact]
    public async Task WebSocket_Request_Is_Accepted() {
        var ctx = new MockHttpContext {
            IsWebSocket = true
        };

        await HandleWebSocketAsync(ctx);

        Assert.Single(ctx.Socket.SentMessages);

        string text = Encoding.UTF8.GetString(ctx.Socket.SentMessages[0]);
        Assert.Equal("hello", text);
    }

    [Fact]
    public async Task WebSocket_Sends_Hello_Message() {
        var ctx = new MockHttpContext {
            IsWebSocket = true
        };

        await HandleWebSocketAsync(ctx);

        string sent = Encoding.UTF8.GetString(ctx.Socket.SentMessages[0]);
        Assert.Equal("hello", sent);
    }

    [Fact]
    public async Task WebSocket_Does_Not_Write_Response() {
        var ctx = new MockHttpContext {
            IsWebSocket = true
        };
        await HandleWebSocketAsync(ctx);
        Assert.Equal(0, ctx.StatusCode); // default val
    }

    [Fact]
    public async Task WebSocket_Does_Not_Call_Accept_When_Not_WS() {
        var ctx = new MockHttpContext {
            IsWebSocket = false
        };
        await HandleWebSocketAsync(ctx);
        Assert.Equal(1, ctx.AcceptCalls);
    }

    [Fact]
    public async Task WebSocket_Is_Disposed() {
        var ctx = new MockHttpContext {
            IsWebSocket = true
        };
        await HandleWebSocketAsync(ctx);
        Assert.True(ctx.Socket.Disposed);
    }

    [Fact]
    public async Task WebSocket_SendGoalsUpdated_List() {
        WebServer server = CreateServer();
        SetupServices();
        var ctx = new MockHttpContext {
            IsWebSocket = true
        };

        var goal = new SubathonGoal {
            Text = "Test Goal",
            Points = 5
        };

        var goals = new List<SubathonGoal>();
        goals.Add(goal);

        await server.HandleWebSocketRequestAsync(ctx); // does nothing as it exists, but gets coverage
        var client = new WebSocketClient(ctx.Socket);
        client.ClientTypes.Add(WebsocketClientMessageType.Widget);
        server.AddSocketClient(client); // ACTUAL adding to clients list
        server.SendGoalsUpdated(goals, 10, GoalsType.Points);

        await WaitForMessageMatchingAsync(ctx.Socket, m => m.Contains("goals_list"), TimeSpan.FromSeconds(5));
        string sent = ctx.Socket.SentMessages
            .Select(m => Encoding.UTF8.GetString(m))
            .First(m => m.Contains("goals_list"));
        Assert.Equal(
            "{\"type\":\"goals_list\",\"points\":10,\"goals\":[{\"text\":\"Test Goal\",\"points\":5,\"completed\":true}],\"goals_type\":\"Points\"}",
            sent);
        AppServices.Provider = null!;
        await server.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task WebSocket_SendSubathonValues() {
        WebServer server = CreateServer();
        SetupServices();
        var ctx = new MockHttpContext {
            IsWebSocket = true
        };


        await server.HandleWebSocketRequestAsync(ctx); // does nothing as it exists, but gets coverage
        var client = new WebSocketClient(ctx.Socket);
        client.ClientTypes.Add(WebsocketClientMessageType.ValueConfig);
        client.ClientTypes.Add(WebsocketClientMessageType.Widget);
        server.AddSocketClient(client); // ACTUAL adding to clients list
        server.SendSubathonValues("[{}]");

        await WaitForMessageMatchingAsync(ctx.Socket, m => m.Contains("value_config"), TimeSpan.FromSeconds(5));
        string sent = ctx.Socket.SentMessages
            .Select(m => Encoding.UTF8.GetString(m))
            .First(m => m.Contains("value_config"));
        Assert.Equal("{ \"type\": \"value_config\", \"ws_type\": \"ValueConfig\", \"data\": [{}] }", sent);
        AppServices.Provider = null!;
        await server.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task WebSocket_SendGoalComplete() {
        WebServer server = CreateServer();
        SetupServices();
        var ctx = new MockHttpContext {
            IsWebSocket = true
        };

        var goal = new SubathonGoal {
            Text = "Test Goal",
            Points = 5
        };

        await server.HandleWebSocketRequestAsync(ctx); // does nothing as it exists, but gets coverage
        var client = new WebSocketClient(ctx.Socket);
        client.ClientTypes.Add(WebsocketClientMessageType.Widget);
        server.AddSocketClient(client); // ACTUAL adding to clients list
        server.SendGoalCompleted(goal, 10);

        await WaitForMessageMatchingAsync(ctx.Socket, m => m.Contains("goal_completed"), TimeSpan.FromSeconds(5));
        string sent = ctx.Socket.SentMessages
            .Select(m => Encoding.UTF8.GetString(m))
            .First(m => m.Contains("goal_completed"));
        Assert.Equal("{\"type\":\"goal_completed\",\"goal_text\":\"Test Goal\",\"goal_points\":5,\"points\":10}", sent);
        AppServices.Provider = null!;
        await server.StopAsync(TestContext.Current.CancellationToken);
    }


    [Fact]
    public async Task WebSocket_SendSubathonEvent() {
        WebServer server = CreateServer();
        SetupServices();
        var ctx = new MockHttpContext {
            IsWebSocket = true
        };

        var subathonEvent = new SubathonEvent {
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
        var client = new WebSocketClient(ctx.Socket);
        client.ClientTypes.Add(WebsocketClientMessageType.Widget);
        server.AddSocketClient(client); // ACTUAL adding to clients list

        server.SendSubathonEventProcessed(subathonEvent, true);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        List<string> messagesAfterUnprocessed = ctx.Socket.SentMessages
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
        string sent = ctx.Socket.SentMessages
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
    public async Task WebSocket_SendRefreshRequest() {
        WebServer server = CreateServer();
        SetupServices();
        var ctx = new MockHttpContext {
            IsWebSocket = true
        };

        await server.HandleWebSocketRequestAsync(ctx); // does nothing as it exists, but gets coverage
        var client = new WebSocketClient(ctx.Socket);
        client.ClientTypes.Add(WebsocketClientMessageType.Overlay); // only one that gets refresh
        server.AddSocketClient(client); // ACTUAL adding to clients list
        var guid = Guid.Empty;
        server.SendRefreshRequest(guid);

        await WaitForMessageMatchingAsync(ctx.Socket, m => m.Contains("refresh_request"), TimeSpan.FromSeconds(5));
        string sent = ctx.Socket.SentMessages
            .Select(m => Encoding.UTF8.GetString(m))
            .First(m => m.Contains("refresh_request"));
        Assert.Equal($"{{\"type\":\"refresh_request\",\"id\":\"{guid}\"}}", sent);
        AppServices.Provider = null!;
        await server.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task WebSocket_SendSubathonData() {
        WebServer server = CreateServer();
        SetupServices();
        var ctx = new MockHttpContext {
            IsWebSocket = true
        };

        await server.HandleWebSocketRequestAsync(ctx); // does nothing as it exists, but gets coverage
        var client = new WebSocketClient(ctx.Socket);
        client.ClientTypes.Add(WebsocketClientMessageType.Widget);
        server.AddSocketClient(client); // ACTUAL adding to clients list

        var mult = new MultiplierData {
            Multiplier = 2.0,
            ApplyToPoints = false,
            ApplyToSeconds = true
        };

        var subathon = new SubathonData {
            MillisecondsCumulative = (long)TimeSpan.FromDays(5).TotalMilliseconds,
            MillisecondsElapsed = (long)TimeSpan.FromDays(3).TotalMilliseconds,
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
        string sent = ctx.Socket.SentMessages
            .Select(m => Encoding.UTF8.GetString(m))
            .First(m => m.Contains("subathon_timer"));
        Assert.Equal(
            "{\"type\":\"subathon_timer\",\"total_seconds\":172800,\"days\":2,\"hours\":0,\"minutes\":0,\"seconds\":0,\"total_points\":5678,\"rounded_money\":6769,\"fractional_money\":6769.55,\"currency\":\"CAD\",\"is_paused\":false,\"is_locked\":false,\"is_reversed\":false,\"multiplier_points\":1,\"multiplier_time\":2,\"multiplier_start_time\":null,\"multiplier_seconds_total\":0,\"multiplier_seconds_remaining\":0,\"total_seconds_elapsed\":259200,\"total_seconds_added\":432000}",
            sent);
        AppServices.Provider = null!;
        await server.StopAsync(TestContext.Current.CancellationToken);
    }


    [Fact]
    public async Task WebSocket_SelectSend() {
        WebServer server = CreateServer();
        SetupServices();
        var ctx = new MockHttpContext {
            IsWebSocket = true
        };

        await server.HandleWebSocketRequestAsync(ctx); // does nothing as it exists, but gets coverage
        var client = new WebSocketClient(ctx.Socket);
        client.ClientTypes.Add(WebsocketClientMessageType.Widget);
        server.AddSocketClient(client); // ACTUAL adding to clients list

        object data = new {
            type = "test",
            points = 5
        };

        await server.SelectSendAsync(client, data);
        await WaitForMessageMatchingAsync(ctx.Socket, m => m.Contains("\"type\":\"test\""), TimeSpan.FromSeconds(5));
        string sent = ctx.Socket.SentMessages
            .Select(m => Encoding.UTF8.GetString(m))
            .First(m => m.Contains("\"type\":\"test\""));
        Assert.Equal("{\"type\":\"test\",\"points\":5}", sent);
        AppServices.Provider = null!;
        await server.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task WebSocket_ReceivePing_ReturnsPong() {
        WebServer server = CreateServer();
        SetupServices();

        var ctx = new MockHttpContext {
            IsWebSocket = true
        };

        ctx.Socket.EnqueueReceive("{\"ws_type\":\"ping\"}");
        ctx.Socket.EnqueueClose();

        await server.HandleWebSocketRequestAsync(ctx);
        await WaitForMessageMatchingAsync(ctx.Socket, m => m.Contains("pong"), TimeSpan.FromSeconds(5));
        string sent = ctx.Socket.SentMessages
            .Select(m => Encoding.UTF8.GetString(m))
            .First(m => m.Contains("pong"));
        Assert.Equal("{\"ws_type\":\"pong\"}", sent);

        AppServices.Provider = null!;
        await server.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task WebSocket_ReceiveHello_DoesNotSendMessage() {
        WebServer server = CreateServer();
        SetupServices();

        var ctx = new MockHttpContext {
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
    public async Task WebSocket_ReceiveIntegrationSource_AddsSource_AndRaisesEvent() {
        WebServer server = CreateServer();
        SetupServices();

        var tcs = new TaskCompletionSource<string>();


        Action<string, bool> handler = (src, connected) => {
            if (connected)
                tcs.TrySetResult(src);
        };
        WebServerEvents.WebSocketIntegrationSourceChange += handler;

        var ctx = new MockHttpContext {
            IsWebSocket = true
        };

        ctx.Socket.EnqueueReceive(
            "{\"ws_type\":\"IntegrationSource\",\"source\":\"KoFi\"}"
        );
        ctx.Socket.EnqueueClose();

        await server.HandleWebSocketRequestAsync(ctx);

        string result = await tcs.Task;
        Assert.Equal(nameof(SubathonEventSource.KoFi), result);
        WebServerEvents.WebSocketIntegrationSourceChange -= handler;
        AppServices.Provider = null!;
        await server.StopAsync(TestContext.Current.CancellationToken);
    }


    [Fact]
    public async Task WebSocket_ReceiveAndInitConsumer() {
        SetupServices();
        WebServer server = CreateServer();

        try {
            var factory = AppServices.Provider.GetService<IDbContextFactory<AppDbContext>>();
            await using AppDbContext db = await factory!.CreateDbContextAsync(TestContext.Current.CancellationToken);

            var subathon = new SubathonData { IsActive = true };
            db.SubathonGoalSets.Add(new SubathonGoalSet { Type = null });
            db.SubathonDatas.Add(subathon);

            db.SubathonEvents.Add(new SubathonEvent {
                SubathonId = subathon.Id,
                EventType = SubathonEventType.KoFiDonation,
                Currency = "USD",
                Value = "5",
                ProcessedToSubathon = true
            });

            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            db.ChangeTracker.Clear();

            var ctx = new MockHttpContext {
                IsWebSocket = true
            };

            ctx.Socket.EnqueueReceive("{\"ws_type\":\"Widget\",\"origin\":\"unit-test\"}");
            ctx.Socket.EnqueueClose();

            await server.HandleWebSocketRequestAsync(ctx);

            await WaitForMessageAsync(ctx.Socket, TimeSpan.FromSeconds(5));

            Assert.NotEmpty(ctx.Socket.SentMessages);
        }
        finally {
            AppServices.Provider = null!;
            server.Stop();
        }
    }

    [Fact]
    public async Task WebSocket_ReceiveAndInitConfigConsumer() {
        SetupServices();
        WebServer server = CreateServer();

        try {
            var factory = AppServices.Provider.GetService<IDbContextFactory<AppDbContext>>();
            await using AppDbContext db = await factory!.CreateDbContextAsync(TestContext.Current.CancellationToken);

            var subathon = new SubathonData { IsActive = true };
            db.SubathonGoalSets.Add(new SubathonGoalSet { Type = null });
            db.SubathonDatas.Add(subathon);

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

            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            db.ChangeTracker.Clear();

            var ctx = new MockHttpContext {
                IsWebSocket = true
            };

            ctx.Socket.EnqueueReceive("{\"ws_type\":\"ValueConfig\",\"type\":\"value_config\"}");
            ctx.Socket.EnqueueClose();

            await server.HandleWebSocketRequestAsync(ctx);

            await WaitForMessageAsync(ctx.Socket, TimeSpan.FromSeconds(5));

            Assert.NotEmpty(ctx.Socket.SentMessages);
        }
        finally {
            AppServices.Provider = null!;
            server.Stop();
        }
    }

    [Fact]
    public async Task WebSocket_CommandListRequest_ReturnsCatalog() {
        SetupServices();
        WebServer server = CreateServer();

        try {
            var ctx = new MockHttpContext {
                IsWebSocket = true
            };

            ctx.Socket.EnqueueReceive("{\"ws_type\":\"Command\",\"request\":\"commands\"}");
            ctx.Socket.EnqueueClose();

            await server.HandleWebSocketRequestAsync(ctx);

            await WaitForMessageMatchingAsync(ctx.Socket, m => m.Contains("command_list"), TimeSpan.FromSeconds(5));

            string msg = ctx.Socket.SentMessages
                .Select(b => Encoding.UTF8.GetString(b))
                .First(m => m.Contains("command_list"));

            Assert.Contains($"\"{nameof(SubathonCommandType.AddTime)}\"", msg);
            Assert.Contains($"\"{nameof(SubathonCommandType.Pause)}\"", msg);
            Assert.Contains("requires_parameter", msg);
            Assert.Contains("is_control", msg);
            Assert.DoesNotContain($"\"{nameof(SubathonCommandType.Unknown)}\"", msg);
            Assert.DoesNotContain($"\"{nameof(SubathonCommandType.None)}\"", msg);
        }
        finally {
            AppServices.Provider = null!;
            server.Stop();
        }
    }

    [Fact]
    public async Task WebSocket_Command_SendsAckWithContext() {
        SetupServices();
        WebServer server = CreateServer();

        try {
            var ctx = new MockHttpContext {
                IsWebSocket = true
            };

            ctx.Socket.EnqueueReceive(
                "{\"ws_type\":\"Command\",\"type\":\"Command\",\"command\":\"pause\",\"message\":\"\",\"user\":\"StreamDeck\",\"context\":\"key-context-1\"}");
            ctx.Socket.EnqueueClose();

            await server.HandleWebSocketRequestAsync(ctx);

            await WaitForMessageMatchingAsync(ctx.Socket, m => m.Contains("command_ack"), TimeSpan.FromSeconds(5));

            string msg = ctx.Socket.SentMessages
                .Select(b => Encoding.UTF8.GetString(b))
                .First(m => m.Contains("command_ack"));

            Assert.Contains("\"success\":true", msg);
            Assert.Contains("key-context-1", msg);
            Assert.Contains("\"pause\"", msg);
        }
        finally {
            AppServices.Provider = null!;
            server.Stop();
        }
    }
}