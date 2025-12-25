using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Models;
using SubathonManager.Core.Events;

namespace SubathonManager.Server;

public partial class WebServer
{
    private readonly List<WebSocket> _clients = new();
    private readonly object _lock = new();
    private SemaphoreSlim _sendLock = new SemaphoreSlim(1,1);

    private void SetupWebsocketListeners()
    {
        SubathonEvents.SubathonDataUpdate += SendSubathonDataUpdate;
        SubathonEvents.SubathonEventProcessed += SendSubathonEventProcessed;
        SubathonEvents.SubathonGoalCompleted += SendGoalCompleted;
        SubathonEvents.SubathonGoalListUpdated += SendGoalsUpdated;
        OverlayEvents.OverlayRefreshRequested += SendRefreshRequest;
        SubathonEvents.SubathonValueConfigRequested += SendSubathonValues;
    }

    private void StopWebsocketServer()
    {
        SubathonEvents.SubathonDataUpdate -= SendSubathonDataUpdate;
        SubathonEvents.SubathonEventProcessed -= SendSubathonEventProcessed;
        SubathonEvents.SubathonGoalCompleted -= SendGoalCompleted;
        OverlayEvents.OverlayRefreshRequested -= SendRefreshRequest;
        SubathonEvents.SubathonGoalListUpdated -= SendGoalsUpdated;
        SubathonEvents.SubathonValueConfigRequested -= SendSubathonValues;
    }

    private void SendSubathonValues(string jsonData)
    {
        var newData = $"{{ \"type\": \"value_config\", \"data\": {jsonData} }}";
        Task.Run(() => BroadcastAsync(newData));
    }

    private void SendGoalsUpdated(List<SubathonGoal> goals, long currentPoints, GoalsType type)
    {
        List<object> objGoals = new List<object>();
        foreach (var goal in goals)
        {
            objGoals.Add(GoalToObject(goal, currentPoints));
        }
        object data = new
        {
            type = "goals_list",
            points = currentPoints,
            goals = objGoals.ToArray(),
            goals_type = $"{type}"
        };
        Task.Run(() => BroadcastAsyncObject(data));
    }

    private object GoalToObject(SubathonGoal goal, long currentPoints)
    {
        return new
        {
            text = goal.Text,
            points = goal.Points,
            completed = goal.Points <= currentPoints
        };
    }

    private void SendGoalCompleted(SubathonGoal goal, long currentPoints)
    {
        object data = new
        {
            type = "goal_completed",
            goal_text = goal.Text,
            goal_points =  goal.Points,
            points = currentPoints
        };
        Task.Run(() => BroadcastAsyncObject(data));
    }

    private async Task InitConnection(WebSocket socket)
    {
        await using var db = await _factory.CreateDbContextAsync();
        SubathonData? subathon = await db.SubathonDatas.Include(s => s.Multiplier)
            .FirstOrDefaultAsync(s => s.IsActive);
        if (subathon is null) return;

        string configValues = await _valueHelper.GetAllAsJsonAsync();
        SendSubathonValues(configValues);
        
        await SelectSendAsync(socket, SubathonDataToObject(subathon));
        
        SubathonGoalSet? goalSet = await db.SubathonGoalSets.AsNoTracking().
            Include(g=> g.Goals).FirstOrDefaultAsync(g => g.IsActive);
        if (goalSet != null)
        {
            long val = subathon.Points;
            if (goalSet.Type == GoalsType.Money)
                val = (long) Math.Floor(subathon.MoneySum ?? 0);
            List<object> objGoals = new List<object>();
            foreach (var goal in goalSet!.Goals)
            {
                objGoals.Add(GoalToObject(goal, val));
            }
        
            object data = new
            {
                type = "goals_list",
                points = val,
                goals = objGoals.ToArray(),
                goals_type = $"{goalSet.Type}"
            };
            await SelectSendAsync(socket, data);
        }
        
    }

    private object SubathonEventToObject(SubathonEvent subathonEvent)
    {
        object data = new
        {
            type = "event",
            event_type =  subathonEvent.EventType.ToString(),
            source =  subathonEvent.Source.ToString(),
            seconds_added = subathonEvent.GetFinalSecondsValueRaw() < 0.5 ? 0 : subathonEvent.GetFinalSecondsValue(),
            points_added = subathonEvent.GetFinalPointsValue(),
            user =  subathonEvent.User,
            value = subathonEvent.Value, // sometimes useful
            amount = subathonEvent.Amount, // sometimes useful
            currency = subathonEvent.Currency, // sometimes useful
            command =  subathonEvent.Command.ToString(), // only useful if eventType is command
            event_timestamp = subathonEvent.EventTimestamp,
            reversed = subathonEvent.WasReversed
        };
        return data;
    }

    private void SendSubathonEventProcessed(SubathonEvent subathonEvent, bool effective)
    {
        if (!subathonEvent.ProcessedToSubathon) return;
        Task.Run(() => BroadcastAsyncObject(SubathonEventToObject(subathonEvent)));
    }

    private void SendRefreshRequest(Guid id)
    {
        Task.Run(() =>
            BroadcastAsyncObject(new
            {
                type = "refresh_request",
                id = id.ToString()
            })
        );
    }

    private object SubathonDataToObject(SubathonData subathon)
    {
        TimeSpan? multiplierRemaining = TimeSpan.Zero;
        if (subathon.Multiplier.Duration != null && subathon.Multiplier.Duration > TimeSpan.Zero
            && subathon.Multiplier.Started != null)
        {
            DateTime? multEndTime = subathon.Multiplier.Started + subathon.Multiplier.Duration;
            multiplierRemaining = multEndTime! - DateTime.Now;
        }

        long roundedMoney = subathon.GetRoundedMoneySum();
        double fractionalMoney = subathon.GetRoundedMoneySumWithCents();
        
        object data = new
        {
            type = "subathon_timer",
            total_seconds = subathon.TimeRemainingRounded().TotalSeconds,
            days = subathon.TimeRemainingRounded().Days,
            hours = subathon.TimeRemainingRounded().Hours,
            minutes = subathon.TimeRemainingRounded().Minutes,
            seconds = subathon.TimeRemainingRounded().Seconds,
            total_points = subathon.Points,
            rounded_money = roundedMoney,
            fractional_money = fractionalMoney,
            currency = subathon.Currency,
            is_paused = subathon.IsPaused,
            is_locked =  subathon.IsLocked,
            is_reversed = subathon.IsSubathonReversed(),
            multiplier_points = subathon.Multiplier.ApplyToPoints ? subathon.Multiplier.Multiplier : 1,
            multiplier_time = subathon.Multiplier.ApplyToSeconds ? subathon.Multiplier.Multiplier : 1,
            multiplier_start_time = subathon.Multiplier.Started,
            multiplier_seconds_total = Math.Round(subathon.Multiplier.Duration?.TotalSeconds ?? 0),
            multiplier_seconds_remaining = Math.Round(multiplierRemaining.Value.TotalSeconds), 
            total_seconds_elapsed = (int) (subathon.MillisecondsElapsed / 1000),
            total_seconds_added = (int) (subathon.MillisecondsCumulative / 1000)
        };
        return data;
    }

    private void SendSubathonDataUpdate(SubathonData subathon, DateTime time)
    {
        Task.Run(() => BroadcastAsyncObject(SubathonDataToObject(subathon)));
    }
    
    public async Task HandleWebSocketRequestAsync(IHttpContext ctx)
    {
        if (!ctx.IsWebSocket)
        {
            await ctx.WriteResponse(400, "Invalid Websocket Request");
            return;
        }

        var accept = ctx.AcceptWebSocketAsync();

        if (accept is null)
        {
            await ctx.WriteResponse(400, "Not a WebSocket request");
            return;
        }

        using WebSocket socket = await accept;

        lock (_lock)
            _clients.Add(socket);
        
        _logger?.LogDebug("New WebSocket Client Connected");
        await InitConnection(socket);
        
        try
        {
            await Listen(socket);
        }
        finally
        {
            lock (_lock)
                _clients.Remove(socket);

            _logger?.LogDebug("WebSocket Client Disconnected");
        }
    }
    
    private async Task Listen(WebSocket socket)
    {
        var buffer = new byte[1024 * 8];
        while (socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client closed", CancellationToken.None);
                break;
            }

            string msg = Encoding.UTF8.GetString(buffer, 0, result.Count);
            try
            {
                var json = JsonDocument.Parse(msg);
                if (json.RootElement.TryGetProperty("type", out var type))
                {
                    switch (type.GetString())
                    {
                        case "ping":
                            var pong = Encoding.UTF8.GetBytes("{\"type\":\"pong\"}");
                            await _sendLock.WaitAsync();
                            try
                            {
                                await socket.SendAsync(pong, WebSocketMessageType.Text, true, CancellationToken.None);
                            }
                            finally
                            {
                                _sendLock.Release();
                            }
                            break;
                        case "hello":
                            _logger?.LogDebug($"[WebSocket] Hello from {json.RootElement.GetProperty("origin").GetString()}");
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, ex.Message);
            }
        }
    }

    private async Task BroadcastAsyncObject(object data)
    {
        string json = JsonSerializer.Serialize(data);
        await BroadcastAsync(json);
    }
    
    private async Task BroadcastAsync(string json)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json);

        List<WebSocket> clientsCopy;
        lock (_lock)
            clientsCopy = _clients.ToList();

        foreach (var ws in clientsCopy)
        {
            if (ws.State == WebSocketState.Open)
            {
                await _sendLock.WaitAsync();
                try
                {
                    await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
                }
                finally
                {
                    _sendLock.Release();
                }
            }
        }
    }

    public async Task SelectSendAsync(WebSocket client, object data)
    {
        string json = JsonSerializer.Serialize(data);
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        if (client.State == WebSocketState.Open)
        {
            await _sendLock.WaitAsync();
            try
            {
                await client.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
            }
            finally
            {
                _sendLock.Release();
            }
        }
    }

    public string GetWebsocketInjectionScript(string? routeId = "")
    {
        string script = $@"
                        <script>
                        (function() {{
                            const WS_URL = 'ws://localhost:{Port}/ws';
                            let socket, reconnectTimer, pingTimer;
                        
                            function connect() {{
                                if (socket && socket.readyState === WebSocket.OPEN) return;
                                console.log('[Subathon WS] Connecting...');
                                socket = new WebSocket(WS_URL);
                        
                                socket.onopen = () => {{
                                    console.log('[Subathon WS] Connected');
                                    if (reconnectTimer) clearTimeout(reconnectTimer);
                                    startPing();
                                    socket.send(JSON.stringify({{ type: 'hello', origin: window.location.href }}));
                                }};
                        
                                socket.onmessage = (event) => {{
                                    try {{
                                        const data = JSON.parse(event.data);
                                        if (typeof window.handleSubathonUpdate === 'function' && data.type == 'subathon_timer')
                                            window.handleSubathonUpdate(data);
                                        else if (typeof window.handleSubathonEvent === 'function' && data.type == 'event')
                                            window.handleSubathonEvent(data);
                                        else if (typeof window.handleGoalsUpdate === 'function' && data.type == 'goals_list')
                                            window.handleGoalsUpdate(data);
                                        else if (typeof window.handleGoalCompleted === 'function' && data.type == 'goal_completed')
                                            window.handleGoalCompleted(data);
                                        else if (typeof window.handleValueConfig === 'function' && data.type == 'value_config')
                                            window.handleValueConfig(data);
                                        else if (data.type == 'refresh_request' && document.title.startsWith('overlay') && (document.title.includes(data.id) || data.id == '{Guid.Empty}')) {{
                                            // for only the merged page
                                            window.location.reload();
                                        }}
                                        //else console.log('[Subathon WS] Received:', data);
                                    }} catch (e) {{
                                        console.error('[Subathon WS] JSON error:', e);
                                    }}
                                }};
                        
                                socket.onclose = () => {{
                                    console.warn('[Subathon WS] Closed. Reconnecting...');
                                    stopPing();
                                    if (typeof window.handleSubathonDisconnect === 'function') {{
                                        window.handleSubathonDisconnect();
                                    }}
                                    reconnectTimer = setTimeout(connect, 5000);
                                }};
                        
                                socket.onerror = (e) => {{
                                    console.error('[Subathon WS] Error:', e);
                                    socket.close();
                                }};
                            }}
                        
                            function startPing() {{
                                stopPing();
                                pingTimer = setInterval(() => {{
                                    if (socket && socket.readyState === WebSocket.OPEN)
                                        socket.send(JSON.stringify({{ type: 'ping', t: Date.now() }}));
                                }}, 15000);
                            }}
                        
                            function stopPing() {{
                                if (pingTimer) clearInterval(pingTimer);
                                pingTimer = null;
                            }}
                        
                            document.addEventListener('visibilitychange', () => {{
                                if (!document.hidden && (!socket || socket.readyState > 1)) connect();
                            }});
                        
                            connect();
                        }})();
                        </script>                        
                        ";
        return script;
    }
}