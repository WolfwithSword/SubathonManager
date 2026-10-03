using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Objects;
using SubathonManager.Integration;
using SubathonManager.UI.Services;
using SubathonManager.UI.UiUtils;

namespace SubathonManager.UI.Views.Actions;

public partial class ActionEditorWindow {
    private static string ToDurationText(double? seconds) {
        if (seconds is not > 0.0) return "";
        TimeSpan span = TimeSpan.FromSeconds(Math.Round(seconds.Value));
        var text = "";
        if (span.TotalHours >= 1) text += $"{(int)span.TotalHours}h";
        if (span.Minutes > 0) text += $"{span.Minutes}m";
        if (span.Seconds > 0 || text.Length == 0) text += $"{span.Seconds}s";
        return text;
    }

    private void ShowSelection() {
        ActionNode? node = SelectedNode;
        AboutPanel.IsVisible = node == null;
        StepPanel.IsVisible = node != null;
        if (node == null) return;

        ActionStep step = node.Step;
        ActionStepType type = step.Type;
        _suppress++;
        try {
            StepHeader.Text = $"{type.GetGroup()} - {type.GetLabel()}";

            OpBox.Items.Clear();
            foreach (ActionOperation op in type.GetOps())
                OpBox.Items.Add(new ComboBoxItem { Content = op.GetOpLabel(), Tag = op });
            OpBox.SelectedItem = OpBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (ActionOperation?)i.Tag == step.Operation);
            OpPanel.IsVisible = OpBox.Items.Count > 1;

            ScopePanel.IsVisible = type.HasScope();
            ScopeLabel.Text = type.GetScopeLabel() ?? "";
            ScopeBox.Text = step.Scope ?? "";

            TargetPanel.IsVisible = type.HasTarget();
            TargetLabel.Text = type.GetTargetLabel() ?? "";
            TargetBox.Text = step.TargetName ?? step.Target;

            ValueLabel.Text = type.GetValueLabel() ?? "";
            ValueBox.Text = step.Value?.ToString(CultureInfo.InvariantCulture) ?? "";

            DurationLabel.Text = type.GetDurationLabel() ?? "";
            DurationBox.Text = ToDurationText(step.Seconds);

            BodyPanel.IsVisible = type.HasBody();
            BodyLabel.Text = type.GetBodyLabel() ?? "";
            BodyBox.Text = step.Body ?? "";
            VariablesHint.IsVisible = type.AllowsVariables();
            RefreshFieldVisibility(step);
        }
        finally {
            _suppress--;
        }

        _ = LoadTargetSuggestionsAsync();
    }

    private void RefreshFieldVisibility(ActionStep step) {
        ValuePanel.IsVisible = step.Type.HasValue(step.Operation);
        DurationPanel.IsVisible = step.Type.HasDuration();
        StepError.Text = step.IsValid(out string error) ? "" : error;
    }

    private void StepField_Changed(object? sender, SelectionChangedEventArgs e) {
        ApplyStepFields();
    }

    private void ApplyStepFields() {
        if (_suppress > 0 || SelectedNode is not { } node) return;
        ActionStep step = node.Step;

        if ((OpBox.SelectedItem as ComboBoxItem)?.Tag is ActionOperation op) step.Operation = op;
        step.Scope = step.Type.HasScope() ? (ScopeBox.Text ?? "").Trim() : null;

        if (step.Type.HasTarget()) {
            string text = (TargetBox.Text ?? "").Trim();
            if (_targetIds.TryGetValue(text, out string? id)) {
                step.Target = id;
                step.TargetName = text;
            }
            else if (text != step.TargetName) {
                // integ disconnected, manually typed
                step.Target = text;
                step.TargetName = null;
            }
        }

        step.Value = step.Type.HasValue(step.Operation)
                     && double.TryParse((ValueBox.Text ?? "").Trim(), NumberStyles.Float,
                         CultureInfo.InvariantCulture, out double value)
            ? value : null;
        TimeSpan duration = Utils.ParseDurationString((DurationBox.Text ?? "").Trim());
        step.Seconds = step.Type.HasDuration() && duration > TimeSpan.Zero ? duration.TotalSeconds : null;
        step.Body = step.Type.HasBody() && !string.IsNullOrEmpty(BodyBox.Text) ? BodyBox.Text : null;

        RefreshFieldVisibility(step);
        RefreshNodes();
        Validate();
        MarkDirty();
    }

    private async void RefreshTargets_Click(object? sender, RoutedEventArgs e) {
        _mixItUpCommands = null;
        _streamerBotActions = null;
        if (ServiceManager.VTubeStudio.Connected && SelectedNode?.Step.Type.GetGroup() == "VTube Studio")
            await ServiceManager.VTubeStudio.RefreshAsync();
        await LoadTargetSuggestionsAsync();
    }

    private async Task LoadTargetSuggestionsAsync() {
        if (SelectedNode is not { } node || !node.Step.Type.HasTarget()) return;
        ActionStepType type = node.Step.Type;
        
        _targetIds.Clear();
        ScopeBox.ItemsSource = null;
        TargetBox.ItemsSource = null;
        TargetHint.Text = "loading...";

        string scope = (ScopeBox.Text ?? "").Trim();
        var targets = new List<string>();
        var scopes = new List<string>();
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? offline = null;

        try {
            switch (type) {
                // ugly switch time \o/
                case ActionStepType.VtsExpression:
                case ActionStepType.VtsParameter:
                case ActionStepType.VtsHotkey: {
                    VTSService vts = ServiceManager.VTubeStudio;
                    if (!vts.Connected) {
                        offline = "VTube Studio not connected";
                        break;
                    }

                    if (type == ActionStepType.VtsExpression)
                        targets.AddRange(vts.CachedExpressions.Select(x => x.File));
                    else if (type == ActionStepType.VtsParameter)
                        targets.AddRange(vts.CachedParameters.Select(p => p.Name));
                    else
                        foreach (VtsHotkey hotkey in vts.CachedHotkeys) {
                            string name = string.IsNullOrWhiteSpace(hotkey.Name) ? hotkey.Id : hotkey.Name;
                            ids[name] = hotkey.Id;
                            targets.Add(name);
                        }

                    break;
                }
                case ActionStepType.MixItUpCommand: {
                    MixItUpService mixItUp = ServiceManager.MixItUp;
                    if (!mixItUp.Enabled) {
                        offline = "MixItUp not enabled";
                        break;
                    }

                    _mixItUpCommands ??= await mixItUp.GetCommandsAsync();
                    if (_mixItUpCommands == null) {
                        offline = "MixItUp not reachable";
                        break;
                    }

                    foreach (MixItUpCommandInfo command in _mixItUpCommands) {
                        ids[command.DisplayName] = command.Id.ToString();
                        targets.Add(command.DisplayName);
                    }
                    break;
                }
                case ActionStepType.StreamerBotAction: {
                    StreamerBotService streamerBot = ServiceManager.StreamerBot;
                    if (!streamerBot.HttpEnabled) {
                        offline = "Streamer.bot HTTP server not enabled in settings";
                        break;
                    }

                    _streamerBotActions ??= await streamerBot.GetActionsAsync();
                    if (_streamerBotActions == null) {
                        offline = "Streamer.bot not reachable";
                        break;
                    }

                    foreach (StreamerBotActionInfo sbAction in _streamerBotActions) {
                        ids[sbAction.DisplayName] = sbAction.Id.ToString();
                        targets.Add(sbAction.DisplayName);
                    }

                    break;
                }
                case ActionStepType.HttpGet:
                case ActionStepType.HttpPost:
                    if (SelectedNode?.Id == node.Id)
                        TargetHint.Text = "variables also work in the URL";
                    return;
                default: {
                    OBSService obs = ServiceManager.OBS;
                    if (!obs.Connected) {
                        offline = "OBS not connected";
                        break;
                    }

                    (List<string> s, List<string> t) = await Task.Run(() => obs.GetActionTargets(type, scope));
                    scopes.AddRange(s);
                    targets.AddRange(t);
                    break;
                }
            }
        }
        catch (Exception ex) {
            if (_logger?.IsEnabled(LogLevel.Debug) ?? false)
                _logger?.LogDebug(ex, "[ActionEditor] Loading suggestions for {Type} failed", type);
        }

        if (SelectedNode?.Id != node.Id) return;
        _targetIds.Clear();
        foreach (KeyValuePair<string, string> pair in ids) _targetIds[pair.Key] = pair.Value;
        ScopeBox.ItemsSource = scopes;
        TargetBox.ItemsSource = targets;
        TargetHint.Text = offline != null ? $"{offline} - type it by hand" : $"{targets.Count} found";
    }

    /////////////////////////////// variables check

    private readonly List<(Expander Section, List<(Control Row, ActionVariable Variable)> Rows)> _variableSections = [];
    
    private void BuildVariablesPanel() {
        foreach (IGrouping<string, ActionVariable> group in Enum.GetValues<ActionVariable>().GroupBy(v => v.GetGroup())) {
            var rows = new StackPanel { Spacing = 2 };
            var entries = new List<(Control, ActionVariable)>();
            foreach (ActionVariable variable in group) {
                Control row = BuildVariableRow(variable);
                rows.Children.Add(row);
                entries.Add((row, variable));
            }

            var section = new Expander {
                Header = group.Key, IsExpanded = false, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(4, 2),
                Content = rows
            };
            _variableSections.Add((section, entries));
            VariablesPanel.Children.Add(section);
        }

        VariableSearchBox.TextChanged += (_, _) => FilterVariables();
    }

    private Control BuildVariableRow(ActionVariable variable) {
        string placeholder = variable.GetPlaceholder();
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        heading.Children.Add(new TextBlock {
            Text = placeholder, FontSize = 12, FontWeight = FontWeight.SemiBold,
            FontFamily = new FontFamily("Consolas, Menlo, monospace") // looks code-y for looking like vars explicitly
        });
        var type = new TextBlock {
            Text = variable.GetValueType(), FontSize = 10, Foreground = Brushes.Gray,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(type, 1);
        heading.Children.Add(type);

        var row = new Button {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = Brushes.Transparent,
            Padding = new Thickness(6, 4),
            Content = new StackPanel {
                Children = {
                    heading,
                    new TextBlock {
                        Text = variable.GetDescription(), FontSize = 11, Foreground = Brushes.Gray,
                        TextWrapping = TextWrapping.Wrap
                    }
                }
            }
        };
        ToolTip.SetTip(row, "Click to copy var name");

        row.Click += async (_, _) => {
            await UiHelpers.TrySetClipboardTextAsync(placeholder);
            StatusText.Foreground = Brushes.Gray;
            StatusText.Text = $"Copied {placeholder}";
        };
        return row;
    }

    private void FilterVariables() {
        string query = (VariableSearchBox.Text ?? "").Trim().Trim('%');
        foreach ((Expander section, List<(Control Row, ActionVariable Variable)> rows) in _variableSections) {
            var any = false;
            foreach ((Control row, ActionVariable variable) in rows) {
                bool show = query.Length == 0
                            || variable.GetToken().Contains(query, StringComparison.OrdinalIgnoreCase)
                            || variable.GetDescription().Contains(query, StringComparison.OrdinalIgnoreCase)
                            || variable.GetGroup().Contains(query, StringComparison.OrdinalIgnoreCase);
                row.IsVisible = show;
                any |= show;
            }

            section.IsVisible = any;
            if (query.Length > 0) section.IsExpanded = true;
        }
    }
}
