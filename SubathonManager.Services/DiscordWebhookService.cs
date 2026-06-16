using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.Services;

public class DiscordWebhookService : IDisposable, IAppService
{
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly ConcurrentQueue<SubathonEvent> _eventQueue = new();
    private readonly ConcurrentQueue<SubathonValueDto> _configQueue = new();
    private readonly ConcurrentQueue<WheelLogEntry> _wheelQueue = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly CancellationTokenSource _ctsConfig = new();
    private readonly CancellationTokenSource _ctsWheel = new();

    private string? _eventWebhookUrl; // from config
    private string? _webhookUrl; // from config
    private string? _wheelWebhookUrl; // from config
    private readonly TimeSpan _flushInterval = TimeSpan.FromMinutes(1);
    private readonly int _maxMsgPerMinute = 15;
    private Task? _backgroundTask;
    private Task? _backgroundConfigTask;
    private Task? _backgroundWheelTask;

    private List<SubathonEventType> _auditEventTypes = new();
    private bool _doSimulatedEvents = false;
    private bool _doRemoteValuePatches = false;
    private bool _doWheelSpinEvents = false;
    
    private readonly ILogger? _logger;
    private readonly IConfig _config;
    private readonly CurrencyService _currencyService;

    private const string AppUsername = "Subathon Manager";

    private const string AppAvatarUrl =
        "https://raw.githubusercontent.com/WolfwithSword/SubathonManager/refs/heads/main/assets/icon.png";

    public DiscordWebhookService(ILogger<DiscordWebhookService>? logger, IConfig config, CurrencyService currencyService, IHttpClientFactory? httpClientFactory = null)
    {
        _logger = logger;
        _config = config;
        _currencyService = currencyService;
        _httpClientFactory = httpClientFactory;
        LoadFromConfig();
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        SubathonEvents.SubathonEventProcessed += OnSubathonEventProcessed;
        SubathonEvents.SubathonEventsDeleted += OnSubathonEventDeleted;
        ErrorMessageEvents.ErrorEventOccured += SendErrorEvent;
        ErrorMessageEvents.SendCustomEvent += OnCustomEvent;
        SubathonEvents.SubathonValuesPatched += OnSubathonConfigValuesPatched;
        WheelEvents.WheelSpinResult += OnWheelSpinResult;
        WheelEvents.WheelSpinStatusChanged += OnWheelSpinStatusChanged;
        _backgroundTask = Task.Run(ProcessQueueAsync, cancellationToken);
        _backgroundConfigTask = Task.Run(ProcessValueQueueAsync, cancellationToken);
        _backgroundWheelTask = Task.Run(ProcessWheelQueueAsync, cancellationToken);
        return Task.CompletedTask;
    }

    public void LoadFromConfig()
    {
        _eventWebhookUrl = _config.Get("Discord", "Events.WebhookUrl", "");
        _webhookUrl = _config.Get("Discord", "WebhookUrl", "");
        _wheelWebhookUrl = _config.Get("Discord", "Wheel.WebhookUrl", "");

        _auditEventTypes.Clear();
        foreach (SubathonEventType type in Enum.GetValues<SubathonEventType>())
        {
            if (_config.GetBool("Discord", $"Events.Log.{type}", false))
                _auditEventTypes.Add(type);
        }

        _doSimulatedEvents = _config.GetBool("Discord", "Events.Log.Simulated", false);
        _doRemoteValuePatches = _config.GetBool("Discord", "Events.Log.RemoteConfig", false);
        _doWheelSpinEvents = _config.GetBool("Discord", "Wheel.Log.Enabled", false);
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

    private void OnSubathonConfigValuesPatched(List<SubathonValueDto> patched)
    {
        if (!_doRemoteValuePatches) return;
        foreach(var v in patched)
            _configQueue.Enqueue(v);
    }

    private string MakeUsername()
    {
        string username = $"{AppUsername} - {AppServices.AppVersion}";
        return username.Substring(0, Math.Min(username.Length, 42));
    }

    private void OnCustomEvent(string message)
    {
        if (string.IsNullOrEmpty(_eventWebhookUrl) ) return;
        var payload = new
        {
            username = MakeUsername(),
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
        if (subathonEvents?.Count > 1 || subathonEvents is [{ Source: SubathonEventSource.Simulated }])
        {
            double totalPoints = 0;
            double totalSeconds = 0;
            double totalMoney = 0;
            var currency = _config.Get("Currency", "Primary", "USD");
            foreach (var subathonEvent in subathonEvents)
            {
                totalPoints += subathonEvent.GetFinalPointsValue();
                totalSeconds += subathonEvent.GetFinalSecondsValueRaw();
                if (subathonEvent.EventType.IsCurrencyDonation() &&
                    _currencyService.IsValidCurrency(subathonEvent.Currency))
                {
                    double result = Task.Run(async () =>
                    {
                        return await _currencyService.ConvertAsync(
                            double.Parse(subathonEvent.Value),
                            subathonEvent.Currency!, currency);
                    }).GetAwaiter().GetResult();
                    totalMoney += result;
                }
            }
            
            var embed = new
            {
                title=$"Deleted {subathonEvents.Count} Events",
                description=$"**Seconds:** {Math.Round(totalSeconds, 2)}s\n**Points:** {totalPoints}\n**Dollars {currency}:** {Math.Round(totalMoney, 2)}",
                color = 0x86ACBD,
                timestamp = DateTime.Now.ToUniversalTime().ToString("o")
            };

            var payload = new
            {
                username = MakeUsername(),
                avatar_url = AppAvatarUrl,
                embeds = new[] {embed}
            };

            Task.Run(() =>
                SendWebhookAsync(payload, _eventWebhookUrl)
            );
        }
        
        if (subathonEvents?.Count == 1)
        {
            SubathonEvent subathonEvent = subathonEvents.Single().ShallowClone();
            if (string.IsNullOrEmpty(_eventWebhookUrl)) return;
            if (!_auditEventTypes.Contains(subathonEvent.EventType ?? SubathonEventType.Unknown)) return;
            if (subathonEvent.Source == SubathonEventSource.Simulated && !_doSimulatedEvents) return;


            subathonEvent.Value += " [DELETED]";
            _eventQueue.Enqueue(subathonEvent);
        }
    }

    private async Task ProcessValueQueueAsync()
    {
        try
        {
            while (!_ctsConfig.IsCancellationRequested)
            {
                await Task.Delay(_flushInterval, _ctsConfig.Token);
                await FlushConfigQueueAsync();
            }
        }
        catch (OperationCanceledException ex)
        {
            _logger?.LogDebug(ex, ex.Message);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, ex.Message);
        }  
    }
    
    [ExcludeFromCodeCoverage]
    private async Task FlushConfigQueueAsync()
    {
        if (_configQueue.IsEmpty) return;
        var events = new List<SubathonValueDto>();
        // we will hard limit this to 5msg per min. 10 embeds per msg
        while (events.Count < (5 * 10) && _configQueue.TryDequeue(out var sValue) )
        {
            events.Add(sValue);
        }
        if (events.Count == 0) return;
        
        foreach (var batch in events.Chunk(10))
        {
            var embeds = batch.Select(e => new
            {
                title=$"{e.Source} - {e.EventType}" + (string.IsNullOrWhiteSpace(e.Meta) ? "" : $" [{e.Meta}]"),
                description=$"Config Updated Remotely: {e.ToValueString()}",
                color = 0xffaa55
            });

            var payload = new
            {
                username = MakeUsername(),
                avatar_url = AppAvatarUrl,
                embeds
            };

            await SendWebhookAsync(payload, _webhookUrl);
            await Task.Delay(1500, _ctsConfig.Token);
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
            _logger?.LogDebug(ex, ex.Message);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, ex.Message);
        }
    }

    [ExcludeFromCodeCoverage]
    private async Task FlushQueueAsync()
    {
        if (_eventQueue.IsEmpty) return;
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
                username = MakeUsername(),
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
            val = val switch
            {
                "1000" => "Tier 1",
                "2000" => "Tier 2",
                "3000" => "Tier 3",
                _ => val
            };
        }

        if (e.Command is not (SubathonCommandType.Pause or SubathonCommandType.Resume or SubathonCommandType.Lock or SubathonCommandType.Unlock))
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
    
    private async Task SendWebhookAsync(object payload, string? url, int retryCount = 0)
    {
        if (string.IsNullOrEmpty(url)) return;
        try
        {
            var http = _httpClientFactory?.CreateClient(nameof(DiscordWebhookService));
            if (http == null) return;
            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await http.PostAsync(url, content, _cts.Token);
            string rc = "";

            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests && retryCount < 3)
            {
                rc = await response.Content.ReadAsStringAsync();
                double retryAfter = 1.0;
                try
                {
                    using var doc = JsonDocument.Parse(rc);
                    if (doc.RootElement.TryGetProperty("retry_after", out var ra))
                        retryAfter = ra.GetDouble();
                }
                catch {/**/}

                int delayMs = (int)(retryAfter * 1000) + 200;
                _logger?.LogWarning("Discord rate limited, retrying in {DelayMs}ms, attempt #{Attempt}", delayMs, retryCount);
                await Task.Delay(delayMs, _cts.Token);
                await SendWebhookAsync(payload, url, retryCount + 1);
                return;
            }

            if (!response.IsSuccessStatusCode)
            {
                rc = await response.Content.ReadAsStringAsync();
                _logger?.LogError("Webhook failed: {ResponseStatusCode} - {Rc}", response.StatusCode, rc);
            }
        }
        catch (OperationCanceledException) { /**/ }
        catch (Exception ex)
        {
            _logger?.LogError(ex, ex.Message);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _cts.CancelAsync();
        if (_backgroundTask != null)
            await _backgroundTask;
        await _ctsConfig.CancelAsync();
        if (_backgroundConfigTask != null)
            await _backgroundConfigTask;
        await _ctsWheel.CancelAsync();
        if (_backgroundWheelTask != null)
            await _backgroundWheelTask;
        await FlushQueueAsync();
        await FlushConfigQueueAsync();
        await FlushWheelQueueAsync();
    }

    public void Dispose()
    {
        if (!_cts.IsCancellationRequested)
            _cts.Cancel();
        if (!_ctsConfig.IsCancellationRequested)
            _ctsConfig.Cancel();
        if (!_ctsWheel.IsCancellationRequested)
            _ctsWheel.Cancel();

        SubathonEvents.SubathonEventProcessed -= OnSubathonEventProcessed;
        SubathonEvents.SubathonEventsDeleted -= OnSubathonEventDeleted;
        ErrorMessageEvents.ErrorEventOccured -= SendErrorEvent;
        ErrorMessageEvents.SendCustomEvent -= OnCustomEvent;
        SubathonEvents.SubathonValuesPatched -= OnSubathonConfigValuesPatched;
        WheelEvents.WheelSpinResult -= OnWheelSpinResult;
        WheelEvents.WheelSpinStatusChanged -= OnWheelSpinStatusChanged;
        _cts.Dispose();
        _ctsConfig.Dispose();
        _ctsWheel.Dispose();
    }

    public void SendErrorEvent(string level, string source, string message, DateTime time)
    {
        if (string.IsNullOrEmpty(_webhookUrl)) return;
        try
        {
            var payload = new
            {
                username = MakeUsername(),
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

    [ExcludeFromCodeCoverage]
    public async Task SendErrorLogAsync(string message, Exception? ex = null)
    {
        if (string.IsNullOrEmpty(_webhookUrl)) return;
        try
        {
            var payload = new
            {
                username = MakeUsername(),
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

    private void OnWheelSpinResult(WheelSet wheel, WheelItem? item, WheelSpinHistory history, int spinsOwed)
    {
        if (string.IsNullOrEmpty(_wheelWebhookUrl) || !_doWheelSpinEvents) return;
        _wheelQueue.Enqueue(new WheelLogEntry(wheel.Name, item, history, spinsOwed, false));
    }

    private void OnWheelSpinStatusChanged(WheelSpinHistory history, int spinsOwed)
    {
        if (string.IsNullOrEmpty(_wheelWebhookUrl) || !_doWheelSpinEvents) return;
        _wheelQueue.Enqueue(new WheelLogEntry(history.LinkedWheel?.Name ?? "Unknown", history.LinkedItem, history, spinsOwed, true));
    }

    private async Task ProcessWheelQueueAsync()
    {
        try
        {
            while (!_ctsWheel.IsCancellationRequested)
            {
                await Task.Delay(_flushInterval, _ctsWheel.Token);
                await FlushWheelQueueAsync();
            }
        }
        catch (OperationCanceledException ex)
        {
            _logger?.LogDebug(ex, ex.Message);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, ex.Message);
        }
    }

    [ExcludeFromCodeCoverage]
    private async Task FlushWheelQueueAsync()
    {
        if (_wheelQueue.IsEmpty) return;
        var entries = new List<WheelLogEntry>();
        while (entries.Count < (_maxMsgPerMinute * 10) && _wheelQueue.TryDequeue(out var entry))
            entries.Add(entry);
        if (entries.Count == 0) return;

        foreach (var batch in entries.Chunk(10))
        {
            var embeds = batch.Select(e => new
            {
                title = e.IsStatusChange
                    ? $"WheelSpin Status Change - {e.WheelName}"
                    : $"WheelSpin - {e.WheelName}",
                description = BuildWheelLogDescription(e),
                color = WheelStatusColor(e.History.Status),
                timestamp = e.History.UpdatedAt.ToUniversalTime().ToString("o"),
                footer = new { text = $"Spin {e.History.Id}\nStatus: {e.History.Status}" }
            });

            var payload = new
            {
                username = MakeUsername(),
                avatar_url = AppAvatarUrl,
                embeds
            };

            await SendWebhookAsync(payload, _wheelWebhookUrl);
            await Task.Delay(1500, _ctsWheel.Token);
        }
    }

    private string BuildWheelLogDescription(WheelLogEntry e)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"**Item:** {e.Item?.Text ?? "(unknown)"}");
        sb.AppendLine($"**Quantity Remaining:** {(e.Item?.IsInfinite ?? false ? "∞" : e.Item?.Quantity.ToString() ?? "?")}");
        var action = e.Item?.Action;
        sb.AppendLine($"**Action:** {(action != null ? action.ActionType.ToString() : "Manual")}");
        if (action != null && !string.IsNullOrEmpty(action.Parameter))
            sb.AppendLine($"**Parameter:** {action.Parameter}");
        sb.AppendLine($"**Spins Owed Remaining:** {e.SpinsOwed}");
        sb.AppendLine($"**Status:** {e.History.Status}");
        return sb.ToString();
    }

    private static int WheelStatusColor(WheelSpinHistoryStatus status) => status switch
    {
        WheelSpinHistoryStatus.Done      => 0x00ff88,
        WheelSpinHistoryStatus.Cancelled => 0xFF6B6B,
        _                                => 0x6495ED
    };

    private readonly record struct WheelLogEntry(string WheelName, WheelItem? Item, WheelSpinHistory History, int SpinsOwed, bool IsStatusChange);

}


[ExcludeFromCodeCoverage]
internal static class LinqExtensions
{
    public static IEnumerable<List<T>> Chunk<T>(this IEnumerable<T> source, int size)
    {
        var list = new List<T>(size);
        foreach (var item in source)
        {
            list.Add(item);
            if (list.Count < size) continue;
            yield return list;
            list = new List<T>(size);
        }

        if (list.Count > 0)
            yield return list;
    }
}
