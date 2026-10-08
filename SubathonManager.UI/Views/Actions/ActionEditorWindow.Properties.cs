using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;
using SubathonManager.Integration;
using SubathonManager.Services;
using SubathonManager.UI.Controls;
using SubathonManager.UI.Services;
using SubathonManager.UI.UiUtils;

namespace SubathonManager.UI.Views.Actions;

public partial class ActionEditorWindow {
    /////////////////////////////// variables check

    private readonly List<(Expander Section, List<(Control Row, string Search)> Rows)> _variableSections = [];
    private Expander? _globalSection;
    private Expander? _runSection;
    private Expander? _triggerSection;
    private Expander? _secretSection;
    private List<ActionGlobal> _stored = [];

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
            StepHeader.Text = type == ActionStepType.Trigger
                ? $"Trigger - {step.Trigger?.GetLabel()}" : $"{type.GetGroup()} - {type.GetLabel()}";

            TriggerPanel.IsVisible = type == ActionStepType.Trigger;
            TriggerDescription.Text = step.Trigger is { } trigger ? $"Runs the steps after this when: {trigger.GetDescription()}" : "";
            TriggerEventPanel.IsVisible = step.Trigger == SubathonTrigger.SubathonEvent;
            if (TriggerEventPanel.IsVisible) {
                TriggerEventTypes.SetOptions(FilterOption.EventTypes(true));
                TriggerEventTypes.SetSelected(step.EventTypes ?? []);
            }

            IgnoreSimulatedCheck.IsChecked = step.IgnoreSimulated;

            OpLabel.Text = type.GetOperationLabel();
            OpBox.Items.Clear();
            foreach (ActionOperation op in type.GetOps())
                OpBox.Items.Add(new ComboBoxItem { Content = op.GetOpLabel(), Tag = op });
            OpBox.SelectedItem = OpBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => (ActionOperation?)i.Tag == step.Operation);
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

            BodyLabel.Text = type.GetBodyLabel() ?? "";
            BodyBox.Text = step.Body ?? "";
            VariablesHint.IsVisible = type.AllowsVariables();

            WebPanel.IsVisible = type.IsWebRequest();
            OutputPanel.IsVisible = type.SavesOutput();
            HeadersBox.Text = step.Headers ?? "";

            AuthBox.Items.Clear();
            foreach ((ActionHttpAuth auth, string label) in new[] {
                         (ActionHttpAuth.None, "None"), (ActionHttpAuth.Bearer, "Bearer token"),
                         (ActionHttpAuth.Basic, "Basic (username & password)")
                     })
                AuthBox.Items.Add(new ComboBoxItem { Content = label, Tag = auth });

            AuthBox.SelectedItem = AuthBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => (ActionHttpAuth?)i.Tag == (step.Auth ?? ActionHttpAuth.None));
            AuthUserBox.Text = step.AuthUser ?? "";
            AuthTokenBox.Text = step.AuthToken ?? "";
            OutputBox.Text = step.OutputVariable ?? "";

            IgnoreErrorsCheck.IsVisible = type is not (ActionStepType.Condition or ActionStepType.Trigger);
            IgnoreErrorsCheck.IsChecked = node.IgnoreErrors;
            RefreshFieldVisibility(step);
        }
        finally {
            _suppress--;
        }

        _ = LoadTargetSuggestionsAsync();
    }

    private void RefreshFieldVisibility(ActionStep step) {
        TargetPanel.IsVisible = step.Type.NeedsTarget(step.Operation);
        ValuePanel.IsVisible = step.Type.HasValue(step.Operation);
        DurationPanel.IsVisible = step.Type.HasDuration();

        ActionHttpAuth auth = step.Auth ?? ActionHttpAuth.None;
        AuthUserPanel.IsVisible = auth == ActionHttpAuth.Basic;
        AuthTokenPanel.IsVisible = auth != ActionHttpAuth.None;
        AuthTokenLabel.Text = auth == ActionHttpAuth.Basic ? "Password:" : "Token:";
        AuthTokenWarning.IsVisible = !string.IsNullOrWhiteSpace(step.AuthToken)
                                     && ActionStepTypeHelper.FindStoreRefs(step.AuthToken)
                                         .All(r => r.Kind != ActionStoreKind.Secret);

        BodyPanel.IsVisible = step.Type.NeedsBody(step.Operation);
        if (step.Type == ActionStepType.SetGlobal)
            BodyLabel.Text = step.Operation == ActionOperation.Adjust
                ? "Amount to add (negative removes):"
                : step.Type.GetBodyLabel() ?? "";

        string? output = step.OutputVariable;
        OutputHint.Text = string.IsNullOrEmpty(output)
            ? "Name it to use the response in later steps"
            : $"Later steps can use %{output}% response, %{output}.some.path[0]% to read into JSON, " +
              $"and %{output}{ActionService.StatusSuffix}% (status code, 0 if it could not connect)";

        StepError.Text = step.IsValid(out string error) ? "" : error;
        StepWarning.Text = SelectedNode is { } node ? string.Join("\n", StepWarnings(node)) : "";
    }

    private IEnumerable<string> StepWarnings(ActionNode node) {
        if (node.Step.Type is ActionStepType.RunAction or ActionStepType.SetActionEnabled) {
            CustomAction? target = ServiceManager.Actions.ResolveActionTarget(node.Step);

            if (target == null && !string.IsNullOrWhiteSpace(node.Step.Target))
                yield return "Action not found in actions library";
            else if (target?.Id == _action.Id && node.Step.Type == ActionStepType.RunAction)
                yield return $"Self-Run detected - Will run at most {ActionService.MaxActionDepth}x deep";
            else if (target is { Disabled: true } && node.Step.Type == ActionStepType.RunAction)
                yield return "Action is currently disabled - it will not run unless enabled";
        }

        if (!node.Step.Type.AllowsVariables()) yield break;

        HashSet<string> declared = Graph.OutputVariables();
        HashSet<string> before = Graph.Upstream(node.Id);
        HashSet<string> available = Graph.Nodes
            .Where(n => before.Contains(n.Id) && !string.IsNullOrEmpty(n.Step.OutputVariable))
            .Select(n => n.Step.OutputVariable!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (node.Step.Type == ActionStepType.SetGlobal && ActionStepTypeHelper.IsValidName(node.Step.Target.Trim())) {
            string globalName = node.Step.Target.Trim();
            ActionGlobal? global = FindStored(ActionStoreKind.Global, globalName);

            if (global == null)
                yield return $"There's no %global.{globalName}% - saving adds it as empty";

            else if ((node.Step.Operation == ActionOperation.Adjust && global.ValueType != ActionValueType.Number)
                     || (node.Step.Operation == ActionOperation.Toggle && global.ValueType != ActionValueType.Boolean))
                yield return
                    $"%global.{global.Name}% is {global.ValueType.GetLabel()} - this step will fail";
        }

        foreach (string token in ActionStepTypeHelper.FindTokens(node.Step.AllText)
                     .Distinct(StringComparer.OrdinalIgnoreCase)) {
            if (ActionStepTypeHelper.TryGetStoreRef(token, out ActionStoreKind kind, out string name)) {
                ActionGlobal? stored = FindStored(kind, name);

                if (stored == null)
                    yield return $"%{token}% isn't created yet - saving adds it as empty";

                else if (kind == ActionStoreKind.Secret
                             ? string.IsNullOrEmpty(ServiceManager.Actions.GetSecretValue(name))
                             : string.IsNullOrEmpty(stored.Value))
                    yield return $"%{token}% has no value yet";
                continue;
            }

            if (ActionStepTypeHelper.TokenRoot(token).Equals(ActionRunProgress.TriggerVariable,
                    StringComparison.OrdinalIgnoreCase)) {
                if (!Graph.Nodes.Any(n => before.Contains(n.Id) && n.Step.Type == ActionStepType.Trigger))
                    yield return $"%{token}% only has a value when a trigger leads to this step";
                continue;
            }

            string root = OutputRoot(ActionStepTypeHelper.TokenRoot(token));
            if (declared.Contains(root) && !available.Contains(root))
                yield return $"%{token}% is saved by a step that doesn't run before this one - it will be empty";
        }
    }

    // %resp_status% belongs to %resp%
    private static string OutputRoot(string root) {
        return root.EndsWith(ActionService.StatusSuffix, StringComparison.OrdinalIgnoreCase)
            ? root[..^ActionService.StatusSuffix.Length]
            : root;
    }

    private void StepField_Changed(object? sender, SelectionChangedEventArgs e) {
        ApplyStepFields();
    }

    private void TriggerField_Changed(object? sender, RoutedEventArgs e) {
        if (_suppress > 0 || SelectedNode is not { Step.Type: ActionStepType.Trigger } node) return;
        if (node.Step.Trigger == SubathonTrigger.SubathonEvent) {
            List<string> picked = TriggerEventTypes.SelectedOptions.Select(o => o.Value).ToList();
            node.Step.EventTypes = picked.Count == 0 ? null : picked;
            node.Step.IgnoreSimulated = IgnoreSimulatedCheck.IsChecked == true;
        }

        RefreshFieldVisibility(node.Step);
        RefreshNodes();
        Validate();
        MarkDirty();
    }

    private void IgnoreErrors_Changed(object? sender, RoutedEventArgs e) {
        if (_suppress > 0 || SelectedNode is not { } node) return;
        node.IgnoreErrors = IgnoreErrorsCheck.IsChecked == true;
        RefreshNodes();
        MarkDirty();
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
            ? value
            : null;
        TimeSpan duration = Utils.ParseDurationString((DurationBox.Text ?? "").Trim());
        step.Seconds = step.Type.HasDuration() && duration > TimeSpan.Zero ? duration.TotalSeconds : null;
        step.Body = step.Type.HasBody() && !string.IsNullOrEmpty(BodyBox.Text) ? BodyBox.Text : null;

        if (step.Type.IsWebRequest()) {
            ActionHttpAuth auth = (AuthBox.SelectedItem as ComboBoxItem)?.Tag is ActionHttpAuth picked
                ? picked
                : ActionHttpAuth.None;
            step.Headers = string.IsNullOrWhiteSpace(HeadersBox.Text) ? null : HeadersBox.Text;
            step.Auth = auth == ActionHttpAuth.None ? null : auth;
            step.AuthUser = auth == ActionHttpAuth.Basic ? NullIfBlank(AuthUserBox.Text) : null;
            step.AuthToken = auth != ActionHttpAuth.None ? NullIfBlank(AuthTokenBox.Text) : null;
        }

        step.OutputVariable = step.Type.SavesOutput() ? NullIfBlank(OutputBox.Text) : null;

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
        if (_suggestionsFor != node.Id) ScopeBox.ItemsSource = null;
        _suggestionsFor = node.Id;
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
                case ActionStepType.SetActionEnabled:
                case ActionStepType.RunAction:
                    foreach (CustomAction other in ServiceManager.Actions.CustomActions) {
                        ids[other.Name] = other.Id.ToString();
                        targets.Add(other.Name);
                    }

                    break;
                case ActionStepType.SetGlobal:
                    targets.AddRange(_stored.Where(g => g.Kind == ActionStoreKind.Global).Select(g => g.Name));
                    break;
                case ActionStepType.Condition:
                    if (SelectedNode?.Id != node.Id) return;
                    SetScopeSuggestions(ConditionSuggestions(node));
                    TargetHint.Text = "value or %variable%";
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
        SetScopeSuggestions(scopes);
        TargetBox.ItemsSource = targets;
        TargetHint.Text = offline != null ? $"{offline} - type it by hand" : $"{targets.Count} found";
    }

    private string? _suggestionsFor;

    private void SetScopeSuggestions(List<string> scopes) {
        if (ScopeBox.ItemsSource is IEnumerable<string> current && current.SequenceEqual(scopes)) return;
        ScopeBox.ItemsSource = scopes;
    }

    private static string? NullIfBlank(string? text) {
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private List<string> ConditionSuggestions(ActionNode node) {
        HashSet<string> before = Graph.Upstream(node.Id);
        var suggestions = new List<string>();
        foreach (ActionNode previous in
                 Graph.Nodes.Where(n => before.Contains(n.Id)
                                        && !string.IsNullOrEmpty(n.Step.OutputVariable))) {
            suggestions.Add($"%{previous.Step.OutputVariable}%");
            suggestions.Add($"%{previous.Step.OutputVariable}{ActionService.StatusSuffix}%");
        }

        foreach (SubathonTrigger trigger in Graph.Nodes
                     .Where(n => before.Contains(n.Id) && n.Step is { Type: ActionStepType.Trigger, Trigger: not null })
                     .Select(n => n.Step.Trigger!.Value).Distinct())
            suggestions.AddRange(SubathonTriggerWatcher.SampleValues(trigger).Keys
                .Select(k => $"%{ActionRunProgress.TriggerVariable}.{k}%"));

        suggestions.AddRange(_stored.Where(g => g.Kind == ActionStoreKind.Global)
            .Select(g => ActionStepTypeHelper.StorePlaceholder(g.Kind, g.Name)));
        suggestions.AddRange(Enum.GetValues<ActionVariable>().Select(v => v.GetPlaceholder()));
        return suggestions;
    }

    private void BuildVariablesPanel() {
        _triggerSection = AddVariableSection("Trigger", []);
        _runSection = AddVariableSection("This Run", []);
        _globalSection = AddVariableSection("Globals", []);
        _secretSection = AddVariableSection("Secrets", []);
        foreach (IGrouping<string, ActionVariable> group in Enum.GetValues<ActionVariable>().GroupBy(v => v.GetGroup()))
            AddVariableSection(group.Key, group.Select(v =>
                (v.GetPlaceholder(), v.GetValueType(), v.GetDescription(), $"{v.GetGroup()} {v.GetToken()}")));

        RefreshRunVariables();
        VariableSearchBox.TextChanged += (_, _) => FilterVariables();

        ServiceManager.Actions.GlobalsChanged += OnGlobalsChanged;
        Closed += (_, _) => ServiceManager.Actions.GlobalsChanged -= OnGlobalsChanged;
        _ = LoadStoredAsync();
    }

    private Expander AddVariableSection(string header,
        IEnumerable<(string Placeholder, string Type, string Description, string Search)> rows) {
        var section = new Expander {
            Header = header, IsExpanded = false, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(4, 2)
        };
        _variableSections.Add((section, []));
        VariablesPanel.Children.Add(section);
        SetVariableRows(section, rows);
        return section;
    }

    private void SetVariableRows(Expander section,
        IEnumerable<(string Placeholder, string Type, string Description, string Search)> rows) {
        var panel = new StackPanel { Spacing = 2 };
        var entries = new List<(Control, string)>();
        foreach ((string placeholder, string type, string description, string search) in rows) {
            Control row = BuildVariableRow(placeholder, type, description);
            panel.Children.Add(row);
            entries.Add((row, $"{placeholder} {description} {search}"));
        }

        section.Content = panel;
        int index = _variableSections.FindIndex(s => s.Section == section);
        _variableSections[index] = (section, entries);
        FilterVariables();
    }

    private void RefreshRunVariables() {
        if (_runSection == null) return;
        var rows = new List<(string, string, string, string)>();
        foreach (ActionNode node in Graph.Nodes.Where(n => !string.IsNullOrEmpty(n.Step.OutputVariable))) {
            string name = node.Step.OutputVariable!;
            string from = node.Step.Describe();
            rows.Add(($"%{name}%", "text", $"Response from {from}; add .path or [#] to read into JSON", "this run"));
            rows.Add(($"%{name}{ActionService.StatusSuffix}%", "number",
                $"Status code from {from}, 0 if it could not connect", "this run"));
        }

        SetVariableRows(_runSection, rows);

        var triggerRows = new List<(string, string, string, string)>();
        foreach (SubathonTrigger trigger in Graph.Nodes.Select(n => n.Step)
                     .Where(s => s is { Type: ActionStepType.Trigger, Trigger: not null })
                     .Select(s => s.Trigger!.Value).Distinct())

        foreach ((string key, string sample) in SubathonTriggerWatcher.SampleValues(trigger))
            triggerRows.Add(($"%{ActionRunProgress.TriggerVariable}.{key}%", "text",
                $"{trigger.GetLabel()}, e.g. {Shorten(sample)}", "trigger"));

        if (_triggerSection != null) SetVariableRows(_triggerSection, triggerRows);
    }

    private void OnGlobalsChanged() {
        Dispatcher.UIThread.Post(() => _ = LoadStoredAsync());
    }

    private ActionGlobal? FindStored(ActionStoreKind kind, string name) {
        return _stored.FirstOrDefault(g => g.Kind == kind && g.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private async Task LoadStoredAsync() {
        try {
            _stored = await ServiceManager.Actions.GetGlobalsAsync();
        }
        catch (Exception ex) {
            _logger?.LogWarning(ex, "[ActionEditor] Could not load globals and secrets");
            return;
        }

        if (_globalSection != null)
            SetVariableRows(_globalSection, _stored.Where(g => g.Kind == ActionStoreKind.Global).Select(g => (
                ActionStepTypeHelper.StorePlaceholder(g.Kind, g.Name), g.ValueType.GetLabel(),
                g.Value == null ? "No value yet" : $"Current: {Shorten(g.Value)}", "global")));
        if (_secretSection != null)
            SetVariableRows(_secretSection, _stored.Where(g => g.Kind == ActionStoreKind.Secret).Select(g => (
                ActionStepTypeHelper.StorePlaceholder(g.Kind, g.Name), "secret",
                "Fetched when the step runs, never saved to the .sma file", "secret")));
        if (SelectedNode is { } node) RefreshFieldVisibility(node.Step);
    }

    private static string Shorten(string value) {
        value = value.ReplaceLineEndings(" ");
        return value.Length > 60 ? $"{value[..57]}..." : value;
    }

    private Control BuildVariableRow(string placeholder, string valueType, string description) {
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        heading.Children.Add(new TextBlock {
            Text = placeholder, FontSize = 12, FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontFamily = new FontFamily("Consolas, Menlo, monospace") // looks code-y for looking like vars explicitly
        });
        var type = new TextBlock {
            Text = valueType, FontSize = 10, Foreground = Brushes.Gray,
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
                        Text = description, FontSize = 11, Foreground = Brushes.Gray,
                        TextWrapping = TextWrapping.Wrap
                    }
                }
            }
        };
        ToolTip.SetTip(row, "Click to copy");

        row.Click += async (_, _) => {
            await UiHelpers.TrySetClipboardTextAsync(placeholder);
            StatusText.Foreground = Brushes.Gray;
            StatusText.Text = $"Copied {placeholder}";
        };
        return row;
    }

    private void FilterVariables() {
        string query = (VariableSearchBox.Text ?? "").Trim().Trim('%');
        foreach ((Expander section, List<(Control Row, string Search)> rows) in _variableSections) {
            var any = false;
            foreach ((Control row, string search) in rows) {
                bool show = query.Length == 0 || search.Contains(query, StringComparison.OrdinalIgnoreCase);
                row.IsVisible = show;
                any |= show;
            }

            section.IsVisible = any;
            if (query.Length > 0) section.IsExpanded = true;
        }
    }
}