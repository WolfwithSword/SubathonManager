using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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

    public const string FromActionMeta = "from-action";

    // web step saving to %resp% also sets %resp_status%
    public const string StatusSuffix = ActionRunProgress.StatusSuffix;

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
        SubathonEvents.PromptRunUpdate += OnPromptRunUpdate;
        TriggerWatcher.Start();
        await LoadLibraryAsync();

        await using AppDbContext db = await factory.CreateDbContextAsync(ct);
        await db.WheelSpinHistories
            .Where(h => h.Status == WheelSpinHistoryStatus.Running)
            .ExecuteUpdateAsync(s => s.SetProperty(h => h.Status, WheelSpinHistoryStatus.Pending), ct);
        await db.SubathonPromptRuns
            .Where(r => r.ActionStatus == SubathonPromptActionStatus.Running)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.ActionStatus, SubathonPromptActionStatus.Failed), ct);
    }

    public Task StopAsync(CancellationToken ct = default) {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose() {
        SubathonEvents.SubathonDataUpdate -= OnSubathonDataUpdate;
        WheelEvents.WheelSpinStatusChanged -= OnWheelSpinStatusChanged;
        ActionEvents.CustomActionRunRequested -= OnCustomActionRunRequested;
        SubathonEvents.PromptRunUpdate -= OnPromptRunUpdate;
        _triggerWatcher?.Stop();
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

        if (action.Disabled) {
            logger?.LogInformation("[Actions] RunAction asked for disabled action: \"{Name}\"", action.Name);
            return;
        }

        string user = string.IsNullOrWhiteSpace(ev.User) ? "SYSTEM" : ev.User;
        _ = RunCustomActionAsync(action, user, ev.Source);
    }

    ////////////////// triggers

    private SubathonTriggerWatcher? _triggerWatcher;
    private SubathonTriggerWatcher TriggerWatcher => _triggerWatcher ??= new SubathonTriggerWatcher(OnTrigger);

    public IEnumerable<(CustomAction Action, ActionNode Node)> TriggerSteps() {
        return CustomActions.SelectMany(a => a.Graph.Nodes
            .Where(n => n.Step.Type == ActionStepType.Trigger)
            .Select(n => (a, n)));
    }

    private void OnTrigger(SubathonTrigger trigger, Dictionary<string, string> values, SubathonEvent? subathonEvent) {
        if (subathonEvent != null) {
            if (subathonEvent.EventTypeMeta is LoggedRunMeta or FromActionMeta) return;
            if (!subathonEvent.ProcessedToSubathon && !(config?.GetBool("App", "ShowLockedEvents", false) ?? false))
                return;
        }

        foreach (IGrouping<CustomAction, (CustomAction Action, ActionNode Node)> matched in TriggerSteps()
                     .Where(t => !t.Action.Disabled && !t.Node.Disabled
                                 && t.Node.Step.MatchesTrigger(trigger, subathonEvent))
                     .GroupBy(t => t.Action)) {
            HashSet<string> nodes = matched.Select(t => t.Node.Id).ToHashSet(StringComparer.Ordinal);
            _ = RunTriggeredAsync(matched.Key, trigger, nodes, values, subathonEvent);
        }
    }

    private async Task<ActionRunResult> RunTriggeredAsync(CustomAction action, SubathonTrigger trigger,
        IReadOnlySet<string> nodes, IReadOnlyDictionary<string, string> values, SubathonEvent? subathonEvent,
        string? repeatKey = null) {
        string user = subathonEvent?.User is { } named && !string.IsNullOrWhiteSpace(named) ? named : "SYSTEM";
        var ctx = new ActionContext(subathonEvent?.Source ?? SubathonEventSource.Unknown, user,
            repeatKey ?? $"custom-action-{action.Id}", action.Name, trigger, nodes, values);
        try {
            LogRun(action.Name, ctx);
            return await RunAsync(Guid.NewGuid(), action.Graph, ctx);
        }
        catch (Exception ex) {
            logger?.LogError(ex, "[Actions] Running {Name} from {Trigger} failed", action.Name, trigger);
            return ActionRunResult.Paused;
        }
    }

    ////////////////// actions switching and running other actions
    public const int MaxActionDepth = 8;

    public CustomAction? ResolveActionTarget(ActionStep step) {
        if (Guid.TryParse(step.Target.Trim(), out Guid id) && GetCustomAction(id) is { } byId) return byId;
        string name = string.IsNullOrWhiteSpace(step.TargetName) ? step.Target : step.TargetName;
        return string.IsNullOrWhiteSpace(name) ? null : FindCustomAction(name);
    }

    public async Task<bool> SetCustomActionEnabledAsync(Guid id, bool enabled) {
        if (GetCustomAction(id) is not { } action) return false;
        if (action.Disabled != enabled) return true;
        CustomAction copy = action.Clone();
        copy.Disabled = !enabled;
        await SaveCustomActionAsync(copy);
        logger?.LogInformation("[Actions] \"{Name}\" turned {State}", action.Name, enabled ? "on" : "off");
        return true;
    }

    private async Task<bool> RunSetActionEnabledAsync(ActionStep step) {
        if (ResolveActionTarget(step) is not { } target) {
            logger?.LogWarning("[Actions] There is no action \"{Name}\" to turn on/off", step.TargetName ?? step.Target);
            return false;
        }

        bool enable = step.Operation switch {
            ActionOperation.Enable => true,
            ActionOperation.Disable => false,
            _ => target.Disabled
        };
        return await SetCustomActionEnabledAsync(target.Id, enable);
    }

    private async Task<bool> RunOtherActionAsync(ActionStep step, ActionContext ctx, CancellationToken ct) {
        if (ResolveActionTarget(step) is not { } target) {
            logger?.LogWarning("[Actions] There is no action \"{Name}\" to run", step.TargetName ?? step.Target);
            return false;
        }

        if (target.Disabled) {
            logger?.LogInformation("[Actions] Not running disabled action \"{Name}\"", target.Name);
            return true;
        }

        if (ctx.Depth >= MaxActionDepth) {
            logger?.LogWarning("[Actions] Not running \"{Name}\": action(s) are running each other more than {Max} levels deep",
                target.Name, MaxActionDepth);
            return false;
        }

        var child = new ActionContext(ctx.Source, ctx.User, $"custom-action-{target.Id}", target.Name,
            Depth: ctx.Depth + 1);
        LogRun(target.Name, child);
        Task<ActionRunResult> run = RunChildAsync(target, child);
        if (step.Operation == ActionOperation.Start) return true;

        ActionRunResult result = await run.WaitAsync(ct);
        return result is ActionRunResult.Done or ActionRunResult.Skipped;
    }

    private async Task<ActionRunResult> RunChildAsync(CustomAction action, ActionContext ctx) {
        try {
            return await RunAsync(Guid.NewGuid(), action.Graph, ctx);
        }
        catch (Exception ex) {
            logger?.LogError(ex, "[Actions] Running {Name} from another action failed", action.Name);
            return ActionRunResult.Paused;
        }
    }

    public Task<ActionRunResult> TestTriggerAsync(CustomAction action, string nodeId, bool unsavedCopy = false) {
        ActionNode? node = action.Graph.Nodes.FirstOrDefault(n => n.Id == nodeId);
        if (node?.Step is not { Type: ActionStepType.Trigger, Trigger: { } trigger })
            return Task.FromResult(ActionRunResult.Skipped);

        Dictionary<string, string> values = SubathonTriggerWatcher.SampleValues(trigger);
        if (trigger == SubathonTrigger.SubathonEvent
            && node.Step.EventTypes?.FirstOrDefault() is { } picked
            && Enum.TryParse(picked, out SubathonEventType eventType)) {
            values["eventtype"] = $"{eventType}";
            values["source"] = $"{SubathonEventSource.Simulated}";
        }

        values["user"] = values.GetValueOrDefault("user") is { Length: > 0 } user ? user : "CustomAction";
        return RunTriggeredAsync(action, trigger, new HashSet<string>([nodeId]), values,
            new SubathonEvent { Source = SubathonEventSource.Simulated, User = values["user"] },
            unsavedCopy ? $"custom-action-test-{action.Id}" : null);
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

    private void NotifyGlobals(params string[] globalNames) {
        GlobalsChanged?.Invoke();
        ActionEvents.RaiseGlobalsUpdated(globalNames);
    }

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

    public async Task<Dictionary<string, ActionValueType>> GetGlobalTypesAsync() {
        return (await GetGlobalsAsync(ActionStoreKind.Global))
            .ToDictionary(g => g.Name, g => g.ValueType, StringComparer.OrdinalIgnoreCase);
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

        NotifyGlobals();
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

        NotifyGlobals(name);
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

        NotifyGlobals(name);
        return kept;
    }

    public async Task DeleteGlobalAsync(ActionStoreKind kind, string name) {
        await using AppDbContext db = await factory.CreateDbContextAsync();
        await db.ActionGlobals.Where(g => g.Kind == kind && g.Name == name).ExecuteDeleteAsync();
        if (kind == ActionStoreKind.Secret) secureStorage?.Delete(SecretKey(name));
        if (kind == ActionStoreKind.Global) NotifyGlobals(name);
        else NotifyGlobals();
    }

    private async Task<bool> RunSetGlobalAsync(ActionStep step, CancellationToken ct) {
        (_, _, string? error) = await ChangeGlobalAsync(step.Target, null, step.Operation, step.Body ?? "", false, ct);
        if (error == null) return true;
        logger?.LogWarning("[Actions] {Error}", error);
        return false;
    }

    public async Task<(ActionGlobal? Global, bool Created, string? Error)> ChangeGlobalAsync(string name,
        ActionValueType? type, ActionOperation operation, string? value, bool create, CancellationToken ct = default) {
        name = name.Trim();
        if (!ActionStepTypeHelper.IsValidName(name))
            return (null, false, $"\"{name}\" isn't a valid global name (letters, numbers, and _ only)");

        ActionGlobal? row;
        bool created;
        await _globalsLock.WaitAsync(ct);
        try {
            await using AppDbContext db = await factory.CreateDbContextAsync(ct);
            row = (await db.ActionGlobals.Where(g => g.Kind == ActionStoreKind.Global).ToListAsync(ct))
                .FirstOrDefault(g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            created = row == null;

            if (row == null && !create) return (null, false, $"There is no global named \"{name}\" to set");
            if (row != null && type != null && type != row.ValueType)
                return (null, false, $"%global.{row.Name}% is {row.ValueType.GetLabel()}, not {type.Value.GetLabel()}");

            ActionValueType? implied = operation switch {
                ActionOperation.Adjust => ActionValueType.Number,
                ActionOperation.Toggle => ActionValueType.Boolean,
                _ => null
            };
            if (row == null && (type ?? implied) == null)
                return (null, false, $"A type is needed to make the global \"{name}\"");

            ActionValueType valueType = row?.ValueType ?? type ?? implied!.Value;
            string current = valueType.NormalizeValue(row?.Value) ?? valueType.DefaultValue();
            string input = valueType == ActionValueType.Text ? value ?? "" : (value ?? "").Trim();
            string? next = operation switch {
                ActionOperation.Toggle when valueType == ActionValueType.Boolean =>
                    current == "true" ? "false" : "true",
                ActionOperation.Adjust when valueType == ActionValueType.Number
                                            && ActionValueType.Number.NormalizeValue(input) is { } amount =>
                    ActionValueType.Number.NormalizeValue(
                        (double.Parse(current, CultureInfo.InvariantCulture)
                         + double.Parse(amount, CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture)),
                ActionOperation.Set when value != null => valueType.NormalizeValue(input),
                _ => null
            };

            if (next == null)
                return (null, false, operation is ActionOperation.Set or ActionOperation.Adjust && value == null
                    ? $"A value is needed to {operation.GetOpLabel().ToLowerInvariant()} %global.{row?.Name ?? name}%"
                    : $"Can't {operation.GetOpLabel().ToLowerInvariant()} %global.{row?.Name ?? name}% " +
                      $"({valueType.GetLabel()}) with \"{input}\"");

            if (row == null) {
                row = new ActionGlobal { Kind = ActionStoreKind.Global, Name = name, ValueType = valueType };
                db.ActionGlobals.Add(row);
            }

            row.Value = next;
            row.UpdatedAt = DateTime.Now;
            await db.SaveChangesAsync(ct);
        }
        finally {
            _globalsLock.Release();
        }

        NotifyGlobals(row.Name);
        return (row, created, null);
    }

    public async Task<int> EnsureGlobalsAsync(CustomAction action, bool reportConflicts = false) {
        var used = new Dictionary<(ActionStoreKind, string), (ActionValueType Type, bool Strict)>(
            new StoreKeyComparer());
        foreach (ActionNode node in action.Graph.Nodes) {
            foreach ((ActionStoreKind kind, string name) in ActionStepTypeHelper.FindStoreRefs(node.Step.AllText))
                used.TryAdd((kind, name), (ActionValueType.Text, false));
            if (node.Step.Type != ActionStepType.SetGlobal ||
                !ActionStepTypeHelper.IsValidName(node.Step.Target.Trim()))
                continue;

            used[(ActionStoreKind.Global, node.Step.Target.Trim())] = node.Step.Operation switch {
                ActionOperation.Adjust => (ActionValueType.Number, true),
                ActionOperation.Toggle => (ActionValueType.Boolean, true),
                _ => (ActionValueType.Text, false)
            };
        }

        return (await EnsureStoreAsync(used, $"Action \"{action.Name}\"", reportConflicts)).Added;
    }

    public async Task<IReadOnlyList<GlobalTypeConflict>> EnsureTypedGlobalsAsync(
        IReadOnlyDictionary<string, ActionValueType> wanted, string source) {
        Dictionary<(ActionStoreKind, string), (ActionValueType, bool)> used = wanted
            .Where(w => ActionStepTypeHelper.IsValidName(w.Key.Trim()))
            .DistinctBy(w => w.Key.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(w => (ActionStoreKind.Global, w.Key.Trim()), w => (w.Value, true), new StoreKeyComparer());
        return (await EnsureStoreAsync(used, source, true)).Conflicts;
    }

    public event Action<string, IReadOnlyList<GlobalTypeConflict>>? GlobalTypeConflictsFound;

    private async Task<(int Added, IReadOnlyList<GlobalTypeConflict> Conflicts)> EnsureStoreAsync(
        Dictionary<(ActionStoreKind, string), (ActionValueType Type, bool Strict)> used, string source,
        bool reportConflicts) {
        if (used.Count == 0) return (0, []);

        List<ActionGlobal> added;
        List<GlobalTypeConflict> conflicts;
        await _globalsLock.WaitAsync();
        try {
            await using AppDbContext db = await factory.CreateDbContextAsync();
            Dictionary<(ActionStoreKind, string), ActionGlobal> known = (await db.ActionGlobals.AsNoTracking()
                    .ToListAsync())
                .ToDictionary(g => (g.Kind, g.Name), g => g, new StoreKeyComparer());

            conflicts = used.Where(u => u.Value.Strict && known.TryGetValue(u.Key, out ActionGlobal? g)
                                                       && g.Kind == ActionStoreKind.Global
                                                       && g.ValueType != u.Value.Type)
                .Select(u => new GlobalTypeConflict(known[u.Key].Name, known[u.Key].ValueType, u.Value.Type))
                .ToList();

            added = used.Where(u => !known.ContainsKey(u.Key))
                .Select(u => new ActionGlobal {
                    Kind = u.Key.Item1, Name = u.Key.Item2, ValueType = u.Value.Type,
                    Value = u.Key.Item1 == ActionStoreKind.Global ? u.Value.Type.DefaultValue() : null,
                    StorageKey = u.Key.Item1 == ActionStoreKind.Secret ? SecretKey(u.Key.Item2) : null
                })
                .ToList();
            if (added.Count > 0) {
                db.ActionGlobals.AddRange(added);
                await db.SaveChangesAsync();
            }
        }
        finally {
            _globalsLock.Release();
        }

        if (added.Count > 0) {
            logger?.LogInformation("[Actions] {Source} uses globals / secrets not set up yet: {Names}", source,
                string.Join(", ", added.Select(g => ActionStepTypeHelper.StorePlaceholder(g.Kind, g.Name))));
            NotifyGlobals(added.Where(g => g.Kind == ActionStoreKind.Global).Select(g => g.Name).ToArray());
        }

        if (conflicts.Count > 0 && reportConflicts) {
            logger?.LogWarning("[Actions] {Source} expects different types for: {Names}", source,
                string.Join(", ", conflicts.Select(c => $"{c.Name} ({c.Existing} -> {c.Wanted})")));
            GlobalTypeConflictsFound?.Invoke(source, conflicts);
        }

        return (added.Count, conflicts);
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

    public async Task SaveCustomActionAsync(CustomAction action, bool imported = false) {
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
            await EnsureGlobalsAsync(action, imported);
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
        await SaveCustomActionAsync(action, true);
        return (action, replaced);
    }

    public async Task<CustomAction?> AddCustomActionCopyAsync(string file) {
        if (!_libraryLoaded) await LoadLibraryAsync();
        CustomAction? action = await ReadActionFileAsync(file);
        if (action == null) return null;

        if (_library.ContainsKey(action.Id)) action.Id = Guid.NewGuid();
        action.Name = UniqueName(action.Name);
        await SaveCustomActionAsync(action, true);
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
        return Guid.TryParse(parameter, out Guid id) && GetCustomAction(id) is { Disabled: false } action
            ? action.Graph : null;
    }

    public async Task<ActionRunResult> RunAsync(Guid runId, ActionGraph graph, ActionContext ctx,
        ActionRunProgress? progress = null, Func<ActionRunProgress, Task>? onProgress = null) {
        progress ??= new ActionRunProgress();

        if (!graph.IsValid(out string invalid)) {
            logger?.LogWarning("[Actions] Not running {Label}: {Error}", ctx.Label ?? ctx.RepeatKey, invalid);
            return ActionRunResult.Paused;
        }

        ActionRepeatMode mode = graph.EffectiveRepeat;
        List<LiveRun> overlapping = _runs.Values.Where(r => r.RepeatKey == ctx.RepeatKey && r.Id != runId).ToList();
        if (overlapping.Count > 0 && mode == ActionRepeatMode.Skip) return ActionRunResult.Skipped;
        if (mode == ActionRepeatMode.Restart)
            foreach (LiveRun previous in overlapping) {
                previous.Superseded = true;
                previous.Cancel();
            }

        // queue logic
        Task ahead = Task.CompletedTask;
        TaskCompletionSource? turn = null;
        if (mode == ActionRepeatMode.Queue) {
            turn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_queueTails) {
                ahead = _queueTails.GetValueOrDefault(ctx.RepeatKey) ?? Task.CompletedTask;
                _queueTails[ctx.RepeatKey] = turn.Task;
            }
        }

        var live = new LiveRun(runId, ctx.RepeatKey);
        try {
            if (!_runs.TryAdd(runId, live)) return ActionRunResult.Skipped;
            try {
                await ahead.WaitAsync(live.Token);
            }
            catch (OperationCanceledException) {
                return ActionRunResult.Cancelled;
            }

            bool finished = await RunGraphAsync(graph, ctx, progress, onProgress, live.Token);
            if (live.Superseded || finished) return ActionRunResult.Done;
            return live.Cancelled ? ActionRunResult.Cancelled : ActionRunResult.Paused;
        }
        finally {
            _runs.TryRemove(runId, out _);
            live.Dispose();
            if (turn != null) {
                void Release() {
                    turn.TrySetResult();
                    lock (_queueTails) {
                        if (_queueTails.GetValueOrDefault(ctx.RepeatKey) == turn.Task)
                            _queueTails.Remove(ctx.RepeatKey);
                    }
                }

                if (ahead.IsCompleted) Release();
                else _ = ahead.ContinueWith(_ => Release(), TaskScheduler.Default);
            }
        }
    }

    private readonly Dictionary<string, Task> _queueTails = new(StringComparer.Ordinal);

    private async Task<bool> RunGraphAsync(ActionGraph graph, ActionContext ctx, ActionRunProgress progress,
        Func<ActionRunProgress, Task>? onProgress, CancellationToken outerCt) {
        using var failCts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        CancellationToken ct = failCts.Token;

        Dictionary<string, ActionNode> pending = graph.Nodes
            .Where(n => !progress.IsResolved(n.Id))
            .ToDictionary(n => n.Id);
        var running = new Dictionary<Task<bool>, ActionNode>();
        HashSet<string> outputs = graph.OutputVariables();

        if (graph.Nodes.Any(n => n.Step.Type == ActionStepType.Trigger)) outputs.Add(ActionRunProgress.TriggerVariable);
        if (ctx.TriggerValues != null && !progress.TryReadVariable(ActionRunProgress.TriggerVariable, out _))
            progress.SetVariable(ActionRunProgress.TriggerVariable, JsonSerializer.Serialize(ctx.TriggerValues));

        bool StartsRun(ActionNode node) {
            return node.Step.Type == ActionStepType.Trigger
                ? ctx.TriggerNodes?.Contains(node.Id) ?? false : ctx.Trigger == null;
        }

        void StartReady() {
            bool changed;
            do {
                changed = false;
                foreach (ActionNode node in pending.Values.ToList()) {
                    List<ActionEdge> incoming = graph.IncomingEdges(node.Id).ToList();
                    if (!incoming.All(e => progress.IsResolved(e.From))) continue;
                    pending.Remove(node.Id);

                    if (incoming.Count > 0 ? !incoming.Any(progress.IsLive) : !StartsRun(node)) {
                        progress.MarkSkipped(node.Id);
                        changed = true;
                        continue;
                    }

                    running[StartStep(node)] = node;
                }
            } while (changed);
        }

        Task<bool> StartStep(ActionNode node) {
            if (node.Disabled || node.Step.Type == ActionStepType.Trigger) return Task.Run(() => true, ct);
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

            case ActionStepType.SetActionEnabled:
                return await RunSetActionEnabledAsync(step);

            case ActionStepType.RunAction:
                return await RunOtherActionAsync(step, ctx, ct);

            case ActionStepType.Wait:
                await Task.Delay(step.Duration, ct); // when here, easier to do a delay vs the timerservice callback
                return true;

            case ActionStepType.AddTime:
            case ActionStepType.SubtractTime: {
                SubathonCommandType cmd = step.Type == ActionStepType.AddTime
                    ? SubathonCommandType.AddTime : SubathonCommandType.SubtractTime;
                SubathonEvents.RaiseSubathonEventCreated(new SubathonEvent {
                    Source = ctx.Source,
                    EventTimestamp = DateTime.Now,
                    Command = cmd,
                    EventType = SubathonEventType.Command,
                    User = ctx.User,
                    Value = $"{cmd} {Utils.FormatShortDuration(step.Duration)}",
                    SecondsValue = step.Duration.TotalSeconds,
                    PointsValue = 0,
                    EventTypeMeta = FromActionMeta
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
                    Value = $"{amount}|{duration}|{points}|{time}",
                    EventTypeMeta = FromActionMeta
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

    ////////////////// prompts
    private void OnPromptRunUpdate(SubathonPromptRun run, SubathonPrompt? prompt) {
        if (run is { Status: SubathonPromptRunStatus.Completed, ActionId: not null })
            _ = RunPromptActionAsync(run.Id);
    }

    public async Task<ActionRunResult> RunPromptActionAsync(Guid runId, bool retry = false) {
        SubathonPromptRun? run;
        try {
            await using AppDbContext db = await factory.CreateDbContextAsync();
            run = await db.SubathonPromptRuns.Include(r => r.LinkedPrompt).AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == runId);
            if (run is not { Status: SubathonPromptRunStatus.Completed, ActionId: not null })
                return ActionRunResult.Skipped;

            SubathonPromptActionStatus from = retry ? SubathonPromptActionStatus.Failed : SubathonPromptActionStatus.None;
            int claimed = await db.SubathonPromptRuns.Where(r => r.Id == runId && r.ActionStatus == from)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.ActionStatus, SubathonPromptActionStatus.Running));
            if (claimed == 0) return ActionRunResult.Skipped;
        }
        catch (Exception ex) {
            logger?.LogError(ex, "[Actions] Could not start the action for prompt run {Id}", runId);
            return ActionRunResult.Skipped;
        }

        run.ActionStatus = SubathonPromptActionStatus.Running;
        SubathonEvents.RaisePromptRunActionStatusChanged(run);

        CustomAction? action = GetCustomAction(run.ActionId.Value);
        ActionGraph? graph = ResolveGraph(WheelSpinActionType.CustomAction, run.ActionId.Value.ToString());
        if (action == null || graph == null) {
            logger?.LogWarning("[Actions] Prompt \"{Prompt}\" completed, but action {Reason}",
                run.LinkedPrompt?.Text, action == null ? "is missing from the actions library" : "is disabled");
            await SavePromptActionAsync(run, SubathonPromptActionStatus.Failed, run.ActionProgress);
            return ActionRunResult.Skipped;
        }

        var ctx = new ActionContext(SubathonEventSource.Unknown, "Prompt", $"custom-action-{action.Id}",
            run.LinkedPrompt?.Text ?? action.Name);
        LogRun(action.Name, ctx);

        ActionRunProgress progress = ActionRunProgress.Parse(run.ActionProgress);
        ActionRunResult result;
        try {
            result = await RunAsync(runId, graph, ctx, progress, async p => {
                string json = p.ToJson();
                await using AppDbContext db = await factory.CreateDbContextAsync();
                await db.SubathonPromptRuns.Where(r => r.Id == runId)
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.ActionProgress, json));
            });
        }
        catch (Exception ex) {
            logger?.LogError(ex, "[Actions] \"{Action}\" failed for prompt \"{Prompt}\"", action.Name,
                run.LinkedPrompt?.Text);
            result = ActionRunResult.Paused;
        }

        bool done = result == ActionRunResult.Done;
        await SavePromptActionAsync(run, done ? SubathonPromptActionStatus.Done : SubathonPromptActionStatus.Failed,
            done ? null : progress.ToJson());
        return result;
    }

    private async Task SavePromptActionAsync(SubathonPromptRun run, SubathonPromptActionStatus status,
        string? progress) {
        run.ActionStatus = status;
        run.ActionProgress = progress;
        try {
            await using AppDbContext db = await factory.CreateDbContextAsync();
            await db.SubathonPromptRuns.Where(r => r.Id == run.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.ActionStatus, status)
                    .SetProperty(r => r.ActionProgress, progress));
        }
        catch (Exception ex) {
            logger?.LogError(ex, "[Actions] Could not save action status for prompt run {Id}", run.Id);
        }

        SubathonEvents.RaisePromptRunActionStatusChanged(run);
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