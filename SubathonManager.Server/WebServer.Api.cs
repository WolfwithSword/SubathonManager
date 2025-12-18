using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SubathonManager.Core.Models;
using SubathonManager.Core.Events;
using SubathonManager.Core.Enums;
using SubathonManager.Integration;
using SubathonManager.Data;

namespace SubathonManager.Server;

public partial class WebServer
{
    private async Task<bool> HandleApiRequestAsync(HttpListenerContext ctx, string path)
    {
        if (path.StartsWith("/api/data/control") && ctx.Request.HasEntityBody && (ctx.Request.HttpMethod == "POST" || ctx.Request.HttpMethod == "PUT"))
        {
            return await HandleDataControlRequestAsync(ctx);
        }
        if (path.StartsWith("/api/data/"))
        {
            return await HandleDataRequestAsync(ctx, path.Replace("/api/data/", ""));
        }
        if (path.StartsWith("/api/select/", StringComparison.OrdinalIgnoreCase))
        {                    
            ctx.Response.StatusCode = 200;
            await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("OK"));
            ctx.Response.Close(); // fast close request, just a GET
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3)
            {
                string widgetId = parts[2];
                if (Guid.TryParse(widgetId, out var widgetGuid))
                {
                    WidgetEvents.RaiseSelectEditorWidget(widgetGuid);
                    return true;
                }
            }
            return false;
        }
        if (path.StartsWith("/api/update-", StringComparison.OrdinalIgnoreCase))
        {
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3)
            {
                string widgetId = parts[2];
                    
                string body;
                using (var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding))
                    body = await reader.ReadToEndAsync();
                var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body);
                    
                if (data == null)
                {
                    ctx.Response.StatusCode = 400;
                    await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("Invalid update data"));
                    ctx.Response.Close();
                    return true;
                }
                    
                var widgetHelper = new WidgetEntityHelper();
                bool success = false;
                if (path.StartsWith("/api/update-size/", StringComparison.OrdinalIgnoreCase))
                    success = await widgetHelper.UpdateWidgetScale(widgetId, data);
                else if (path.StartsWith("/api/update-position/", StringComparison.OrdinalIgnoreCase))
                    success = await widgetHelper.UpdateWidgetPosition(widgetId, data);
                
                if (success)
                {
                    ctx.Response.StatusCode = 200;
                    await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("OK"));
                    ctx.Response.Close();
                    return true;
                }
                ctx.Response.StatusCode = 404;
                await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("Widget Not Found"));
                ctx.Response.Close();
                return true;
            }
            ctx.Response.StatusCode = 400;
            await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("Invalid Widget ID"));
            ctx.Response.Close();
            return true;
        }
        ctx.Response.StatusCode = 400;
        await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("Invalid API Request"));
        ctx.Response.Close();
        return false;
    }

    private async Task<bool> HandleDataControlRequestAsync(HttpListenerContext ctx)
    {
        string body;
        using (var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding))
            body = await reader.ReadToEndAsync();
        
        var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>($"{body}");
        
        if (data == null)
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("Invalid control data"));
            ctx.Response.Close();
            return true;
        }

        SubathonEventType type = SubathonEventType.Unknown;
        if (!data.ContainsKey("type") || !data.TryGetValue("type", out JsonElement elem)
            || !Enum.TryParse(elem.GetString()!, ignoreCase: true, out type))
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("Invalid control data"));
            ctx.Response.Close();
            return true;
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
            ctx.Response.StatusCode = 400;
            await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("Invalid control data"));
            ctx.Response.Close();
            return true;
        }

        if (success)
        {
            ctx.Response.StatusCode = 200;
            await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("OK"));
            ctx.Response.Close();
            return success;
        }

        ctx.Response.StatusCode = 400;
        await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("Invalid API Request"));
        ctx.Response.Close();
        return false;
    }

    private async Task<bool> HandleDataRequestAsync(HttpListenerContext ctx, string path)
    {
        await using var db = await _factory.CreateDbContextAsync();
        SubathonData? subathon = await db.SubathonDatas.Include(s => s.Multiplier)
            .FirstOrDefaultAsync(s => s.IsActive);

        if (subathon != null && path.StartsWith("status"))
        {
            TimeSpan? multiplierRemaining = TimeSpan.Zero;
            if (subathon.Multiplier.Duration != null && subathon.Multiplier.Duration > TimeSpan.Zero
                                                     && subathon.Multiplier.Started != null)
            {
                DateTime? multEndTime = subathon.Multiplier.Started + subathon.Multiplier.Duration;
                multiplierRemaining = multEndTime! - DateTime.Now;
            }
  
            bool.TryParse(_config.Get("App", "ReverseSubathon", "False"), out bool isReverse);
            object response = new
            {
                millis_cumulated = subathon.MillisecondsCumulative,
                millis_elapsed = subathon.MillisecondsElapsed,
                millis_remaining = subathon.MillisecondsRemaining(isReverse),
                total_seconds = subathon.TimeRemainingRounded(isReverse).TotalSeconds,
                days = subathon.TimeRemainingRounded(isReverse).Days,
                hours = subathon.TimeRemainingRounded(isReverse).Hours,
                minutes = subathon.TimeRemainingRounded(isReverse).Minutes,
                seconds = subathon.TimeRemainingRounded(isReverse).Seconds,
                points = subathon.Points,
                is_paused = subathon.IsPaused,
                is_locked = subathon.IsLocked,
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

            ctx.Response.StatusCode = 200;
            await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(json));
            ctx.Response.Close();
            return true;
            
        }
        else if (subathon != null && path.StartsWith("amounts"))
        {
            var events = await db.SubathonEvents
                .Where(e => e.SubathonId == subathon.Id && e.ProcessedToSubathon)
                .Where(e => e.EventType != SubathonEventType.Command &&
                            e.EventType != SubathonEventType.Unknown)
                .ToListAsync();
            
            var simulated = events.Where(e => e.User == "SYSTEM" || e.User == "SIMULATED").ToList();
            var real = events.Where(e => e.User != "SYSTEM" && e.User != "SIMULATED").ToList();
            
            object response = new
            {
                simulated = BuildDataSummary(simulated),
                real = BuildDataSummary(real)
            };

            string json = JsonSerializer.Serialize(response, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            ctx.Response.StatusCode = 200;
            await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(json));
            ctx.Response.Close();
            return true;
        }

        ctx.Response.StatusCode = 500;
        await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("Invalid API Request"));
        ctx.Response.Close();
        return false;
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
            else
            {
                switch (g.Key)
                {
                    case SubathonEventType.TwitchCheer:
                        result[key] = g.Sum(e => int.TryParse(e.Value, out var v) ? v : 0);
                        break;

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