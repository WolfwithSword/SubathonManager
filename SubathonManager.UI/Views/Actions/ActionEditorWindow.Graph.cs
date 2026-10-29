using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Nodify.Avalonia;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Objects;

namespace SubathonManager.UI.Views.Actions;

public partial class ActionEditorWindow {
    private const double RightClickSlop = 6;
    private Point? _rightPressAt;

    private string PaletteQuery => (PaletteSearchBox.Text ?? "").Trim();

    private sealed record PaletteItem(ActionStepType Type, SubathonTrigger? Trigger, string Label);

    private static IEnumerable<PaletteItem> PaletteItems(ActionStepType type) {
        if (type != ActionStepType.Trigger) return [new PaletteItem(type, null, type.GetLabel())];
        return Enum.GetValues<SubathonTrigger>().OrderBy(t => t.GetOrderNumber())
            .Select(t => new PaletteItem(type, t, t.GetLabel()));
    }

    private void BuildPalette() {
        foreach (IGrouping<string, ActionStepType> group in Enum.GetValues<ActionStepType>()
                     .Where(t => t.IsAvailable()).GroupBy(t => t.GetGroup())
                     .OrderBy(g => g.Contains(ActionStepType.Trigger) ? 0 : 1)) {
            var buttons = new StackPanel { Spacing = 4 };
            var section = new Expander {
                Header = group.Key, IsExpanded = true, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(6, 4),
                Content = buttons
            };
            section.Expanded += (_, _) => {
                if (PaletteQuery.Length == 0) _collapsedGroups.Remove(group.Key);
            };
            section.Collapsed += (_, _) => {
                if (PaletteQuery.Length == 0) _collapsedGroups.Add(group.Key);
            };

            foreach (PaletteItem item in group.SelectMany(PaletteItems)) {
                var button = new Button {
                    Content = $"+ {item.Label}",
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    FontSize = 12,
                    Tag = item
                };

                button.Click += (_, _) => AddStep(item);
                buttons.Children.Add(button);
            }

            _paletteSections.Add((group.Key, section, buttons));
            PalettePanel.Children.Add(section);
        }

        PaletteSearchBox.TextChanged += (_, _) => FilterPalette();
        PaletteSearchBox.KeyDown += (_, e) => {
            if (e.Key != Key.Enter || FirstPaletteMatch() is not { } item) return;
            AddStep(item);
            e.Handled = true;
        };
    }

    private static bool PaletteMatches(string group, PaletteItem item, string query) {
        return group.Contains(query, StringComparison.OrdinalIgnoreCase)
               || item.Label.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void FilterPalette() {
        string query = PaletteQuery;
        foreach ((string group, Expander section, StackPanel buttons) in _paletteSections) {
            var any = false;
            foreach (Button button in buttons.Children.OfType<Button>()) {
                bool show = query.Length == 0 || PaletteMatches(group, (PaletteItem)button.Tag!, query);
                button.IsVisible = show;
                any |= show;
            }

            section.IsVisible = any;
            section.IsExpanded = query.Length > 0 || !_collapsedGroups.Contains(group);
        }
    }

    private PaletteItem? FirstPaletteMatch() {
        string query = PaletteQuery;
        if (query.Length == 0) return null;
        foreach ((string group, Expander _, StackPanel buttons) in _paletteSections)
        foreach (Button button in buttons.Children.OfType<Button>())
            if (PaletteMatches(group, (PaletteItem)button.Tag!, query))
                return (PaletteItem)button.Tag!;
        return null;
    }

    private void AddStep(PaletteItem item) {
        ActionStepType type = item.Type;
        var step = new ActionStep { Type = type, Operation = type.DefaultOp(), Trigger = item.Trigger };
        // set defaults
        switch (type) {
            case ActionStepType.Wait:
                step.Seconds = 5;
                break;
            case ActionStepType.AddTime:
            case ActionStepType.SubtractTime:
                step.Seconds = 60;
                break;
            case ActionStepType.SetMultiplier:
                step.Value = 2;
                step.Seconds = 600;
                break;
            case ActionStepType.Reroll:
                step.Value = 1;
                break;
        }

        int next = Graph.Nodes.Select(n => int.TryParse(n.Id, out int i) ? i : 0).DefaultIfEmpty(0).Max() + 1;
        var node = new ActionNode { Id = $"{next}", Step = step };

        ActionNode? after = SelectedNode;
        ActionNode? before = null;
        if (type == ActionStepType.Trigger) {
            before = after is { Step.Type: not ActionStepType.Trigger } && !Graph.Incoming(after.Id).Any() ? after : null;
            after = null;
        }

        if (before != null) {
            node.X = before.X - StepSpacingX;
            node.Y = before.Y;
            while (Graph.Nodes.Any(n => Math.Abs(n.X - node.X) < StepSpacingX - 20
                                        && Math.Abs(n.Y - node.Y) < StepSpacingY - 10))
                node.Y += StepSpacingY;
        }
        else if (after != null) {
            node.X = after.X + StepSpacingX;
            node.Y = after.Y;
            while (Graph.Nodes.Any(n => Math.Abs(n.X - node.X) < StepSpacingX - 20
                                        && Math.Abs(n.Y - node.Y) < StepSpacingY - 10))
                node.Y += StepSpacingY;
        }
        else {
            Point centre = Editor.ViewportLocation
                           + new Vector(Editor.ViewportSize.Width / 2 - 120, Editor.ViewportSize.Height / 2 - 30);
            node.X = Math.Round(centre.X / 10) * 10;
            node.Y = Math.Round(centre.Y / 10) * 10;
        }

        Graph.Nodes.Add(node);
        var view = new ActionNodeVm(node);
        _nodes.Add(view);
        if (after != null || before != null) {
            var edge = new ActionEdge { From = after?.Id ?? node.Id, To = before?.Id ?? node.Id };
            Graph.Edges.Add(edge);
            AddConnectionView(edge);
        }

        RefreshNodes();
        Editor.SelectedItem = view;
        MarkDirty();
        Validate();
    }

    private void AddConnectionView(ActionEdge edge) {
        ActionNodeVm? from = _nodes.FirstOrDefault(n => n.Node.Id == edge.From);
        ActionNodeVm? to = _nodes.FirstOrDefault(n => n.Node.Id == edge.To);
        if (from == null || to == null) return;
        _connections.Add(new ActionConnectionVm(edge, edge.Port == ActionEdge.ElsePort ? from.ElseOutput : from.Output,
            to.Input));
    }

    private void RefreshNodes() {
        foreach (ActionNodeVm node in _nodes) node.Refresh(Graph);
        RefreshRunVariables();
        RefreshRepeatOptions();
    }

    private void OnConnectionCompleted(object? parameter) {
        if (parameter is not ValueTuple<object, object> {
                Item1: ActionConnectorVm a, Item2: ActionConnectorVm b
            }) return;
        (ActionConnectorVm from, ActionConnectorVm to) = a.IsInput ? (b, a) : (a, b);
        if (from.IsInput || !to.IsInput || from.Owner == to.Owner || to.Owner.IsTrigger) return;
        if (from is { Port: not null, Owner.IsCondition: false }) return;

        string fromId = from.Owner.Node.Id, toId = to.Owner.Node.Id;
        if (Graph.Edges.Any(e => e.From == fromId && e.To == toId)) {
            StatusText.Foreground = Brushes.OrangeRed;
            StatusText.Text = "Error: Those steps are already connected";
            return;
        }

        if (Graph.WouldLoop(fromId, toId)) {
            StatusText.Foreground = Brushes.OrangeRed;
            StatusText.Text = "Error: That connection would make a loop";
            return;
        }

        var edge = new ActionEdge { From = fromId, To = toId, Port = from.Port };
        Graph.Edges.Add(edge);
        AddConnectionView(edge);
        RefreshNodes();
        MarkDirty();
        Validate();
    }

    private void RemoveConnections(IReadOnlyCollection<ActionConnectionVm> connections) {
        if (connections.Count == 0) return;
        foreach (ActionConnectionVm connection in connections) {
            Graph.Edges.Remove(connection.Edge);
            _connections.Remove(connection);
        }

        RefreshNodes();
        MarkDirty();
        Validate();
    }

    private void Window_KeyDown(object? sender, KeyEventArgs e) {
        if (e.Key != Key.Delete) return;
        if (FocusManager?.GetFocusedElement() is TextBox or AutoCompleteBox) return;
        if (DeleteSelection()) e.Handled = true;
    }

    private void DeleteStep_Click(object? sender, RoutedEventArgs e) {
        DeleteSelection();
    }

    private bool DeleteSelection() {
        List<ActionConnectionVm> connections =
            Editor.SelectedConnections?.OfType<ActionConnectionVm>().ToList() ?? [];
        if (Editor.SelectedConnection is ActionConnectionVm single && !connections.Contains(single))
            connections.Add(single);
        List<ActionNodeVm> nodes = Editor.SelectedItems?.OfType<ActionNodeVm>().ToList() ?? [];
        return DeleteItems(nodes, connections);
    }

    private bool DeleteItems(List<ActionNodeVm> nodes, List<ActionConnectionVm> connections) {
        if (connections.Count == 0 && nodes.Count == 0) return false;

        foreach (ActionNodeVm node in nodes)
            connections.AddRange(_connections.Where(c => c.Source.Owner == node || c.Target.Owner == node)
                .Except(connections).ToList());
        foreach (ActionConnectionVm connection in connections) {
            Graph.Edges.Remove(connection.Edge);
            _connections.Remove(connection);
        }

        foreach (ActionNodeVm node in nodes) {
            Graph.Nodes.Remove(node.Node);
            _nodes.Remove(node);
        }

        RefreshNodes();
        ShowSelection();
        MarkDirty();
        Validate();
        return true;
    }

    private void AttachNodeMenus() {
        Editor.AddHandler(PointerPressedEvent,
            (_, e) => {
                _rightPressAt = e.GetCurrentPoint(Editor).Properties.IsRightButtonPressed
                    ? e.GetPosition(Editor)
                    : null;
            }, RoutingStrategies.Tunnel, true);

        Editor.AddHandler(PointerReleasedEvent, (_, e) => {
            if (e.InitialPressMouseButton != MouseButton.Right || _rightPressAt is not { } start) return;
            _rightPressAt = null;
            Point at = e.GetPosition(Editor);
            if (Math.Abs(at.X - start.X) > RightClickSlop || Math.Abs(at.Y - start.Y) > RightClickSlop) return;
            if (StepContainerFor(e.Source, at) is not { DataContext: ActionNodeVm clicked } container)
                // not an action step right clicked
                return;

            if (Editor.SelectedItems is { } selected && !selected.Contains(clicked)) {
                selected.Clear();
                selected.Add(clicked);
            }

            MenuFlyout menu = BuildNodeMenu(clicked);
            Dispatcher.UIThread.Post(() => menu.ShowAt(container, true));
        }, RoutingStrategies.Tunnel, true);
    }

    private ItemContainer? StepContainerFor(object? source, Point editorPoint) {
        return FindContainer(source as Visual) ?? FindContainer(Editor.InputHitTest(editorPoint) as Visual);

        static ItemContainer? FindContainer(Visual? visual) {
            while (visual != null && visual is not ItemContainer) visual = visual.GetVisualParent();
            return visual as ItemContainer;
        }
    }

    private MenuFlyout BuildNodeMenu(ActionNodeVm clicked) {
        List<ActionNodeVm> targets = MenuTargets(clicked);
        string suffix = targets.Count > 1 ? $" {targets.Count} steps" : "";

        var duplicate = new MenuItem { Header = $"Duplicate{suffix}" };
        duplicate.Click += (_, _) => DuplicateNodes(targets);
        var toggle = new MenuItem { Header = $"{(clicked.Node.Disabled ? "Enable" : "Disable")}{suffix}" };
        toggle.Click += (_, _) => ToggleNodes(targets, !clicked.Node.Disabled);
        var ignore = new MenuItem { Header = "Ignore Errors" };
        ignore.Click += (_, _) => SetIgnoreErrors(targets, !clicked.Node.IgnoreErrors);
        var multiple = new MenuItem {
            Header = "Run For Each Input",
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = clicked.Node.Inputs == ActionInputMode.Multiple,
            IsVisible = targets.Any(t => t.HasInput)
        };

        multiple.Click += (_, _) => SetInputMode(targets,
            clicked.Node.Inputs == ActionInputMode.Multiple ? ActionInputMode.Wait : ActionInputMode.Multiple);
        var delete = new MenuItem { Header = $"Delete{suffix}" };
        delete.Click += (_, _) => DeleteItems(targets, []);

        var menu = new MenuFlyout { Items = { duplicate, toggle, ignore, multiple, new Separator(), delete } };
        if (clicked.IsTrigger && targets.Count == 1) {
            var test = new MenuItem { Header = "Test Trigger" };
            ToolTip.SetTip(test, "Run the action with sample trigger values");
            test.Click += async (_, _) => await TestTriggerAsync(clicked.Node.Id);
            menu.Items.Insert(0, test);
            menu.Items.Insert(1, new Separator());
        }

        return menu;
    }

    private List<ActionNodeVm> MenuTargets(ActionNodeVm clicked) {
        List<ActionNodeVm> selected = Editor.SelectedItems?.OfType<ActionNodeVm>().ToList() ?? [];
        return selected.Count > 1 && selected.Contains(clicked) ? selected : [clicked];
    }

    private void SetInputMode(List<ActionNodeVm> targets, ActionInputMode mode) {
        foreach (ActionNodeVm node in targets.Where(t => t.HasInput)) node.Node.Inputs = mode;
        RefreshNodes();
        if (SelectedVm is { } selected && targets.Contains(selected)) ShowSelection();
        MarkDirty();
    }

    private void SetIgnoreErrors(List<ActionNodeVm> targets, bool ignore) {
        foreach (ActionNodeVm node in targets) node.Node.IgnoreErrors = ignore;
        RefreshNodes();
        if (SelectedVm is { } selected && targets.Contains(selected)) ShowSelection();
        MarkDirty();
    }

    private void ToggleNodes(List<ActionNodeVm> targets, bool disable) {
        foreach (ActionNodeVm node in targets) node.Node.Disabled = disable;
        RefreshNodes();
        MarkDirty();
        Validate();
    }

    private void DuplicateNodes(List<ActionNodeVm> targets) {
        if (targets.Count == 0) return;

        int next = Graph.Nodes.Select(n => int.TryParse(n.Id, out int i) ? i : 0).DefaultIfEmpty(0).Max() + 1;
        var newIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var copies = new List<ActionNodeVm>();

        foreach (ActionNodeVm original in targets) {
            var copy = new ActionNode {
                Id = $"{next++}",
                Step = CloneStep(original.Node.Step),
                X = original.Node.X + 30,
                Y = original.Node.Y + StepSpacingY,
                Disabled = original.Node.Disabled,
                IgnoreErrors = original.Node.IgnoreErrors,
                Inputs = original.Node.Inputs
            };
            newIds[original.Node.Id] = copy.Id;
            Graph.Nodes.Add(copy);
            var view = new ActionNodeVm(copy);
            _nodes.Add(view);
            copies.Add(view);
        }

        foreach (ActionEdge edge in Graph.Edges.ToList()) {
            if (!newIds.TryGetValue(edge.From, out string? from) || !newIds.TryGetValue(edge.To, out string? to))
                continue;
            var copiedEdge = new ActionEdge { From = from, To = to, Port = edge.Port };
            Graph.Edges.Add(copiedEdge);
            AddConnectionView(copiedEdge);
        }

        RefreshNodes();
        Editor.SelectedItems?.Clear();
        foreach (ActionNodeVm view in copies) Editor.SelectedItems?.Add(view);
        MarkDirty();
        Validate();
    }

    private static ActionStep CloneStep(ActionStep step) {
        return JsonSerializer.Deserialize<ActionStep>(
            JsonSerializer.Serialize(step, ActionGraph.JsonOptions), ActionGraph.JsonOptions)!;
    }
}