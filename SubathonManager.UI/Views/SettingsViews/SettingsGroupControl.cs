using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;
using SubathonManager.Data;
using SubathonManager.UI.UiUtils;

namespace SubathonManager.UI.Views.SettingsViews;

public class SettingsGroupControl : SettingsControl {
    internal readonly Dictionary<SubathonEventSource, SettingsControl> _settingsControls = new();
    private SubathonEventSource _activeSource = SubathonEventSource.Unknown;
    protected virtual IEnumerable<SubathonEventSource> _eventSources => [];
    protected virtual StackPanel? GetSourceContents => null;
    protected virtual Panel? GetSourceList => null;

    protected virtual SettingsControl? GetSettingsControl(SubathonEventSource eventSource) {
        return _settingsControls.TryGetValue(eventSource, out SettingsControl? control) ? control : null;
    }

    public override void Init(SettingsView host) {
        Host = host;
        GetSourceList?.Children.Clear();
        SubathonEventSource? firstWithControl = null;

        foreach (SubathonEventSource source in _eventSources) {
            var navBtn = new Button {
                Content = source.GetDescription(),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 1, 0, 0),
                FontSize = 13,
                MinWidth = 100,
                Tag = $"{source}"
            };
            navBtn.StyleAsTab();
            navBtn.SetTabActive(false);
            navBtn.Click += GroupNav_Click;
            GetSourceList?.Children.Add(navBtn);

            SettingsControl? control = GetSettingsControl(source);

            if (control != null && firstWithControl == null)
                firstWithControl = source;
        }

        if (firstWithControl is { } first)
            Dispatcher.UIThread.Post(() => SelectGroup(first.ToString()));
    }

    private void GroupNav_Click(object? sender, RoutedEventArgs e) {
        if (sender is Button { Tag: string label })
            SelectGroup(label);
    }

    private void SelectGroup(string label) {
        if (GetSourceList == null) return;
        foreach (Control? child in GetSourceList.Children) {
            if (child is not Button btn) continue;
            btn.SetTabActive(btn.Tag as string == label);
        }

        if (!Enum.TryParse(label, out SubathonEventSource source)) return;
        if (source == _activeSource) return;

        SettingsControl? control = GetSettingsControl(source);
        if (control == null) return;

        GetSourceContents?.Children.Clear();
        GetSourceContents?.Children.Add(control);
        _activeSource = source;
    }

    public void TryHotLinkToSource(SubathonEventSource eventSource) {
        Dispatcher.UIThread.Post(() => {
            if (Content is Expander expander) expander.IsExpanded = true;
            SelectGroup(eventSource.ToString());
            Dispatcher.UIThread.Post(this.BringIntoView, DispatcherPriority.Background);
        });
    }

    public SettingsControl? GetControlForSource(SubathonEventSource eventSource) {
        return GetSettingsControl(eventSource);
    }

    internal override void UpdateStatus(IntegrationConnection? connection) {
        throw new NotImplementedException();
    }

    protected internal override void LoadValues(AppDbContext db) {
        foreach (KeyValuePair<SubathonEventSource, SettingsControl> controlPair in _settingsControls)
            controlPair.Value.LoadValues(db);
    }

    public override bool UpdateValueSettings(AppDbContext db) {
        var hasUpdated = false;
        foreach (KeyValuePair<SubathonEventSource, SettingsControl> controlPair in _settingsControls)
            hasUpdated |= controlPair.Value.UpdateValueSettings(db);
        return hasUpdated;
    }

    protected internal override bool UpdateConfigValueSettings() {
        var hasUpdated = false;
        foreach (KeyValuePair<SubathonEventSource, SettingsControl> controlPair in _settingsControls)
            hasUpdated |= controlPair.Value.UpdateConfigValueSettings();
        return hasUpdated;
    }

    public override void UpdateCurrencyBoxes(List<string> currencies, string text) {
        foreach (KeyValuePair<SubathonEventSource, SettingsControl> controlPair in _settingsControls)
            controlPair.Value.UpdateCurrencyBoxes(currencies, text);
    }

    public override (string, string, TextBox?, TextBox?) GetValueBoxes(SubathonValue val) {
        SubathonEventSource source = val.EventType.GetSource();
        SettingsControl? control = GetSettingsControl(source);
        return control?.GetValueBoxes(val) ?? ("", "", null, null);
    }
}