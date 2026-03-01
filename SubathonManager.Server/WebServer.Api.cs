using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SubathonManager.Core.Models;
using SubathonManager.Core.Events;
using SubathonManager.Core.Enums;
using SubathonManager.Integration;
using SubathonManager.Data;

namespace SubathonManager.Server;

public partial class WebServer
{
    private void SetupApiRoutes()
    {
        _routes.Add((new RouteKey("POST", "/api/data/control"),HandleDataControlRequestAsync ));
        _routes.Add((new RouteKey("PUT", "/api/data/control"),HandleDataControlRequestAsync ));
        
        _routes.Add((new RouteKey("GET", "/api/data/status"),HandleStatusRequestAsync));
        
        _routes.Add((new RouteKey("GET", "/api/data/amounts"),HandleAmountsRequestAsync ));
        
        _routes.Add((new RouteKey("GET", "/api/data/values"),HandleValuesRequestAsync ));
        
        _routes.Add((new RouteKey("PUT", "/api/data/values"),HandleValuesPatchRequestAsync));
        _routes.Add((new RouteKey("POST", "/api/data/values"),HandleValuesPatchRequestAsync ));
        _routes.Add((new RouteKey("PATCH", "/api/data/values"),HandleValuesPatchRequestAsync ));
        
        _routes.Add((new RouteKey("GET", "/api/select"),HandleSelectAsync));
        
        _routes.Add((new RouteKey("POST", "/api/update-position/"),HandleWidgetUpdateAsync ));
        
        _routes.Add((new RouteKey("POST", "/api/update-size/"),HandleWidgetUpdateAsync));
    }
    
    internal async Task HandleSelectAsync(IHttpContext ctx)
    {
        var path = ctx.Path;
        
        // Fast Close
        await ctx.WriteResponse(200, "OK");
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3)
        {
            string widgetId = parts[2];
            if (Guid.TryParse(widgetId, out var widgetGuid))
            {
                WidgetEvents.RaiseSelectEditorWidget(widgetGuid);
            }
        }
    }
    
    internal async Task HandleWidgetUpdateAsync(IHttpContext ctx)
    {;
        var path = ctx.Path;

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3)
        {
            string widgetId = parts[2];

            string body;
            using (var reader = new StreamReader(ctx.Body, ctx.Encoding))
                body = await reader.ReadToEndAsync();
            var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body);

            if (data == null)
            {
                await ctx.WriteResponse(400, "Invalid update data");
                return;
            }

            var widgetHelper = new WidgetEntityHelper(_factory, null);
            bool success = false;
            if (path.StartsWith("/api/update-size/", StringComparison.OrdinalIgnoreCase))
                success = await widgetHelper.UpdateWidgetScale(widgetId, data);
            else if (path.StartsWith("/api/update-position/", StringComparison.OrdinalIgnoreCase))
                success = await widgetHelper.UpdateWidgetPosition(widgetId, data);

            if (success)
            {
                await ctx.WriteResponse(200, "OK");
                return;
            }
            
            await ctx.WriteResponse(404, "Widget Not Found");
            return;
        }
        await ctx.WriteResponse(400, "Invalid Widget ID");
    }

    private async Task HandleDataControlRequestAsync(IHttpContext ctx)
    {
        string body;
        using (var reader = new StreamReader(ctx.Body, ctx.Encoding))
            body = await reader.ReadToEndAsync();

        var data = new Dictionary<string, JsonElement>();
        try
        {
            data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>($"{body}");
        }
        catch (JsonException ex)
        {
            _logger?.LogError(ex, "Invalid control data");
            await ctx.WriteResponse(400, "Invalid control data");
            return;
        }

        if (data == null || data.Count == 0)
        {
            await ctx.WriteResponse(400, "Invalid control data");
            return;
        }

        SubathonEventType type = SubathonEventType.Unknown;
        if (!data.ContainsKey("type") || !data.TryGetValue("type", out JsonElement elem)
            || !Enum.TryParse(elem.GetString()!, ignoreCase: true, out type))
        {
            
            await ctx.WriteResponse(400, "Invalid control data");
            return;
        }

        bool success = false;
        if (type == SubathonEventType.Command)
        {
            success = ExternalEventService.ProcessExternalCommand(data);
        }
        else if (((SubathonEventType?)type).IsCurrencyDonation() && ((SubathonEventType?)type).IsExternalType())
        {
            success = ExternalEventService.ProcessExternalDonation(data);
        }
        else if (((SubathonEventType?)type).IsSubOrMembershipType() && ((SubathonEventType?)type).IsExternalType())
        {
            success = ExternalEventService.ProcessExternalSub(data);
        }
        else
        {
            await ctx.WriteResponse(400, "Invalid control data");
            return;
        }

        if (success)
        {
            await ctx.WriteResponse(200, "OK");
            return;
        }
        await ctx.WriteResponse(400, "Invalid API Request");
    }


    internal async Task HandleStatusRequestAsync(IHttpContext ctx)
    {
        await using var db = await _factory.CreateDbContextAsync();
        SubathonData? subathon = await db.SubathonDatas.Include(s => s.Multiplier)
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.IsActive);
        TimeSpan? multiplierRemaining = TimeSpan.Zero;
        if (subathon == null)
        {
            await ctx.WriteResponse(400, "Invalid status request");
            return;
        }
        if (subathon.Multiplier.Duration != null && subathon.Multiplier.Duration > TimeSpan.Zero
                                                 && subathon.Multiplier.Started != null)
        {
            DateTime? multEndTime = subathon.Multiplier.Started + subathon.Multiplier.Duration;
            multiplierRemaining = multEndTime! - DateTime.Now;
        }

        object response = new
        {
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
            multiplier = new
            {
                running = subathon.Multiplier.IsRunning(),
                apply_points = subathon.Multiplier.ApplyToPoints,
                apply_time = subathon.Multiplier.ApplyToSeconds,
                is_from_hypetrain = subathon.Multiplier.FromHypeTrain,
                started_at = subathon.Multiplier.Started,
                duration_seconds = Math.Round(subathon.Multiplier.Duration?.TotalSeconds ?? 0),
                duration_remaining_seconds = Math.Round(multiplierRemaining.Value.TotalSeconds),
            }
        };

        string json = JsonSerializer.Serialize(response, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        await ctx.WriteResponse(200, json);
    }

    internal async Task HandleAmountsRequestAsync(IHttpContext ctx)
    {
        await using var db = await _factory.CreateDbContextAsync();
        SubathonData? subathon = await db.SubathonDatas.Include(s => s.Multiplier)
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.IsActive);
        if (subathon == null)
        {
            await ctx.WriteResponse(400, "Invalid status request");
            return;
        }
        var events = await db.SubathonEvents
            .Where(e => e.SubathonId == subathon.Id && e.ProcessedToSubathon)
            .Where(e => e.EventType != SubathonEventType.Command &&
                        e.EventType != SubathonEventType.Unknown)
            .ToListAsync();
            
        var simulated = events.Where(e => e.User != null && (e.User.StartsWith("SYSTEM") || e.User.StartsWith("SIMULATED"))).ToList();
        var real = events.Where(e => e.User != null && !e.User.StartsWith("SYSTEM") && !e.User.StartsWith("SIMULATED")).ToList();
            
        object response = new
        {
            simulated = BuildDataSummary(simulated),
            real = BuildDataSummary(real)
        };

        string json = JsonSerializer.Serialize(response, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        await ctx.WriteResponse(200, json);
    }
    
    private async Task HandleValuesRequestAsync(IHttpContext ctx)
    {
        var json = await _valueHelper.GetAllAsJsonAsync();
        await ctx.WriteResponse(200, json);
    }

    internal async Task HandleValuesPatchRequestAsync(IHttpContext ctx)
    {
        string body;
        using (var reader = new StreamReader(ctx.Body, ctx.Encoding))
            body = await reader.ReadToEndAsync();
        int patched = await _valueHelper.PatchFromJsonAsync(body);
        int code;
        string msg;
        switch (patched)
        {
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

    private object BuildDataSummary(List<SubathonEvent> events)
    {
        var result = new Dictionary<string, object>();
        
        static string NormalizeTier(string meta)
        {
            return meta switch
            {
                "1000" => "T1",
                "2000" => "T2",
                "3000" => "T3",
                _ => meta
            };
        }

        var groups = events.GroupBy(e => e.EventType);

        foreach (var g in groups)
        {
            string? key = g.Key!.ToString();
            if (key == null) continue;

            if (g.Key.IsCurrencyDonation())
            {
                result[key] = g
                    .Where(e => !string.IsNullOrWhiteSpace(e.Currency))  
                    .GroupBy(e => e.Currency ?? "")
                    .ToDictionary(
                        t => t.Key,
                        t =>
                        {
                            double sum = t.Sum(e =>
                                double.TryParse(e.Value, out var amount)
                                    ? amount
                                    : 0
                            );
                            return Math.Round(sum, 2);
                        }
                    );
            }
            else if (g.Key.IsSubOrMembershipType())
            {
                result[key] = g.GroupBy(e => NormalizeTier(e.Value))
                    .ToDictionary(
                        t => t.Key,
                        t => t.Sum(x => x.Amount)
                    );
            }
            else if (g.Key.IsCheerType())
            {
                result[key] = g.Sum(e => int.TryParse(e.Value, out var v) ? v : 0);
            }
            else if (g.Key.IsOrderType())
            {
                var breakdown = g
                    .Where(e => !string.IsNullOrWhiteSpace(e.Currency))  
                    .GroupBy(e => e.Currency ?? "")
                    .ToDictionary(
                        t => t.Key,
                        t =>
                        {
                            double sum = t.Sum(e =>
                                double.TryParse(string.Equals(e.Value, "new", StringComparison.OrdinalIgnoreCase) 
                                    ? "1" : e.Value, out var amount)
                                    ? amount
                                    : 0
                            );
                            return Math.Round(sum, 2);
                        }
                    );
                result[key] = new Dictionary<string, object>
                {
                    ["count"] = g.Count(),
                    ["breakdown"] = breakdown
                };
            }
            else
            {
                switch (g.Key)
                {
                    case SubathonEventType.TwitchFollow:
                        result[key] = g.Count();
                        break;

                    case SubathonEventType.TwitchRaid:
                        result[key] = new
                        {
                            count = g.Count(),
                            total_viewers = g.Sum(e => int.TryParse(e.Value, out var v) ? v : 0)
                        };
                        break;
                }
            }
        }
        return result;
    }
    
}