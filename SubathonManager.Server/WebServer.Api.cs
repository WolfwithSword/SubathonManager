using System.Collections.Specialized;
using System.Globalization;
using System.Text.Json;
using System.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Models;
using SubathonManager.Data;
using SubathonManager.Integration;
using SubathonManager.Server.Interfaces;
using SubathonManager.Services;

// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.Server;

public partial class WebServer {
    private void SetupApiRoutes() {
        _routes.Add((new RouteKey("POST", "/api/data/control"), HandleDataControlRequestAsync));
        _routes.Add((new RouteKey("PUT", "/api/data/control"), HandleDataControlRequestAsync));

        _routes.Add((new RouteKey("GET", "/api/data/status"), HandleStatusRequestAsync));

        _routes.Add((new RouteKey("GET", "/api/data/amounts"), HandleAmountsRequestAsync));

        _routes.Add((new RouteKey("GET", "/api/data/values"), HandleValuesRequestAsync));

        _routes.Add((new RouteKey("GET", "/api/data/commands"), HandleCommandsRequestAsync));

        _routes.Add((new RouteKey("GET", "/api/data/globals"), HandleGlobalsRequestAsync));
        _routes.Add((new RouteKey("POST", "/api/data/globals"), HandleGlobalsSetRequestAsync));
        _routes.Add((new RouteKey("PUT", "/api/data/globals"), HandleGlobalsSetRequestAsync));
        _routes.Add((new RouteKey("PATCH", "/api/data/globals"), HandleGlobalsSetRequestAsync));

        _routes.Add((new RouteKey("GET", "/api/data/leaderboard"), HandleLeaderboardRequestAsync));

        _routes.Add((new RouteKey("PUT", "/api/data/values"), HandleValuesPatchRequestAsync));
        _routes.Add((new RouteKey("POST", "/api/data/values"), HandleValuesPatchRequestAsync));
        _routes.Add((new RouteKey("PATCH", "/api/data/values"), HandleValuesPatchRequestAsync));

        _routes.Add((new RouteKey("GET", "/api/select"), HandleSelectAsync));

        _routes.Add((new RouteKey("POST", "/api/update-position/"), HandleWidgetUpdateAsync));
        _routes.Add((new RouteKey("POST", "/api/update-size/"), HandleWidgetUpdateAsync));
        _routes.Add((new RouteKey("POST", "/api/update-dimensions/"), HandleWidgetUpdateAsync));
        _routes.Add((new RouteKey("POST", "/api/widget-action/"), HandleWidgetActionAsync));
    }

    internal async Task HandleWidgetActionAsync(IHttpContext ctx) {
        string[] parts = ctx.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || !Guid.TryParse(parts[2], out Guid widgetId)) {
            await ctx.WriteResponse(400, "Invalid Widget ID");
            return;
        }

        string body;
        using (var reader = new StreamReader(ctx.Body, ctx.Encoding)) {
            body = await reader.ReadToEndAsync();
        }

        try {
            var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body);
            string name = data != null && data.TryGetValue("action", out JsonElement el) &&
                          el.ValueKind == JsonValueKind.String
                ? el.GetString() ?? string.Empty
                : string.Empty;

            if (!Enum.TryParse<WidgetContextAction>(name, true, out WidgetContextAction action)) {
                await ctx.WriteResponse(400, "Unknown action");
                return;
            }

            WidgetEvents.RaiseWidgetAction(widgetId, action);
            await ctx.WriteResponse(200, "OK");
        }
        catch (Exception ex) {
            _logger?.LogError(ex, "Failed to handle widget context action");
            await ctx.WriteResponse(400, "Invalid action data");
        }
    }

    internal async Task HandleSelectAsync(IHttpContext ctx) {
        string path = ctx.Path;

        // Fast Close
        await ctx.WriteResponse(200, "OK");
        string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3) {
            string widgetId = parts[2];
            if (Guid.TryParse(widgetId, out Guid widgetGuid)) WidgetEvents.RaiseSelectEditorWidget(widgetGuid);
        }
    }

    internal async Task HandleWidgetUpdateAsync(IHttpContext ctx) {
        ;
        string path = ctx.Path;

        string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3) {
            string widgetId = parts[2];

            string body;
            using (var reader = new StreamReader(ctx.Body, ctx.Encoding)) {
                body = await reader.ReadToEndAsync();
            }

            var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body);

            if (data == null) {
                await ctx.WriteResponse(400, "Invalid update data");
                return;
            }

            var widgetHelper = new WidgetEntityHelper(_factory, null);
            var success = false;
            if (path.StartsWith("/api/update-size/", StringComparison.OrdinalIgnoreCase))
                success = await widgetHelper.UpdateWidgetScale(widgetId, data);
            else if (path.StartsWith("/api/update-position/", StringComparison.OrdinalIgnoreCase))
                success = await widgetHelper.UpdateWidgetPosition(widgetId, data);
            else if (path.StartsWith("/api/update-dimensions/", StringComparison.OrdinalIgnoreCase))
                success = await widgetHelper.UpdateWidgetDimensions(widgetId, data);

            if (success) {
                await ctx.WriteResponse(200, "OK");
                return;
            }

            await ctx.WriteResponse(404, "Widget Not Found");
            return;
        }

        await ctx.WriteResponse(400, "Invalid Widget ID");
    }

    private async Task HandleDataControlRequestAsync(IHttpContext ctx) {
        string body;
        using (var reader = new StreamReader(ctx.Body, ctx.Encoding)) {
            body = await reader.ReadToEndAsync();
        }

        var data = new Dictionary<string, JsonElement>();
        try {
            data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>($"{body}");
        }
        catch (JsonException ex) {
            _logger?.LogError(ex, "Invalid control data");
            await ctx.WriteResponse(400, "Invalid control data");
            return;
        }

        if (data == null || data.Count == 0) {
            await ctx.WriteResponse(400, "Invalid control data");
            return;
        }

        ExternalEventService.NotifySourceSeen(data);

        var type = SubathonEventType.Unknown;
        if (!data.ContainsKey("type") || !data.TryGetValue("type", out JsonElement elem)
                                      || !Enum.TryParse(elem.GetString()!, true, out type)) {
            await ctx.WriteResponse(400, "Invalid control data");
            return;
        }

        var success = false;
        if (type == SubathonEventType.Command) {
            success = ExternalEventService.ProcessExternalCommand(data);
        }
        else if (((SubathonEventType?)type).IsCurrencyDonation() && ((SubathonEventType?)type).IsExternal()) {
            success = ExternalEventService.ProcessExternalDonation(data);
        }
        else if (((SubathonEventType?)type).IsSubscription() && ((SubathonEventType?)type).IsExternal()) {
            success = ExternalEventService.ProcessExternalSub(data);
        }
        else {
            await ctx.WriteResponse(400, "Invalid control data");
            return;
        }

        if (success) {
            await ctx.WriteResponse(200, "OK");
            return;
        }

        await ctx.WriteResponse(400, "Invalid API Request");
    }


    internal async Task HandleStatusRequestAsync(IHttpContext ctx) {
        await using AppDbContext db = await _factory.CreateDbContextAsync();
        SubathonData? subathon = await db.SubathonDatas.Include(s => s.Multiplier)
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.IsActive);
        TimeSpan? multiplierRemaining = TimeSpan.Zero;
        if (subathon == null) {
            await ctx.WriteResponse(400, "Invalid status request");
            return;
        }

        if (subathon.Multiplier.Duration != null && subathon.Multiplier.Duration > TimeSpan.Zero
                                                 && subathon.Multiplier.Started != null) {
            DateTime? multEndTime = subathon.Multiplier.Started + subathon.Multiplier.Duration;
            multiplierRemaining = multEndTime! - DateTime.Now;
        }

        object response = new {
            millis_cumulated = subathon.MillisecondsCumulative,
            millis_elapsed = subathon.MillisecondsElapsed,
            millis_remaining = subathon.MillisecondsRemaining(),
            total_seconds = subathon.TimeRemainingRounded().TotalSeconds,
            days = subathon.TimeRemainingRounded().Days,
            hours = subathon.TimeRemainingRounded().Hours,
            minutes = subathon.TimeRemainingRounded().Minutes,
            seconds = subathon.TimeRemainingRounded().Seconds,
            points = subathon.Points,
            is_paused = subathon.IsPaused,
            is_locked = subathon.IsLocked,
            is_reversed = subathon.IsSubathonReversed(),
            multiplier = new {
                running = subathon.Multiplier.IsRunning(),
                apply_points = subathon.Multiplier.ApplyToPoints,
                apply_time = subathon.Multiplier.ApplyToSeconds,
                is_from_hypetrain = subathon.Multiplier.FromHypeTrain,
                started_at = subathon.Multiplier.Started,
                duration_seconds = Math.Round(subathon.Multiplier.Duration?.TotalSeconds ?? 0),
                duration_remaining_seconds = Math.Round(multiplierRemaining.Value.TotalSeconds)
            }
        };

        string json = JsonSerializer.Serialize(response, new JsonSerializerOptions {
            WriteIndented = true
        });
        await ctx.WriteResponse(200, json);
    }

    internal async Task HandleAmountsRequestAsync(IHttpContext ctx) {
        NameValueCollection query = HttpUtility.ParseQueryString(ctx.QueryString);

        await using AppDbContext db = await _factory.CreateDbContextAsync();
        (SubathonData? subathon, string? subathonError) = await ResolveSubathonAsync(db, query, true);
        if (subathon == null) {
            await ctx.WriteResponse(400, subathonError ?? "Invalid status request");
            return;
        }

        List<SubathonEvent> events = await db.SubathonEvents
            .Where(e => e.SubathonId == subathon.Id && e.ProcessedToSubathon)
            .Where(e => e.EventType != SubathonEventType.Command &&
                        e.EventType != SubathonEventType.Unknown)
            .ToListAsync();

        List<SubathonEvent> simulated = events
            .Where(e => e.User != null && (e.User.StartsWith("SYSTEM") || e.User.StartsWith("SIMULATED"))).ToList();
        List<SubathonEvent> real = events
            .Where(e => e.User != null && !e.User.StartsWith("SYSTEM") && !e.User.StartsWith("SIMULATED")).ToList();

        object response = new {
            subathon_id = subathon.Id,
            subathon_name = subathon.Name,
            subathon_active = subathon.IsActive,
            simulated = BuildDataSummary(simulated),
            real = BuildDataSummary(real)
        };

        string json = JsonSerializer.Serialize(response, new JsonSerializerOptions {
            WriteIndented = true
        });
        await ctx.WriteResponse(200, json);
    }

    internal static object[] BuildCommandCatalog() {
        return Enum.GetValues<SubathonCommandType>()
            .Where(c => c is not (SubathonCommandType.None or SubathonCommandType.Unknown))
            .Select(object (c) => new {
                command = c.ToString(),
                description = c.GetDescription(),
                requires_parameter = c.IsParametersRequired(),
                is_control = c.IsControlTypeCommand()
            }).ToArray();
    }

    internal async Task HandleCommandsRequestAsync(IHttpContext ctx) {
        string json = JsonSerializer.Serialize(new { commands = BuildCommandCatalog() });
        await ctx.WriteResponse(200, json, true, "application/json");
    }

    internal async Task HandleGlobalsRequestAsync(IHttpContext ctx) {
        NameValueCollection query = HttpUtility.ParseQueryString(ctx.QueryString ?? string.Empty);
        await using AppDbContext db = await _factory.CreateDbContextAsync();
        List<ActionGlobal> globals = await LoadGlobalsAsync(db);

        if (query["widget"] is { } widgetParam) {
            Widget? widget = Guid.TryParse(widgetParam, out Guid widgetId)
                ? await db.Widgets.AsNoTracking().FirstOrDefaultAsync(w => w.Id == widgetId)
                : null;
            if (widget == null) {
                await ctx.WriteResponse(404, "Widget not found");
                return;
            }

            globals = globals.Where(g => widget.ListensToGlobal(g.Name)).ToList();
        }

        if (query["name"] is { } nameParam) {
            var names = new HashSet<string>(nameParam.Split(',',
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
            globals = globals.Where(g => names.Contains(g.Name)).ToList();
        }

        string json = JsonSerializer.Serialize(new { globals = globals.Select(GlobalToObject) });
        await ctx.WriteResponse(200, json, true, "application/json");
    }

    internal async Task HandleGlobalsSetRequestAsync(IHttpContext ctx) {
        async Task Fail(int code, string error) {
            await ctx.WriteResponse(code, JsonSerializer.Serialize(new { error }), true, "application/json");
        }

        var actions = AppServices.Provider?.GetService<ActionService>();
        if (actions == null) {
            await Fail(503, "Actions aren't available");
            return;
        }

        NameValueCollection query = HttpUtility.ParseQueryString(ctx.QueryString ?? string.Empty);
        var fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) {
            ["name"] = query["name"], ["type"] = query["type"], ["op"] = query["op"], ["value"] = query["value"]
        };
        ActionValueType? sentType = null;

        string body;
        using (var reader = new StreamReader(ctx.Body, ctx.Encoding)) {
            body = await reader.ReadToEndAsync();
        }

        if (!string.IsNullOrWhiteSpace(body))
            try {
                using JsonDocument doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();

                foreach (JsonProperty prop in doc.RootElement.EnumerateObject()) {
                    fields[prop.Name] = prop.Value.ValueKind switch {
                        JsonValueKind.String => prop.Value.GetString(),
                        JsonValueKind.Null or JsonValueKind.Undefined => null,
                        _ => prop.Value.GetRawText()
                    };

                    if (!prop.Name.Equals("value", StringComparison.OrdinalIgnoreCase)) continue;
                    sentType = prop.Value.ValueKind switch {
                        JsonValueKind.Number => ActionValueType.Number,
                        JsonValueKind.True or JsonValueKind.False => ActionValueType.Boolean,
                        _ => null
                    };
                }
            }
            catch (JsonException) {
                await Fail(400, "Body must be a JSON object");
                return;
            }

        string name = fields.GetValueOrDefault("name")?.Trim() ?? string.Empty;
        if (name.Length == 0) {
            await Fail(400, "name is required");
            return;
        }

        if (fields.GetValueOrDefault("type") is { } typeText && !string.IsNullOrWhiteSpace(typeText)) {
            ActionValueType? named = typeText.Trim().ToLowerInvariant() switch {
                "text" or "string" => ActionValueType.Text,
                "number" => ActionValueType.Number,
                "boolean" or "bool" => ActionValueType.Boolean,
                _ => null
            };
            if (named == null || (sentType != null && sentType != named)) {
                await Fail(400, named == null
                    ? $"Unknown type \"{typeText}\" (text, number or boolean)"
                    : $"value is a {sentType!.Value.GetLabel()} but type says {named.Value.GetLabel()}");
                return;
            }

            sentType = named;
        }

        string? value = fields.GetValueOrDefault("value");
        string op = (fields.GetValueOrDefault("op") ?? "set").Trim().ToLowerInvariant();
        ActionOperation? operation = op switch {
            "set" => ActionOperation.Set,
            "add" or "subtract" => ActionOperation.Adjust,
            "toggle" => ActionOperation.Toggle,
            _ => null
        };
        if (operation == null) {
            await Fail(400, $"Unknown operation \"{op}\" (set, add, subtract or toggle)");
            return;
        }

        if (op == "subtract" && value != null) {
            if (ActionValueType.Number.NormalizeValue(value) is not { } amount) {
                await Fail(400, $"Can't subtract \"{value}\", it isn't a number");
                return;
            }

            value = (-double.Parse(amount, CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture);
        }

        (ActionGlobal? global, bool created, string? errorStr) =
            await actions.ChangeGlobalAsync(name, sentType, operation.Value, value, true);
        if (global == null) {
            await Fail(400, errorStr ?? "Could not set global");
            return;
        }

        await ctx.WriteResponse(200, JsonSerializer.Serialize(new { created, global = GlobalToObject(global) }), true,
            "application/json");
    }

    private async Task HandleValuesRequestAsync(IHttpContext ctx) {
        string json = await _valueHelper.GetAllAsJsonAsync();
        await ctx.WriteResponse(200, json);
    }

    internal async Task HandleValuesPatchRequestAsync(IHttpContext ctx) {
        string body;
        using (var reader = new StreamReader(ctx.Body, ctx.Encoding)) {
            body = await reader.ReadToEndAsync();
        }

        int patched = await _valueHelper.PatchFromJsonAsync(body);
        int code;
        string msg;
        switch (patched) {
            case -1:
                code = 400;
                msg = "Error patching values";
                break;
            case 0:
                code = 201;
                msg = "No patches needed";
                break;
            default:
                code = 200;
                msg = $"Patched {patched} Values";
                break;
        }

        await ctx.WriteResponse(code, msg);
    }

    private object BuildDataSummary(List<SubathonEvent> events) {
        var result = new Dictionary<string, object>();

        static string NormalizeTier(string meta) {
            return meta switch {
                "1000" => "T1",
                "2000" => "T2",
                "3000" => "T3",
                _ => meta
            };
        }

        IEnumerable<IGrouping<SubathonEventType?, SubathonEvent>> groups = events.GroupBy(e => e.EventType);

        foreach (IGrouping<SubathonEventType?, SubathonEvent> g in groups) {
            var key = g.Key!.ToString();
            if (key == null) continue;

            if (g.Key.IsCurrencyDonation()) {
                result[key] = g
                    .Where(e => !string.IsNullOrWhiteSpace(e.Currency))
                    .GroupBy(e => e.Currency ?? "")
                    .ToDictionary(
                        t => t.Key,
                        t => {
                            double sum = t.Sum(e =>
                                Utils.TryParseAmount(e.Value, out double amount)
                                    ? amount
                                    : 0
                            );
                            return Math.Round(sum, 2);
                        }
                    );
            }
            else if (g.Key.IsSubscription()) {
                result[key] = g.GroupBy(e => NormalizeTier(e.Value))
                    .ToDictionary(
                        t => t.Key,
                        t => t.Sum(x => x.Amount)
                    );
            }
            else if (g.Key.IsToken()) {
                result[key] = g.Sum(e => int.TryParse(e.Value, out int v) ? v : 0);
            }
            else if (g.Key.IsOrder()) {
                Dictionary<string, double> breakdown = g
                    .Where(e => !string.IsNullOrWhiteSpace(e.Currency))
                    .GroupBy(e => e.Currency ?? "")
                    .ToDictionary(
                        t => t.Key,
                        t => {
                            double sum = t.Sum(e =>
                                Utils.TryParseAmount(string.Equals(e.Value, "new", StringComparison.OrdinalIgnoreCase)
                                    ? "1"
                                    : e.Value, out double amount)
                                    ? amount
                                    : 0
                            );
                            return Math.Round(sum, 2);
                        }
                    );
                result[key] = new Dictionary<string, object> {
                    ["count"] = g.Count(),
                    ["breakdown"] = breakdown
                };
            }
            else {
                switch (g.Key) {
                    case SubathonEventType.TwitchFollow:
                        result[key] = g.Count();
                        break;

                    case SubathonEventType.TwitchRaid:
                        result[key] = new {
                            count = g.Count(),
                            total_viewers = g.Sum(e => int.TryParse(e.Value, out int v) ? v : 0)
                        };
                        break;
                }
            }
        }

        return result;
    }
}