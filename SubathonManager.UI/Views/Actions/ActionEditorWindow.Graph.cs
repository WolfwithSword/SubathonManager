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

    private void BuildPalette() {
        foreach (IGrouping<string, ActionStepType> group in Enum.GetValues<ActionStepType>()
                     .Where(t => t.IsAvailable()).GroupBy(t => t.GetGroup())) {
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

            foreach (ActionStepType type in group) {
                var button = new Button {
                    Content = $"+ {type.GetLabel()}",
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    FontSize = 12,
                    Tag = type
                };
                ToolTip.SetTip(button, "Adds an action; with an action selected it is chained after");
                button.Click += (_, _) => AddStep(type);
                buttons.Children.Add(button);
            }

            _paletteSections.Add((group.Key, section, buttons));
            PalettePanel.Children.Add(section);
        }

        PaletteSearchBox.TextChanged += (_, _) => FilterPalette();
        PaletteSearchBox.KeyDown += (_, e) => {
            if (e.Key != Key.Enter || FirstPaletteMatch() is not { } type) return;
            AddStep(type);
            e.Handled = true;
        };
    }

    private string PaletteQuery => (PaletteSearchBox.Text ?? "").Trim();

    private static bool PaletteMatches(string group, ActionStepType type, string query) {
        return group.Contains(query, StringComparison.OrdinalIgnoreCase)
               || type.GetLabel().Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void FilterPalette() {
        string query = PaletteQuery;
        foreach ((string group, Expander section, StackPanel buttons) in _paletteSections) {
            var any = false;
            foreach (Button button in buttons.Children.OfType<Button>()) {
                bool show = query.Length == 0 || PaletteMatches(group, (ActionStepType)button.Tag!, query);
                button.IsVisible = show;
                any |= show;
            }

            section.IsVisible = any;
            section.IsExpanded = query.Length > 0 || !_collapsedGroups.Contains(group);
        }
    }

    private ActionStepType? FirstPaletteMatch() {
        string query = PaletteQuery;
        if (query.Length == 0) return null;
        foreach ((string group, Expander _, StackPanel buttons) in _paletteSections)
        foreach (Button button in buttons.Children.OfType<Button>())
            if (PaletteMatches(group, (ActionStepType)button.Tag!, query))
                return (ActionStepType)button.Tag!;
        return null;
    }

    private void AddStep(ActionStepType type) {
        var step = new ActionStep { Type = type, Operation = type.DefaultOp() };
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
        if (after != null) {
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
        if (after != null) {
            var edge = new ActionEdge { From = after.Id, To = node.Id };
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
        if (from != null && to != null) _connections.Add(new ActionConnectionVm(edge, from.Output, to.Input));
    }

    private void RefreshNodes() {
        foreach (ActionNodeVm node in _nodes) node.Refresh(Graph);
    }

    private void OnConnectionCompleted(object? parameter) {
        if (parameter is not ValueTuple<object, object> { Item1: ActionConnectorVm a, Item2: ActionConnectorVm b }) return;
        (ActionConnectorVm from, ActionConnectorVm to) = a.IsInput ? (b, a) : (a, b);
        if (from.IsInput || !to.IsInput || from.Owner == to.Owner) return;

        string fromId = from.Owner.Node.Id, toId = to.Owner.Node.Id;
        if (Graph.Edges.Any(e => e.From == fromId && e.To == toId)) return;
        if (Graph.WouldLoop(fromId, toId)) {
            StatusText.Foreground = Brushes.OrangeRed;
            StatusText.Text = "Error: That connection would make a loop";
            return;
        }

        var edge = new ActionEdge { From = fromId, To = toId };
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
        Editor.AddHandler(PointerPressedEvent, (_, e) => {
            _rightPressAt = e.GetCurrentPoint(Editor).Properties.IsRightButtonPressed ? e.GetPosition(Editor) : null;
        }, RoutingStrategies.Tunnel, true);

        Editor.AddHandler(PointerReleasedEvent, (_, e) => {
            if (e.InitialPressMouseButton != MouseButton.Right || _rightPressAt is not { } start) return;
            _rightPressAt = null;
            Point at = e.GetPosition(Editor);
            if (Math.Abs(at.X - start.X) > RightClickSlop || Math.Abs(at.Y - start.Y) > RightClickSlop) return;
            if (StepContainerFor(e.Source, at) is not { DataContext: ActionNodeVm clicked } container) {
                // not an action step right clicked
                return;
            }

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
        var delete = new MenuItem { Header = $"Delete{suffix}" };
        delete.Click += (_, _) => DeleteItems(targets, []);

        return new MenuFlyout { Items = { duplicate, toggle, new Separator(), delete } };
    }

    private List<ActionNodeVm> MenuTargets(ActionNodeVm clicked) {
        List<ActionNodeVm> selected = Editor.SelectedItems?.OfType<ActionNodeVm>().ToList() ?? [];
        return selected.Count > 1 && selected.Contains(clicked) ? selected : [clicked];
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
                Disabled = original.Node.Disabled
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
            var copiedEdge = new ActionEdge { From = from, To = to };
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
        return System.Text.Json.JsonSerializer.Deserialize<ActionStep>(
            System.Text.Json.JsonSerializer.Serialize(step, ActionGraph.JsonOptions), ActionGraph.JsonOptions)!;
    }
}
