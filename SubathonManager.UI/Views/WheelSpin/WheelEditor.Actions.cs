using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;
using SubathonManager.Integration;
using SubathonManager.Services;
using SubathonManager.UI.Services;
using SubathonManager.UI.UiUtils;
using SubathonManager.UI.Views.Actions;

// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.UI.Views.WheelSpin;

public partial class WheelEditor {
    #region General
    private void ShowActionPanelsFor(WheelSpinActionType type) {
        bool isTime = type is WheelSpinActionType.AddTime or WheelSpinActionType.SubtractTime;
        bool isMult = type == WheelSpinActionType.SetMultiplier;
        bool isReroll = type == WheelSpinActionType.Reroll;
        bool isVts = type == WheelSpinActionType.VTubeStudio && FeatureFlags.VTubeStudioEnabled;
        TimeParamPanel.IsVisible = isTime;
        MultiplierParamPanel.IsVisible = isMult;
        RerollParamPanel.IsVisible = isReroll;
        VtsParamPanel.IsVisible = isVts;
        ObsParamPanel.IsVisible = type == WheelSpinActionType.OBS;
        CustomParamPanel.IsVisible = type == WheelSpinActionType.CustomAction;
        ConvertToCustomBtn.IsVisible = isVts || type == WheelSpinActionType.OBS;
        if (CustomParamPanel.IsVisible) UpdateCustomHint();
        if (isTime) UpdateActionHint(type);
        if (isVts) {
            RefreshVtsKindDependentBoxes();
            LoadVtsTargetSuggestions();
        }

        if (ObsParamPanel.IsVisible) {
            RefreshObsKindDependentBoxes();
            _ = LoadObsSuggestionsAsync();
        }
    }

    private void UpdateActionHint(WheelSpinActionType type) {
        ActionHintText.Text = type switch {
            WheelSpinActionType.AddTime => "Duration to add. e.g. \"5m\", \"300s\", \"1h30m\".",
            WheelSpinActionType.SubtractTime => "Duration to subtract. e.g. \"5m\", \"300s\", \"1h30m\".",
            _ => ""
        };
    }

    private void ParseMultiplierParameter(string parameter) {
        string[] parts = parameter.Split('|');
        if (parts.Length < 4) return;
        MultiplierAmountBox.Text = parts[0];
        MultiplierDurationBox.Text = parts[1] == "xs" ? "" : parts[1];
        MultiplierPointsCheck.IsChecked = parts[2].Equals("True", StringComparison.OrdinalIgnoreCase);
        MultiplierTimeCheck.IsChecked = parts[3].Equals("True", StringComparison.OrdinalIgnoreCase);
    }

    private string BuildMultiplierParameter() {
        if (!double.TryParse(MultiplierAmountBox.Text, NumberStyles.Float,
                CultureInfo.InvariantCulture, out double amount))
            amount = 1.0;
        TimeSpan duration = Utils.ParseDurationString((MultiplierDurationBox.Text ?? "").Trim());
        string durationStr = duration == TimeSpan.Zero ? "x" : ((int)duration.TotalSeconds).ToString();

        bool applyPoints = MultiplierPointsCheck.IsChecked ?? false;
        bool applyTime = MultiplierTimeCheck.IsChecked ?? false;
        return $"{amount.ToString(CultureInfo.InvariantCulture)}|{durationStr}s|{applyPoints}|{applyTime}";
    }

    private void Multiplier_Changed(object? sender, RoutedEventArgs e) {
        bool realChange = DirtySaveGuard.Consume(sender);
        if (_suppressCount > 0 || !realChange) return;
        MarkPendingChanges();
    }

    private void ParseRerollParameter(string parameter) {
        string[] parts = parameter.Split('|');
        RerollCountBox.Text = parts.Length >= 1 && int.TryParse(parts[0], out int c) && c >= 1 ? parts[0] : "1";
    }

    private string BuildRerollParameter() {
        if (!int.TryParse((RerollCountBox.Text ?? "").Trim(), out int count) || count < 1) count = 1;
        return count.ToString();
    }

    private string BuildUiParameter(WheelSpinActionType type) {
        return type switch {
            WheelSpinActionType.SetMultiplier => BuildMultiplierParameter(),
            WheelSpinActionType.Reroll => BuildRerollParameter(),
            WheelSpinActionType.VTubeStudio => BuildVtsParameter(),
            WheelSpinActionType.OBS => ReadObsGraph().ToJson(),
            WheelSpinActionType.CustomAction => SelectedCustomActionId?.ToString() ?? "",
            _ => (ActionParameterBox.Text ?? "").Trim()
        };
    }

    private bool IsCurrentUiActionValid(out string error) {
        if ((ActionTypeBox.SelectedItem as ComboBoxItem)?.Tag is not WheelSpinActionType selected
            || !selected.HasAction()) {
            error = "";
            return true;
        }

        if (selected == WheelSpinActionType.OBS) return IsObsBoxesValid(out error);
        return IsActionValid(selected, BuildUiParameter(selected), out error);
    }

    private static bool IsActionValid(WheelSpinActionType type, string param, out string error) {
        if (type is WheelSpinActionType.AddTime or WheelSpinActionType.SubtractTime) {
            if (string.IsNullOrEmpty(param) || Utils.ParseDurationString(param) == TimeSpan.Zero) {
                error = "Duration must be non-zero (e.g. 5m, 300s, 1h30m)";
                return false;
            }

            error = "";
            return true;
        }

        if (type == WheelSpinActionType.Reroll) {
            string[] parts = param.Split('|');
            if (parts.Length < 1 || !int.TryParse(parts[0], out int count) || count < 1) {
                error = "Reroll count must be at least 1";
                return false;
            }

            error = "";
            return true;
        }

        if (type == WheelSpinActionType.VTubeStudio) {
            if (!VTSWheelAction.TryParse(param, out VTSWheelAction? vtsAction)) {
                error = "VTube Studio action parameters are incomplete";
                return false;
            }

            return vtsAction.IsValid(out error);
        }

        if (type == WheelSpinActionType.CustomAction) return IsCustomActionValid(param, out error);

        if (type == WheelSpinActionType.OBS) {
            ActionGraph? graph = type.BuildActionGraph(param);
            if (graph != null) return graph.IsValid(out error);
            error = "OBS action parameters are incomplete";
            return false;
        }

        if (type == WheelSpinActionType.SetMultiplier) {
            string[] parts = param.Split('|');
            if (parts.Length < 4) {
                error = "Multiplier parameters are incomplete";
                return false;
            }

            if (!double.TryParse(parts[0], NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double amt) || amt == 0) {
                error = "Multiplier amount must be a non-zero number";
                return false;
            }

            string durStr = parts[1].EndsWith('s') ? parts[1][..^1] : parts[1];
            if (parts[1] == "xs" || string.IsNullOrEmpty(durStr) || !int.TryParse(durStr, out int ds) || ds <= 0) {
                error = "Multiplier duration is required and must be non-zero (e.g. 30m or 1h)";
                return false;
            }

            bool applyPoints = parts[2].Equals("True", StringComparison.OrdinalIgnoreCase);
            bool applyTime = parts[3].Equals("True", StringComparison.OrdinalIgnoreCase);
            if (!applyPoints && !applyTime) {
                error = "At least one of Time or Points must be selected";
                return false;
            }
        }

        error = "";
        return true;
    }
    #endregion

    #region VTS

    private void PopulateVtsComboBoxes() {
        VtsKindBox.Items.Clear();
        VtsKindBox.Items.Add(new ComboBoxItem { Content = "Expression", Tag = VtsTargetKind.Expression });
        VtsKindBox.Items.Add(new ComboBoxItem { Content = "Parameter", Tag = VtsTargetKind.Parameter });
        VtsKindBox.Items.Add(new ComboBoxItem { Content = "Hotkey", Tag = VtsTargetKind.Hotkey });
        VtsKindBox.SelectedIndex = 0;

        VtsToggleActionBox.Items.Clear();
        foreach (VtsToggleAction a in new[] { VtsToggleAction.On, VtsToggleAction.Off, VtsToggleAction.Toggle })
            VtsToggleActionBox.Items.Add(new ComboBoxItem { Content = a.ToString(), Tag = a });
        VtsToggleActionBox.SelectedIndex = 0;

        RefreshVtsKindDependentBoxes();
    }

    private void VtsKind_SelectionChanged(object? sender, SelectionChangedEventArgs e) {
        bool realChange = DirtySaveGuard.Consume(sender);
        RefreshVtsKindDependentBoxes();
        LoadVtsTargetSuggestions();
        if (_suppressCount > 0 || !realChange) return;
        MarkPendingChanges();
    }

    private void VtsAfter_SelectionChanged(object? sender, SelectionChangedEventArgs e) {
        bool realChange = DirtySaveGuard.Consume(sender);
        RefreshVtsAfterValueVisibility();
        UpdateVtsHint();
        if (_suppressCount > 0 || !realChange) return;
        MarkPendingChanges();
    }

    private void RefreshVtsKindDependentBoxes() {
        VtsTargetKind kind = SelectedVtsKind;

        SuppressChanges(() => {
            VtsToggleActionPanel.IsVisible = kind == VtsTargetKind.Expression;
            VtsValuePanel.IsVisible = kind == VtsTargetKind.Parameter;

            object? previous = (VtsAfterBox.SelectedItem as ComboBoxItem)?.Tag;
            VtsAfterBox.Items.Clear();

            switch (kind) {
                case VtsTargetKind.Expression:
                    foreach (VtsToggleAction a in Enum.GetValues<VtsToggleAction>())
                        VtsAfterBox.Items.Add(new ComboBoxItem { Content = LabelFor(a), Tag = a });
                    break;
                case VtsTargetKind.Parameter:
                    foreach (VtsParameterAfterAction a in Enum.GetValues<VtsParameterAfterAction>())
                        VtsAfterBox.Items.Add(new ComboBoxItem { Content = LabelFor(a), Tag = a });
                    break;
                case VtsTargetKind.Hotkey:
                    foreach (VtsHotkeyAfterAction a in Enum.GetValues<VtsHotkeyAfterAction>())
                        VtsAfterBox.Items.Add(new ComboBoxItem { Content = LabelFor(a), Tag = a });
                    break;
            }

            ComboBoxItem? restored = VtsAfterBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => Equals(i.Tag, previous));
            VtsAfterBox.SelectedItem = restored ?? VtsAfterBox.Items.OfType<ComboBoxItem>().FirstOrDefault();

            VtsTargetBox.PlaceholderText = kind switch {
                VtsTargetKind.Expression => "e.g. cat_ears.exp3.json",
                VtsTargetKind.Parameter => "e.g. EyeOpenRight or custom parameter",
                _ => "hotkey name or id"
            };
        });

        RefreshVtsAfterValueVisibility();
        UpdateVtsHint();
    }

    private void RefreshVtsAfterValueVisibility() {
        VtsAfterValuePanel.IsVisible = SelectedVtsKind == VtsTargetKind.Parameter
                                       && (VtsAfterBox.SelectedItem as ComboBoxItem)?.Tag
                                       as VtsParameterAfterAction? == VtsParameterAfterAction.SetNewValue;
    }

    private static string LabelFor(VtsToggleAction action) {
        return action switch {
            VtsToggleAction.DoNothing => "Do Nothing",
            _ => action.ToString()
        };
    }

    private static string LabelFor(VtsParameterAfterAction action) {
        return action switch {
            VtsParameterAfterAction.DoNothing => "Do Nothing / Release",
            VtsParameterAfterAction.ResetToOriginal => "Reset to Original",
            VtsParameterAfterAction.SetNewValue => "Change to New Value",
            _ => action.ToString()
        };
    }

    private static string LabelFor(VtsHotkeyAfterAction action) {
        return action switch {
            VtsHotkeyAfterAction.DoNothing => "Do Nothing",
            VtsHotkeyAfterAction.TriggerAgain => "Trigger Again",
            _ => action.ToString()
        };
    }

    private void LoadVtsTargetSuggestions() {
        VTSService vts = ServiceManager.VTubeStudio;
        VtsTargetKind kind = SelectedVtsKind;

        if (!vts.Connected) {
            VtsTargetBox.ItemsSource = null;
            VtsConnectionHint.Text = "VTube Studio not connected - type the value by hand";
            return;
        }

        List<string> suggestions = kind switch {
            VtsTargetKind.Expression => vts.CachedExpressions.Select(e => e.File).ToList(),
            VtsTargetKind.Parameter => vts.CachedParameters.Select(pa => pa.Name).ToList(),
            VtsTargetKind.Hotkey => BuildHotkeySuggestions(vts.CachedHotkeys),
            _ => []
        };

        VtsTargetBox.ItemsSource = suggestions;
        VtsConnectionHint.Text = suggestions.Count > 0
            ? $"{suggestions.Count} available on \"{vts.CurrentModelName ?? "current model"}\""
            : "Nothing found on current model";
    }

    private List<string> BuildHotkeySuggestions(IReadOnlyList<VtsHotkey> hotkeys) {
        _vtsHotkeyIdsByDisplay.Clear();

        HashSet<string> duplicates = hotkeys
            .GroupBy(h => h.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var display = new List<string>(hotkeys.Count);
        foreach (VtsHotkey hotkey in hotkeys) {
            string name = string.IsNullOrWhiteSpace(hotkey.Name) ? hotkey.Id : hotkey.Name;
            string label = duplicates.Contains(name) && hotkey.Id.Length >= 6
                ? $"{name} ({hotkey.Id[^6..]})"
                : name;
            _vtsHotkeyIdsByDisplay[label] = hotkey.Id;
            display.Add(label);
        }

        return display;
    }

    private string DisplayForHotkeyId(string hotkeyId) {
        foreach (KeyValuePair<string, string> pair in _vtsHotkeyIdsByDisplay.Where(pair => 
                     string.Equals(pair.Value, hotkeyId, StringComparison.OrdinalIgnoreCase)))
            return pair.Key;

        return ResolveHotkeyName(hotkeyId) ?? hotkeyId;
    }

    private static string? ResolveHotkeyName(string? hotkeyId) {
        if (string.IsNullOrWhiteSpace(hotkeyId)) return null;
        return ServiceManager.VTubeStudio.CachedHotkeys
            .FirstOrDefault(h => string.Equals(h.Id, hotkeyId, StringComparison.OrdinalIgnoreCase))?.Name;
    }

    private string ResolveHotkeyTargetFromBox() {
        string text = (VtsTargetBox.Text ?? "").Trim();
        if (text.Length == 0) return "";
        if (_vtsHotkeyIdsByDisplay.TryGetValue(text, out string? mapped)) return mapped;

        VtsHotkey? byName = ServiceManager.VTubeStudio.CachedHotkeys
            .FirstOrDefault(h => string.Equals(h.Name, text, StringComparison.OrdinalIgnoreCase));
        return byName?.Id ?? text;
    }

    private void OnVtsModelDataChanged() {
        Dispatcher.UIThread.Post(() => {
            if (!VtsParamPanel.IsVisible) return;
            SuppressChanges(LoadVtsTargetSuggestions);
            UpdateVtsHint();
        });
    }

    private void OnVtsConnectionUpdated(IntegrationConnection connection) {
        if (connection.Source != SubathonEventSource.VTubeStudio) return;
        OnVtsModelDataChanged();
    }

    private async void VtsRefreshTargets_Click(object? sender, RoutedEventArgs e) {
        VTSService vts = ServiceManager.VTubeStudio;
        if (!vts.Connected) {
            LoadVtsTargetSuggestions();
            VtsHintText.Text = "Connect VTubeStudio under Settings -> External Software";
            return;
        }

        await vts.RefreshAsync();
        await Dispatcher.UIThread.InvokeAsync(() => {
            LoadVtsTargetSuggestions();
            UpdateVtsHint();
        });
    }

    private void UpdateVtsHint() {
        VTSWheelAction action = ReadVtsBoxes();

        if (!action.IsValid(out string error)) {
            VtsHintText.Text = error;
            return;
        }

        string summary = action.Describe();
        if (action.Kind == VtsTargetKind.Hotkey && ResolveHotkeyName(action.Target) is { } hotkeyName)
            summary = $"\"{hotkeyName}\" {summary[action.Target.Length..].TrimStart()}";

        VtsHintText.Text = action.HasRevert
            ? summary
            : $"{summary} (no duration set)";
    }

    private VTSWheelAction ReadVtsBoxes() {
        VtsTargetKind kind = SelectedVtsKind;
        var action = new VTSWheelAction {
            Kind = kind,
            Target = kind == VtsTargetKind.Hotkey
                ? ResolveHotkeyTargetFromBox()
                : (VtsTargetBox.Text ?? "").Trim(),
            Duration = Utils.ParseDurationString((VtsDurationBox.Text ?? "").Trim())
        };

        switch (kind) {
            case VtsTargetKind.Expression:
                action.ToggleAction = (VtsToggleActionBox.SelectedItem as ComboBoxItem)?.Tag as VtsToggleAction?
                                      ?? VtsToggleAction.On;
                action.AfterToggle = (VtsAfterBox.SelectedItem as ComboBoxItem)?.Tag as VtsToggleAction?
                                     ?? VtsToggleAction.DoNothing;
                break;
            case VtsTargetKind.Parameter:
                action.Value = ParseDoubleOrZero(VtsValueBox.Text);
                action.AfterParameter = (VtsAfterBox.SelectedItem as ComboBoxItem)?.Tag as VtsParameterAfterAction?
                                        ?? VtsParameterAfterAction.DoNothing;
                action.AfterValue = ParseDoubleOrZero(VtsAfterValueBox.Text);
                break;
            case VtsTargetKind.Hotkey:
                action.AfterHotkey = (VtsAfterBox.SelectedItem as ComboBoxItem)?.Tag as VtsHotkeyAfterAction?
                                     ?? VtsHotkeyAfterAction.DoNothing;
                break;
        }

        return action;
    }

    private static double ParseDoubleOrZero(string? text) {
        return double.TryParse((text ?? "").Trim(), NumberStyles.Float,
            CultureInfo.InvariantCulture, out double v) ? v : 0d;
    }

    private string BuildVtsParameter() {
        return ReadVtsBoxes().ToParameterString();
    }

    private void ParseVtsParameter(string parameter) {
        if (!VTSWheelAction.TryParse(parameter, out VTSWheelAction? action)) {
            ResetVtsBoxes();
            return;
        }

        SuppressChanges(() => {
            ComboBoxItem? kindItem = VtsKindBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => (VtsTargetKind?)i.Tag == action.Kind);
            VtsKindBox.SelectedItem = kindItem ?? VtsKindBox.Items.OfType<ComboBoxItem>().FirstOrDefault();
        });

        RefreshVtsKindDependentBoxes();
        LoadVtsTargetSuggestions();

        SuppressChanges(() => {
            VtsTargetBox.Text = action.Kind == VtsTargetKind.Hotkey
                ? DisplayForHotkeyId(action.Target) : action.Target;
            
            VtsDurationBox.Text = action.Duration > TimeSpan.Zero
            
                ? $"{(int)action.Duration.TotalSeconds}s" : "";
            VtsValueBox.Text = action.Kind == VtsTargetKind.Parameter
                ? action.Value.ToString(CultureInfo.InvariantCulture) : "";
            
            VtsAfterValueBox.Text = action.Kind == VtsTargetKind.Parameter
                ? action.AfterValue.ToString(CultureInfo.InvariantCulture) : "";

            ComboBoxItem? toggleItem = VtsToggleActionBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => (VtsToggleAction?)i.Tag == action.ToggleAction);
            if (toggleItem != null) VtsToggleActionBox.SelectedItem = toggleItem;

            object afterTag = action.Kind switch {
                VtsTargetKind.Expression => action.AfterToggle,
                VtsTargetKind.Parameter => action.AfterParameter,
                _ => action.AfterHotkey
            };

            ComboBoxItem? afterItem = VtsAfterBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => Equals(i.Tag, afterTag));
            if (afterItem != null) VtsAfterBox.SelectedItem = afterItem;
        });

        RefreshVtsAfterValueVisibility();
        UpdateVtsHint();
    }

    private void ResetVtsBoxes() {
        SuppressChanges(() => {
            VtsKindBox.SelectedIndex = 0;
            VtsTargetBox.Text = "";
            VtsValueBox.Text = "";
            VtsDurationBox.Text = "";
            VtsAfterValueBox.Text = "";
            if (VtsToggleActionBox.Items.Count > 0) VtsToggleActionBox.SelectedIndex = 0;
        });

        RefreshVtsKindDependentBoxes();
    }

    private static string DescribeHistoryAction(WheelSpinAction? action) {
        if (action == null) return "Manual";
        if (action.ActionType == WheelSpinActionType.VTubeStudio
            && VTSWheelAction.TryParse(action.Parameter, out VTSWheelAction? parsed)) {
            string described = parsed.Describe();
            if (parsed.Kind == VtsTargetKind.Hotkey && ResolveHotkeyName(parsed.Target) is { } hotkeyName)
                described = $"\"{hotkeyName}\" {described[parsed.Target.Length..].TrimStart()}";

            return $"VTubeStudio: {described}";
        }

        if (action.ActionType == WheelSpinActionType.OBS
            && action.ActionType.BuildActionGraph(action.Parameter) is { } graph)
            return $"OBS: {graph.Describe()}";

        if (action.ActionType == WheelSpinActionType.CustomAction)
            return Guid.TryParse(action.Parameter, out Guid id) && ServiceManager.Actions.GetCustomAction(id) is { } custom
                ? $"Custom: {custom.Name}"
                : "Custom: (missing from actions library)";

        return $"{action.ActionType}: {action.Parameter}";
    }
    
    #endregion

    #region OBS

    private ActionStepType SelectedObsType =>
        (ObsKindBox.SelectedItem as ComboBoxItem)?.Tag as ActionStepType? ?? ActionStepType.ObsSourceVisibility;

    private void PopulateObsComboBoxes() {
        ObsKindBox.Items.Clear();
        foreach (ActionStepType type in new[] {
                     ActionStepType.ObsSourceVisibility, ActionStepType.ObsFilter, ActionStepType.ObsAudio,
                     ActionStepType.ObsMedia
                 })
            ObsKindBox.Items.Add(new ComboBoxItem { Content = type.GetLabel(), Tag = type });
        ObsKindBox.SelectedIndex = 0;
        RefreshObsKindDependentBoxes();
    }

    private void ObsKind_SelectionChanged(object? sender, SelectionChangedEventArgs e) {
        bool realChange = DirtySaveGuard.Consume(sender);
        RefreshObsKindDependentBoxes();
        _ = LoadObsSuggestionsAsync();
        if (_suppressCount > 0 || !realChange) return;
        MarkPendingChanges();
    }

    private void ObsOp_SelectionChanged(object? sender, SelectionChangedEventArgs e) {
        bool realChange = DirtySaveGuard.Consume(sender);
        UpdateObsHint();
        if (_suppressCount > 0 || !realChange) return;
        MarkPendingChanges();
    }

    private void RefreshObsKindDependentBoxes() {
        ActionStepType type = SelectedObsType;
        SuppressChanges(() => {
            ObsScopePanel.IsVisible = type.HasScope();
            ObsScopeLabel.Text = type.GetScopeLabel() ?? "";
            ObsTargetLabel.Text = type.GetTargetLabel() ?? "";

            object? previousOp = (ObsOpBox.SelectedItem as ComboBoxItem)?.Tag;
            object? previousAfter = (ObsAfterBox.SelectedItem as ComboBoxItem)?.Tag;
            ObsOpBox.Items.Clear();
            ObsAfterBox.Items.Clear();
            ObsAfterBox.Items.Add(new ComboBoxItem { Content = ActionOperation.None.GetOpLabel(), Tag = ActionOperation.None });
            foreach (ActionOperation op in type.GetOps()) {
                ObsOpBox.Items.Add(new ComboBoxItem { Content = op.GetOpLabel(), Tag = op });
                ObsAfterBox.Items.Add(new ComboBoxItem { Content = op.GetOpLabel(), Tag = op });
            }

            ObsOpBox.SelectedItem = ObsOpBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, previousOp))
                                    ?? ObsOpBox.Items.OfType<ComboBoxItem>().FirstOrDefault();
            ObsAfterBox.SelectedItem = ObsAfterBox.Items.OfType<ComboBoxItem>()
                                           .FirstOrDefault(i => Equals(i.Tag, previousAfter))
                                       ?? ObsAfterBox.Items.OfType<ComboBoxItem>().FirstOrDefault();
        });
        UpdateObsHint();
    }

    private async Task LoadObsSuggestionsAsync() {
        OBSService obs = ServiceManager.OBS;
        if (!obs.Connected) {
            ObsScopeBox.ItemsSource = null;
            ObsTargetBox.ItemsSource = null;
            ObsConnectionHint.Text = "OBS not connected - type names by hand";
            return;
        }

        ActionStepType type = SelectedObsType;
        string scope = (ObsScopeBox.Text ?? "").Trim();
        (List<string> scopes, List<string> targets) = await Task.Run(() => obs.GetActionTargets(type, scope));
        await Dispatcher.UIThread.InvokeAsync(() => {
            if (SelectedObsType != type) return;
            ObsScopeBox.ItemsSource = scopes;
            ObsTargetBox.ItemsSource = targets;
            ObsConnectionHint.Text = type.HasScope() && scope.Length == 0
                ? $"Pick {(type == ActionStepType.ObsFilter ? "a source" : "a scene")} to list what is in it"
                : $"{targets.Count} found";
        });
    }

    private async void ObsRefreshTargets_Click(object? sender, RoutedEventArgs e) {
        await LoadObsSuggestionsAsync();
    }

    private void OnObsConnectionUpdated(IntegrationConnection connection) {
        if (connection.Source != SubathonEventSource.OBS) return;
        Dispatcher.UIThread.Post(() => {
            if (ObsParamPanel.IsVisible) _ = LoadObsSuggestionsAsync();
        });
    }

    private ActionGraph ReadObsGraph() {
        ActionStepType type = SelectedObsType;
        string? scope = type.HasScope() ? (ObsScopeBox.Text ?? "").Trim() : null;
        string target = (ObsTargetBox.Text ?? "").Trim();
        var first = new ActionStep {
            Type = type,
            Operation = (ObsOpBox.SelectedItem as ComboBoxItem)?.Tag as ActionOperation? ?? type.DefaultOp(),
            Target = target,
            Scope = scope
        };

        TimeSpan duration = Utils.ParseDurationString((ObsDurationBox.Text ?? "").Trim());
        ActionOperation after = (ObsAfterBox.SelectedItem as ComboBoxItem)?.Tag as ActionOperation? ?? ActionOperation.None;
        if (after == ActionOperation.None || duration <= TimeSpan.Zero) return ActionGraph.Sequence(first);

        return ActionGraph.Sequence(first,
            new ActionStep { Type = ActionStepType.Wait, Seconds = duration.TotalSeconds },
            new ActionStep { Type = type, Operation = after, Target = target, Scope = scope });
    }

    private bool IsObsBoxesValid(out string error) {
        bool hasAfter = (ObsAfterBox.SelectedItem as ComboBoxItem)?.Tag as ActionOperation? is { } after
                        && after != ActionOperation.None;
        if (hasAfter && Utils.ParseDurationString((ObsDurationBox.Text ?? "").Trim()) <= TimeSpan.Zero) {
            error = "A duration is required for the action after the timer";
            return false;
        }

        return ReadObsGraph().IsValid(out error);
    }

    private void UpdateObsHint() {
        if (!ObsParamPanel.IsVisible) return;
        ObsHintText.Text = IsObsBoxesValid(out string error) ? ReadObsGraph().Describe() : error;
    }

    private void ParseObsParameter(string parameter) {
        ActionGraph? graph = WheelSpinActionType.OBS.BuildActionGraph(parameter);
        if (graph == null) {
            ResetObsBoxes();
            return;
        }

        ActionStep first = graph.Nodes[0].Step;
        ActionStep? wait = graph.Nodes.ElementAtOrDefault(1)?.Step;
        ActionStep? after = graph.Nodes.ElementAtOrDefault(2)?.Step;

        SuppressChanges(() => ObsKindBox.SelectedItem = ObsKindBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => (ActionStepType?)i.Tag == first.Type));
        RefreshObsKindDependentBoxes();

        SuppressChanges(() => {
            ObsScopeBox.Text = first.Scope ?? "";
            ObsTargetBox.Text = first.Target;
            ObsDurationBox.Text = wait != null && wait.Duration > TimeSpan.Zero
                ? $"{(int)wait.Duration.TotalSeconds}s"
                : "";
            ObsOpBox.SelectedItem = ObsOpBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => (ActionOperation?)i.Tag == first.Operation);
            ObsAfterBox.SelectedItem = ObsAfterBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => (ActionOperation?)i.Tag == (after?.Operation ?? ActionOperation.None));
        });

        _ = LoadObsSuggestionsAsync();
        UpdateObsHint();
    }

    private void ResetObsBoxes() {
        SuppressChanges(() => {
            ObsKindBox.SelectedIndex = 0;
            ObsScopeBox.Text = "";
            ObsTargetBox.Text = "";
            ObsDurationBox.Text = "";
        });

        RefreshObsKindDependentBoxes();
    }
    
    #endregion

    #region Custom Action
    private Guid? SelectedCustomActionId => (CustomActionBox.SelectedItem as ComboBoxItem)?.Tag as Guid?;

    private static bool IsCustomActionValid(string? parameter, out string error) {
        if (!Guid.TryParse(parameter, out Guid id)) {
            error = "Pick a custom action, or create one with +";
            return false;
        }

        if (ServiceManager.Actions.GetCustomAction(id) is not { } action) {
            error = "That custom action is missing from the actions library";
            return false;
        }

        if (action.Graph.Nodes.Count == 0) {
            error = $"\"{action.Name}\" has no steps yet";
            return false;
        }

        return action.Graph.IsValid(out error);
    }

    private void PopulateCustomActionBox(Guid? select = null) {
        Guid? keep = select ?? SelectedCustomActionId;
        SuppressChanges(() => {
            CustomActionBox.Items.Clear();
            foreach (CustomAction action in ServiceManager.Actions.CustomActions)
                CustomActionBox.Items.Add(new ComboBoxItem { Content = action.Name, Tag = (Guid?)action.Id });

            if (keep is { } id && CustomActionBox.Items.OfType<ComboBoxItem>().All(i => i.Tag as Guid? != id))
                CustomActionBox.Items.Add(new ComboBoxItem { Content = "(missing from actions library)", Tag = (Guid?)id });

            CustomActionBox.SelectedItem = CustomActionBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => i.Tag as Guid? == keep);
        });
        UpdateCustomHint();
    }

    private void OnCustomActionsChanged() {
        Dispatcher.UIThread.Post(() => {
            PopulateCustomActionBox();
            LoadItemRows();
        });
    }

    private void UpdateCustomHint() {
        CustomHintText.Text = SelectedCustomActionId is { } id && ServiceManager.Actions.GetCustomAction(id) is { } action
            ? IsCustomActionValid(id.ToString(), out string error)
                ? $"{action.Graph.Describe()}{(string.IsNullOrWhiteSpace(action.Description) ? "" : $"\n{action.Description}")}"
                : error
            : $"{ServiceManager.Actions.CustomActions.Count} in the library ({ActionService.DefaultActionsFolder})";
    }

    private void CustomAction_SelectionChanged(object? sender, SelectionChangedEventArgs e) {
        if (_suppressCount > 0) return;
        UpdateCustomHint();
        MarkPendingChanges();
    }

    private void OpenActionEditor(CustomAction action) {
        Guid? openedFor = _selectedItem?.Id;
        ActionEditorWindow editor = ActionEditorWindow.Open(action);
        editor.Saved += id => {
            if (openedFor == null || _selectedItem?.Id != openedFor || !CustomParamPanel.IsVisible) return;
            if (SelectedCustomActionId == id) {
                UpdateCustomHint();
                return;
            }

            PopulateCustomActionBox(id);
            MarkPendingChanges();
        };
    }

    private void EditCustomAction_Click(object? sender, RoutedEventArgs e) {
        if (SelectedCustomActionId is { } id && ServiceManager.Actions.GetCustomAction(id) is { } action)
            OpenActionEditor(action);
        else
            OpenActionEditor(new CustomAction { Name = (ItemTextBox.Text ?? "").Trim() });
    }

    private void NewCustomAction_Click(object? sender, RoutedEventArgs e) {
        string name = (ItemTextBox.Text ?? "").Trim();
        OpenActionEditor(new CustomAction { Name = name.Length > 0 ? name : "New Action" });
    }

    private async void ImportCustomAction_Click(object? sender, RoutedEventArgs e) {
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        IReadOnlyList<IStorageFile> picked = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
            Title = "Import Custom Action",
            AllowMultiple = false,
            FileTypeFilter = [
                new FilePickerFileType("Subathon Manager Action") { Patterns = [$"*{CustomAction.FileExtension}"] }
            ]
        });
        if (picked.Count == 0) return;

        (CustomAction Action, bool Replaced)? imported =
            await ServiceManager.Actions.ImportCustomActionAsync(picked[0].Path.LocalPath);
        if (imported is not { } result) {
            StatusText.Text = "That file is not a valid custom action";
            return;
        }

        PopulateCustomActionBox(result.Action.Id);
        StatusText.Text = result.Replaced
            ? $"Updated \"{result.Action.Name}\" in the actions library"
            : $"Imported \"{result.Action.Name}\"";
        MarkPendingChanges();
    }

    private async void ConvertToCustom_Click(object? sender, RoutedEventArgs e) {
        var selected = (ActionTypeBox.SelectedItem as ComboBoxItem)?.Tag as WheelSpinActionType?;
        ActionGraph? graph = selected switch {
            WheelSpinActionType.VTubeStudio => ReadVtsBoxes().ToActionGraph(),
            WheelSpinActionType.OBS => ReadObsGraph(),
            _ => null
        };
        if (graph == null) return;

        string name = (ItemTextBox.Text ?? "").Trim();
        var action = new CustomAction { Name = name.Length > 0 ? name : "Converted Action", Graph = graph };
        await ServiceManager.Actions.SaveCustomActionAsync(action);

        SuppressChanges(() => ActionTypeBox.SelectedItem = ActionTypeBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => (WheelSpinActionType?)i.Tag == WheelSpinActionType.CustomAction));
        ShowActionPanelsFor(WheelSpinActionType.CustomAction);

        PopulateCustomActionBox(action.Id);
        MarkPendingChanges();
        OpenActionEditor(action);
    }
    #endregion
}
