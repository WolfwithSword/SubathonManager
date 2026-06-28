using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Models;
using SubathonManager.Core.Events;
using SubathonManager.Core.Objects;
using SubathonManager.Data;
using SubathonManager.Integration;
using SubathonManager.Services;
// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.Server;

public partial class WebServer
{
    private readonly List<IWebSocketClient> _clients = new();
    private readonly object _lock = new();
    private readonly SemaphoreSlim _sendLock = new(1,1);

    private void SetupWebsocketListeners()
    {
        SubathonEvents.SubathonDataUpdate += SendSubathonDataUpdate;
        SubathonEvents.SubathonEventProcessed += SendSubathonEventProcessed;
        SubathonEvents.SubathonGoalCompleted += SendGoalCompleted;
        SubathonEvents.SubathonGoalListUpdated += SendGoalsUpdated;
        OverlayEvents.OverlayRefreshRequested += SendRefreshRequest;
        SubathonEvents.SubathonValueConfigRequested += SendSubathonValues;
        SubathonEvents.SubathonTotalsUpdated += SendSubathonTotals;

        SubathonEvents.PromptRunStarted += OnPromptStart;
        SubathonEvents.PromptRunUpdate += OnPromptRunUpdate;
        SubathonEvents.PromptRunProgressUpdated += OnPromptProgress;
        
        OverlayEvents.WidgetVarsUpdated += SendWidgetVarsUpdate;
        OverlayEvents.WidgetRefreshRequested += SendWidgetReload;

        WheelEvents.WheelSpinStarted += SendWheelSpinStarted;
        WheelEvents.WheelSpinResult += SendWheelSpinResult;
        WheelEvents.WheelSpinStatusChanged += SendWheelSpinStatusChanged;
        WheelEvents.WheelDataChanged += SendWheelDataChanged;
    }

    private void StopWebsocketServer()
    {
        SubathonEvents.SubathonDataUpdate -= SendSubathonDataUpdate;
        SubathonEvents.SubathonEventProcessed -= SendSubathonEventProcessed;
        SubathonEvents.SubathonGoalCompleted -= SendGoalCompleted;
        OverlayEvents.OverlayRefreshRequested -= SendRefreshRequest;
        SubathonEvents.SubathonGoalListUpdated -= SendGoalsUpdated;
        SubathonEvents.SubathonValueConfigRequested -= SendSubathonValues;
        SubathonEvents.SubathonTotalsUpdated -= SendSubathonTotals;
        
        SubathonEvents.PromptRunStarted -= OnPromptStart;
        SubathonEvents.PromptRunUpdate -= OnPromptRunUpdate;
        SubathonEvents.PromptRunProgressUpdated -= OnPromptProgress;
        
        OverlayEvents.WidgetVarsUpdated -= SendWidgetVarsUpdate;
        OverlayEvents.WidgetRefreshRequested -= SendWidgetReload;

        WheelEvents.WheelSpinStarted -= SendWheelSpinStarted;
        WheelEvents.WheelSpinResult -= SendWheelSpinResult;
        WheelEvents.WheelSpinStatusChanged -= SendWheelSpinStatusChanged;
        WheelEvents.WheelDataChanged -= SendWheelDataChanged;
    }

    private void OnPromptStart(SubathonPromptRun subathonPromptRun, SubathonPrompt? subathonPrompt)
    {
        SendPromptData(subathonPromptRun, 0);
    }

    private void OnPromptRunUpdate(SubathonPromptRun subathonPromptRun, SubathonPrompt? subathonPrompt)
    {
        Task.Run(async () =>
        {
            await using var db = await _factory.CreateDbContextAsync();
            long current = await PromptOrchestratorService.GetCurrentCountAsync(db, subathonPromptRun.LinkedPrompt!);
            long progress = current - subathonPromptRun.BaselineCount;
            SendPromptData(subathonPromptRun, progress);
        });
    }

    private void OnPromptProgress(SubathonPromptRun subathonPromptRun, long progress)
    {
        SendPromptData(subathonPromptRun, progress);
    }
    
    internal void SendWidgetReload(Guid widgetId, float x, float y, int width, int height, float scaleX, float scaleY)
    {
        var data = new { 
            type = "widget_reload", 
            widgetId = widgetId.ToString(),
            x, y, width, height, scaleX, scaleY
        };
        Task.Run(() => BroadcastAsyncObject(data, WebsocketClientMessageType.Overlay));
    }

    internal void SendPromptData(SubathonPromptRun? run, long progress = 0)
    {
        object data = new
        {
            type = "prompt_update",
            status = run == null ? "None" : run.Status.ToString(),
            progress = progress,
            target = run?.LinkedPrompt?.Value ?? 0,
            seconds_remaining = run?.TimeRemaining().TotalSeconds ?? 0,
            start_time = run?.StartedAt,
            end_time = run?.ExpiresAt,
            duration_seconds = run?.LinkedPrompt?.CompletionDuration.TotalSeconds ?? 0,
            text = run?.LinkedPrompt?.Text,
            prompt_type = $"{run?.LinkedPrompt?.Type}",
            prompt_subtype = $"{run?.LinkedPrompt?.SubType}",
            prompt_eventtype = $"{run?.LinkedPrompt?.FilterEventType}",
            prompt_eventtype_metafilter =  run?.LinkedPrompt?.FilterMeta
        };
        Task.Run(() => BroadcastAsyncObject(data, WebsocketClientTypeHelper.ConsumersList));
    }
    
    internal void SendWidgetVarsUpdate(Guid widgetId, 
        IEnumerable<CssVariable> cssVars, IEnumerable<JsVariable> jsVars)
    {
        var data = new
        {
            type = "widget_vars_update",
            widgetId = widgetId.ToString(),
            cssVars = cssVars.Select(v => new { name = v.Name, value = v.Value }),
            jsVars  = jsVars.Select(v => new { name = v.Name, value = v.Value, 
                injectLine = v.GetInjectLine() })
        };
        Task.Run(() => BroadcastAsyncObject(data, WebsocketClientTypeHelper.ConsumersList));
    }

    internal void SendSubathonValues(string jsonData)
    {
        var newData = $"{{ \"type\": \"value_config\", \"ws_type\": \"{WebsocketClientMessageType.ValueConfig}\", \"data\": {jsonData} }}";
        Task.Run(() => BroadcastAsync(newData, WebsocketClientTypeHelper.ConfigConsumersList));
    }

    internal void SendGoalsUpdated(List<SubathonGoal> goals, long currentPoints, GoalsType type)
    {
        object data = new
        {
            type = "goals_list",
            points = currentPoints,
            goals = goals.Select(goal => GoalToObject(goal, currentPoints)).ToArray(),
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

            object data = new
            {
                type = "goals_list",
                points = val,
                goals = goalSet.Goals.Select(goal => GoalToObject(goal, val)).ToArray(),
                goals_type = $"{goalSet.Type}"
            };
            await SelectSendAsync(socket, data);
        }

        SubathonPromptRun? promptRun = await db.SubathonPromptRuns.AsNoTracking()
            .Include(p => p.LinkedPrompt)
            .FirstOrDefaultAsync(p => p.Status == SubathonPromptRunStatus.Active && p.ExpiresAt > DateTime.Now);
        if (promptRun is { LinkedPrompt: not null })
        {
            long current = await PromptOrchestratorService.GetCurrentCountAsync(db, promptRun.LinkedPrompt);
            long progress = current - promptRun.BaselineCount;
            SendPromptData(promptRun, progress);
        }
        else
            SendPromptData(null, 0);
        
        var totals = await EventService.GetSubathonTotalsAsync(db);

        if (totals != null)
            await SelectSendAsync(socket, SubathonTotalsToObject(totals));

        var activeWheel = await db.WheelSets
            .Include(w => w.WheelItems)
            .ThenInclude(i => i.Action)
            .AsNoTracking()
            .FirstOrDefaultAsync(w => w.IsActive);

        if (activeWheel != null)
        {
            int spinsOwed = StateValueHelper.Get<int>(db, StateKeys.WheelSpinsOwed);
            var items = activeWheel.WheelItems.Select(WheelItemToObject).ToList();
            var wheelData = new
            {
                type = "wheel_data",
                wheel = new { id = activeWheel.Id, name = activeWheel.Name, spin_count = activeWheel.SpinCount, items },
                spins_owed = spinsOwed
            };
            await SelectSendAsync(socket, wheelData);
        }
    }

    private object SubathonTotalsToObject(SubathonTotals totals)
    {
        return new
        {
            type = "subathon_totals",
            currency = totals.Currency,
            money_sum = totals.MoneySum,
            sub_like_total = totals.SubLikeTotal,
            sub_like_by_type = totals.SubLikeByEvent
                .ToDictionary(k => k.Key.ToString(), k => k.Value),
            token_like_total = totals.TokenLikeTotal,
            token_like_by_type = totals.TokenLikeByEvent
                .ToDictionary(k => k.Key.ToString(), k => k.Value),
            order_count_by_type = totals.OrderCountByType
                .ToDictionary(k => k.Key.ToString(), k => k.Value),
            order_items_count_by_type = totals.OrderItemsCountByType
                .ToDictionary(k => k.Key.ToString(), k => k.Value),
            follow_count = totals.FollowLikeTotal,
            follow_count_by_type = totals.FollowLikeByEvent
                .ToDictionary(k => k.Key.ToString(), k => k.Value),
            simulated = new {
                sub_like_total = totals.Simulated.SubLikeTotal,
                sub_like_by_type = totals.Simulated.SubLikeByEvent
                    .ToDictionary(k => k.Key.ToString(), k => k.Value),
                token_like_total = totals.Simulated.TokenLikeTotal,
                token_like_by_type = totals.Simulated.TokenLikeByEvent
                    .ToDictionary(k => k.Key.ToString(), k => k.Value),
                order_count_by_type = totals.Simulated.OrderCountByType
                    .ToDictionary(k => k.Key.ToString(), k => k.Value),
                order_items_count_by_type = totals.Simulated.OrderItemsCountByType
                    .ToDictionary(k => k.Key.ToString(), k => k.Value),
                follow_count = totals.Simulated.FollowLikeTotal,
                follow_count_by_type = totals.Simulated.FollowLikeByEvent
                    .ToDictionary(k => k.Key.ToString(), k => k.Value),
            }
        };
    }

    private object SubathonEventToObject(SubathonEvent subathonEvent)
    {
        var trueSource = subathonEvent.EventType.GetTypeTrueSource();
        var eventType = subathonEvent.EventType.ToString();
        // if (subathonEvent.EventType == SubathonEventType.GoAffProOrder)
        // {
        //     GoAffProStoreRegistry.TryGetBySiteId(int.Parse(subathonEvent.EventTypeMeta!), out var store);
        //     if (store != null)
        //     {
        //         trueSource = store.InternalName;
        //         eventType = store.InternalEventName;
        //     }
        // }
        object data = new
        {
            type = "event",
            event_type = eventType,
            source =  subathonEvent.Source.ToString(),
            seconds_added = subathonEvent.GetFinalSecondsValueRaw() < 0.5 ? 0 : subathonEvent.GetFinalSecondsValue(),
            points_added = subathonEvent.GetFinalPointsValue(),
            user =  subathonEvent.User,
            value = subathonEvent.Value, // sometimes useful
            amount = subathonEvent.Amount, // sometimes useful
            currency = subathonEvent.Currency, // sometimes useful
            command =  subathonEvent.Command.ToString(), // only useful if eventType is command
            event_timestamp = subathonEvent.EventTimestamp,
            reversed = subathonEvent.WasReversed,
            sub_type = subathonEvent.EventType.GetSubType().ToString(),
            secondary_value = subathonEvent.SecondaryValue,
            tertiary_value = subathonEvent.TertiaryValue,
            type_true_source = trueSource
        };
        return data;
    }

    internal void SendSubathonEventProcessed(SubathonEvent subathonEvent, bool effective)
    {
        bool showOverride = _config.GetBool("App", "ShowLockedEvents", false);
        if (!showOverride && !subathonEvent.ProcessedToSubathon) return;
        Task.Run(() => BroadcastAsyncObject(SubathonEventToObject(subathonEvent), WebsocketClientTypeHelper.ConsumersList));
    }

    internal void SendSubathonTotals(SubathonTotals totals)
    {
        Task.Run(() => BroadcastAsyncObject(SubathonTotalsToObject(totals), WebsocketClientTypeHelper.ConsumersList));
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
            multiplierRemaining = multEndTime - DateTime.Now;
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
            _logger?.LogDebug("{ClientsCount} websocket clients connected", _clients.Count);
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
        
        _logger?.LogDebug("New WebSocket Client Connected [{ClientClientId}].", client.ClientId);
        
        try
        {
            await Listen(client);
        }
        finally
        {
            foreach (var clientIntegrationSource in client.IntegrationSources)
            {
                WebServerEvents.RaiseWebSocketIntegrationSourceChange(clientIntegrationSource.ToString(), false);
                _logger?.LogDebug("WebSocket Client disconnected for Integration: {ClientIntegrationSource}", clientIntegrationSource);
            }

            lock (_lock)
            {
                _clients.Remove(client);
                _logger?.LogDebug("{ClientsCount} websocket clients connected", _clients.Count);
            }

            _logger?.LogDebug("WebSocket Client Disconnected [{ClientClientId}]", client.ClientId);
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

                switch (clientMessageType)
                {
                    // received type *may* not equal socket type, we want to be able to reuse sockets like this
                    // set type of socket is just primary type for events
                    case WebsocketClientMessageType.Command:
                    {
                        if (!json.RootElement.TryGetProperty("type", out JsonElement elem)
                            || !Enum.TryParse(elem.GetString()!, ignoreCase: true, out SubathonEventType seType)
                            || seType == SubathonEventType.Unknown)
                            continue;
                        if (seType != SubathonEventType.Command) continue;
                    
                        Dictionary<string, JsonElement> data =
                            json.RootElement
                                .EnumerateObject()
                                .ToDictionary(p => p.Name, p => p.Value);
                    
                        ExternalEventService.ProcessExternalCommand(data);
                        break;
                    }
                    case WebsocketClientMessageType.IntegrationSource:
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
                    
                        if (((SubathonEventType?)seType).IsCurrencyDonation() && ((SubathonEventType?)seType).IsExternal())
                        {
                            if (!socket.IntegrationSources.Contains(((SubathonEventType?)seType).GetSource()))
                                socket.IntegrationSources.Add(((SubathonEventType?)seType).GetSource());
                            ExternalEventService.ProcessExternalDonation(data);
                        }
                        else if (((SubathonEventType?)seType).IsSubscription() && ((SubathonEventType?)seType).IsExternal())
                        {
                            if (!socket.IntegrationSources.Contains(((SubathonEventType?)seType).GetSource()))
                                socket.IntegrationSources.Add(((SubathonEventType?)seType).GetSource());
                            ExternalEventService.ProcessExternalSub(data);
                        }

                        break;
                    }
                    case WebsocketClientMessageType.ValueConfig when json.RootElement.TryGetProperty("data", out var data):
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
                        break;
                    }
                    case WebsocketClientMessageType.ValueConfig:
                    {
                        string configValues = await _valueHelper.GetAllAsJsonAsync();
                        var newData = $"{{ \"type\": \"value_config\", \"ws_type\": \"{WebsocketClientMessageType.ValueConfig}\", \"data\": {configValues} }}";
                        await SelectSendStringAsync(socket, newData);
                        break;
                    }
                    case WebsocketClientMessageType.WheelControl:
                    {
                        if (!json.RootElement.TryGetProperty("id", out var idProp)
                            || !Guid.TryParse(idProp.GetString(), out var histId))
                            break;
                        if (!json.RootElement.TryGetProperty("status", out var statusProp)
                            || !Enum.TryParse(statusProp.GetString(), ignoreCase: true, out WheelSpinHistoryStatus newStatus))
                            break;

                        await using var db = await _factory.CreateDbContextAsync();
                        var history = await db.WheelSpinHistories
                            .Include(h => h.LinkedItem).ThenInclude(i => i!.Action)
                            .Include(h => h.LinkedWheel)
                            .FirstOrDefaultAsync(h => h.Id == histId);
                        if (history == null || history.Status == newStatus)
                            break;

                        history.Status = newStatus;
                        history.UpdatedAt = DateTime.Now;
                        await db.SaveChangesAsync();

                        int spinsOwed = StateValueHelper.Get<int>(db, StateKeys.WheelSpinsOwed);
                        WheelEvents.RaiseWheelSpinStatusChanged(history, spinsOwed);
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

    private static object? WheelItemToObject(WheelItem? item)
    {
        if (item == null) return null;
        return new
        {
            id = item.Id,
            text = item.Text,
            weight = item.Weight,
            quantity = item.Quantity,
            is_infinite = item.IsInfinite,
            enabled = item.Enabled,
            index = item.Index,
            action = item.Action == null ? null : (object?)new
            {
                type = item.Action.ActionType.ToString(),
                parameter = item.Action.Parameter
            }
        };
    }

    private void SendWheelSpinStarted(WheelSet wheel, int delaySeconds)
    {
        var data = new
        {
            type = "wheel_spin_start",
            wheel_id = wheel.Id,
            wheel_name = wheel.Name,
            spin_delay_seconds = delaySeconds,
            timestamp = DateTime.Now
        };
        Task.Run(() => BroadcastAsyncObject(data, WebsocketClientTypeHelper.ConsumersList));
    }

    private void SendWheelSpinResult(WheelSet wheel, WheelItem? item, WheelSpinHistory history, int _)
    {
        var itemSnapshot = WheelItemToObject(item);
        var data = new
        {
            type = "wheel_spin_result",
            wheel = new { id = wheel.Id, name = wheel.Name },
            item = itemSnapshot,
            history = new
            {
                id = history.Id,
                status = history.Status.ToString(),
                created_at = history.CreatedAt,
                updated_at = history.UpdatedAt
            },
            timestamp = DateTime.Now
        };
        Task.Run(() => BroadcastAsyncObject(data, WebsocketClientTypeHelper.ConsumersList));
    }

    private void SendWheelSpinStatusChanged(WheelSpinHistory history, int _)
    {
        var itemSnapshot = WheelItemToObject(history.LinkedItem);
        var data = new
        {
            type = "wheel_spin_status",
            history_id = history.Id,
            status = history.Status.ToString(),
            updated_at = history.UpdatedAt,
            wheel_item = itemSnapshot
        };
        Task.Run(() => BroadcastAsyncObject(data, WebsocketClientTypeHelper.ConsumersList));
    }

    private void SendWheelDataChanged(WheelSet wheel, int spinsOwed)
    {
        // snapshot
        var wheelId = wheel.Id;
        var wheelName = wheel.Name;
        var spinCount = wheel.SpinCount;
        var items = wheel.WheelItems.Select(WheelItemToObject).ToList();
        var data = new
        {
            type = "wheel_data",
            wheel = new { id = wheelId, name = wheelName, spin_count = spinCount, items },
            spins_owed = spinsOwed
        };
        Task.Run(() => BroadcastAsyncObject(data, WebsocketClientTypeHelper.ConsumersList));
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

        foreach (var ws in clientsCopy.Where(ws =>
                     ws.State == WebSocketState.Open && ws.ClientTypes.Any(types.Contains)))
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
                                        else if (typeof window.handlePromptUpdate === 'function' && data.type == 'prompt_update')
                                            window.handlePromptUpdate(data);
                                        else if (typeof window.handleGoalsUpdate === 'function' && data.type == 'goals_list')
                                            window.handleGoalsUpdate(data);
                                        else if (typeof window.handleGoalCompleted === 'function' && data.type == 'goal_completed')
                                            window.handleGoalCompleted(data);
                                        else if (typeof window.handleValueConfig === 'function' && data.type == 'value_config')
                                            window.handleValueConfig(data);
                                        else if (typeof window.handleTotalsUpdate === 'function' && data.type == 'subathon_totals')
                                            window.handleTotalsUpdate(data);
                                        else if (data.type == 'refresh_request' && document.title.startsWith('overlay') && (document.title.includes(data.id) || data.id == '{Guid.Empty}')) {{
                                            // for only the merged page
                                            window.location.reload();
                                        }}
                                        else if (data.type === 'widget_reload' && document.title.startsWith('overlay')) {{
                                            const iframe = document.querySelector(`iframe[data-widget-id=""${{data.widgetId}}""]`);
                                            if (iframe) {{
                                                const wrapper = iframe.parentElement;
                                                if (data.width != null) {{
                                                    iframe.dataset.origWidth  = data.width;
                                                    iframe.dataset.origHeight = data.height;
                                                    iframe.dataset.scalex = data.scaleX;
                                                    iframe.dataset.scaley = data.scaleY;
                                                    wrapper.style.left = data.x + 'px';
                                                    wrapper.style.top = data.y + 'px';
                                                }}
                                                iframe.onload = () => resizeIframe(iframe);
                                                iframe.src = iframe.src;
                                            }}
                                            
                                        }}
                                        else if (data.type === 'widget_vars_update' && !document.title.startsWith('overlay') ) {{
                                            const myId = window.location.pathname.split('/')[2];
                                            if (data.widgetId !== myId) return;
                                            if (data.cssVars) {{
                                                for (const v of data.cssVars) {{
                                                    document.documentElement.style.setProperty(`--${{v.name}}`, v.value, 'important');
                                                }}
                                            }}
                                            if (typeof window.handleVarsUpdate === 'function' && data.jsVars) {{
                                                // not used, attempt to maybe pass in js vars, but will break for consts so w/e
                                                // if desired, we can stop setting vars as constants, then call this with data, and *not* refresh it
                                                window.handleVarsUpdate(data.jsVars);
                                            }}
                                        }}
                                        else if (typeof window.handleWheelSpinResult === 'function' && data.type == 'wheel_spin_result')
                                            window.handleWheelSpinResult(data);
                                        else if (typeof window.handleWheelData === 'function' && data.type == 'wheel_data')
                                            window.handleWheelData(data);
                                        else if (typeof window.handleWheelSpinStart === 'function' && data.type == 'wheel_spin_start')
                                            window.handleWheelSpinStart(data);
                                        else if (typeof window.handleWheelSpinStatus === 'function' && data.type == 'wheel_spin_status')
                                            window.handleWheelSpinStatus(data);
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