using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Models;

namespace SubathonManager.Services;

public class DiscordWebhookService : IDisposable
{
    private readonly ConcurrentQueue<SubathonEvent> _eventQueue = new();
    private readonly CancellationTokenSource _cts = new();

    private string? _eventWebhookUrl; // from config
    private string? _webhookUrl; // from config
    private readonly TimeSpan _flushInterval = TimeSpan.FromMinutes(1);
    private readonly int _maxMsgPerMinute = 15;
    private Task? _backgroundTask;

    private List<SubathonEventType> _auditEventTypes = new();
    private bool _doSimulatedEvents = false;
    
    private readonly ILogger? _logger;
    private readonly IConfig _config;

    private const string AppUsername = "Subathon Manager";

    private const string AppAvatarUrl =
        "https://raw.githubusercontent.com/WolfwithSword/SubathonManager/refs/heads/main/assets/icon.png";

    // todo handle rate limit and retry_after
    public DiscordWebhookService(ILogger<DiscordWebhookService>? logger, IConfig config)
    {
        _logger = logger;
        _config = config;
        LoadFromConfig();
        SubathonEvents.SubathonEventProcessed += OnSubathonEventProcessed;
        SubathonEvents.SubathonEventsDeleted += OnSubathonEventDeleted;
        ErrorMessageEvents.ErrorEventOccured += SendErrorEvent;
        ErrorMessageEvents.SendCustomEvent += OnCustomEvent;
        _backgroundTask = Task.Run(ProcessQueueAsync);
    }

    public void LoadFromConfig()
    {
        _eventWebhookUrl = _config.Get("Discord", "Events.WebhookUrl");
        _webhookUrl = _config.Get("Discord", "WebhookUrl");
        
        _auditEventTypes.Clear();
        foreach (SubathonEventType type in Enum.GetValues(typeof(SubathonEventType)))
        {
            bool.TryParse(_config.Get("Discord", $"Events.Log.{type}", "false"), out bool result);
            if (result)
                _auditEventTypes.Add(type);
        }
        bool.TryParse(_config.Get("Discord", "Events.Log.Simulated", "false"), out _doSimulatedEvents);
    }

    private void OnSubathonEventProcessed(SubathonEvent? subathonEvent, bool effective)
    {
        if (subathonEvent == null || string.IsNullOrEmpty(_eventWebhookUrl) ) return;
        if (!_auditEventTypes.Contains(subathonEvent.EventType ?? SubathonEventType.Unknown)) return;
        if (subathonEvent.Source == SubathonEventSource.Simulated && !_doSimulatedEvents) return;
        // only queue events we care about based on settings
        // so not-logged ones don't clog the queue downstream
        _eventQueue.Enqueue(subathonEvent);
    }

    private void OnCustomEvent(string message)
    {
        if (string.IsNullOrEmpty(_eventWebhookUrl) ) return;
        var payload = new
        {
            username = AppUsername,
            avatar_url = AppAvatarUrl,
            embeds = new[]
            {
                new
                {
                    title = "INFO",
                    description = $"**Test**\n{message}",
                    color = 0xE3E3E3 ,
                    timestamp = DateTime.Now.ToString("o")
                }
            }
        };
        Task.Run(async () => { await SendWebhookAsync(payload, _eventWebhookUrl); });
    }

    private void OnSubathonEventDeleted(List<SubathonEvent>? subathonEvents)
    {
        if (subathonEvents?.Count == 1)
        {
            SubathonEvent subathonEvent = subathonEvents.Single().ShallowClone();
            if (string.IsNullOrEmpty(_eventWebhookUrl)) return;
            if (!_auditEventTypes.Contains(subathonEvent.EventType ?? SubathonEventType.Unknown)) return;
            if (subathonEvent.Source == SubathonEventSource.Simulated && !_doSimulatedEvents) return;


            subathonEvent.Value += " [DELETED]";
            _eventQueue.Enqueue(subathonEvent);
        }
        else if (subathonEvents?.Count > 1)
        {
            double totalPoints = 0;
            double totalSeconds = 0;
            foreach (var subathonEvent in subathonEvents)
            {
                totalPoints += subathonEvent.GetFinalPointsValue();
                totalSeconds += subathonEvent.GetFinalSecondsValueRaw();
            }
            
            var embed = new
            {
                title=$"Deleted {subathonEvents.Count} Events",
                description=$"**Total Seconds:** {Math.Round(totalSeconds, 2)}s\n**Total Points:** {totalPoints}",
                color = 0x86ACBD,
                timestamp = DateTime.Now.ToUniversalTime().ToString("o")
            };

            var payload = new
            {
                username = AppUsername,
                avatar_url = AppAvatarUrl,
                embeds = new[] {embed}
            };

            Task.Run(() =>
                SendWebhookAsync(payload, _eventWebhookUrl)
            );
        }
    }

    private async Task ProcessQueueAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(_flushInterval, _cts.Token);
                await FlushQueueAsync();
            }
        }
        catch (OperationCanceledException ex)
        {
            _logger?.LogError(ex, ex.Message);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, ex.Message);
        }
    }

    private async Task FlushQueueAsync()
    {
        if (_eventQueue.IsEmpty) return;
        var sb = new StringBuilder();
        var events = new List<SubathonEvent>();
        while (events.Count < (_maxMsgPerMinute * 10) && _eventQueue.TryDequeue(out var subathonEvent) )
        {
            events.Add(subathonEvent);
        }
        if (events.Count == 0) return;
        
        // max 10 embeds per msg
        // rate limit is ~30msgs/min
        // we do half that to be safe
        foreach (var batch in events.Chunk(10))
        {
            var embeds = batch.Select(e => new
            {
                title=$"{e.EventType}{(e.Value.EndsWith(" [DELETED]") ? " - Deleted" : (e.EventType == SubathonEventType.Command ? $" - {e.Command}" : 
                    (e.Source == SubathonEventSource.Simulated ? " - Simulated" :  "")))}",
                description=BuildEventDescription(e),
                color = (e.ProcessedToSubathon && e.Value.EndsWith(" [DELETED]") ? 0x691911 : (e.ProcessedToSubathon ? 0x00ff88 : 0xffaa55)),
                timestamp = e.EventTimestamp.ToUniversalTime().ToString("o"),
                footer = new {text = $"{e.Source} {e.Id}\nCurrent: {TimeSpan.FromSeconds(e.CurrentTime)} | {e.CurrentPoints}"}
            });

            var payload = new
            {
                username = AppUsername,
                avatar_url = AppAvatarUrl,
                embeds
            };

            await SendWebhookAsync(payload, _eventWebhookUrl);
            await Task.Delay(1500, _cts.Token);
        }
    }

    private string BuildEventDescription(SubathonEvent e)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"**User:** {e.User}");
        var val = e.Value.Replace("[DELETED]", "").Trim();
        if (e.Currency == "sub")
        {
            switch (val)
            {
                case "1000":
                    val = "Tier 1";
                    break;
                case "2000":
                    val = "Tier 2";
                    break;
                case "3000":
                    val = "Tier 3";
                    break;
            }
        }

        if (!(e.Command == SubathonCommandType.Pause || e.Command == SubathonCommandType.Resume ||
              e.Command == SubathonCommandType.Lock || e.Command == SubathonCommandType.Unlock))
        {
            sb.AppendLine($"**Value:** {val} {(string.IsNullOrEmpty(e.Currency) ? "" : e.Currency)}");
            if (e.Command != SubathonCommandType.SetMultiplier && e.Command != SubathonCommandType.StopMultiplier)
            {
                sb.AppendLine($"**Seconds:** {Math.Round(e.GetFinalSecondsValueRaw(), 2)} | **Points:** {e.GetFinalPointsValue()}");
                if (e.Command == SubathonCommandType.None)
                    sb.AppendLine($"**Multipliers:** x{e.MultiplierSeconds} time | x{e.MultiplierPoints} pts");
            }
        }

        if (e.EventType == SubathonEventType.TwitchGiftSub)
        {
            sb.AppendLine($"**Amt:** x{e.Amount}");
        }
        return sb.ToString();
    }
    
    private async Task SendWebhookAsync(object payload, string? url)
    {
        if (string.IsNullOrEmpty(url)) return;
        try
        {
            using var http = new HttpClient();
            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await http.PostAsync(url, content, _cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogError($"Webhook failed: {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, ex.Message);
        }
    }

    public async Task StopAsync()
    {
        _cts.Cancel();
        if (_backgroundTask != null)
            await _backgroundTask;
        await FlushQueueAsync();
    }

    public void Dispose()
    {
        _cts.Cancel();
        SubathonEvents.SubathonEventProcessed -= OnSubathonEventProcessed;
        SubathonEvents.SubathonEventsDeleted -= OnSubathonEventDeleted;
        ErrorMessageEvents.ErrorEventOccured -= SendErrorEvent;
        ErrorMessageEvents.SendCustomEvent -= OnCustomEvent;
        _cts.Dispose();
    }

    public void SendErrorEvent(string level, string source, string message, DateTime time)
    {
        if (string.IsNullOrEmpty(_webhookUrl)) return;
        try
        {
            var payload = new
            {
                username = AppUsername,
                avatar_url = AppAvatarUrl,
                embeds = new[]
                {
                    new
                    {
                        title = $"{level}",
                        description = $"**{source}**\n{message}",
                        color = level == "WARN" ? 0xFF7B00 : (level == "ERROR" ? 0xDE360D : 0xE3E3E3 ),
                        timestamp = time.ToString("o")
                    }
                }
            };

            Task.Run(async () => { await SendWebhookAsync(payload, _webhookUrl); });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, $"Failed to send webhook log: {ex.Message}");
        }
    }
    
    public async Task SendErrorLogAsync(string message, Exception? ex = null)
    {
        if (string.IsNullOrEmpty(_webhookUrl)) return;
        try
        {
            var payload = new
            {
                username = AppUsername,
                avatar_url = AppAvatarUrl,
                embeds = new[]
                {
                    new
                    {
                        title = "⚠️ Error ⚠️",
                        description = BuildErrorDescription(message, ex),
                        color = 0xff5555,
                        timestamp = DateTime.UtcNow.ToString("o")
                    }
                }
            };

            await SendWebhookAsync(payload, _webhookUrl);
        }
        catch (Exception ex2)
        {
            _logger?.LogError(ex2, $"Failed to send webhook log: {ex2.Message}");
        }
    }
    
    private string BuildErrorDescription(string message, Exception? ex)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"**Message:** {message}");
        if (ex != null)
        {
            sb.AppendLine();
            sb.AppendLine($"**Exception:** `{ex.GetType().Name}`");
            sb.AppendLine($"**Details:** {ex.Message}");
            if (!string.IsNullOrWhiteSpace(ex.StackTrace))
                sb.AppendLine($"```{ex.StackTrace.Split('\n').Take(5).Aggregate((a, b) => a + "\n" + b)}```");
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine("_No exception details provided._");
        }

        return sb.ToString();
    }
    
}

internal static class LinqExtensions
{
    public static IEnumerable<List<T>> Chunk<T>(this IEnumerable<T> source, int size)
    {
        var list = new List<T>(size);
        foreach (var item in source)
        {
            list.Add(item);
            if (list.Count >= size)
            {
                yield return list;
                list = new List<T>(size);
            }
        }

        if (list.Count > 0)
            yield return list;
    }
}
