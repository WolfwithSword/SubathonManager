using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;
using SubathonManager.Data;
using SubathonManager.Integration;
using SubathonManager.Server.Interfaces;
using SubathonManager.Services;

// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.Server;

public partial class WebServer {
    private static readonly TimeSpan OutboundDrainTimeout = TimeSpan.FromSeconds(2);
    private readonly List<IWebSocketClient> _clients = new();
    private readonly object _lock = new();

    private void SetupWebsocketListeners() {
        SubathonEvents.SubathonDataUpdate += SendSubathonDataUpdate;
        SubathonEvents.SubathonEventProcessed += SendSubathonEventProcessed;
        SubathonEvents.SubathonGoalCompleted += SendGoalCompleted;
        SubathonEvents.SubathonGoalListUpdated += SendGoalsUpdated;
        OverlayEvents.OverlayRefreshRequested += SendRefreshRequest;
        SubathonEvents.SubathonValueConfigRequested += SendSubathonValues;
        SubathonEvents.SubathonTotalsUpdated += SendSubathonTotals;
        SubathonEvents.SubscriptionTotalsUpdated += SendSubscriptionTotals;

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

    private void StopWebsocketServer() {
        SubathonEvents.SubathonDataUpdate -= SendSubathonDataUpdate;
        SubathonEvents.SubathonEventProcessed -= SendSubathonEventProcessed;
        SubathonEvents.SubathonGoalCompleted -= SendGoalCompleted;
        OverlayEvents.OverlayRefreshRequested -= SendRefreshRequest;
        SubathonEvents.SubathonGoalListUpdated -= SendGoalsUpdated;
        SubathonEvents.SubathonValueConfigRequested -= SendSubathonValues;
        SubathonEvents.SubathonTotalsUpdated -= SendSubathonTotals;
        SubathonEvents.SubscriptionTotalsUpdated -= SendSubscriptionTotals;

        SubathonEvents.PromptRunStarted -= OnPromptStart;
        SubathonEvents.PromptRunUpdate -= OnPromptRunUpdate;
        SubathonEvents.PromptRunProgressUpdated -= OnPromptProgress;

        OverlayEvents.WidgetVarsUpdated -= SendWidgetVarsUpdate;
        OverlayEvents.WidgetRefreshRequested -= SendWidgetReload;

        WheelEvents.WheelSpinStarted -= SendWheelSpinStarted;
        WheelEvents.WheelSpinResult -= SendWheelSpinResult;
        WheelEvents.WheelSpinStatusChanged -= SendWheelSpinStatusChanged;
        WheelEvents.WheelDataChanged -= SendWheelDataChanged;

        List<IWebSocketClient> clientsCopy;
        lock (_lock) {
            clientsCopy = _clients.ToList();
            _clients.Clear();
        }

        foreach (IWebSocketClient client in clientsCopy)
            client.CompleteOutbound();
    }

    private void OnPromptStart(SubathonPromptRun subathonPromptRun, SubathonPrompt? subathonPrompt) {
        SendPromptData(subathonPromptRun);
    }

    private void OnPromptRunUpdate(SubathonPromptRun subathonPromptRun, SubathonPrompt? subathonPrompt) {
        Task.Run(async () => {
            await using AppDbContext db = await _factory.CreateDbContextAsync();
            long current = await PromptOrchestratorService.GetCurrentCountAsync(db, subathonPromptRun.LinkedPrompt!);
            long progress = current - subathonPromptRun.BaselineCount;
            SendPromptData(subathonPromptRun, progress);
        });
    }

    private void OnPromptProgress(SubathonPromptRun subathonPromptRun, long progress) {
        SendPromptData(subathonPromptRun, progress);
    }

    internal void SendWidgetReload(Guid widgetId, float x, float y, int width, int height, float scaleX, float scaleY) {
        var data = new {
            type = "widget_reload",
            widgetId = widgetId.ToString(),
            x, y, width, height, scaleX, scaleY
        };
        BroadcastObject(data, WebsocketClientMessageType.Overlay);
    }

    internal void SendPromptData(SubathonPromptRun? run, long progress = 0) {
        object data = new {
            type = "prompt_update",
            status = run == null ? "None" : run.Status.ToString(),
            progress,
            target = run?.LinkedPrompt?.Value ?? 0,
            seconds_remaining = run?.TimeRemaining().TotalSeconds ?? 0,
            start_time = run?.StartedAt,
            end_time = run?.ExpiresAt,
            duration_seconds = run?.LinkedPrompt?.CompletionDuration.TotalSeconds ?? 0,
            text = run?.LinkedPrompt?.Text,
            prompt_type = $"{run?.LinkedPrompt?.Type}",
            prompt_subtype = $"{run?.LinkedPrompt?.SubType}",
            prompt_eventtype = $"{run?.LinkedPrompt?.FilterEventType}",
            prompt_eventtype_metafilter = run?.LinkedPrompt?.FilterMeta
        };
        BroadcastObject(data, WebsocketClientTypeHelper.ConsumersList);
    }

    internal void SendWidgetVarsUpdate(Guid widgetId,
        IEnumerable<CssVariable> cssVars, IEnumerable<JsVariable> jsVars) {
        var data = new {
            type = "widget_vars_update",
            widgetId = widgetId.ToString(),
            cssVars = cssVars.Select(v => new { name = v.Name, value = v.Value }),
            jsVars = jsVars.Select(v => new {
                name = v.Name, value = v.Value,
                injectLine = v.GetInjectLine()
            })
        };
        BroadcastObject(data, WebsocketClientTypeHelper.ConsumersList);
    }

    internal void SendSubathonValues(string jsonData) {
        var newData =
            $"{{ \"type\": \"value_config\", \"ws_type\": \"{WebsocketClientMessageType.ValueConfig}\", \"data\": {jsonData} }}";
        Broadcast(newData, OutboundCoalesceKey.None, WebsocketClientTypeHelper.ConfigConsumersList);
    }

    internal void SendGoalsUpdated(List<SubathonGoal> goals, long currentPoints, GoalsType type) {
        object data = new {
            type = "goals_list",
            points = currentPoints,
            goals = goals.Select(goal => GoalToObject(goal, currentPoints)).ToArray(),
            goals_type = $"{type}"
        };
        BroadcastObject(data, OutboundCoalesceKey.GoalsList, WebsocketClientTypeHelper.ConsumersList);
    }

    private object GoalToObject(SubathonGoal goal, long currentPoints) {
        return new {
            text = goal.Text,
            points = goal.Points,
            completed = goal.Points <= currentPoints
        };
    }

    internal void SendGoalCompleted(SubathonGoal goal, long currentPoints) {
        object data = new {
            type = "goal_completed",
            goal_text = goal.Text,
            goal_points = goal.Points,
            points = currentPoints
        };
        BroadcastObject(data, WebsocketClientTypeHelper.ConsumersList);
    }

    private async Task InitConnection(IWebSocketClient socket) {
        await using AppDbContext db = await _factory.CreateDbContextAsync();
        SubathonData? subathon = await db.SubathonDatas.Include(s => s.Multiplier)
            .FirstOrDefaultAsync(s => s.IsActive);
        if (subathon is null) return;

        string configValues = await _valueHelper.GetAllAsJsonAsync();
        SendSubathonValues(configValues);

        await SelectSendAsync(socket, SubathonDataToObject(subathon));

        SubathonGoalSet? goalSet = await db.SubathonGoalSets.AsNoTracking().Include(g => g.Goals)
            .FirstOrDefaultAsync(g => g.IsActive);
        if (goalSet != null) {
            long val = subathon.Points;
            if (goalSet.Type == GoalsType.Money)
                val = (long)Math.Floor(subathon.MoneySum ?? 0);

            object data = new {
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
        if (promptRun is { LinkedPrompt: not null }) {
            long current = await PromptOrchestratorService.GetCurrentCountAsync(db, promptRun.LinkedPrompt);
            long progress = current - promptRun.BaselineCount;
            SendPromptData(promptRun, progress);
        }
        else {
            SendPromptData(null);
        }

        SubathonTotals? totals = await EventService.GetSubathonTotalsAsync(db);

        if (totals != null)
            await SelectSendAsync(socket, SubathonTotalsToObject(totals));

        SubscriptionTotals? subTotals = await EventService.GetSubscriptionTotalsAsync(db);

        if (subTotals != null)
            await SelectSendAsync(socket, SubscriptionTotalsToObject(subTotals));

        WheelSet? activeWheel = await db.WheelSets
            .Include(w => w.WheelItems)
            .ThenInclude(i => i.Action)
            .AsNoTracking()
            .FirstOrDefaultAsync(w => w.IsActive);

        if (activeWheel != null) {
            var spinsOwed = StateValueHelper.Get<int>(db, StateKeys.WheelSpinsOwed);
            List<object?> items = activeWheel.WheelItems.Select(WheelItemToObject).ToList();
            var wheelData = new {
                type = "wheel_data",
                wheel = new { id = activeWheel.Id, name = activeWheel.Name, spin_count = activeWheel.SpinCount, items },
                spins_owed = spinsOwed
            };
            await SelectSendAsync(socket, wheelData);
        }
    }

    private object SubathonTotalsToObject(SubathonTotals totals) {
        return new {
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
                    .ToDictionary(k => k.Key.ToString(), k => k.Value)
            }
        };
    }

    private object SubscriptionTotalsToObject(SubscriptionTotals totals) {
        return new {
            type = "subscription_totals",
            sub_total = totals.SubTotal,
            sub_total_by_type = totals.SubTotalByEvent
                .ToDictionary(k => k.Key.ToString(), k => k.Value),
            sub_total_by_type_tier = totals.SubTotalByEventTier
                .ToDictionary(k => k.Key.ToString(), k => k.Value),
            simulated = new {
                sub_total = totals.Simulated.SubTotal,
                sub_total_by_type = totals.Simulated.SubTotalByEvent
                    .ToDictionary(k => k.Key.ToString(), k => k.Value),
                sub_total_by_type_tier = totals.Simulated.SubTotalByEventTier
                    .ToDictionary(k => k.Key.ToString(), k => k.Value)
            }
        };
    }

    private object SubathonEventToObject(SubathonEvent subathonEvent) {
        string? trueSource = subathonEvent.EventType.GetTypeTrueSource(subathonEvent.EventTypeMeta);
        var eventType = subathonEvent.EventType.ToString();
        if (subathonEvent.EventType == SubathonEventType.GoAffProOrder
            && GoAffProOrderHelper.TryGetStore(subathonEvent.EventTypeMeta, out GoAffProStore? store)) {
            trueSource = store.InternalName;
            eventType = store.InternalEventName;
        }

        object data = new {
            type = "event",
            event_type = eventType,
            source = subathonEvent.Source.ToString(),
            seconds_added = subathonEvent.GetFinalSecondsValueRaw() < 0.5 ? 0 : subathonEvent.GetFinalSecondsValue(),
            points_added = subathonEvent.GetFinalPointsValue(),
            user = subathonEvent.User,
            value = subathonEvent.Value, // sometimes useful
            amount = subathonEvent.Amount, // sometimes useful
            currency = subathonEvent.Currency, // sometimes useful
            command = subathonEvent.Command.ToString(), // only useful if eventType is command
            event_timestamp = subathonEvent.EventTimestamp,
            reversed = subathonEvent.WasReversed,
            sub_type = subathonEvent.EventType.GetSubType().ToString(),
            secondary_value = subathonEvent.SecondaryValue,
            tertiary_value = subathonEvent.TertiaryValue,
            type_true_source = trueSource
        };
        return data;
    }

    internal void SendSubathonEventProcessed(SubathonEvent subathonEvent, bool effective) {
        bool showOverride = _config.GetBool("App", "ShowLockedEvents");
        if (!showOverride && !subathonEvent.ProcessedToSubathon) return;
        BroadcastObject(SubathonEventToObject(subathonEvent), WebsocketClientTypeHelper.ConsumersList);
    }

    internal void SendSubathonTotals(SubathonTotals totals) {
        BroadcastObject(SubathonTotalsToObject(totals), OutboundCoalesceKey.SubathonTotals,
            WebsocketClientTypeHelper.ConsumersList);
    }

    internal void SendSubscriptionTotals(SubscriptionTotals totals) {
        BroadcastObject(SubscriptionTotalsToObject(totals), OutboundCoalesceKey.SubscriptionTotals,
            WebsocketClientTypeHelper.ConsumersList);
    }

    internal void SendRefreshRequest(Guid id) {
        BroadcastObject(new {
            type = "refresh_request",
            id = id.ToString()
        }, WebsocketClientMessageType.Overlay);
    }

    private object SubathonDataToObject(SubathonData subathon) {
        TimeSpan? multiplierRemaining = TimeSpan.Zero;
        if (subathon.Multiplier.Duration != null && subathon.Multiplier.Duration > TimeSpan.Zero
                                                 && subathon.Multiplier.Started != null) {
            DateTime? multEndTime = subathon.Multiplier.Started + subathon.Multiplier.Duration;
            multiplierRemaining = multEndTime - DateTime.Now;
        }

        long roundedMoney = subathon.GetRoundedMoneySum();
        double fractionalMoney = subathon.GetRoundedMoneySumWithCents();

        object data = new {
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
            is_locked = subathon.IsLocked,
            is_reversed = subathon.IsSubathonReversed(),
            multiplier_points = subathon.Multiplier.ApplyToPoints ? subathon.Multiplier.Multiplier : 1,
            multiplier_time = subathon.Multiplier.ApplyToSeconds ? subathon.Multiplier.Multiplier : 1,
            multiplier_start_time = subathon.Multiplier.Started,
            multiplier_seconds_total = Math.Round(subathon.Multiplier.Duration?.TotalSeconds ?? 0),
            multiplier_seconds_remaining = Math.Round(multiplierRemaining.Value.TotalSeconds),
            total_seconds_elapsed = (int)(subathon.MillisecondsElapsed / 1000),
            total_seconds_added = (int)(subathon.MillisecondsCumulative / 1000)
        };
        return data;
    }

    internal void SendSubathonDataUpdate(SubathonData subathon, DateTime time) {
        BroadcastObject(SubathonDataToObject(subathon), OutboundCoalesceKey.SubathonTimer,
            WebsocketClientTypeHelper.ConsumersList);
    }

    internal void AddSocketClient(IWebSocketClient socket) {
        lock (_lock) {
            _clients.Add(socket);
            _logger?.LogDebug("{ClientsCount} websocket clients connected", _clients.Count);
        }

        socket.StartOutbound();
    }

    public async Task HandleWebSocketRequestAsync(IHttpContext ctx) {
        if (!ctx.IsWebSocket) {
            await ctx.WriteResponse(400, "Invalid Websocket Request");
            return;
        }

        Task<WebSocket>? accept = ctx.AcceptWebSocketAsync();

        if (accept is null) {
            await ctx.WriteResponse(400, "Not a WebSocket request");
            return;
        }

        using WebSocket socket = await accept;
        IWebSocketClient client = new WebSocketClient(socket);
        AddSocketClient(client);

        _logger?.LogDebug("New WebSocket Client Connected [{ClientClientId}].", client.ClientId);

        try {
            await Listen(client);
        }
        finally {
            await client.CompleteOutboundAsync(OutboundDrainTimeout);

            foreach (SubathonEventSource clientIntegrationSource in client.IntegrationSources) {
                WebServerEvents.RaiseWebSocketIntegrationSourceChange(clientIntegrationSource.ToString(), false);
                _logger?.LogDebug("WebSocket Client disconnected for Integration: {ClientIntegrationSource}",
                    clientIntegrationSource);
            }

            lock (_lock) {
                _clients.Remove(client);
                _logger?.LogDebug("{ClientsCount} websocket clients connected", _clients.Count);
            }

            _logger?.LogDebug("WebSocket Client Disconnected [{ClientClientId}]", client.ClientId);
        }
    }

    private async Task Listen(IWebSocketClient socket) {
        var buffer = new byte[1024 * 8];
        while (socket.State == WebSocketState.Open) {
            WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close) {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client closed", CancellationToken.None);
                break;
            }

            string msg = Encoding.UTF8.GetString(buffer, 0, result.Count);
            var clientMessageType = WebsocketClientMessageType.None;
            try {
                JsonDocument json = JsonDocument.Parse(msg);
                if (json.RootElement.TryGetProperty("ws_type", out JsonElement type)) {
                    switch (type.GetString()) {
                        case "ping":
                            socket.TryEnqueue(Encoding.UTF8.GetBytes("{\"ws_type\":\"pong\"}"));
                            break;
                        case "hello":
                            _logger?.LogDebug(
                                $"[WebSocket] [{socket.ClientId}] Hello from {json.RootElement.GetProperty("origin").GetString()}");
                            break;
                    }

                    if (Enum.TryParse(type.GetString(), out clientMessageType))
                        if (socket.ClientTypes.Count >= 1 && !socket.ClientTypes.Contains(clientMessageType)) {
                            _logger?.LogDebug(
                                $"WebSocket ClientType [{clientMessageType}] identified for client [{socket.ClientId}]");
                            socket.ClientTypes.Add(clientMessageType);
                            if (clientMessageType.IsConsumer() &&
                                socket.ClientTypes.Contains(WebsocketClientMessageType.Generic)) {
                                socket.ClientTypes.Remove(WebsocketClientMessageType.Generic);
                                await InitConnection(socket);
                            }
                            else if (clientMessageType == WebsocketClientMessageType.ValueConfig &&
                                     socket.ClientTypes.Contains(WebsocketClientMessageType.Generic)) {
                                socket.ClientTypes.Remove(WebsocketClientMessageType.Generic);
                                string configValues = await _valueHelper.GetAllAsJsonAsync();
                                var newData =
                                    $"{{ \"type\": \"value_config\", \"ws_type\": \"{WebsocketClientMessageType.ValueConfig}\", \"data\": {configValues} }}";
                                await SelectSendStringAsync(socket, newData);
                            }
                        }
                }

                switch (clientMessageType) {
                    case WebsocketClientMessageType.Command: {
                        if (json.RootElement.TryGetProperty("request", out JsonElement reqElem)
                            && string.Equals(reqElem.GetString(), "commands", StringComparison.OrdinalIgnoreCase)) {
                            await SelectSendAsync(socket, new {
                                type = "command_list",
                                ws_type = nameof(WebsocketClientMessageType.Command),
                                commands = BuildCommandCatalog()
                            });
                            break;
                        }

                        if (!json.RootElement.TryGetProperty("type", out JsonElement elem)
                            || !Enum.TryParse(elem.GetString()!, true, out SubathonEventType seType)
                            || seType == SubathonEventType.Unknown)
                            continue;
                        if (seType != SubathonEventType.Command) continue;

                        Dictionary<string, JsonElement> data =
                            json.RootElement
                                .EnumerateObject()
                                .ToDictionary(p => p.Name, p => p.Value);
                        //Console.WriteLine(data);
                        bool success = ExternalEventService.ProcessExternalCommand(data);
                        data.TryGetValue("command", out JsonElement cmdElem);
                        data.TryGetValue("context", out JsonElement ctxElem);
                        await SelectSendAsync(socket, new {
                            type = "command_ack",
                            ws_type = nameof(WebsocketClientMessageType.Command),
                            command = cmdElem.ValueKind == JsonValueKind.String ? cmdElem.GetString() : null,
                            context = ctxElem.ValueKind == JsonValueKind.String ? ctxElem.GetString() : null,
                            success
                        });
                        break;
                    }
                    case WebsocketClientMessageType.IntegrationSource: {
                        if (json.RootElement.TryGetProperty("source", out JsonElement src) &&
                            Enum.TryParse(src.GetString()!, true, out SubathonEventSource source)
                            && !socket.IntegrationSources.Contains(source)) {
                            _logger?.LogDebug($"WebSocket Client [{socket.ClientId}] added Integration: {source}");
                            socket.IntegrationSources.Add(source);
                            WebServerEvents.RaiseWebSocketIntegrationSourceChange(source.ToString(), true);
                        }

                        if (!json.RootElement.TryGetProperty("type", out JsonElement elem)
                            || !Enum.TryParse(elem.GetString()!, true, out SubathonEventType seType)
                            || seType == SubathonEventType.Unknown)
                            continue;

                        Dictionary<string, JsonElement> data =
                            json.RootElement
                                .EnumerateObject()
                                .ToDictionary(p => p.Name, p => p.Value);

                        if (((SubathonEventType?)seType).IsCurrencyDonation() &&
                            ((SubathonEventType?)seType).IsExternal()) {
                            if (!socket.IntegrationSources.Contains(((SubathonEventType?)seType).GetSource()))
                                socket.IntegrationSources.Add(((SubathonEventType?)seType).GetSource());
                            ExternalEventService.ProcessExternalDonation(data);
                        }
                        else if (((SubathonEventType?)seType).IsSubscription() &&
                                 ((SubathonEventType?)seType).IsExternal()) {
                            if (!socket.IntegrationSources.Contains(((SubathonEventType?)seType).GetSource()))
                                socket.IntegrationSources.Add(((SubathonEventType?)seType).GetSource());
                            ExternalEventService.ProcessExternalSub(data);
                        }

                        break;
                    }
                    case WebsocketClientMessageType.ValueConfig
                        when json.RootElement.TryGetProperty("data", out JsonElement data): {
                        int patched = await _valueHelper.PatchFromJsonDataAsync(data);

                        var resMsg = "";
                        if (patched == -1) resMsg = "Error Patching";
                        else if (patched == 0) resMsg = "No patches needed";
                        else resMsg = $"Patched {patched} Values";
                        object resp = new {
                            ws_type = WebsocketClientMessageType.ValueConfig,
                            response = resMsg
                        };
                        await SelectSendAsync(socket, resp);
                        break;
                    }
                    case WebsocketClientMessageType.ValueConfig: {
                        string configValues = await _valueHelper.GetAllAsJsonAsync();
                        var newData =
                            $"{{ \"type\": \"value_config\", \"ws_type\": \"{WebsocketClientMessageType.ValueConfig}\", \"data\": {configValues} }}";
                        await SelectSendStringAsync(socket, newData);
                        break;
                    }
                    case WebsocketClientMessageType.WheelControl: {
                        if (!json.RootElement.TryGetProperty("id", out JsonElement idProp)
                            || !Guid.TryParse(idProp.GetString(), out Guid histId))
                            break;
                        if (!json.RootElement.TryGetProperty("status", out JsonElement statusProp)
                            || !Enum.TryParse(statusProp.GetString(), true, out WheelSpinHistoryStatus newStatus))
                            break;

                        await using AppDbContext db = await _factory.CreateDbContextAsync();
                        WheelSpinHistory? history = await db.WheelSpinHistories
                            .Include(h => h.LinkedItem).ThenInclude(i => i!.Action)
                            .Include(h => h.LinkedWheel)
                            .FirstOrDefaultAsync(h => h.Id == histId);
                        if (history == null || history.Status == newStatus)
                            break;

                        history.Status = newStatus;
                        history.UpdatedAt = DateTime.Now;
                        await db.SaveChangesAsync();

                        var spinsOwed = StateValueHelper.Get<int>(db, StateKeys.WheelSpinsOwed);
                        WheelEvents.RaiseWheelSpinStatusChanged(history, spinsOwed);
                        break;
                    }
                }
            }
            catch (Exception ex) {
                _logger?.LogError(ex, ex.Message);
            }
        }
    }

    private static object? WheelItemToObject(WheelItem? item) {
        if (item == null) return null;
        return new {
            id = item.Id,
            text = item.Text,
            weight = item.Weight,
            quantity = item.Quantity,
            is_infinite = item.IsInfinite,
            enabled = item.Enabled,
            index = item.Index,
            action = item.Action == null
                ? null
                : (object?)new {
                    type = item.Action.ActionType.ToString(),
                    parameter = item.Action.Parameter
                }
        };
    }

    private void SendWheelSpinStarted(WheelSet wheel, int delaySeconds) {
        var data = new {
            type = "wheel_spin_start",
            wheel_id = wheel.Id,
            wheel_name = wheel.Name,
            spin_delay_seconds = delaySeconds,
            timestamp = DateTime.Now
        };
        BroadcastObject(data, WebsocketClientTypeHelper.ConsumersList);
    }

    private void SendWheelSpinResult(WheelSet wheel, WheelItem? item, WheelSpinHistory history, int _) {
        object? itemSnapshot = WheelItemToObject(item);
        var data = new {
            type = "wheel_spin_result",
            wheel = new { id = wheel.Id, name = wheel.Name },
            item = itemSnapshot,
            history = new {
                id = history.Id,
                status = history.Status.ToString(),
                created_at = history.CreatedAt,
                updated_at = history.UpdatedAt
            },
            timestamp = DateTime.Now
        };
        BroadcastObject(data, WebsocketClientTypeHelper.ConsumersList);
    }

    private void SendWheelSpinStatusChanged(WheelSpinHistory history, int _) {
        object? itemSnapshot = WheelItemToObject(history.LinkedItem);
        var data = new {
            type = "wheel_spin_status",
            history_id = history.Id,
            status = history.Status.ToString(),
            updated_at = history.UpdatedAt,
            wheel_item = itemSnapshot
        };
        BroadcastObject(data, WebsocketClientTypeHelper.ConsumersList);
    }

    private void SendWheelDataChanged(WheelSet wheel, int spinsOwed) {
        // snapshot
        Guid wheelId = wheel.Id;
        string wheelName = wheel.Name;
        int spinCount = wheel.SpinCount;
        List<object?> items = wheel.WheelItems.Select(WheelItemToObject).ToList();
        var data = new {
            type = "wheel_data",
            wheel = new { id = wheelId, name = wheelName, spin_count = spinCount, items },
            spins_owed = spinsOwed
        };
        BroadcastObject(data, WebsocketClientTypeHelper.ConsumersList);
    }

    private void BroadcastObject(object data, OutboundCoalesceKey key, params WebsocketClientMessageType[] types) {
        Broadcast(JsonSerializer.Serialize(data), key, types);
    }

    private void BroadcastObject(object data, params WebsocketClientMessageType[] types) {
        Broadcast(JsonSerializer.Serialize(data), OutboundCoalesceKey.None, types);
    }

    private void Broadcast(string json, OutboundCoalesceKey key, params WebsocketClientMessageType[] types) {
        byte[] bytes = Encoding.UTF8.GetBytes(json);

        List<IWebSocketClient> clientsCopy;
        lock (_lock) {
            clientsCopy = _clients.ToList();
        }

        foreach (IWebSocketClient ws in clientsCopy.Where(ws =>
                     ws.State == WebSocketState.Open && ws.ClientTypes.Any(types.Contains)))
            ws.TryEnqueue(bytes, key);
    }

    internal Task SelectSendAsync(IWebSocketClient client, object data) {
        return SelectSendStringAsync(client, JsonSerializer.Serialize(data));
    }

    internal Task SelectSendStringAsync(IWebSocketClient client, string data) {
        if (client.State == WebSocketState.Open)
            client.TryEnqueue(Encoding.UTF8.GetBytes(data));
        return Task.CompletedTask;
    }

    public string GetWebsocketInjectionScript(string? routeId = "") {
        string route = routeId ?? string.Empty;
        bool isOverlay = !string.IsNullOrWhiteSpace(route);

        return ClientScriptTemplate
            .Replace("__WS_URL__", $"ws://localhost:{Port}/ws")
            .Replace("__ROUTE_ID__", route)
            .Replace("__EMPTY_GUID__", Guid.Empty.ToString())
            .Replace("__IS_OVERLAY__", isOverlay ? "true" : "false");
    }

    private const string ClientScriptTemplate = """
        <script data-subathon-client>
        (function () {
            if (window.Subathon && window.Subathon.__installed) return;

            var WS_URL = '__WS_URL__';
            var ROUTE_ID = '__ROUTE_ID__';
            var EMPTY_GUID = '__EMPTY_GUID__';
            var IS_OVERLAY = __IS_OVERLAY__;

            var RELAY_TAG = '__subathon_relay__';
            var RELAY_TRIES = 10;
            var RELAY_BACKOFF = 100;
            var PROTOCOL = 1;

            var STATE_TYPES = {
                subathon_timer: 1, subathon_totals: 1, subscription_totals: 1,
                goals_list: 1, value_config: 1, prompt_update: 1, wheel_data: 1
            };

            var LEGACY = {
                subathon_timer:      'handleSubathonUpdate',
                event:               'handleSubathonEvent',
                prompt_update:       'handlePromptUpdate',
                goals_list:          'handleGoalsUpdate',
                goal_completed:      'handleGoalCompleted',
                value_config:        'handleValueConfig',
                subathon_totals:     'handleTotalsUpdate',
                subscription_totals: 'handleSubscriptionTotalsUpdate',
                wheel_spin_result:   'handleWheelSpinResult',
                wheel_data:          'handleWheelData',
                wheel_spin_start:    'handleWheelSpinStart',
                wheel_spin_status:   'handleWheelSpinStatus'
            };

            var listeners = {};
            var state = {};

            var socket = null, reconnectTimer = null, pingTimer = null, connected = false;
            var relayParent = null, relayTimer = null, relayTries = 0, gaveUpOnRelay = false;
            var relayFrames = [];

            function safe(fn, arg) {
                try { fn(arg); } catch (e) { console.error('[Subathon] handler error:', e); }
            }

            //////////////////////////////////////////////////
            
            function emit(type, data) {
                var list = listeners[type];
                if (!list) return;
                var snapshot = list.slice();
                for (var i = 0; i < snapshot.length; i++) safe(snapshot[i], data);
            }

            var legacyServed = {};

            function callLegacy(data) {
                var name = LEGACY[data.type];
                if (!name || typeof window[name] !== 'function') return false;
                safe(window[name], data);
                legacyServed[data.type] = true;
                return true;
            }

            function dispatch(data) {
                if (!data || typeof data.type !== 'string') return;
                if (STATE_TYPES[data.type]) state[data.type] = data;

                builtin(data);
                callLegacy(data);

                emit(data.type, data);
                emit('*', data);
            }

            function replayStateToLegacy() {
                for (var type in state)
                    if (!legacyServed[type]) callLegacy(state[type]);
            }

            function builtin(data) {
                if (data.type === 'refresh_request') {
                    if (IS_OVERLAY && (data.id === ROUTE_ID || data.id === EMPTY_GUID)) window.location.reload();
                    return;
                }

                if (data.type === 'widget_reload') {
                    if (!IS_OVERLAY) return;
                    var iframe = document.querySelector('iframe[data-widget-id="' + data.widgetId + '"]');
                    if (!iframe) return;
                    var wrapper = iframe.parentElement;
                    if (data.width != null && typeof window.applyWidgetLayout === 'function') {
                        window.applyWidgetLayout(wrapper, data.width, data.height, data.scaleX, data.scaleY);
                        wrapper.style.left = data.x + 'px';
                        wrapper.style.top = data.y + 'px';
                    }
                    iframe.src = iframe.src;
                    return;
                }

                if (data.type === 'widget_vars_update') {
                    if (IS_OVERLAY) return;
                    if (data.widgetId !== api.widgetId) return;
                    if (data.cssVars) {
                        for (var i = 0; i < data.cssVars.length; i++) {
                            var v = data.cssVars[i];
                            document.documentElement.style.setProperty('--' + v.name, v.value, 'important');
                        }
                    }
                    if (typeof window.handleVarsUpdate === 'function' && data.jsVars)
                        safe(window.handleVarsUpdate, data.jsVars);
                }
            }

            function setConnected(isUp) {
                connected = isUp;
                if (IS_OVERLAY) relayPostAll({ kind: 'status', connected: isUp });
                if (isUp) {
                    if (typeof window.handleSubathonConnect === 'function') safe(window.handleSubathonConnect);
                    emit('connect', { type: 'connect' });
                } else {
                    if (typeof window.handleSubathonDisconnect === 'function') safe(window.handleSubathonDisconnect);
                    emit('disconnect', { type: 'disconnect' });
                }
            }

            function liveFrames() {
                var out = [], frames = document.querySelectorAll('iframe');
                for (var i = 0; i < frames.length; i++) {
                    try { if (frames[i].contentWindow) out.push(frames[i].contentWindow); } catch (e) { /**/ }
                }
                return out;
            }

            function relayPost(win, msg) {
                msg.__sm = RELAY_TAG;
                try { win.postMessage(msg, location.origin); } catch (e) { /**/ }
            }

            function relayPostAll(msg) {
                if (!IS_OVERLAY || relayFrames.length === 0) return;
                var live = liveFrames(), kept = [];
                for (var i = 0; i < relayFrames.length; i++) {
                    if (live.indexOf(relayFrames[i]) < 0) continue;
                    kept.push(relayFrames[i]);
                    relayPost(relayFrames[i], Object.assign({}, msg));
                }
                relayFrames = kept;
            }

            function onHostHello(source) {
                if (liveFrames().indexOf(source) < 0) return;
                if (relayFrames.indexOf(source) < 0) relayFrames.push(source);
                relayPost(source, { kind: 'ack', connected: connected });
                for (var type in state) relayPost(source, { kind: 'msg', data: state[type] });
            }

            function onMessage(e) {
                if (e.origin !== location.origin) return;
                var d = e.data;
                if (!d || d.__sm !== RELAY_TAG) return;

                if (IS_OVERLAY) {
                    if (d.kind === 'hello') onHostHello(e.source);
                    else if (d.kind === 'send' && liveFrames().indexOf(e.source) >= 0) rawSend(d.payload);
                    return;
                }

                if (e.source !== window.parent) return;

                if (d.kind === 'ready' && !relayParent && !socket) { relayHello(); return; }
                if (d.kind === 'ack') { adoptRelay(d.connected); return; }
                if (relayParent !== e.source) return;
                if (d.kind === 'msg') dispatch(d.data);
                else if (d.kind === 'status') setConnected(!!d.connected);
            }

            function adoptRelay(hostConnected) {
                if (relayParent || socket) return;
                relayParent = window.parent;
                gaveUpOnRelay = false;
                if (relayTimer) { clearTimeout(relayTimer); relayTimer = null; }
                if (hostConnected) setConnected(true);
            }

            function relayHello() {
                relayPost(window.parent, { kind: 'hello', v: PROTOCOL });
            }

            function parentIsSameOrigin() {
                if (window.parent === window) return false;
                try { return window.parent.location.origin === location.origin; } catch (e) { return false; }
            }

            function startRelayHandshake() {
                if (relayParent || socket) return;
                if (relayTries++ >= RELAY_TRIES) {
                    gaveUpOnRelay = true;
                    openSocket();
                    return;
                }
                relayHello();
                relayTimer = setTimeout(startRelayHandshake, RELAY_BACKOFF);
            }

            function rawSend(payload) {
                if (!socket || socket.readyState !== WebSocket.OPEN) return false;
                socket.send(typeof payload === 'string' ? payload : JSON.stringify(payload));
                return true;
            }

            function startPing() {
                stopPing();
                pingTimer = setInterval(function () { rawSend({ ws_type: 'ping', t: Date.now() }); }, 15000);
            }

            function stopPing() {
                if (pingTimer) clearInterval(pingTimer);
                pingTimer = null;
            }

            function openSocket() {
                if (socket && (socket.readyState === WebSocket.OPEN || socket.readyState === WebSocket.CONNECTING))
                    return;

                console.log('[Subathon WS] Connecting...');
                socket = new WebSocket(WS_URL);

                socket.onopen = function () {
                    console.log('[Subathon WS] Connected');
                    if (reconnectTimer) { clearTimeout(reconnectTimer); reconnectTimer = null; }
                    startPing();
                    if (IS_OVERLAY) rawSend({ ws_type: 'Overlay', origin: window.location.href });
                    rawSend({ ws_type: 'Widget', origin: window.location.href });
                    setConnected(true);
                };

                socket.onmessage = function (e) {
                    var data;
                    try { data = JSON.parse(e.data); } catch (err) {
                        console.error('[Subathon WS] JSON error:', err);
                        return;
                    }
                    if (!data || typeof data.type !== 'string') return;
                    if (STATE_TYPES[data.type]) state[data.type] = data;
                    relayPostAll({ kind: 'msg', data: data });
                    dispatch(data);
                };

                socket.onclose = function () {
                    console.warn('[Subathon WS] Closed. Reconnecting...');
                    stopPing();
                    setConnected(false);
                    reconnectTimer = setTimeout(openSocket, 5000);
                };

                socket.onerror = function (e) {
                    console.error('[Subathon WS] Error:', e);
                    socket.close();
                };
            }

            //////////////////////////////////////////
            /// API
            //////////////////////////////////////////
            var api = {
                __installed: true,
                version: PROTOCOL,
                isOverlay: IS_OVERLAY,
                routeId: ROUTE_ID || null,
                widgetId: IS_OVERLAY ? null : (window.location.pathname.split('/')[2] || null),

                get connected() { return connected; },
                get transport() { return relayParent ? 'relay' : (socket ? 'socket' : 'pending'); },
                get state() { return state; },
                get: function (type) { return state[type] || null; },

                on: function (type, fn, opts) {
                    if (typeof fn !== 'function') return api;
                    (listeners[type] || (listeners[type] = [])).push(fn);
                    if (opts && opts.replay === false) return api;
                    if (type === '*') { for (var k in state) safe(fn, state[k]); }
                    else if (state[type]) safe(fn, state[type]);
                    return api;
                },

                off: function (type, fn) {
                    var list = listeners[type];
                    if (!list) return api;
                    var i = list.indexOf(fn);
                    if (i >= 0) list.splice(i, 1);
                    return api;
                },

                once: function (type, fn, opts) {
                    var wrap = function (data) { api.off(type, wrap); fn(data); };
                    return api.on(type, wrap, opts);
                },

                send: function (payload) {
                    if (relayParent) { relayPost(relayParent, { kind: 'send', payload: payload }); return true; }
                    return rawSend(payload);
                }
            };

            window.Subathon = api;

            window.addEventListener('message', onMessage);

            if (document.readyState === 'loading')
                document.addEventListener('DOMContentLoaded', replayStateToLegacy);
            else
                replayStateToLegacy();

            document.addEventListener('visibilitychange', function () {
                if (document.hidden) return;
                if (relayParent) return;
                if (socket && socket.readyState <= 1) return;
                if (IS_OVERLAY || gaveUpOnRelay || !parentIsSameOrigin()) openSocket();
            });

            if (IS_OVERLAY) {
                openSocket();
                var frames = liveFrames();
                for (var i = 0; i < frames.length; i++) relayPost(frames[i], { kind: 'ready' });
            } else if (parentIsSameOrigin()) {
                startRelayHandshake();
            } else {
                openSocket();
            }
        })();
        </script>
        """;
}
