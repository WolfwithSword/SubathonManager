using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;
using SubathonManager.Data;
using SubathonManager.Data.Widgets;

namespace SubathonManager.Services;

public partial class ActionService(
    IDbContextFactory<AppDbContext> factory,
    IEnumerable<IActionStepRunner> runners,
    ILogger<ActionService>? logger = null,
    string? actionsFolder = null,
    IConfig? config = null) : IAppService, IDisposable {
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public static string DefaultActionsFolder => Path.GetFullPath("actions");
    public static string ExportsFolder => Path.GetFullPath(Path.Combine("exports", "actions"));

    private readonly string _folder = actionsFolder ?? DefaultActionsFolder;
    private readonly ConcurrentDictionary<Guid, (CustomAction Action, string Path)> _library = new();

    private readonly Dictionary<ActionStepType, IActionStepRunner> _runners = runners
        .SelectMany(r => r.StepTypes.Select(t => (Type: t, Runner: r)))
        .GroupBy(x => x.Type)
        .ToDictionary(g => g.Key, g => g.First().Runner);

    private readonly ConcurrentDictionary<Guid, LiveRun> _runs = new();
    private volatile bool _multiplierActive;
    private volatile bool _libraryLoaded;
    public event Action? CustomActionsChanged;
    
    public const string LoggedRunMeta = "logged-run";

    public async Task StartAsync(CancellationToken ct = default) {
        SubathonEvents.SubathonDataUpdate += OnSubathonDataUpdate;
        WheelEvents.WheelSpinStatusChanged += OnWheelSpinStatusChanged;
        ActionEvents.CustomActionRunRequested += OnCustomActionRunRequested;
        await LoadLibraryAsync();

        await using AppDbContext db = await factory.CreateDbContextAsync(ct);
        await db.WheelSpinHistories
            .Where(h => h.Status == WheelSpinHistoryStatus.Running)
            .ExecuteUpdateAsync(s => s.SetProperty(h => h.Status, WheelSpinHistoryStatus.Pending), ct);
    }

    public Task StopAsync(CancellationToken ct = default) {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose() {
        SubathonEvents.SubathonDataUpdate -= OnSubathonDataUpdate;
        WheelEvents.WheelSpinStatusChanged -= OnWheelSpinStatusChanged;
        ActionEvents.CustomActionRunRequested -= OnCustomActionRunRequested;
        foreach (LiveRun run in _runs.Values) run.Cancel();
        GC.SuppressFinalize(this);
    }

    public bool IsRunning(Guid runId) {
        return _runs.ContainsKey(runId);
    }

    public void Cancel(Guid runId) {
        if (_runs.TryGetValue(runId, out LiveRun? run)) run.Cancel();
    }

    public IReadOnlyList<CustomAction> CustomActions =>
        _library.Values.Select(e => e.Action).OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public CustomAction? FindCustomAction(string name) {
        string wanted = name.Trim();
        return _library.Values.Select(e => e.Action)
            .FirstOrDefault(a => string.Equals(a.Name.Trim(), wanted, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<ActionRunResult> RunCustomActionAsync(CustomAction action, string user,
        SubathonEventSource source) {
        try {
            return await RunAsync(Guid.NewGuid(), action.Graph,
                new ActionContext(source, user, $"custom-action-{action.Id}", action.Name));
        }
        catch (Exception ex) {
            logger?.LogError(ex, "[Actions] Running {Name} failed", action.Name);
            return ActionRunResult.Paused;
        }
    }

    private static void LogRun(string actionName, ActionContext ctx) {
        SubathonEvents.RaiseSubathonEventCreated(new SubathonEvent {
            Source = ctx.Source,
            EventTimestamp = DateTime.Now,
            EventType = SubathonEventType.Command,
            Command = SubathonCommandType.RunAction,
            User = ctx.User,
            Value = $"{SubathonCommandType.RunAction} {actionName}",
            EventTypeMeta = LoggedRunMeta
        });
    }

    public Task<ActionRunResult> RunManuallyAsync(CustomAction action, bool unsavedCopy = false) {
        var ctx = new ActionContext(SubathonEventSource.Simulated, "CustomAction",
            unsavedCopy ? $"custom-action-test-{action.Id}" : $"custom-action-{action.Id}", action.Name);
        LogRun(action.Name, ctx);
        return RunAsync(Guid.NewGuid(), action.Graph, ctx);
    }

    private void OnCustomActionRunRequested(SubathonEvent ev) {
        if (ev.EventTypeMeta == LoggedRunMeta) return;
        string name = ev.Value?.Trim() ?? "";
        string prefix = $"{SubathonCommandType.RunAction} ";

        if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) name = name[prefix.Length..].Trim();
        if (FindCustomAction(name) is not { } action) {
            logger?.LogWarning("[Actions] RunAction asked for \"{Name}\", which is not in the actions library", name);
            return;
        }

        string user = string.IsNullOrWhiteSpace(ev.User) ? "SYSTEM" : ev.User;
        _ = RunCustomActionAsync(action, user, ev.Source);
    }

    private async Task<ActionStep> FillVariablesAsync(ActionStep step, ActionContext ctx) {
        string joined = $"{step.Target}\n{step.Scope}\n{step.Body}";
        List<ActionVariable> used = ActionStepTypeHelper.FindVariables(joined).Distinct().ToList();
        if (used.Count == 0) return step;

        Dictionary<ActionVariable, string> values = await ActionVariableResolver.ResolveAsync(factory, used, ctx, config);
        return new ActionStep {
            Type = step.Type,
            Operation = step.Operation,
            Target = ActionStepTypeHelper.ReplaceVariables(step.Target, values),
            TargetName = step.TargetName,
            Scope = step.Scope == null ? null : ActionStepTypeHelper.ReplaceVariables(step.Scope, values),
            Value = step.Value,
            Seconds = step.Seconds,
            Body = step.Body == null ? null : ActionStepTypeHelper.ReplaceVariables(step.Body, values)
        };
    }

    public CustomAction? GetCustomAction(Guid id) {
        return _library.TryGetValue(id, out (CustomAction Action, string Path) entry) ? entry.Action : null;
    }

    public string? GetCustomActionPath(Guid id) {
        return _library.TryGetValue(id, out (CustomAction Action, string Path) entry) ? entry.Path : null;
    }

    public async Task LoadLibraryAsync() {
        Directory.CreateDirectory(_folder);
        _library.Clear();
        foreach (string file in Directory.EnumerateFiles(_folder, $"*{CustomAction.FileExtension}",
                     SearchOption.AllDirectories)) {
            CustomAction? action = await ReadActionFileAsync(file);
            if (action == null) continue;
            if (!_library.TryAdd(action.Id, (action, file)))
                logger?.LogWarning("[Actions] {File} has the same id as {Other}; ignoring it", file,
                    _library[action.Id].Path);
        }

        _libraryLoaded = true;
        logger?.LogInformation("[Actions] Loaded {Count} custom action(s) from {Folder}", _library.Count, _folder);
        CustomActionsChanged?.Invoke();
    }

    public async Task SaveCustomActionAsync(CustomAction action) {
        string path = _library.TryGetValue(action.Id, out (CustomAction Action, string Path) existing)
            ? existing.Path
            : NewActionPath(_folder, action.Name);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = $"{path}.tmp";
        await File.WriteAllTextAsync(temp, action.ToJson());
        File.Move(temp, path, true);

        _library[action.Id] = (action, path);
        CustomActionsChanged?.Invoke();
    }

    public void DeleteCustomAction(Guid id) {
        if (!_library.TryRemove(id, out (CustomAction Action, string Path) entry)) return;
        try {
            File.Delete(entry.Path);
        }
        catch (Exception ex) {
            logger?.LogWarning(ex, "[Actions] Could not delete {File}", entry.Path);
        }

        CustomActionsChanged?.Invoke();
    }

    public async Task<(CustomAction Action, bool Replaced)?> ImportCustomActionAsync(string file) {
        CustomAction? action = await ReadActionFileAsync(file);
        if (action == null) return null;
        bool replaced = _library.ContainsKey(action.Id);
        await SaveCustomActionAsync(action);
        return (action, replaced);
    }

    public async Task<CustomAction?> AddCustomActionCopyAsync(string file) {
        if (!_libraryLoaded) await LoadLibraryAsync();
        CustomAction? action = await ReadActionFileAsync(file);
        if (action == null) return null;

        if (_library.ContainsKey(action.Id)) action.Id = Guid.NewGuid();
        action.Name = UniqueName(action.Name);
        await SaveCustomActionAsync(action);
        return action;
    }

    public async Task<CustomAction> DuplicateCustomActionAsync(CustomAction original) {
        CustomAction copy = original.Clone();
        copy.Id = Guid.NewGuid();
        copy.Name = UniqueName(original.Name);
        await SaveCustomActionAsync(copy);
        return copy;
    }


    [GeneratedRegex(@"\s\(Copy \d+\)$", RegexOptions.IgnoreCase)]
    private static partial Regex CopySuffix();

    private string UniqueName(string name) {
        var taken = new HashSet<string>(_library.Values.Select(e => e.Action.Name), StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(name)) return name;

        string baseName = CopySuffix().Replace(name, "");
        var n = 1;
        while (taken.Contains($"{baseName} (Copy {n})")) n++;
        return $"{baseName} (Copy {n})";
    }

    public static async Task<string> ExportCustomActionAsync(CustomAction action) {
        Directory.CreateDirectory(ExportsFolder);
        string path = Path.Combine(ExportsFolder,
            $"{WidgetPackPaths.Slug(action.Name)}_{WidgetPackPaths.Slug(action.Version)}{CustomAction.FileExtension}");
        await File.WriteAllTextAsync(path, action.ToJson());
        return path;
    }

    private async Task<CustomAction?> ReadActionFileAsync(string file) {
        try {
            if (CustomAction.TryParse(await File.ReadAllTextAsync(file), out CustomAction? action)) return action;
            logger?.LogWarning("[Actions] {File} is not a valid custom action", file);
        }
        catch (Exception ex) {
            logger?.LogWarning(ex, "[Actions] Could not read {File}", file);
        }

        return null;
    }

    private static string NewActionPath(string folder, string name) {
        string slug = WidgetPackPaths.Slug(name);
        if (string.IsNullOrEmpty(slug)) slug = "action";
        string path = Path.Combine(folder, $"{slug}{CustomAction.FileExtension}");
        for (var i = 2; File.Exists(path); i++)
            path = Path.Combine(folder, $"{slug}-{i}{CustomAction.FileExtension}");
        return path;
    }

    public ActionGraph? ResolveGraph(WheelSpinActionType type, string? parameter) {
        if (!type.IsAvailable()) return null;
        if (type != WheelSpinActionType.CustomAction) return type.BuildActionGraph(parameter);
        return Guid.TryParse(parameter, out Guid id) ? GetCustomAction(id)?.Graph : null;
    }

    public async Task<ActionRunResult> RunAsync(Guid runId, ActionGraph graph, ActionContext ctx,
        ActionRunProgress? progress = null, Func<ActionRunProgress, Task>? onProgress = null) {
        progress ??= new ActionRunProgress();

        if (!graph.IsValid(out string invalid)) {
            logger?.LogWarning("[Actions] Not running {Label}: {Error}", ctx.Label ?? ctx.RepeatKey, invalid);
            return ActionRunResult.Paused;
        }

        List<LiveRun> overlapping = _runs.Values.Where(r => r.RepeatKey == ctx.RepeatKey && r.Id != runId).ToList();
        if (overlapping.Count > 0 && graph.OnRepeat == ActionRepeatMode.Skip) return ActionRunResult.Skipped;
        if (graph.OnRepeat == ActionRepeatMode.Restart)
            foreach (LiveRun previous in overlapping) {
                previous.Superseded = true;
                previous.Cancel();
            }

        var live = new LiveRun(runId, ctx.RepeatKey);
        if (!_runs.TryAdd(runId, live)) return ActionRunResult.Skipped;

        try {
            bool finished = await RunGraphAsync(graph, ctx, progress, onProgress, live.Token);
            if (live.Superseded || finished) return ActionRunResult.Done;
            return live.Cancelled ? ActionRunResult.Cancelled : ActionRunResult.Paused;
        }
        finally {
            _runs.TryRemove(runId, out _);
            live.Dispose();
        }
    }

    private async Task<bool> RunGraphAsync(ActionGraph graph, ActionContext ctx, ActionRunProgress progress,
        Func<ActionRunProgress, Task>? onProgress, CancellationToken outerCt) {
        using var failCts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        CancellationToken ct = failCts.Token;

        Dictionary<string, ActionNode> pending = graph.Nodes
            .Where(n => !progress.IsDone(n.Id))
            .ToDictionary(n => n.Id);
        var running = new Dictionary<Task<bool>, ActionNode>();

        void StartReady() {
            foreach (ActionNode node in pending.Values.ToList().Where(node => graph.Incoming(node.Id).All(progress.IsDone))) {
                pending.Remove(node.Id);
                running[node.Disabled ? Task.Run(() => true, ct) : RunStepAsync(node.Step, ctx, progress, ct)] = node;
            }
        }

        StartReady();
        var failed = false;
        while (running.Count > 0) {
            Task<bool> next = await Task.WhenAny(running.Keys);
            ActionNode node = running[next];
            running.Remove(next);

            bool ok;
            try {
                ok = await next;
            }
            catch (OperationCanceledException) {
                ok = false;
            }
            catch (Exception ex) {
                logger?.LogError(ex, "[Actions] Step {Step} threw", node.Step.Describe());
                ok = false;
            }

            if (ok) {
                progress.MarkDone(node.Id);
                if (onProgress != null) await onProgress(progress);
                if (!failed && !ct.IsCancellationRequested) StartReady();
                continue;
            }

            if (failed || ct.IsCancellationRequested) continue;
            failed = true;
            logger?.LogWarning("[Actions] {Label} paused at \"{Step}\"", ctx.Label ?? ctx.RepeatKey,
                node.Step.Describe());
            await failCts.CancelAsync();
        }

        return !failed && pending.Count == 0 && !outerCt.IsCancellationRequested;
    }

    private async Task<bool> RunStepAsync(ActionStep step, ActionContext ctx, ActionRunProgress progress,
        CancellationToken ct) {
        ct.ThrowIfCancellationRequested();
        if (!step.Type.IsAvailable()) return false;

        if (step.Type.AllowsVariables()) step = await FillVariablesAsync(step, ctx);

        switch (step.Type) {
            case ActionStepType.HttpGet:
            case ActionStepType.HttpPost: {
                using var request = new HttpRequestMessage(
                    step.Type == ActionStepType.HttpGet ? HttpMethod.Get : HttpMethod.Post, step.Target.Trim());
                if (step.Type == ActionStepType.HttpPost) {
                    string body = step.Body ?? "";
                    string start = body.TrimStart();
                    string mediaType = start.StartsWith('{') || start.StartsWith('[') ? "application/json" : "text/plain";
                    request.Content = new StringContent(body, Encoding.UTF8, mediaType);
                }

                using HttpResponseMessage response =
                    await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.IsSuccessStatusCode) return true;
                logger?.LogWarning("[Actions] {Method} {Url} answered {Status}", request.Method, step.Target,
                    (int)response.StatusCode);
                return false;
            }

            case ActionStepType.Wait:
                await Task.Delay(step.Duration, ct); // when here, easier to do a delay vs the timerservice callback
                return true;

            case ActionStepType.AddTime:
            case ActionStepType.SubtractTime: {
                SubathonCommandType cmd = step.Type == ActionStepType.AddTime
                    ? SubathonCommandType.AddTime
                    : SubathonCommandType.SubtractTime;
                SubathonEvents.RaiseSubathonEventCreated(new SubathonEvent {
                    Source = ctx.Source,
                    EventTimestamp = DateTime.Now,
                    Command = cmd,
                    EventType = SubathonEventType.Command,
                    User = ctx.User,
                    Value = $"{cmd} {Utils.FormatShortDuration(step.Duration)}",
                    SecondsValue = step.Duration.TotalSeconds,
                    PointsValue = 0
                });
                return true;
            }

            case ActionStepType.SetMultiplier: {
                if (_multiplierActive) {
                    logger?.LogInformation("[Actions] A multiplier is already running; holding \"{Step}\"",
                        step.Describe());
                    return false;
                }

                string amount = (step.Value ?? 1).ToString(CultureInfo.InvariantCulture);
                string duration = step.Duration > TimeSpan.Zero ? $"{(int)step.Duration.TotalSeconds}s" : "xs";
                bool points = step.Operation is ActionOperation.Points or ActionOperation.PointsAndTime;
                bool time = step.Operation is ActionOperation.Time or ActionOperation.PointsAndTime;
                SubathonEvents.RaiseSubathonEventCreated(new SubathonEvent {
                    Source = ctx.Source,
                    EventTimestamp = DateTime.Now,
                    Command = SubathonCommandType.SetMultiplier,
                    EventType = SubathonEventType.Command,
                    User = ctx.User,
                    Value = $"{amount}|{duration}|{points}|{time}"
                });
                _multiplierActive = true;
                return true;
            }

            case ActionStepType.Reroll: {
                var count = (int)(step.Value ?? 0);
                if (count < 1) return false;
                int owed = await StateValueHelper.AddIntAsync(factory, StateKeys.WheelSpinsOwed, count);
                WheelEvents.RaiseSpinsOwedUpdateFromEvent(owed);
                return true;
            }

            default:
                if (_runners.TryGetValue(step.Type, out IActionStepRunner? runner))
                    return await runner.RunStepAsync(step, ctx, progress, ct);
                logger?.LogWarning("[Actions] No runner for step type {Type}", step.Type);
                return false;
        }
    }

    public async Task<ActionRunResult> RunWheelSpinAsync(Guid historyId, Func<WheelSpinHistoryStatus,
        Task>? announce = null) {

        WheelSpinHistory? history = await LoadHistoryAsync(historyId);
        if (history == null) return ActionRunResult.Skipped;

        WheelSpinAction? action = history.LinkedItem?.Action;
        ActionGraph? graph = action == null ? null : ResolveGraph(action.ActionType, action.Parameter);
        if (action == null || graph == null || IsRunning(historyId)) {
            if (action?.ActionType.HasAction() == true && graph == null)
                logger?.LogWarning("[Actions] Wheel item \"{Item}\" has no runnable action ({Type}: \"{Parameter}\")",
                    history.LinkedItem?.Text, action.ActionType, action.Parameter);
            if (announce != null) await announce(history.Status);
            return ActionRunResult.Skipped;
        }

        bool quiet = announce != null && graph.Nodes.All(n => n.Disabled || n.Step.Type != ActionStepType.Wait);
        if (announce != null && !quiet) await announce(history.Status);
        await SaveHistoryAsync(history, WheelSpinHistoryStatus.Running, history.ActionProgress, !quiet);

        var ctx = new ActionContext(SubathonEventSource.WheelSpin, "WheelSpin",
            action.ActionType.RepeatKey(action.Parameter, action.WheelItemId), history.LinkedItem?.Text);

        if (action.ActionType == WheelSpinActionType.CustomAction
            && Guid.TryParse(action.Parameter, out Guid customId) && GetCustomAction(customId) is { } custom)
            LogRun(custom.Name, ctx);
        ActionRunProgress progress = ActionRunProgress.Parse(history.ActionProgress);
        ActionRunResult result = await RunAsync(historyId, graph, ctx, progress,
            p => SaveProgressAsync(historyId, p));

        bool keepStatus = result == ActionRunResult.Cancelled
                          || (result == ActionRunResult.Skipped && IsRunning(historyId));

        history = await LoadHistoryAsync(historyId) ?? history;
        if (!keepStatus && history.Status == WheelSpinHistoryStatus.Running) {
            if (result == ActionRunResult.Paused)
                await SaveHistoryAsync(history, WheelSpinHistoryStatus.Pending, progress.ToJson(), !quiet);
            else
                await SaveHistoryAsync(history, WheelSpinHistoryStatus.Done, null, !quiet);
        }

        if (quiet) await announce!(history.Status);
        return result;
    }

    private async Task<WheelSpinHistory?> LoadHistoryAsync(Guid historyId) {
        await using AppDbContext db = await factory.CreateDbContextAsync();
        return await db.WheelSpinHistories
            .Include(h => h.LinkedItem).ThenInclude(i => i!.Action)
            .Include(h => h.LinkedWheel)
            .AsNoTracking()
            .FirstOrDefaultAsync(h => h.Id == historyId);
    }

    private async Task SaveProgressAsync(Guid historyId, ActionRunProgress progress) {
        string json = progress.ToJson();
        await using AppDbContext db = await factory.CreateDbContextAsync();
        await db.WheelSpinHistories.Where(h => h.Id == historyId)
            .ExecuteUpdateAsync(s => s.SetProperty(h => h.ActionProgress, json));
    }

    private async Task SaveHistoryAsync(WheelSpinHistory history, WheelSpinHistoryStatus status, string? progress,
        bool raise) {
        history.Status = status;
        history.ActionProgress = progress;
        history.UpdatedAt = DateTime.Now;

        await using AppDbContext db = await factory.CreateDbContextAsync();
        await db.WheelSpinHistories.Where(h => h.Id == history.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(h => h.Status, status)
                .SetProperty(h => h.ActionProgress, progress)
                .SetProperty(h => h.UpdatedAt, history.UpdatedAt));

        if (raise)
            WheelEvents.RaiseWheelSpinStatusChanged(history, StateValueHelper.Get<int>(db, StateKeys.WheelSpinsOwed));
    }

    private void OnWheelSpinStatusChanged(WheelSpinHistory history, int _) {
        if (history.Status != WheelSpinHistoryStatus.Running) Cancel(history.Id);
    }

    private void OnSubathonDataUpdate(SubathonData data, DateTime _) {
        _multiplierActive = data.Multiplier?.IsRunning() ?? false;
    }

    private sealed class LiveRun(Guid id, string repeatKey) : IDisposable {
        private readonly CancellationTokenSource _cts = new();
        public Guid Id { get; } = id;
        public string RepeatKey { get; } = repeatKey;
        public CancellationToken Token => _cts.Token;
        public bool Cancelled { get; private set; }
        public bool Superseded { get; set; }

        public void Cancel() {
            Cancelled = true;
            try {
                _cts.Cancel();
            }
            catch (ObjectDisposedException) {
                /* */
            }
        }

        public void Dispose() {
            _cts.Dispose();
        }
    }
}
