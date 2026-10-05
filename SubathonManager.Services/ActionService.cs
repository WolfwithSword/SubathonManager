using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
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
using SubathonManager.Core.Security;
using SubathonManager.Core.Security.Interfaces;
using SubathonManager.Data;
using SubathonManager.Data.Widgets;

namespace SubathonManager.Services;

public partial class ActionService(
    IDbContextFactory<AppDbContext> factory,
    IEnumerable<IActionStepRunner> runners,
    ILogger<ActionService>? logger = null,
    string? actionsFolder = null,
    IConfig? config = null,
    ISecureStorage? secureStorage = null) : IAppService, IDisposable {
    public const string LoggedRunMeta = "logged-run";

    // web step saving to %resp% also sets %resp_status%
    public const string StatusSuffix = "_status";

    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaxRequestTimeout = TimeSpan.FromMinutes(5);

    private readonly string _folder = actionsFolder ?? DefaultActionsFolder;
    private readonly SemaphoreSlim _globalsLock = new(1, 1);
    private readonly ConcurrentDictionary<Guid, (CustomAction Action, string Path)> _library = new();

    private readonly Dictionary<ActionStepType, IActionStepRunner> _runners = runners
        .SelectMany(r => r.StepTypes.Select(t => (Type: t, Runner: r)))
        .GroupBy(x => x.Type)
        .ToDictionary(g => g.Key, g => g.First().Runner);

    private readonly ConcurrentDictionary<Guid, LiveRun> _runs = new();
    private volatile bool _libraryLoaded;
    private volatile bool _multiplierActive;

    public static string DefaultActionsFolder => Path.GetFullPath("actions");
    public static string ExportsFolder => Path.GetFullPath(Path.Combine("exports", "actions"));

    public IReadOnlyList<CustomAction> CustomActions =>
        _library.Values.Select(e => e.Action).OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();

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

    public event Action? CustomActionsChanged;

    public bool IsRunning(Guid runId) {
        return _runs.ContainsKey(runId);
    }

    public void Cancel(Guid runId) {
        if (_runs.TryGetValue(runId, out LiveRun? run)) run.Cancel();
    }

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
        var prefix = $"{SubathonCommandType.RunAction} ";

        if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) name = name[prefix.Length..].Trim();
        if (FindCustomAction(name) is not { } action) {
            logger?.LogWarning("[Actions] RunAction asked for \"{Name}\", which is not in the actions library", name);
            return;
        }

        string user = string.IsNullOrWhiteSpace(ev.User) ? "SYSTEM" : ev.User;
        _ = RunCustomActionAsync(action, user, ev.Source);
    }

    private async Task<ActionStep> FillVariablesAsync(ActionStep step, ActionContext ctx, ActionRunProgress progress,
        IReadOnlySet<string> outputs) {
        List<string> tokens = ActionStepTypeHelper.FindTokens(step.AllText)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (tokens.Count == 0) return step;

        List<ActionVariable> builtIns = ActionStepTypeHelper.FindVariables(step.AllText).Distinct().ToList();
        Dictionary<ActionVariable, string> values = builtIns.Count == 0
            ? []
            : await ActionVariableResolver.ResolveAsync(factory, builtIns, ctx, config);

        List<string> globalNames = ActionStepTypeHelper.FindStoreRefs(step.AllText)
            .Where(r => r.Kind == ActionStoreKind.Global).Select(r => r.Name).ToList();
        Dictionary<string, string?> globals = globalNames.Count == 0 ? [] : await GetGlobalValuesAsync(globalNames);

        return step.Fill(text => ActionStepTypeHelper.ReplaceTokens(text, token => {
            if (ActionStepTypeHelper.TryGetStoreRef(token, out ActionStoreKind kind, out string name))
                return (kind == ActionStoreKind.Secret ? GetSecretValue(name) : globals.GetValueOrDefault(name)) ?? "";
            if (progress.TryReadVariable(token, out string saved)) return saved;

            string root = ActionStepTypeHelper.TokenRoot(token);
            if (outputs.Contains(root) || (root.EndsWith(StatusSuffix, StringComparison.OrdinalIgnoreCase)
                                           && outputs.Contains(root[..^StatusSuffix.Length]))) return "";

            return ActionStepTypeHelper.TryParseToken(token, out ActionVariable variable)
                   && values.TryGetValue(variable, out string? value)
                ? value
                : null;
        }));
    }

    ////////////////// globals & secrets
    public event Action? GlobalsChanged;

    private static string SecretKey(string name) {
        return $"{StorageKeys.ActionSecretPrefix}{name.ToLowerInvariant()}";
    }

    public async Task<List<ActionGlobal>> GetGlobalsAsync(ActionStoreKind? kind = null) {
        await using AppDbContext db = await factory.CreateDbContextAsync();
        return await db.ActionGlobals.AsNoTracking()
            .Where(g => kind == null || g.Kind == kind)
            .OrderBy(g => g.Kind).ThenBy(g => g.Name)
            .ToListAsync();
    }

    private async Task<Dictionary<string, string?>> GetGlobalValuesAsync(List<string> names) {
        var wanted = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        await using AppDbContext db = await factory.CreateDbContextAsync();
        return (await db.ActionGlobals.AsNoTracking().Where(g => g.Kind == ActionStoreKind.Global).ToListAsync())
            .Where(g => wanted.Contains(g.Name))
            .ToDictionary(g => g.Name, g => g.Value, StringComparer.OrdinalIgnoreCase);
    }

    public string? GetSecretValue(string name) {
        return secureStorage?.Get(SecretKey(name));
    }

    public async Task<bool> SetSecretAsync(string name, string value) {
        name = name.Trim();
        if (!ActionStepTypeHelper.IsValidName(name) || secureStorage == null) return false;

        await _globalsLock.WaitAsync();
        try {
            await using AppDbContext db = await factory.CreateDbContextAsync();
            ActionGlobal? row = await db.ActionGlobals
                .FirstOrDefaultAsync(g => g.Kind == ActionStoreKind.Secret && g.Name == name);
            if (row == null)
                db.ActionGlobals.Add(new ActionGlobal {
                    Kind = ActionStoreKind.Secret, Name = name, StorageKey = SecretKey(name)
                });
            else row.UpdatedAt = DateTime.Now;
            await db.SaveChangesAsync();
            if (!secureStorage.Set(SecretKey(name), value)) return false;
        }
        finally {
            _globalsLock.Release();
        }

        GlobalsChanged?.Invoke();
        return true;
    }

    public async Task<string?> SetGlobalAsync(string name, ActionValueType type, string? value) {
        name = name.Trim();
        if (!ActionStepTypeHelper.IsValidName(name)) return "Names are letters, numbers, and _ only";

        string? stored = string.IsNullOrEmpty(value) ? type.DefaultValue() : type.NormalizeValue(value);
        if (stored == null && !string.IsNullOrEmpty(value)) return $"\"{value}\" isn't a {type.GetLabel()} value";

        await _globalsLock.WaitAsync();
        try {
            await using AppDbContext db = await factory.CreateDbContextAsync();
            ActionGlobal? row = await db.ActionGlobals
                .FirstOrDefaultAsync(g => g.Kind == ActionStoreKind.Global && g.Name == name);
            if (row == null) {
                row = new ActionGlobal { Kind = ActionStoreKind.Global, Name = name };
                db.ActionGlobals.Add(row);
            }

            row.ValueType = type;
            row.Value = stored;
            row.UpdatedAt = DateTime.Now;
            await db.SaveChangesAsync();
        }
        finally {
            _globalsLock.Release();
        }

        GlobalsChanged?.Invoke();
        return null;
    }

    public async Task<bool> SetGlobalTypeAsync(string name, ActionValueType type) {
        bool kept;
        await _globalsLock.WaitAsync();
        try {
            await using AppDbContext db = await factory.CreateDbContextAsync();
            ActionGlobal? row = await db.ActionGlobals
                .FirstOrDefaultAsync(g => g.Kind == ActionStoreKind.Global && g.Name == name);
            if (row == null) return false;
            if (row.ValueType == type) return true;

            string? converted = ActionStepTypeHelper.ConvertValue(row.Value, row.ValueType, type);
            kept = converted != null || string.IsNullOrEmpty(row.Value);
            row.Value = converted ?? type.DefaultValue();
            row.ValueType = type;
            row.UpdatedAt = DateTime.Now;
            await db.SaveChangesAsync();
        }
        finally {
            _globalsLock.Release();
        }

        GlobalsChanged?.Invoke();
        return kept;
    }

    public async Task DeleteGlobalAsync(ActionStoreKind kind, string name) {
        await using AppDbContext db = await factory.CreateDbContextAsync();
        await db.ActionGlobals.Where(g => g.Kind == kind && g.Name == name).ExecuteDeleteAsync();
        if (kind == ActionStoreKind.Secret) secureStorage?.Delete(SecretKey(name));
        GlobalsChanged?.Invoke();
    }

    private async Task<bool> RunSetGlobalAsync(ActionStep step, CancellationToken ct) {
        string name = step.Target.Trim();
        await _globalsLock.WaitAsync(ct);
        try {
            await using AppDbContext db = await factory.CreateDbContextAsync(ct);
            ActionGlobal? row = await db.ActionGlobals
                .FirstOrDefaultAsync(g => g.Kind == ActionStoreKind.Global && g.Name == name, ct);
            if (row == null) {
                logger?.LogWarning("[Actions] There is no global named \"{Name}\" to set", name);
                return false;
            }

            string input = row.ValueType == ActionValueType.Text ? step.Body ?? "" : (step.Body ?? "").Trim();
            string? next = step.Operation switch {
                ActionOperation.Toggle when row.ValueType == ActionValueType.Boolean =>
                    row.Value == "true" ? "false" : "true",
                ActionOperation.Adjust when row.ValueType == ActionValueType.Number
                                            && ActionValueType.Number.NormalizeValue(input) is { } amount =>
                    ActionValueType.Number.NormalizeValue(
                        (double.Parse(row.Value ?? "0", CultureInfo.InvariantCulture)
                         + double.Parse(amount, CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture)),
                ActionOperation.Set => row.ValueType.NormalizeValue(input),
                _ => null
            };

            if (next == null) {
                logger?.LogWarning("[Actions] Can't {Operation} %global.{Name}% ({Type}) with \"{Value}\"",
                    step.Operation.GetOpLabel().ToLowerInvariant(), row.Name, row.ValueType.GetLabel(), input);
                return false;
            }

            row.Value = next;
            row.UpdatedAt = DateTime.Now;
            await db.SaveChangesAsync(ct);
        }
        finally {
            _globalsLock.Release();
        }

        GlobalsChanged?.Invoke();
        return true;
    }

    public async Task<int> EnsureGlobalsAsync(CustomAction action) {
        var used = new Dictionary<(ActionStoreKind, string), ActionValueType>(new StoreKeyComparer());
        foreach (ActionNode node in action.Graph.Nodes) {
            foreach ((ActionStoreKind kind, string name) in ActionStepTypeHelper.FindStoreRefs(node.Step.AllText))
                used.TryAdd((kind, name), ActionValueType.Text);
            if (node.Step.Type != ActionStepType.SetGlobal ||
                !ActionStepTypeHelper.IsValidName(node.Step.Target.Trim()))
                continue;

            used[(ActionStoreKind.Global, node.Step.Target.Trim())] = node.Step.Operation switch {
                ActionOperation.Adjust => ActionValueType.Number,
                ActionOperation.Toggle => ActionValueType.Boolean,
                _ => ActionValueType.Text
            };
        }

        if (used.Count == 0) return 0;

        List<ActionGlobal> added;
        await _globalsLock.WaitAsync();
        try {
            await using AppDbContext db = await factory.CreateDbContextAsync();
            HashSet<(ActionStoreKind, string)> known = (await db.ActionGlobals.Select(g => new { g.Kind, g.Name })
                    .ToListAsync())
                .Select(g => (g.Kind, g.Name)).ToHashSet(new StoreKeyComparer());
            added = used.Where(u => !known.Contains(u.Key))
                .Select(u => new ActionGlobal {
                    Kind = u.Key.Item1, Name = u.Key.Item2, ValueType = u.Value,
                    Value = u.Key.Item1 == ActionStoreKind.Global ? u.Value.DefaultValue() : null,
                    StorageKey = u.Key.Item1 == ActionStoreKind.Secret ? SecretKey(u.Key.Item2) : null
                })
                .ToList();
            if (added.Count == 0) return 0;
            db.ActionGlobals.AddRange(added);
            await db.SaveChangesAsync();
        }
        finally {
            _globalsLock.Release();
        }

        logger?.LogInformation("[Actions] \"{Action}\" uses globals / secrets not set up yet: {Names}", action.Name,
            string.Join(", ", added.Select(g => ActionStepTypeHelper.StorePlaceholder(g.Kind, g.Name))));
        GlobalsChanged?.Invoke();
        return added.Count;
    }

    public IReadOnlyList<CustomAction> ActionsUsing(ActionStoreKind kind, string name) {
        return CustomActions.Where(a => a.Graph.Nodes.Any(n =>
                ActionStepTypeHelper.FindStoreRefs(n.Step.AllText)
                    .Any(r => r.Kind == kind && r.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                || (kind == ActionStoreKind.Global && n.Step.Type == ActionStepType.SetGlobal
                                                   && n.Step.Target.Trim().Equals(name,
                                                       StringComparison.OrdinalIgnoreCase))))
            .ToList();
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
        var temp = $"{path}.tmp";
        await File.WriteAllTextAsync(temp, action.ToJson());
        File.Move(temp, path, true);

        _library[action.Id] = (action, path);
        CustomActionsChanged?.Invoke();

        try {
            await EnsureGlobalsAsync(action);
        }
        catch (Exception ex) {
            logger?.LogWarning(ex, "[Actions] Could not add the globals and secrets \"{Name}\" uses", action.Name);
        }
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
            .Where(n => !progress.IsResolved(n.Id))
            .ToDictionary(n => n.Id);
        var running = new Dictionary<Task<bool>, ActionNode>();
        HashSet<string> outputs = graph.OutputVariables();

        void StartReady() {
            bool changed;
            do {
                changed = false;
                foreach (ActionNode node in pending.Values.ToList()) {
                    List<ActionEdge> incoming = graph.IncomingEdges(node.Id).ToList();
                    if (!incoming.All(e => progress.IsResolved(e.From))) continue;
                    pending.Remove(node.Id);

                    if (incoming.Count > 0 && !incoming.Any(progress.IsLive)) {
                        progress.MarkSkipped(node.Id);
                        changed = true;
                        continue;
                    }

                    running[StartStep(node)] = node;
                }
            } while (changed);
        }

        Task<bool> StartStep(ActionNode node) {
            if (node.Disabled) return Task.Run(() => true, ct);
            return node.Step.Type == ActionStepType.Condition
                ? RunConditionAsync(node, ctx, progress, outputs, ct)
                : RunStepAsync(node.Step, ctx, progress, outputs, ct);
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

            if (!ok && node.IgnoreErrors && !ct.IsCancellationRequested) {
                logger?.LogInformation("[Actions] {Label}: \"{Step}\" failed. Ignore Errors = true",
                    ctx.Label ?? ctx.RepeatKey, node.Step.Describe());
                ok = true;
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

    private async Task<bool> RunConditionAsync(ActionNode node, ActionContext ctx, ActionRunProgress progress,
        IReadOnlySet<string> outputs, CancellationToken ct) {
        ct.ThrowIfCancellationRequested();
        ActionStep step = await FillVariablesAsync(node.Step, ctx, progress, outputs);

        bool result = ActionStepTypeHelper.Compare(step.Scope ?? "", step.Operation, step.Target);
        progress.SetPort(node.Id, result ? null : ActionEdge.ElsePort);

        if (logger?.IsEnabled(LogLevel.Debug) ?? false)
            logger.LogDebug("[Actions] {Label}: \"{Step}\" was {Result}", ctx.Label ?? ctx.RepeatKey,
                node.Step.Describe(), result);
        return true;
    }

    private async Task<bool> RunStepAsync(ActionStep step, ActionContext ctx, ActionRunProgress progress,
        IReadOnlySet<string> outputs, CancellationToken ct) {
        ct.ThrowIfCancellationRequested();
        if (!step.Type.IsAvailable()) return false;

        ActionStep original = step;
        if (step.Type.AllowsVariables()) step = await FillVariablesAsync(step, ctx, progress, outputs);

        switch (step.Type) {
            case ActionStepType.HttpGet:
            case ActionStepType.HttpPost:
                return await RunWebRequestAsync(step, original, progress, ct);

            case ActionStepType.SetGlobal:
                return await RunSetGlobalAsync(step, ct);

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

                var amount = (step.Value ?? 1).ToString(CultureInfo.InvariantCulture);
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

    private async Task<bool> RunWebRequestAsync(ActionStep step, ActionStep original, ActionRunProgress progress,
        CancellationToken ct) {
        string? output = string.IsNullOrEmpty(step.OutputVariable) ? null : step.OutputVariable;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(step.Duration > TimeSpan.Zero
            ? step.Duration < MaxRequestTimeout ? step.Duration : MaxRequestTimeout
            : DefaultRequestTimeout);

        try {
            using var request = new HttpRequestMessage(
                step.Type == ActionStepType.HttpGet ? HttpMethod.Get : HttpMethod.Post, step.Target.Trim());
            if (step.Type == ActionStepType.HttpPost) {
                string body = step.Body ?? "";
                string start = body.TrimStart();
                string mediaType = start.StartsWith('{') || start.StartsWith('[') ? "application/json" : "text/plain";
                request.Content = new StringContent(body, Encoding.UTF8, mediaType);
            }

            foreach ((string name, string value) in step.HeaderLines()) {
                if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) {
                    if (request.Content != null && MediaTypeHeaderValue.TryParse(value, out MediaTypeHeaderValue? type))
                        request.Content.Headers.ContentType = type;
                    continue;
                }

                if (!request.Headers.TryAddWithoutValidation(name, value))
                    request.Content?.Headers.TryAddWithoutValidation(name, value);
            }

            request.Headers.Authorization = step.Auth switch {
                ActionHttpAuth.Bearer => new AuthenticationHeaderValue("Bearer", step.AuthToken?.Trim()),
                ActionHttpAuth.Basic => new AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes($"{step.AuthUser}:{step.AuthToken}"))),
                _ => request.Headers.Authorization
            };

            using HttpResponseMessage response =
                await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (output != null) {
                progress.SetVariable(output, await response.Content.ReadAsStringAsync(timeout.Token));
                progress.SetVariable($"{output}{StatusSuffix}", $"{(int)response.StatusCode}");
            }

            if (response.IsSuccessStatusCode) return true;
            logger?.LogWarning("[Actions] {Method} {Url} answered {Status}", request.Method, original.Target,
                (int)response.StatusCode);
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or UriFormatException
                                   || (ex is OperationCanceledException && !ct.IsCancellationRequested)) {
            if (output != null) {
                progress.SetVariable(output, "");
                progress.SetVariable($"{output}{StatusSuffix}", "0");
            }

            logger?.LogWarning("[Actions] {Type} {Url} failed: {Message}", original.Type.GetLabel(), original.Target,
                ex is OperationCanceledException ? "timed out" : ex.Message);
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

    private sealed class StoreKeyComparer : IEqualityComparer<(ActionStoreKind Kind, string Name)> {
        public bool Equals((ActionStoreKind Kind, string Name) x, (ActionStoreKind Kind, string Name) y) {
            return x.Kind == y.Kind && string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
        }

        public int GetHashCode((ActionStoreKind Kind, string Name) key) {
            return HashCode.Combine(key.Kind, StringComparer.OrdinalIgnoreCase.GetHashCode(key.Name));
        }
    }

    private sealed class LiveRun(Guid id, string repeatKey) : IDisposable {
        private readonly CancellationTokenSource _cts = new();
        public Guid Id { get; } = id;
        public string RepeatKey { get; } = repeatKey;
        public CancellationToken Token => _cts.Token;
        public bool Cancelled { get; private set; }
        public bool Superseded { get; set; }

        public void Dispose() {
            _cts.Dispose();
        }

        public void Cancel() {
            Cancelled = true;
            try {
                _cts.Cancel();
            }
            catch (ObjectDisposedException) {
                /* */
            }
        }
    }
}