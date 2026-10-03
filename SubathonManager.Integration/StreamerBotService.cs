using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Json;
using System.Text.Json;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Objects;

namespace SubathonManager.Integration;

[ExcludeFromCodeCoverage]
public sealed record StreamerBotActionInfo(Guid Id, string Name, string Group) {
    public string DisplayName => string.IsNullOrWhiteSpace(Group) ? Name : $"{Group} / {Name}";
}

// Separate from the SB extension
// Communicates over the HTTP Server api in SB
public class StreamerBotService(
    ILogger<StreamerBotService>? logger,
    IConfig config,
    IHttpClientFactory httpClientFactory,
    ITimerService timerService) : IAppService, IActionStepRunner {
    public const string ConfigSection = "StreamerBot";
    public const string DefaultHttpUrl = "http://127.0.0.1:7474";
    public const string ArgumentPrefix = "subathonmanager";
    public const string HttpService = "Http";
    private const string HttpCheckTimerKey = "StreamerBot.HttpCheck";
    private static readonly TimeSpan HttpCheckInterval = TimeSpan.FromSeconds(120);

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private bool? _lastHttpStatus;
    private bool _lastHttpEnabled;

    public bool HttpEnabled => config.GetBool(ConfigSection, "Http.Enabled");

    public string HttpUrl {
        get {
            string url = (config.Get(ConfigSection, "Http.Url", DefaultHttpUrl) ?? "").Trim().TrimEnd('/');
            return string.IsNullOrWhiteSpace(url) ? DefaultHttpUrl : url;
        }
    }

    public IReadOnlyCollection<ActionStepType> StepTypes { get; } = [ActionStepType.StreamerBotAction];

    private HttpClient CreateClient() {
        HttpClient client = httpClientFactory.CreateClient(nameof(StreamerBotService));
        client.Timeout = TimeSpan.FromSeconds(5);
        return client;
    }

    public Task StartAsync(CancellationToken ct = default) {
        timerService.Register(HttpCheckTimerKey, HttpCheckInterval, () => _ = CheckHttpAsync(ct));
        _ = Task.Run(() => CheckHttpAsync(ct), ct);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default) {
        timerService.Unregister(HttpCheckTimerKey);
        return Task.CompletedTask;
    }

    public async Task CheckHttpAsync(CancellationToken ct = default) {
        if (HttpEnabled) await GetActionsAsync(ct);
        else ReportHttpStatus(false);
    }

    private void ReportHttpStatus(bool reachable) {
        bool enabled = HttpEnabled;
        if (_lastHttpStatus == reachable && _lastHttpEnabled == enabled) return;
        _lastHttpStatus = reachable;
        _lastHttpEnabled = enabled;
        IntegrationEvents.RaiseConnectionUpdate(new IntegrationConnection {
            Source = SubathonEventSource.StreamerBot,
            Service = HttpService,
            Name = HttpUrl,
            Status = reachable && enabled,
            Configured = enabled
        });
    }

    public async Task<IReadOnlyList<StreamerBotActionInfo>?> GetActionsAsync(CancellationToken ct = default) {
        try {
            using HttpClient client = CreateClient();
            ActionList? list = await client.GetFromJsonAsync<ActionList>($"{HttpUrl}/GetActions", JsonOptions, ct);
            ReportHttpStatus(true);
            return (list?.Actions ?? [])
                .Where(a => a.Id != Guid.Empty)
                .Select(a => new StreamerBotActionInfo(a.Id, a.Name ?? "", a.Group ?? ""))
                .OrderBy(a => a.Group, StringComparer.OrdinalIgnoreCase)
                .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) {
            if (!ct.IsCancellationRequested) ReportHttpStatus(false);
            if (logger?.IsEnabled(LogLevel.Debug) ?? false)
                logger?.LogDebug("[StreamerBot] GetActions failed: {Message}", ex.Message);
            return null;
        }
    }

    public async Task<bool> DoActionAsync(Guid actionId, string? actionName, IReadOnlyDictionary<string, string> args,
        CancellationToken ct = default) {
        try {
            var payload = new {
                action = new { id = actionId, name = actionName },
                args
            };
            using HttpClient client = CreateClient();
            using HttpResponseMessage response = await client.PostAsJsonAsync($"{HttpUrl}/DoAction", payload, ct);
            ReportHttpStatus(true);
            if (response.IsSuccessStatusCode) return true;
            logger?.LogWarning("[StreamerBot] Running action {Action} failed with {Status}", actionName ?? $"{actionId}",
                (int)response.StatusCode);
            return false;
        }
        catch (Exception ex) {
            if (!ct.IsCancellationRequested) ReportHttpStatus(false);
            logger?.LogWarning("[StreamerBot] Running action {Action} failed: {Message}", actionName ?? $"{actionId}",
                ex.Message);
            return false;
        }
    }

    public async Task<bool> RunStepAsync(ActionStep step, ActionContext ctx, ActionRunProgress progress,
        CancellationToken ct) {
        if (!HttpEnabled || !Guid.TryParse(step.Target, out Guid actionId)) return false;

        var args = new Dictionary<string, string>(StringComparer.Ordinal) {
            [$"{ArgumentPrefix}trigger"] = "action",
            [$"{ArgumentPrefix}user"] = ctx.User,
            [$"{ArgumentPrefix}source"] = $"{ctx.Source}",
            [$"{ArgumentPrefix}label"] = ctx.Label ?? ""
        };
        foreach ((string name, string value) in step.BodyArguments()) args[name] = value;

        return await DoActionAsync(actionId, step.TargetName, args, ct);
    }

    private sealed class ActionList {
        public List<ActionEntry>? Actions { get; set; }
    }

    [UsedImplicitly]
    private sealed class ActionEntry {
        public Guid Id { get; set; }
        public string? Name { get; set; }
        public string? Group { get; set; }
    }
}
