using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Models;
using SubathonManager.Core.Events;
using SubathonManager.Integration;

namespace SubathonManager.Server;

public partial class WebServer
{
    private readonly List<IWebSocketClient> _clients = new();
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

    internal void SendSubathonValues(string jsonData)
    {
        var newData = $"{{ \"type\": \"value_config\", \"ws_type\": \"{WebsocketClientMessageType.ValueConfig}\", \"data\": {jsonData} }}";
        Task.Run(() => BroadcastAsync(newData, WebsocketClientTypeHelper.ConfigConsumersList));
    }

    internal void SendGoalsUpdated(List<SubathonGoal> goals, long currentPoints, GoalsType type)
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
        Task.Run(() => BroadcastAsyncObject(data, WebsocketClientTypeHelper.ConsumersList));
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

    internal void SendGoalCompleted(SubathonGoal goal, long currentPoints)
    {
        object data = new
        {
            type = "goal_completed",
            goal_text = goal.Text,
            goal_points =  goal.Points,
            points = currentPoints
        };
        Task.Run(() => BroadcastAsyncObject(data, WebsocketClientTypeHelper.ConsumersList));
    }

    private async Task InitConnection(IWebSocketClient socket)
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

    internal void SendSubathonEventProcessed(SubathonEvent subathonEvent, bool effective)
    {
        if (!subathonEvent.ProcessedToSubathon) return;
        Task.Run(() => BroadcastAsyncObject(SubathonEventToObject(subathonEvent), WebsocketClientTypeHelper.ConsumersList));
    }

    internal void SendRefreshRequest(Guid id)
    {
        Task.Run(() =>
            BroadcastAsyncObject(new
            {
                type = "refresh_request",
                id = id.ToString()
            }, WebsocketClientMessageType.Overlay)
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

    internal void SendSubathonDataUpdate(SubathonData subathon, DateTime time)
    {
        Task.Run(() => BroadcastAsyncObject(SubathonDataToObject(subathon), WebsocketClientTypeHelper.ConsumersList));
    }

    internal void AddSocketClient(IWebSocketClient socket)
    {
        lock (_lock)
        {
            _clients.Add(socket);
            _logger?.LogDebug($"{_clients.Count} websocket clients connected");
        }
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
        IWebSocketClient client = new WebSocketClient(socket);
        AddSocketClient(client);
        
        _logger?.LogDebug($"New WebSocket Client Connected [{client.ClientId}].");
        
        try
        {
            await Listen(client);
        }
        finally
        {
            foreach (var clientIntegrationSource in client.IntegrationSources)
            {
                WebServerEvents.RaiseWebSocketIntegrationSourceChange(clientIntegrationSource.ToString(), false);
                _logger?.LogDebug($"WebSocket Client disconnected for Integration: {clientIntegrationSource}");
            }

            lock (_lock)
            {
                _clients.Remove(client);
                _logger?.LogDebug($"{_clients.Count} websocket clients connected");
            }

            _logger?.LogDebug($"WebSocket Client Disconnected [{client.ClientId}]");
        }
    }
    
    private async Task Listen(IWebSocketClient socket)
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
            WebsocketClientMessageType clientMessageType = WebsocketClientMessageType.None;
            try
            {
                var json = JsonDocument.Parse(msg);
                if (json.RootElement.TryGetProperty("ws_type", out var type))
                {
                    switch (type.GetString())
                    {
                        case "ping":
                            var pong = Encoding.UTF8.GetBytes("{\"ws_type\":\"pong\"}");
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
                            _logger?.LogDebug($"[WebSocket] [{socket.ClientId}] Hello from {json.RootElement.GetProperty("origin").GetString()}");
                            break;
                    }

                    if (Enum.TryParse(type.GetString(), out clientMessageType))
                    {
                        if (socket.ClientTypes.Count >= 1 && !socket.ClientTypes.Contains(clientMessageType))
                        {
                            _logger?.LogDebug($"WebSocket ClientType [{clientMessageType}] identified for client [{socket.ClientId}]");
                            socket.ClientTypes.Add(clientMessageType);
                            if (clientMessageType.IsConsumer() && socket.ClientTypes.Contains(WebsocketClientMessageType.Generic))
                            {
                                socket.ClientTypes.Remove(WebsocketClientMessageType.Generic);
                                await InitConnection(socket);
                            }
                            else if (clientMessageType == WebsocketClientMessageType.ValueConfig &&
                                     socket.ClientTypes.Contains(WebsocketClientMessageType.Generic))
                            {
                                socket.ClientTypes.Remove(WebsocketClientMessageType.Generic);
                                string configValues = await _valueHelper.GetAllAsJsonAsync();
                                var newData = $"{{ \"type\": \"value_config\", \"ws_type\": \"{WebsocketClientMessageType.ValueConfig}\", \"data\": {configValues} }}";
                                await SelectSendStringAsync(socket, newData);
                            }
                        }
                    }
                }
                
                // received type *may* not equal socket type, we want to be able to reuse sockets like this
                // set type of socket is just primary type for events
                if (clientMessageType.Equals(WebsocketClientMessageType.IntegrationSource))
                {
                    if (json.RootElement.TryGetProperty("source", out JsonElement src) &&
                        Enum.TryParse(src.GetString()!, ignoreCase: true, out SubathonEventSource source)
                        && !socket.IntegrationSources.Contains(source))
                    {
                        _logger?.LogDebug($"WebSocket Client [{socket.ClientId}] added Integration: {source}");
                        socket.IntegrationSources.Add(source);
                        WebServerEvents.RaiseWebSocketIntegrationSourceChange(source.ToString(), true);
                    }
                    
                    if (!json.RootElement.TryGetProperty("type", out JsonElement elem)
                        || !Enum.TryParse(elem.GetString()!, ignoreCase: true, out SubathonEventType seType)
                        || seType == SubathonEventType.Unknown)
                        continue;
                    
                    Dictionary<string, JsonElement> data =
                        json.RootElement
                            .EnumerateObject()
                            .ToDictionary(p => p.Name, p => p.Value);

                    if (seType == SubathonEventType.Command)
                    {
                        ExternalEventService.ProcessExternalCommand(data);
                    }
                    else if (((SubathonEventType?)seType).IsCurrencyDonation() && ((SubathonEventType?)seType).IsExternalType())
                    {
                        if (!socket.IntegrationSources.Contains(((SubathonEventType?)seType).GetSource()))
                            socket.IntegrationSources.Add(((SubathonEventType?)seType).GetSource());
                        ExternalEventService.ProcessExternalDonation(data);
                    }
                    else if (((SubathonEventType?)seType).IsSubOrMembershipType() && ((SubathonEventType?)seType).IsExternalType())
                    {
                        if (!socket.IntegrationSources.Contains(((SubathonEventType?)seType).GetSource()))
                            socket.IntegrationSources.Add(((SubathonEventType?)seType).GetSource());
                        ExternalEventService.ProcessExternalSub(data);
                    }
                }
                else if (clientMessageType.Equals(WebsocketClientMessageType.ValueConfig))
                {
                    if (json.RootElement.TryGetProperty("data", out var data))
                    {
                        int patched = await _valueHelper.PatchFromJsonDataAsync(data);

                        var resMsg = "";
                        if (patched == -1) resMsg = "Error Patching";
                        else if (patched == 0) resMsg = "No patches needed";
                        else resMsg = $"Patched {patched} Values";
                        object resp = new
                        {
                            ws_type = WebsocketClientMessageType.ValueConfig,
                            response = resMsg
                        };
                        await SelectSendAsync(socket, resp);
                    }
                    else
                    {
                        string configValues = await _valueHelper.GetAllAsJsonAsync();
                        var newData = $"{{ \"type\": \"value_config\", \"ws_type\": \"{WebsocketClientMessageType.ValueConfig}\", \"data\": {configValues} }}";
                        await SelectSendStringAsync(socket, newData);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, ex.Message);
            }
        }
    }

    private async Task BroadcastAsyncObject(object data, params WebsocketClientMessageType[] types)
    {
        string json = JsonSerializer.Serialize(data);
        await BroadcastAsync(json, types);
    }
    
    private async Task BroadcastAsync(string json, params WebsocketClientMessageType[] types)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json);

        List<IWebSocketClient> clientsCopy;
        lock (_lock)
            clientsCopy = _clients.ToList();

        foreach (var ws in clientsCopy)
        {
            if (ws.State == WebSocketState.Open && ws.ClientTypes.Any(types.Contains))
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

    internal async Task SelectSendAsync(IWebSocketClient client, object data)
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
    
    internal async Task SelectSendStringAsync(IWebSocketClient client, string data)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(data);
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
                                    var _type = 'Widget';
                                    if ('{routeId}' != '') _type = 'Overlay';
                                    socket.send(JSON.stringify({{ ws_type: _type, origin: window.location.href }}));
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