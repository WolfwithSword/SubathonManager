using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Objects;
using SubathonManager.Data;
using SubathonManager.Services;
using SubathonManager.UI.Services;
using SubathonManager.UI.UiUtils;

namespace SubathonManager.UI.Views.Actions;

public partial class ActionsView : UserControl {
    private readonly IDbContextFactory<AppDbContext> _factory =
        AppServices.Provider.GetRequiredService<IDbContextFactory<AppDbContext>>();

    private readonly ILogger? _logger = AppServices.Provider.GetService<ILogger<ActionsView>>();
    private List<ActionCard> _cards = [];
    private Guid? _selectedId;

    public ActionsView() {
        InitializeComponent();
        SearchBox.TextChanged += (_, _) => Refresh();
        InitStore();

        Loaded += (_, _) => {
            ServiceManager.Actions.CustomActionsChanged -= OnLibraryChanged;
            ServiceManager.Actions.CustomActionsChanged += OnLibraryChanged;

            ServiceManager.Actions.GlobalsChanged -= OnGlobalsChanged;
            ServiceManager.Actions.GlobalsChanged += OnGlobalsChanged;
            Refresh();
            RefreshTriggers();
            _ = RefreshStoreAsync();
        };
        Unloaded += (_, _) => {
            ServiceManager.Actions.CustomActionsChanged -= OnLibraryChanged;
            ServiceManager.Actions.GlobalsChanged -= OnGlobalsChanged;
        };
    }

    private void OnLibraryChanged() {
        Dispatcher.UIThread.Post(() => {
            Refresh();
            RefreshTriggers();
            _ = RefreshStoreAsync();
        });
    }

    private void Refresh() {
        IReadOnlyList<CustomAction> all = ServiceManager.Actions.CustomActions;
        Dictionary<string, int> wheelUses = WheelUses();
        string query = (SearchBox.Text ?? "").Trim();
        List<ActionCard> cards = all
            .Where(a => query.Length == 0
                        || a.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || (a.Author?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                        || (a.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
            .Select(a => new ActionCard(a) {
                IsSelected = a.Id == _selectedId, WheelUses = wheelUses.GetValueOrDefault(a.Id.ToString())
            })
            .ToList();

        _cards = cards;
        ActionsList.ItemsSource = cards;
        EmptyText.IsVisible = cards.Count == 0;
        EmptyText.Text = all.Count == 0
            ? "No actions yet. Create one, import a .sma file, or convert a compatible wheel action"
            : $"Nothing matches \"{query}\"";
        ShowDetails();
    }

    private Dictionary<string, int> WheelUses() {
        try {
            using AppDbContext db = _factory.CreateDbContext();
            return db.WheelSpinActions
                .Where(a => a.ActionType == WheelSpinActionType.CustomAction && !string.IsNullOrWhiteSpace(a.Parameter))
                .GroupBy(a => a.Parameter!)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToDictionary(g => g.Key, g => g.Count, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) {
            _logger?.LogWarning(ex, "[Actions] Could not count wheel items using actions");
            return [];
        }
    }

    public void ShowAdded(CustomAction? action, string source) {
        if (action == null) {
            StatusText.Text = $"{Path.GetFileName(source)} is not a valid custom action";
            return;
        }

        SearchBox.Text = "";
        _selectedId = action.Id;
        Refresh();
        StatusText.Text = $"Added \"{action.Name}\" to your actions";
    }

    private static CustomAction? CardOf(object? sender) {
        return (sender as Control)?.DataContext is ActionCard card ? card.Action : null;
    }

    private void Card_Tapped(object? sender, TappedEventArgs e) {
        if (CardOf(sender) is not { } action) return;
        _selectedId = action.Id;
        foreach (ActionCard card in _cards) card.IsSelected = card.Action.Id == action.Id;
        ShowDetails();
    }

    private void ShowDetails() {
        CustomAction? action = _selectedId is { } id ? ServiceManager.Actions.GetCustomAction(id) : null;
        DetailsPanel.IsVisible = action != null;
        NoSelectionText.IsVisible = action == null;
        if (action == null) {
            _selectedId = null;
            return;
        }

        DetailTitle.Text = action.Name;
        _detailSuppress = true;
        EnabledSwitch.IsChecked = !action.Disabled;
        _detailSuppress = false;

        var meta = new List<string> { $"v{action.Version}" };
        if (!string.IsNullOrWhiteSpace(action.Author)) meta.Add($"by {action.Author}");
        string? path = ServiceManager.Actions.GetCustomActionPath(action.Id);
        if (path != null && File.Exists(path)) meta.Add($"updated {File.GetLastWriteTime(path):yyyy-MM-dd HH:mm}");
        DetailMeta.Text = string.Join("  -  ", meta);

        DetailSteps.Text = $"{action.Graph.Nodes.Count}";
        DetailBranches.Text = $"{CountBranches(action.Graph)}";
        DetailError.Text = action.Graph.IsValid(out string error) ? "" : $"Needs fixing: {error}";
        DetailError.IsVisible = DetailError.Text.Length > 0;
        DetailDescription.Text = string.IsNullOrWhiteSpace(action.Description) ? "No description." : action.Description;
    }

    private static int CountBranches(ActionGraph graph) {
        if (graph.Nodes.Count == 0 || !graph.IsValid(out _)) return 0;
        var paths = new Dictionary<string, int>(StringComparer.Ordinal);

        int PathsFrom(string id) {
            if (paths.TryGetValue(id, out int known)) return known;
            List<string> next = graph.Outgoing(id).ToList();
            int count = next.Count == 0 ? 1 : next.Sum(PathsFrom);
            paths[id] = count;
            return count;
        }

        return graph.Nodes.Where(n => !graph.Incoming(n.Id).Any()).Sum(n => PathsFrom(n.Id));
    }

    private async void Run_Click(object? sender, RoutedEventArgs e) {
        if (CardOf(sender) is { } action) await RunAsync(action);
    }

    private bool _detailSuppress;

    private async void EnabledSwitch_Changed(object? sender, RoutedEventArgs e) {
        if (_detailSuppress || _selectedId is not { } id) return;
        bool enabled = EnabledSwitch.IsChecked == true;
        if (!await ServiceManager.Actions.SetCustomActionEnabledAsync(id, enabled)) return;
        StatusText.Text = enabled ? "Turned on" : "Turned off";
    }

    private async void RunSelected_Click(object? sender, RoutedEventArgs e) {
        if (_selectedId is { } id && ServiceManager.Actions.GetCustomAction(id) is { } action) await RunAsync(action);
    }

    private async Task RunAsync(CustomAction action) {
        if (!action.Graph.IsValid(out string error) || action.Graph.Nodes.Count == 0) {
            StatusText.Text = action.Graph.Nodes.Count == 0
                ? $"\"{action.Name}\" has no steps yet"
                : $"\"{action.Name}\" can't run yet: {error}";
            return;
        }

        StatusText.Text = $"Running \"{action.Name}\"...";
        ActionRunResult result = await ServiceManager.Actions.RunManuallyAsync(action);
        StatusText.Text = result switch {
            ActionRunResult.Done => $"\"{action.Name}\" finished",
            ActionRunResult.Skipped => $"\"{action.Name}\" is already running and ignores repeats",
            ActionRunResult.Cancelled => $"\"{action.Name}\" was cancelled",
            _ => $"\"{action.Name}\" stopped at a step that could not run (is the app connected?) - see the log"
        };
    }

    private void EditSelected_Click(object? sender, RoutedEventArgs e) {
        if (_selectedId is { } id && ServiceManager.Actions.GetCustomAction(id) is { } action)
            OpenEditor(action);
    }

    private void OpenEditor(CustomAction action) {
        ActionEditorWindow editor = ActionEditorWindow.Open(action);
        editor.Saved -= OnEditorSaved;
        editor.Saved += OnEditorSaved;
    }

    private void OnEditorSaved(Guid id) {
        _selectedId = id;
        Refresh();
    }

    private void Card_DoubleTapped(object? sender, TappedEventArgs e) {
        if (CardOf(sender) is { } action) OpenEditor(action);
    }

    private void Edit_Click(object? sender, RoutedEventArgs e) {
        if (CardOf(sender) is { } action) OpenEditor(action);
    }

    private void CreateNew_Click(object? sender, RoutedEventArgs e) {
        OpenEditor(new CustomAction { Name = "New Action" });
    }

    private async void Duplicate_Click(object? sender, RoutedEventArgs e) {
        if (CardOf(sender) is not { } original) return;
        CustomAction copy = await ServiceManager.Actions.DuplicateCustomActionAsync(original);
        _selectedId = copy.Id;
        Refresh();
        StatusText.Text = $"Duplicated \"{original.Name}\" as \"{copy.Name}\"";
    }

    private async void Export_Click(object? sender, RoutedEventArgs e) {
        if (CardOf(sender) is not { } action) return;
        try {
            string path = await ActionService.ExportCustomActionAsync(action);
            StatusText.Text = $"Exported to {path}";
            UiHelpers.OpenFolder(Path.GetDirectoryName(path));
        }
        catch (Exception ex) {
            _logger?.LogError(ex, "[Actions] Export of {Name} failed", action.Name);
            StatusText.Text = $"Export failed: {ex.Message}";
        }
    }

    private async void Delete_Click(object? sender, RoutedEventArgs e) {
        if (CardOf(sender) is not { } action) return;

        var id = action.Id.ToString();
        await using AppDbContext db = await _factory.CreateDbContextAsync();

        // what stops when it's gone: wheel items pointing at it, and its own trigger steps
        int uses = await db.WheelSpinActions
            .CountAsync(a => a.ActionType == WheelSpinActionType.CustomAction && a.Parameter == id);
        int triggers = action.Graph.Nodes.Count(n => n.Step.Type == ActionStepType.Trigger && !n.Disabled);
        var effects = new List<string>();
        if (uses > 0) effects.Add($"{uses} wheel item(s) use it and will stop running until you pick another action");
        if (triggers > 0) effects.Add($"its {triggers} trigger(s) will stop firing");

        var dialog = new FAContentDialog {
            Title = "Delete action",
            Content = effects.Count > 0
                ? $"Delete \"{action.Name}\"? {string.Join(", and ", effects)}."
                : $"Delete \"{action.Name}\"? Its .sma file is removed from the actions folder.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel"
        };
        if (await dialog.ShowAsync() != FAContentDialogResult.Primary) return;

        ServiceManager.Actions.DeleteCustomAction(action.Id);
        StatusText.Text = $"Deleted \"{action.Name}\"";
    }

    private async void Import_Click(object? sender, RoutedEventArgs e) {
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        IReadOnlyList<IStorageFile> picked = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
            Title = "Import Custom Action",
            AllowMultiple = true,
            FileTypeFilter = [
                new FilePickerFileType("Subathon Manager Action") { Patterns = [$"*{CustomAction.FileExtension}"] }
            ]
        });
        if (picked.Count == 0) return;

        int added = 0, updated = 0, failed = 0;
        foreach (IStorageFile file in picked) {
            (CustomAction Action, bool Replaced)? result =
                await ServiceManager.Actions.ImportCustomActionAsync(file.Path.LocalPath);
            if (result == null) failed++;
            else if (result.Value.Replaced) updated++;
            else added++;
        }

        StatusText.Text = $"Imported {added}, updated {updated}" + (failed > 0 ? $", {failed} not valid" : "");
    }

    private void OpenActionsFolder_Click(object? sender, RoutedEventArgs e) {
        OpenFolder(ActionService.DefaultActionsFolder);
    }

    private void OpenExportsFolder_Click(object? sender, RoutedEventArgs e) {
        OpenFolder(ActionService.ExportsFolder);
    }

    private async void Reload_Click(object? sender, RoutedEventArgs e) {
        await ServiceManager.Actions.LoadLibraryAsync();
        StatusText.Text = $"Loaded {ServiceManager.Actions.CustomActions.Count} action(s)";
    }

    private void OpenFolder(string path) {
        try {
            Directory.CreateDirectory(path);
            if (!UiHelpers.OpenFolder(path)) _logger?.LogWarning("[Actions] Unable to open {Path}", path);
        }
        catch (Exception ex) {
            _logger?.LogWarning(ex, "[Actions] Unable to open {Path}", path);
        }
    }
}

public sealed class ActionCard(CustomAction action) : INotifyPropertyChanged {
    private bool _isSelected;

    public CustomAction Action { get; } = action;

    public bool IsSelected {
        get => _isSelected;
        set {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public string Name => Action.Name;
    public double CardOpacity => Action.Disabled ? 0.55 : 1;
    public int WheelUses { get; init; }

    //
    public string ModeLetter => Action.Graph.EffectiveRepeat switch {
        ActionRepeatMode.Restart => "R",
        ActionRepeatMode.Parallel => "P",
        ActionRepeatMode.Queue => "Q",
        _ => "S"
    };

    //
    public string ModeTip => "Concurrency Mode: " + Action.Graph.EffectiveRepeat switch {
        ActionRepeatMode.Restart => "Restart",
        ActionRepeatMode.Parallel => "Parallel",
        ActionRepeatMode.Queue => "Queued",
        _ => "Skips New"
    };

    private int TriggerSteps => Action.Graph.Nodes.Count(n => n.Step.Type == ActionStepType.Trigger && !n.Disabled);
    public int TriggerCount => TriggerSteps + WheelUses;
    public bool HasTriggers => TriggerCount > 0;

    public string TriggerTip => string.Join(", ", new[] {
        TriggerSteps > 0 ? $"{TriggerSteps} trigger step(s)" : null,
        WheelUses > 0 ? $"{WheelUses} wheel item(s)" : null
    }.Where(s => s != null)) + " start this action";
    public string? Description => Action.Description;
    public bool HasDescription => !string.IsNullOrWhiteSpace(Action.Description);

    public string Detail =>
        $"{Action.Graph.Nodes.Count} step(s) - v{Action.Version}"
        + (string.IsNullOrWhiteSpace(Action.Author) ? "" : $" - by {Action.Author}")
        + (Action.Graph.IsValid(out _) ? "" : " - needs fixing");

    public event PropertyChangedEventHandler? PropertyChanged;
}