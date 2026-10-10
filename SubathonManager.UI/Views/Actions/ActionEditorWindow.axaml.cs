using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Objects;
using SubathonManager.Integration;
using SubathonManager.Services;
using SubathonManager.UI.Services;
using SubathonManager.UI.UiUtils;

namespace SubathonManager.UI.Views.Actions;

public partial class ActionEditorWindow : Window {
    private const double StepSpacingX = 260;
    private const double StepSpacingY = 90;

    private static readonly Dictionary<Guid, ActionEditorWindow> OpenEditors = [];

    private readonly CustomAction _action;
    private readonly HashSet<string> _collapsedGroups = new(StringComparer.Ordinal);
    private readonly ObservableCollection<ActionConnectionVm> _connections = [];
    private readonly ILogger? _logger = AppServices.Provider.GetService<ILogger<ActionEditorWindow>>();
    private readonly ObservableCollection<ActionNodeVm> _nodes = [];
    private readonly List<(string Group, Expander Section, StackPanel Buttons)> _paletteSections = [];

    private readonly Dictionary<string, string> _targetIds = new(StringComparer.OrdinalIgnoreCase);
    private bool _closeConfirmed;

    private bool _dirty;
    private IReadOnlyList<MixItUpCommandInfo>? _mixItUpCommands;
    private string _savedState = "";
    private IReadOnlyList<StreamerBotActionInfo>? _streamerBotActions;
    private int _suppress;

    public ActionEditorWindow() : this(new CustomAction()) {
    }

    public ActionEditorWindow(CustomAction action) {
        InitializeComponent();
        WindowIcons.Apply(this);
        _action = action.Clone();

        foreach (ActionNode node in Graph.Nodes) _nodes.Add(new ActionNodeVm(node));
        foreach (ActionEdge edge in Graph.Edges) AddConnectionView(edge);
        RefreshNodes();

        Editor.ItemsSource = _nodes;
        Editor.Connections = _connections;
        Editor.PendingConnection = new object();
        Editor.ConnectionCompletedCommand = new EditorCommand(OnConnectionCompleted);
        Editor.RemoveConnectionCommand = new EditorCommand(p => {
            if (p is ActionConnectionVm connection) RemoveConnections([connection]);
        });
        Editor.DisconnectConnectorCommand = new EditorCommand(p => {
            if (p is ActionConnectorVm connector)
                RemoveConnections(_connections.Where(c => c.Source == connector || c.Target == connector).ToList());
        });
        Editor.ItemsDragCompletedCommand = new EditorCommand(_ => MarkDirty());
        Editor.SelectionChanged += (_, _) => ShowSelection();

        BuildPalette();
        BuildVariablesPanel();
        TriggerEventTypes.SelectionChanged += (_, _) => TriggerField_Changed(null, new RoutedEventArgs());
        AttachNodeMenus();
        LoadHeader();
        ShowSelection();
        Validate();

        foreach (TextBox box in new[] { NameBox, AuthorBox, VersionBox, DescriptionBox })
            box.TextChanged += (_, _) => {
                if (_suppress > 0) return;
                MarkDirty();
                if (box == NameBox) Title = $"Custom Action - {NameBox.Text}";
            };
        foreach (TextBox box in new[]
                     { ValueBox, DurationBox, BodyBox, HeadersBox, AuthUserBox, AuthTokenBox, OutputBox })
            box.TextChanged += (_, _) => ApplyStepFields();

        foreach (AutoCompleteBox box in new[] { ScopeBox, TargetBox })
            box.TextChanged += (_, _) => ApplyStepFields();

        ScopeBox.LostFocus += (_, _) => _ = LoadTargetSuggestionsAsync();
        ScopeBox.SelectionChanged += (_, _) => Dispatcher.UIThread.Post(() => _ = LoadTargetSuggestionsAsync());

        AddHandler(KeyDownEvent, Window_KeyDown, RoutingStrategies.Tunnel);
        ResetDirty();
        Opened += (_, _) => {
            if (_nodes.Count > 0) Editor.FitToScreen();
            Dispatcher.UIThread.Post(ResetDirty, DispatcherPriority.Background);
        };
        Closing += Window_Closing;
    }

    private ActionGraph Graph => _action.Graph;

    private ActionNodeVm? SelectedVm =>
        Editor.SelectedItems is { Count: 1 } ? Editor.SelectedItems[0] as ActionNodeVm : null;

    private ActionNode? SelectedNode => SelectedVm?.Node;

    public event Action<Guid>? Saved;

    public static ActionEditorWindow Open(CustomAction action) {
        if (OpenEditors.TryGetValue(action.Id, out ActionEditorWindow? existing)) {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return existing;
        }

        var editor = new ActionEditorWindow(action);
        OpenEditors[action.Id] = editor;
        editor.Closed += (_, _) => OpenEditors.Remove(action.Id);
        editor.Show();
        return editor;
    }

    private void LoadHeader() {
        _suppress++;
        try {
            Title = $"Custom Action - {_action.Name}";
            NameBox.Text = _action.Name;
            AuthorBox.Text = _action.Author ?? "";
            VersionBox.Text = _action.Version;
            DescriptionBox.Text = _action.Description ?? "";

            RefreshRepeatOptions();
        }
        finally {
            _suppress--;
        }
    }

    private void ApplyHeader() {
        _action.Name = (NameBox.Text ?? "").Trim();
        _action.Author = string.IsNullOrWhiteSpace(AuthorBox.Text) ? null : AuthorBox.Text.Trim();
        _action.Version = string.IsNullOrWhiteSpace(VersionBox.Text) ? "1.0.0" : VersionBox.Text.Trim();
        _action.Description = string.IsNullOrWhiteSpace(DescriptionBox.Text) ? null : DescriptionBox.Text.Trim();
    }

    private void RefreshRepeatOptions() {
        bool triggers = Graph.HasTriggers;
        List<(ActionRepeatMode Mode, string Label)> options = new[] {
            (ActionRepeatMode.Restart, "Restart Existing"),
            (ActionRepeatMode.Parallel, "Run Parallel"),
            (ActionRepeatMode.Queue, "Queue New"),
            (ActionRepeatMode.Skip, "Skip New")
        }.Where(o => !triggers || ActionGraph.AllowedWithTriggers(o.Item1)).ToList();

        bool loaded = RepeatBox.Items.Count > 0;
        if (triggers && !ActionGraph.AllowedWithTriggers(Graph.OnRepeat)) {
            Graph.OnRepeat = ActionRepeatMode.Parallel;
            StatusText.Foreground = Brushes.Gray;
            StatusText.Text = "Actions with triggers can only run in parallel or queued - this action now runs in parallel";
            if (loaded && _suppress == 0) MarkDirty();
        }

        _suppress++;
        try {
            if (!RepeatBox.Items.OfType<ComboBoxItem>().Select(i => (ActionRepeatMode)i.Tag!)
                    .SequenceEqual(options.Select(o => o.Mode))) {
                RepeatBox.Items.Clear();
                foreach ((ActionRepeatMode mode, string label) in options)
                    RepeatBox.Items.Add(new ComboBoxItem { Content = label, Tag = mode });
            }

            RepeatBox.SelectedItem = RepeatBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => (ActionRepeatMode?)i.Tag == Graph.OnRepeat);
            ToolTip.SetTip(RepeatBox, triggers
                ? "Triggers can fire many times in a row, so they run either in parallel or in a queue"
                : null);
        }
        finally {
            _suppress--;
        }
    }

    private void Repeat_SelectionChanged(object? sender, SelectionChangedEventArgs e) {
        if (_suppress > 0 || (RepeatBox.SelectedItem as ComboBoxItem)?.Tag is not ActionRepeatMode mode) return;
        Graph.OnRepeat = mode;
        MarkDirty();
    }

    private void MarkDirty() {
        _dirty = CurrentState() != _savedState;
        UiHelpers.UpdateButtonPendingBorder(SaveButtonBorder, _dirty);
    }

    private void ResetDirty() {
        _savedState = CurrentState();
        _dirty = false;
        UiHelpers.UpdateButtonPendingBorder(SaveButtonBorder, false);
    }

    private string CurrentState() {
        ApplyHeader();
        return _action.ToJson();
    }

    private void Validate() {
        if (Graph.IsValid(out string error)) {
            StatusText.Foreground = Brushes.Gray;
            StatusText.Text = Graph.Nodes.Count == 0
                ? "No actions yet - add one from the left"
                : $"{Graph.Nodes.Count} action(s), {Graph.Edges.Count} connection(s) - ready";
        }
        else {
            StatusText.Foreground = Brushes.OrangeRed;
            StatusText.Text = error;
        }
    }

    private async void Save_Click(object? sender, RoutedEventArgs e) {
        await SaveAsync();
    }

    private async Task<bool> SaveAsync() {
        ApplyHeader();
        if (string.IsNullOrWhiteSpace(_action.Name)) {
            StatusText.Foreground = Brushes.OrangeRed;
            StatusText.Text = "Give the action a name before saving";
            return false;
        }

        try {
            if (ServiceManager.Actions.GetCustomAction(_action.Id) is { } current) _action.Disabled = current.Disabled;
            await ServiceManager.Actions.SaveCustomActionAsync(_action.Clone());
        }
        catch (Exception ex) {
            _logger?.LogError(ex, "[ActionEditor] Saving {Name} failed", _action.Name);
            StatusText.Foreground = Brushes.OrangeRed;
            StatusText.Text = $"Could not save: {ex.Message}";
            return false;
        }

        ResetDirty();
        Validate();
        StatusText.Text = Graph.IsValid(out string error)
            ? "Saved"
            : $"Save - Will not run until errors are fixed: {error}";
        Saved?.Invoke(_action.Id);

        return true;
    }

    private async void TestRun_Click(object? sender, RoutedEventArgs e) {
        ApplyHeader();
        if (!Graph.IsValid(out string error)) {
            StatusText.Foreground = Brushes.OrangeRed;
            StatusText.Text = error;
            return;
        }

        if (Graph.Nodes.All(n => n.Step.Type == ActionStepType.Trigger || Graph.Incoming(n.Id).Any())) {
            StatusText.Foreground = Brushes.OrangeRed;
            StatusText.Text = "Every branch starts with a trigger - right click a trigger to run a Test";
            return;
        }

        StatusText.Foreground = Brushes.Gray;
        StatusText.Text = "Test run started...";
        ShowTestResult(await ServiceManager.Actions.RunManuallyAsync(_action.Clone(), true));
    }

    private async Task TestTriggerAsync(string nodeId) {
        ApplyHeader();
        if (!Graph.IsValid(out string error)) {
            StatusText.Foreground = Brushes.OrangeRed;
            StatusText.Text = error;
            return;
        }

        StatusText.Foreground = Brushes.Gray;
        StatusText.Text = "Trigger test started...";
        ShowTestResult(await ServiceManager.Actions.TestTriggerAsync(_action.Clone(), nodeId, true));
    }

    private void ShowTestResult(ActionRunResult result) {
        StatusText.Text = result switch {
            ActionRunResult.Done => "Test run finished",
            ActionRunResult.Skipped => "Test run skipped: one is already running and this action ignores repeats",
            ActionRunResult.Cancelled => "Test run was cancelled",
            _ => "Test run stopped at a step that could not run - check the logs"
        };
    }

    private async void Export_Click(object? sender, RoutedEventArgs e) {
        ApplyHeader();
        try {
            string path = await ActionService.ExportCustomActionAsync(_action.Clone());
            StatusText.Foreground = Brushes.Gray;
            StatusText.Text = $"Exported to {path}";
            UiHelpers.OpenFolder(Path.GetDirectoryName(path));
        }
        catch (Exception ex) {
            _logger?.LogError(ex, "[ActionEditor] Export failed");
            StatusText.Foreground = Brushes.OrangeRed;
            StatusText.Text = $"Export failed: {ex.Message}";
        }
    }

    private async void Window_Closing(object? sender, WindowClosingEventArgs e) {
        if (!_dirty || _closeConfirmed) return;
        e.Cancel = true;

        var dialog = new FAContentDialog {
            Title = "Unsaved changes",
            Content = $"Save the changes to \"{(NameBox.Text ?? "").Trim()}\" before closing?",
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Discard",
            CloseButtonText = "Cancel"
        };
        FAContentDialogResult choice = await dialog.ShowAsync(this);
        if (choice == FAContentDialogResult.None) return;
        if (choice == FAContentDialogResult.Primary && !await SaveAsync()) return;

        _closeConfirmed = true;
        Close();
    }
}