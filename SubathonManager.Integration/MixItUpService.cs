using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;

namespace SubathonManager.Integration;

public sealed record MixItUpCommandInfo(Guid Id, string Name, string Type, string GroupName, bool IsEnabled) {
    public string DisplayName => string.IsNullOrWhiteSpace(GroupName) ? Name : $"{GroupName} / {Name}";
}

public class MixItUpService(
    ILogger<MixItUpService>? logger,
    IConfig config,
    IHttpClientFactory httpClientFactory,
    ITimerService timerService) : IAppService, IDisposable {

    public const string ConfigSection = "MixItUp";
    public const string DefaultApiUrl = "http://localhost:8911/api/v2";
    public const string ServiceName = "MixItUp";
    public const string IdentifierPrefix = "subathonmanager";
    public const string IncludeCommandsKey = "SubathonEvent.IncludeCommands";

    internal static readonly TimeSpan SeenWindow = TimeSpan.FromMinutes(90);
    internal static readonly TimeSpan ProbeInterval = TimeSpan.FromMinutes(10);
    private const int CommandPageSize = 100;
    private const int MaxCommandPages = 50;
    private const string SeenTimerKey = "mixitup-seen";
    private const string ProbeTimerKey = "mixitup-probe";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions RequestJsonOptions = new();

    private readonly Lock _lock = new();

    private bool _lastLocked;
    private MultiplierSnapshot? _lastMultiplier;
    private bool _lastPaused;
    private Guid? _trackedSubathonId;

    public static string CommandsFolder => Path.GetFullPath(Path.Combine("external", "mixitup"));

    public DateTime? LastSeen { get; private set; }
    public string? Version { get; private set; }
    public bool Connected => LastSeen != null;

    public bool Enabled => config.GetBool(ConfigSection, "Enabled");
    public bool IncludeCommands => config.GetBool(ConfigSection, IncludeCommandsKey, false);

    public string ApiUrl {
        get {
            string url = (config.Get(ConfigSection, "ApiUrl", DefaultApiUrl) ?? "").Trim().TrimEnd('/');
            return string.IsNullOrWhiteSpace(url) ? DefaultApiUrl : url;
        }
    }

    public static string CommandConfigKey(MixItUpTrigger trigger) {
        return $"Command.{trigger}";
    }

    public string GetCommandId(MixItUpTrigger trigger) {
        return (config.Get(ConfigSection, CommandConfigKey(trigger), "") ?? "").Trim();
    }

    public Task StartAsync(CancellationToken ct = default) {
        Unsubscribe();
        Subscribe();
        BroadcastStatus();
        timerService.Register(ProbeTimerKey, ProbeInterval, () => _ = ProbeAsync(ct));
        _ = Task.Run(() => ProbeAsync(ct), ct);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default) {
        Unsubscribe();
        timerService.Unregister(ProbeTimerKey);
        timerService.Unregister(SeenTimerKey);
        lock (_lock) {
            LastSeen = null;
            _trackedSubathonId = null;
            _lastMultiplier = null;
        }

        BroadcastStatus();
        return Task.CompletedTask;
    }

    public void Dispose() {
        Unsubscribe();
        timerService.Unregister(ProbeTimerKey);
        timerService.Unregister(SeenTimerKey);
        GC.SuppressFinalize(this);
    }

    private void Subscribe() {
        IntegrationEvents.ExternalSourceSeen += OnExternalSourceSeen;
        SubathonEvents.SubathonEventProcessed += OnSubathonEventProcessed;
        SubathonEvents.SubathonDataUpdate += OnSubathonDataUpdate;
        SubathonEvents.SubathonGoalCompleted += OnGoalCompleted;
        SubathonEvents.PromptRunStarted += OnPromptRunStarted;
        SubathonEvents.PromptRunUpdate += OnPromptRunUpdate;
        WheelEvents.WheelSpinStarted += OnWheelSpinStarted;
        WheelEvents.WheelSpinResult += OnWheelSpinResult;
    }

    private void Unsubscribe() {
        IntegrationEvents.ExternalSourceSeen -= OnExternalSourceSeen;
        SubathonEvents.SubathonEventProcessed -= OnSubathonEventProcessed;
        SubathonEvents.SubathonDataUpdate -= OnSubathonDataUpdate;
        SubathonEvents.SubathonGoalCompleted -= OnGoalCompleted;
        SubathonEvents.PromptRunStarted -= OnPromptRunStarted;
        SubathonEvents.PromptRunUpdate -= OnPromptRunUpdate;
        WheelEvents.WheelSpinStarted -= OnWheelSpinStarted;
        WheelEvents.WheelSpinResult -= OnWheelSpinResult;
    }
    
    public async Task<bool> ProbeAsync(CancellationToken ct = default) {
        try {
            using HttpClient client = CreateClient();
            using HttpResponseMessage response = await client.GetAsync($"{ApiUrl}/status/version", ct);
            if (!response.IsSuccessStatusCode) return false;

            string body = (await response.Content.ReadAsStringAsync(ct)).Trim();
            MarkSeen(DateTime.Now, ParseVersion(body));
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        }
        catch (Exception ex) {
            logger?.LogDebug("[MixItUp] Probe failed: {Message}", ex.Message);
            return false;
        }
    }

    internal static string? ParseVersion(string body) {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try {
            if (body.StartsWith('"')) return JsonSerializer.Deserialize<string>(body);
        }
        catch (JsonException) {
            /**/
        }

        return body;
    }

    private void OnExternalSourceSeen(SubathonEventSource source) {
        if (source == SubathonEventSource.MixItUp) MarkSeen(DateTime.Now);
    }

    internal void MarkSeen(DateTime now, string? version = null) {
        bool changed;
        lock (_lock) {
            changed = LastSeen == null;
            LastSeen = now;
            if (!string.IsNullOrWhiteSpace(version) && version != Version) {
                Version = version;
                changed = true;
            }
        }

        timerService.Register(SeenTimerKey, SeenWindow, Expire);
        if (changed) BroadcastStatus();
    }

    internal void Expire() {
        timerService.Unregister(SeenTimerKey);
        lock (_lock) {
            if (LastSeen == null) return;
            LastSeen = null;
        }

        BroadcastStatus();
    }

    private void BroadcastStatus() {
        bool seen = Connected;
        IntegrationEvents.RaiseConnectionUpdate(new IntegrationConnection {
            Source = SubathonEventSource.MixItUp,
            Service = ServiceName,
            Name = seen && !string.IsNullOrWhiteSpace(Version) ? $"v{Version}" : "",
            Status = seen,
            Configured = seen
        });
    }

    private HttpClient CreateClient() {
        HttpClient client = httpClientFactory.CreateClient(nameof(MixItUpService));
        client.Timeout = TimeSpan.FromSeconds(5);
        return client;
    }

    public async Task<bool> RunCommandAsync(Guid commandId, IReadOnlyDictionary<string, string> identifiers,
        CancellationToken ct = default) {
        try {
            var payload = new {
                Arguments = "",
                SpecialIdentifiers = identifiers,
                IgnoreRequirements = false
            };
            using HttpClient client = CreateClient();
            using HttpResponseMessage response =
                await client.PostAsJsonAsync($"{ApiUrl}/commands/{commandId}", payload, RequestJsonOptions, ct);
            if (response.IsSuccessStatusCode) {
                MarkSeen(DateTime.Now);
                return true;
            }

            string detail = await response.Content.ReadAsStringAsync(ct);
            logger?.LogWarning("[MixItUp] Running command {CommandId} failed with {Status}: {Detail}", commandId,
                (int)response.StatusCode, detail);
            return false;
        }
        catch (Exception ex) {
            logger?.LogDebug("[MixItUp] Running command {CommandId} failed: {Message}", commandId, ex.Message);
            return false;
        }
    }

    public async Task<IReadOnlyList<MixItUpCommandInfo>?> GetCommandsAsync(CancellationToken ct = default) {
        var result = new List<MixItUpCommandInfo>();
        try {
            using HttpClient client = CreateClient();
            for (var page = 0; page < MaxCommandPages; page++) {
                string url = $"{ApiUrl}/commands?skip={page * CommandPageSize}&pageSize={CommandPageSize}";
                using HttpResponseMessage response = await client.GetAsync(url, ct);
                if (!response.IsSuccessStatusCode) return null;

                CommandListResponse? list = await response.Content.ReadFromJsonAsync<CommandListResponse>(
                    JsonOptions, ct);
                if (list?.Commands == null || list.Commands.Count == 0) break;

                result.AddRange(list.Commands.Select(c => new MixItUpCommandInfo(c.Id, c.Name ?? "",
                    c.Type ?? "", c.GroupName ?? "", c.IsEnabled)));
                if (result.Count >= list.TotalCount) break;
            }

            result = await KeepRunnableAsync(client, result, ct);
            MarkSeen(DateTime.Now);
        }
        catch (Exception ex) {
            logger?.LogDebug("[MixItUp] Fetching commands failed: {Message}", ex.Message);
            return null;
        }

        return result.OrderBy(c => c.GroupName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task<List<MixItUpCommandInfo>> KeepRunnableAsync(HttpClient client,
        List<MixItUpCommandInfo> commands, CancellationToken ct) {
        using var gate = new SemaphoreSlim(8);
        bool[] runnable = await Task.WhenAll(commands.Select(async c => {
            await gate.WaitAsync(ct);
            try {
                using HttpResponseMessage response = await client.GetAsync($"{ApiUrl}/commands/{c.Id}", ct);
                return response.IsSuccessStatusCode;
            }
            catch (HttpRequestException) {
                return false;
            }
            finally {
                gate.Release();
            }
        }));
        return commands.Where((_, i) => runnable[i]).ToList();
    }

    private sealed class CommandListResponse {
        public int TotalCount { get; set; }
        public List<CommandEntry>? Commands { get; set; }
    }

    private sealed class CommandEntry {
        [System.Text.Json.Serialization.JsonPropertyName("ID")]
        public Guid Id { get; set; }

        public string? Name { get; set; }
        public string? Type { get; set; }
        public string? GroupName { get; set; }
        public bool IsEnabled { get; set; }
    }

    internal bool Fire(MixItUpTrigger trigger, Dictionary<string, string> identifiers) {
        if (!Enabled) return false;
        if (!Guid.TryParse(GetCommandId(trigger), out Guid commandId)) return false;

        identifiers[$"{IdentifierPrefix}trigger"] = trigger.ToString();
        _ = Task.Run(() => RunCommandAsync(commandId, identifiers));
        return true;
    }

    public Task<bool> TestTriggerAsync(MixItUpTrigger trigger, string commandIdText, CancellationToken ct = default) {
        if (!Guid.TryParse(commandIdText.Trim(), out Guid commandId)) return Task.FromResult(false);
        Dictionary<string, string> identifiers = SampleIdentifiers(trigger);
        identifiers[$"{IdentifierPrefix}trigger"] = trigger.ToString();
        return RunCommandAsync(commandId, identifiers, ct);
    }

    private void OnSubathonEventProcessed(SubathonEvent subathonEvent, bool effective) {
        if (!config.GetBool("App", "ShowLockedEvents", false) && !subathonEvent.ProcessedToSubathon) return;
        if (subathonEvent.EventType == SubathonEventType.Command && !IncludeCommands) return;
        Fire(MixItUpTrigger.SubathonEvent, EventIdentifiers(subathonEvent));
    }

    private void OnSubathonDataUpdate(SubathonData subathon, DateTime timestamp) {
        // check for multiplier and pause/lock info
        var fired = new List<(MixItUpTrigger, Dictionary<string, string>)>(3);
        MultiplierSnapshot? multiplier = subathon.Multiplier?.SubathonId == subathon.Id
            ? MultiplierSnapshot.From(subathon.Multiplier)
            : null;
        lock (_lock) {
            if (_trackedSubathonId != subathon.Id) {
                _trackedSubathonId = subathon.Id;
                _lastPaused = subathon.IsPaused;
                _lastLocked = subathon.IsLocked;
                _lastMultiplier = multiplier;
                return;
            }

            if (subathon.IsPaused != _lastPaused) {
                _lastPaused = subathon.IsPaused;
                fired.Add((subathon.IsPaused ? MixItUpTrigger.TimerPaused : MixItUpTrigger.TimerResumed,
                    TimerIdentifiers(subathon)));
            }

            if (subathon.IsLocked != _lastLocked) {
                _lastLocked = subathon.IsLocked;
                fired.Add((subathon.IsLocked ? MixItUpTrigger.TimerLocked : MixItUpTrigger.TimerUnlocked,
                    TimerIdentifiers(subathon)));
            }

            if (multiplier != null) {
                MultiplierSnapshot? previous = _lastMultiplier;
                _lastMultiplier = multiplier;
                if (previous != null) {
                    if (multiplier.Running && multiplier != previous)
                        fired.Add((MixItUpTrigger.MultiplierStarted, MultiplierIdentifiers(multiplier)));
                    else if (!multiplier.Running && previous.Running)
                        fired.Add((MixItUpTrigger.MultiplierEnded, MultiplierIdentifiers(previous)));
                }
            }
        }

        foreach ((MixItUpTrigger trigger, Dictionary<string, string> identifiers) in fired)
            Fire(trigger, identifiers);
    }

    private void OnGoalCompleted(SubathonGoal goal, long currentValue) {
        Fire(MixItUpTrigger.GoalCompleted, new Dictionary<string, string> {
            [$"{IdentifierPrefix}goaltext"] = goal.Text,
            [$"{IdentifierPrefix}goaltarget"] = ToValueStr(goal.Points),
            [$"{IdentifierPrefix}goalcurrent"] = ToValueStr(currentValue)
        });
    }

    private void OnWheelSpinStarted(WheelSet wheel, int delaySeconds) {
        Fire(MixItUpTrigger.WheelSpinStart, new Dictionary<string, string> {
            [$"{IdentifierPrefix}wheelname"] = wheel.Name,
            [$"{IdentifierPrefix}wheelid"] = wheel.Id.ToString(),
            [$"{IdentifierPrefix}spindelay"] = ToValueStr(delaySeconds)
        });
    }

    private void OnWheelSpinResult(WheelSet wheel, WheelItem? item, WheelSpinHistory history, int spinsOwed) {
        Fire(MixItUpTrigger.WheelSpinEnd, new Dictionary<string, string> {
            [$"{IdentifierPrefix}wheelname"] = wheel.Name,
            [$"{IdentifierPrefix}wheelid"] = wheel.Id.ToString(),
            [$"{IdentifierPrefix}wheelitem"] = item?.Text ?? "",
            [$"{IdentifierPrefix}spinstatus"] = history.Status.ToString(),
            [$"{IdentifierPrefix}spinsowed"] = ToValueStr(spinsOwed)
        });
    }

    private void OnPromptRunStarted(SubathonPromptRun run, SubathonPrompt? prompt) {
        Fire(MixItUpTrigger.PromptStarted, PromptIdentifiers(run, prompt));
    }

    private void OnPromptRunUpdate(SubathonPromptRun run, SubathonPrompt? prompt) {
        if (run.IsActive) return;
        Fire(MixItUpTrigger.PromptEnded, PromptIdentifiers(run, prompt));
    }

    private static string ToValueStr(IFormattable value) {
        return value.ToString(null, CultureInfo.InvariantCulture);
    }

    internal static Dictionary<string, string> EventIdentifiers(SubathonEvent subathonEvent) {
        var eventType = $"{subathonEvent.EventType}";
        string? trueSource = subathonEvent.EventType.GetTypeTrueSource(subathonEvent.EventTypeMeta);
        if (subathonEvent.EventType == SubathonEventType.GoAffProOrder
            && GoAffProOrderHelper.TryGetStore(subathonEvent.EventTypeMeta, out GoAffProStore? store)) {
            trueSource = store.InternalName;
            eventType = store.InternalEventName;
        }

        double seconds = subathonEvent.GetFinalSecondsValueRaw() < 0.5 ? 0 : subathonEvent.GetFinalSecondsValue();
        return new Dictionary<string, string> {
            [$"{IdentifierPrefix}eventtype"] = eventType,
            [$"{IdentifierPrefix}source"] = $"{subathonEvent.Source}",
            [$"{IdentifierPrefix}truesource"] = trueSource ?? "",
            [$"{IdentifierPrefix}subtype"] = $"{subathonEvent.EventType.GetSubType()}",
            [$"{IdentifierPrefix}user"] = subathonEvent.User ?? "",
            [$"{IdentifierPrefix}value"] = subathonEvent.Value,
            [$"{IdentifierPrefix}amount"] = ToValueStr(subathonEvent.Amount),
            [$"{IdentifierPrefix}currency"] = subathonEvent.Currency ?? "",
            [$"{IdentifierPrefix}command"] = $"{subathonEvent.Command}",
            [$"{IdentifierPrefix}secondsadded"] = ToValueStr(seconds),
            [$"{IdentifierPrefix}pointsadded"] = ToValueStr(subathonEvent.GetFinalPointsValue()),
            [$"{IdentifierPrefix}secondaryvalue"] = subathonEvent.SecondaryValue,
            [$"{IdentifierPrefix}tertiaryvalue"] = subathonEvent.TertiaryValue,
            [$"{IdentifierPrefix}reversed"] = $"{subathonEvent.WasReversed}"
        };
    }

    internal static Dictionary<string, string> TimerIdentifiers(SubathonData subathon) {
        TimeSpan remaining = subathon.TimeRemainingRounded();
        return new Dictionary<string, string> {
            [$"{IdentifierPrefix}timeremaining"] =
                $"{(int)remaining.TotalHours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}",
            [$"{IdentifierPrefix}secondsremaining"] = ToValueStr((long)remaining.TotalSeconds),
            [$"{IdentifierPrefix}points"] = ToValueStr(subathon.Points),
            [$"{IdentifierPrefix}paused"] = $"{subathon.IsPaused}",
            [$"{IdentifierPrefix}locked"] = $"{subathon.IsLocked}"
        };
    }

    internal sealed record MultiplierSnapshot(
        bool Running,
        double Multiplier,
        bool Time,
        bool Points,
        TimeSpan? Duration,
        DateTime? Started,
        bool FromHypeTrain) {
        public static MultiplierSnapshot From(MultiplierData data) {
            return new MultiplierSnapshot(data.IsRunning(), data.Multiplier, data.ApplyToSeconds,
                data.ApplyToPoints, data.Duration, data.Started, data.FromHypeTrain);
        }
    }

    internal static Dictionary<string, string> MultiplierIdentifiers(MultiplierSnapshot multiplier) {
        return new Dictionary<string, string> {
            [$"{IdentifierPrefix}multiplier"] = ToValueStr(multiplier.Multiplier),
            [$"{IdentifierPrefix}multipliertime"] = $"{multiplier.Time}",
            [$"{IdentifierPrefix}multiplierpoints"] = $"{multiplier.Points}",
            [$"{IdentifierPrefix}multiplierduration"] = ToValueStr((long)(multiplier.Duration?.TotalSeconds ?? 0)),
            [$"{IdentifierPrefix}multiplierhypetrain"] = $"{multiplier.FromHypeTrain}"
        };
    }

    internal static Dictionary<string, string> PromptIdentifiers(SubathonPromptRun run, SubathonPrompt? prompt) {
        prompt ??= run.LinkedPrompt;
        return new Dictionary<string, string> {
            [$"{IdentifierPrefix}prompttext"] = prompt?.Text ?? "",
            [$"{IdentifierPrefix}prompttype"] = $"{prompt?.Type}",
            [$"{IdentifierPrefix}prompttarget"] = ToValueStr(prompt?.Value ?? run.SnapshotTargetValue),
            [$"{IdentifierPrefix}promptduration"] =
                ToValueStr((long)(prompt?.CompletionDuration.TotalSeconds ?? (run.ExpiresAt - run.StartedAt).TotalSeconds)),
            [$"{IdentifierPrefix}promptstatus"] = $"{run.Status}"
        };
    }

    public static IReadOnlyList<string> IdentifierNames(MixItUpTrigger trigger) {
        return SampleIdentifiers(trigger).Keys.Prepend($"{IdentifierPrefix}trigger").Select(k => $"${k}").ToList();
    }

    internal static Dictionary<string, string> SampleIdentifiers(MixItUpTrigger trigger) {
        switch (trigger) {
            case MixItUpTrigger.SubathonEvent:
                return EventIdentifiers(new SubathonEvent {
                    Source = SubathonEventSource.Simulated,
                    EventType = SubathonEventType.ExternalDonation,
                    User = "TestUser",
                    Value = "5",
                    Currency = "USD",
                    SecondsValue = 600,
                    PointsValue = 5,
                    ProcessedToSubathon = true
                });
            case MixItUpTrigger.MultiplierStarted:
            case MixItUpTrigger.MultiplierEnded:
                return MultiplierIdentifiers(new MultiplierSnapshot(trigger == MixItUpTrigger.MultiplierStarted,
                    2, true, true, TimeSpan.FromMinutes(10), DateTime.Now, false));
            case MixItUpTrigger.TimerPaused:
            case MixItUpTrigger.TimerResumed:
            case MixItUpTrigger.TimerLocked:
            case MixItUpTrigger.TimerUnlocked:
                return new Dictionary<string, string> {
                    [$"{IdentifierPrefix}timeremaining"] = "12:34:56",
                    [$"{IdentifierPrefix}secondsremaining"] = "45296",
                    [$"{IdentifierPrefix}points"] = "250",
                    [$"{IdentifierPrefix}paused"] = (trigger == MixItUpTrigger.TimerPaused).ToString(),
                    [$"{IdentifierPrefix}locked"] = (trigger == MixItUpTrigger.TimerLocked).ToString()
                };
            case MixItUpTrigger.GoalCompleted:
                return new Dictionary<string, string> {
                    [$"{IdentifierPrefix}goaltext"] = "Test Goal",
                    [$"{IdentifierPrefix}goaltarget"] = "100",
                    [$"{IdentifierPrefix}goalcurrent"] = "100"
                };
            case MixItUpTrigger.WheelSpinStart:
                return new Dictionary<string, string> {
                    [$"{IdentifierPrefix}wheelname"] = "Test Wheel",
                    [$"{IdentifierPrefix}wheelid"] = Guid.Empty.ToString(),
                    [$"{IdentifierPrefix}spindelay"] = "0"
                };
            case MixItUpTrigger.WheelSpinEnd:
                return new Dictionary<string, string> {
                    [$"{IdentifierPrefix}wheelname"] = "Test Wheel",
                    [$"{IdentifierPrefix}wheelid"] = Guid.Empty.ToString(),
                    [$"{IdentifierPrefix}wheelitem"] = "Test Item",
                    [$"{IdentifierPrefix}spinstatus"] = "Pending",
                    [$"{IdentifierPrefix}spinsowed"] = "0"
                };
            case MixItUpTrigger.PromptStarted:
            case MixItUpTrigger.PromptEnded:
                return new Dictionary<string, string> {
                    [$"{IdentifierPrefix}prompttext"] = "Test Prompt",
                    [$"{IdentifierPrefix}prompttype"] = $"{SubathonPromptType.Points}",
                    [$"{IdentifierPrefix}prompttarget"] = "10",
                    [$"{IdentifierPrefix}promptduration"] = "300",
                    [$"{IdentifierPrefix}promptstatus"] = trigger == MixItUpTrigger.PromptStarted
                        ? $"{SubathonPromptRunStatus.Active}"
                        : $"{SubathonPromptRunStatus.Completed}"
                };
            default:
                return new Dictionary<string, string>();
        }
    }
}

public static class MixItUpCommandExporter {
    public const string FileExtension = ".miucommand";
    public const string GroupName = "Subathon Manager";

    private const string ActionGroupCommandType = "MixItUp.Base.Model.Commands.ActionGroupCommandModel, MixItUp.Base";
    private const string RequirementsSetType = "MixItUp.Base.Model.Requirements.RequirementsSetModel, MixItUp.Base";
    private const string WebRequestActionType = "MixItUp.Base.Model.Actions.WebRequestActionModel, MixItUp.Base";

    private const int CommandTypeActionGroup = 4;
    private const int ActionTypeWebRequest = 11;
    private const int ResponseTypePlainText = 0;
    private const int HttpMethodPost = 1;

    public static IEnumerable<SubathonCommandType> ExportableCommands =>
        Enum.GetValues<SubathonCommandType>()
            .Where(c => c is not (SubathonCommandType.None or SubathonCommandType.Unknown));

    public static string CommandName(SubathonCommandType command) {
        return $"Subathon - {command.GetDescription()}";
    }

    public static string BuildRequestBody(SubathonCommandType command) {
        return JsonSerializer.Serialize(new Dictionary<string, string> {
            ["type"] = $"{SubathonEventType.Command}",
            ["command"] = $"{command}",
            ["message"] = command.IsParametersRequired() ? "$allargs" : "",
            ["user"] = "$username",
            ["source"] = $"{SubathonEventSource.MixItUp}"
        });
    }

    public static string BuildCommandJson(SubathonCommandType command, int port) {
        var webRequest = new JsonObject {
            ["$type"] = WebRequestActionType,
            ["Url"] = $"http://localhost:{port}/api/data/control",
            ["ResponseType"] = ResponseTypePlainText,
            ["JSONToSpecialIdentifiers"] = new JsonObject(),
            ["HttpMethod"] = HttpMethodPost,
            ["CustomHeaders"] = new JsonObject(),
            ["RequestBody"] = BuildRequestBody(command),
            ["ID"] = Guid.NewGuid().ToString(),
            ["Name"] = "Web Request",
            ["Type"] = ActionTypeWebRequest,
            ["Enabled"] = true
        };

        var root = new JsonObject {
            ["$type"] = ActionGroupCommandType,
            ["RunOneRandomly"] = false,
            ["ID"] = Guid.NewGuid().ToString(),
            ["Name"] = CommandName(command),
            ["Type"] = CommandTypeActionGroup,
            ["IsEnabled"] = true,
            ["Unlocked"] = false,
            ["IsEmbedded"] = false,
            ["GroupName"] = GroupName,
            ["Triggers"] = new JsonArray(),
            ["Requirements"] = new JsonObject {
                ["$type"] = RequirementsSetType,
                ["Requirements"] = new JsonArray()
            },
            ["Actions"] = new JsonArray(webRequest)
        };

        return root.ToJsonString();
    }

    public static IReadOnlyList<string> WriteAll(string folder, int port) {
        Directory.CreateDirectory(folder);
        foreach (string stale in Directory.GetFiles(folder, $"*{FileExtension}"))
            File.Delete(stale);

        var written = new List<string>();
        foreach (SubathonCommandType command in ExportableCommands) {
            string path = Path.Combine(folder, $"{CommandName(command)}{FileExtension}");
            File.WriteAllText(path, BuildCommandJson(command, port));
            written.Add(path);
        }

        return written;
    }
}
