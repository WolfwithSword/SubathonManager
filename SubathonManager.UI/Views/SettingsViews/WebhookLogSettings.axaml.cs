using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;
using SubathonManager.Data;
using SubathonManager.UI.UiUtils;

namespace SubathonManager.UI.Views.SettingsViews;

public partial class WebhookLogSettings : SettingsControl {
    private readonly Dictionary<string, string> _activeSubTab = new();

    private readonly Dictionary<string, Dictionary<string, List<(string ConfigKey, CheckBox CheckBox)>>>
        _groupCheckboxes = new();

    private readonly Dictionary<string, List<string>> _subTabGroups = new();
    private string? _activeGroup;

    public WebhookLogSettings() {
        InitializeComponent();
        Loaded += (_, _) => RegisterUnsavedChangeHandlers();
    }

    public override void Init(SettingsView host) {
        Host = host;
        SuppressUnsavedChanges(InitWebhookSettings);

        GoAffProStoreRegistry.StoreDiscovered -= OnGoAffProStoreDiscovered;
        GoAffProStoreRegistry.StoreDiscovered += OnGoAffProStoreDiscovered;
    }

    private void OnGoAffProStoreDiscovered(GoAffProStore store) {
        Dispatcher.UIThread.Post(() => {
            if (!store.Enabled) return;
            string groupLabel = SubathonEventSource.GoAffPro.GetGroupLabel();
            string sourceName = SubathonEventSource.GoAffPro.GetDescription();
            if (!_groupCheckboxes.TryGetValue(groupLabel,
                    out Dictionary<string, List<(string ConfigKey, CheckBox CheckBox)>>? sourceMap) ||
                !sourceMap.TryGetValue(sourceName, out List<(string ConfigKey, CheckBox CheckBox)>? checkboxes)) return;

            var key = $"{SubathonEventType.GoAffProOrder}.{store.SiteId}";
            if (checkboxes.Any(c => c.ConfigKey == key)) return;

            var config = AppServices.Provider.GetRequiredService<IConfig>();
            var cb = new CheckBox {
                Content = store.EventName,
                IsChecked = config.GetBool("Discord", $"Events.Log.{key}"),
                Margin = new Thickness(0, 4, 8, 4),
                VerticalAlignment = VerticalAlignment.Center,
                Width = 224
            };
            SuppressUnsavedChanges(() => WireControl(cb));
            checkboxes.Add((key, cb));

            if (_activeGroup == groupLabel)
                SelectGroup(groupLabel);
        });
    }

    internal override void UpdateStatus(IntegrationConnection? connection) {
        throw new NotImplementedException();
    }

    public override bool UpdateValueSettings(AppDbContext db) {
        return false;
    }

    private void InitWebhookSettings() {
        var config = AppServices.Provider.GetRequiredService<IConfig>();

        List<(string Label, List<(string SourceName, List<SubathonEventType> Events)> BySource)> rawGroups = Enum
            .GetValues<SubathonEventType>()
            .Where(e => e.IsEnabled())
            .GroupBy(e => e.GetSource().GetGroupLabel())
            .OrderBy(g => g.Min(e => e.GetSource().GetGroupLabelOrder()))
            .Select(g => (
                Label: g.Key,
                BySource: g
                    .GroupBy(e => e.GetSource())
                    .OrderBy(sg => sg.Key.GetGroupLabelOrder())
                    .Select(sg => (
                        SourceName: sg.Key.GetDescription(),
                        Events: sg.OrderBy(e => e.GetOrderNumber()).ToList()
                    ))
                    .ToList()
            ))
            .ToList();

        _groupCheckboxes.Clear();
        _subTabGroups.Clear();
        _activeSubTab.Clear();
        WebhookGroupList.Children.Clear();

        foreach ((string label, List<(string SourceName, List<SubathonEventType> Events)> bySource) in rawGroups) {
            var sourceMap = new Dictionary<string, List<(string, CheckBox)>>();

            foreach ((string sourceName, List<SubathonEventType> events) in bySource) {
                var checkboxes = new List<(string, CheckBox)>();
                foreach (SubathonEventType eventType in events) {
                    IEnumerable<(string Label, string Key)> entries = eventType == SubathonEventType.GoAffProOrder
                        ? GoAffProStoreRegistry.All().Where(s => s.Enabled)
                            .Select(s => (s.EventName, $"{SubathonEventType.GoAffProOrder}.{s.SiteId}"))
                        : new[] { (((SubathonEventType?)eventType).GetLabel(), eventType.ToString()) };

                    foreach ((string entryLabel, string key) in entries) {
                        bool isChecked = config.GetBool("Discord", $"Events.Log.{key}");
                        var cb = new CheckBox {
                            Content = entryLabel,
                            IsChecked = isChecked,
                            Margin = new Thickness(0, 4, 8, 4),
                            VerticalAlignment = VerticalAlignment.Center,
                            Width = 224
                        };
                        WireControl(cb);
                        checkboxes.Add((key, cb));
                    }
                }

                sourceMap[sourceName] = checkboxes;
            }

            _groupCheckboxes[label] = sourceMap;

            if (bySource.Count > 1)
                _subTabGroups[label] = bySource.Select(s => s.SourceName).ToList();

            var navBtn = new Button {
                Content = label,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(2, 1, 2, 1),
                FontSize = 13,
                Height = 34,
                Tag = label
            };
            navBtn.StyleAsTab(false);
            navBtn.SetTabActive(false);
            navBtn.Click += WebhookGroupNav_Click;
            WebhookGroupList.Children.Add(navBtn);
        }

        if (rawGroups.Count > 0)
            SelectGroup(rawGroups[0].Label);

        bool logSim = config.GetBool("Discord", "Events.Log.Simulated");
        LogSimEventsCbx.IsChecked = logSim;
        bool logRemote = config.GetBool("Discord", "Events.Log.RemoteConfig");
        LogRemoteConfigCbx.IsChecked = logRemote;
        bool logWheel = config.GetBool("Discord", "Wheel.Log.Enabled");
        LogWheelSpinEventsCbx.IsChecked = logWheel;
        bool logWheelTriggers = config.GetBool("Discord", "Wheel.Log.Triggers");
        LogWheelTriggerEventsCbx.IsChecked = logWheelTriggers;
        ErrorWebhookUrlBx.Text = config.Get("Discord", "WebhookUrl", string.Empty)!;
        EventWebhookUrlBx.Text = config.Get("Discord", "Events.WebhookUrl", string.Empty)!;
        WheelWebhookUrlBx.Text = config.Get("Discord", "Wheel.WebhookUrl", string.Empty)!;

        WireControl(LogSimEventsCbx);
        WireControl(LogRemoteConfigCbx);
        WireControl(LogWheelSpinEventsCbx);
        WireControl(LogWheelTriggerEventsCbx);
        WireControl(ErrorWebhookUrlBx);
        WireControl(EventWebhookUrlBx);
        WireControl(WheelWebhookUrlBx);
    }

    private void WebhookGroupNav_Click(object? sender, RoutedEventArgs e) {
        if (sender is Button { Tag: string label })
            SelectGroup(label);
    }

    private void SelectGroup(string label) {
        _activeGroup = label;
        foreach (Control? child in WebhookGroupList.Children)
            if (child is Button btn)
                btn.SetTabActive(btn.Tag as string == label);

        WebhookDetailPanel.Children.Clear();
        if (!_groupCheckboxes.TryGetValue(label,
                out Dictionary<string, List<(string ConfigKey, CheckBox CheckBox)>>? sourceMap)) return;

        if (_subTabGroups.TryGetValue(label, out List<string>? subTabs)) {
            var subTabBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };

            if (!_activeSubTab.ContainsKey(label))
                _activeSubTab[label] = subTabs[0];

            foreach (string sourceName in subTabs) {
                string sn = sourceName;
                var subBtn = new Button {
                    Content = sn,
                    Margin = new Thickness(0, 0, 0, 0),
                    FontSize = 13,
                    MinWidth = 100,
                    Tag = sn
                };
                subBtn.StyleAsTab();
                subBtn.SetTabActive(_activeSubTab[label].Equals(sn));

                subBtn.Click += (_, _) => {
                    _activeSubTab[label] = sn;
                    SelectGroup(label);
                };
                subTabBar.Children.Add(subBtn);
            }

            WebhookDetailPanel.Children.Add(subTabBar);
            var sep = new Separator
                { Margin = new Thickness(0, -1, 0, 6), BorderThickness = new Thickness(2, 2, 2, 2) };
            WebhookDetailPanel.Children.Add(sep);
            string activeSource = _activeSubTab[label];
            if (sourceMap.TryGetValue(activeSource, out List<(string ConfigKey, CheckBox CheckBox)>? activeCheckboxes))
                PopulateCheckboxWrap(activeCheckboxes);
        }
        else {
            List<(string ConfigKey, CheckBox CheckBox)> allCheckboxes = sourceMap.Values.SelectMany(x => x).ToList();
            PopulateCheckboxWrap(allCheckboxes);
        }
    }

    private void PopulateCheckboxWrap(IEnumerable<(string, CheckBox cb)> checkboxes) {
        var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach ((string _, CheckBox cb) in checkboxes) {
            switch (cb.Parent) {
                case Panel parent:
                    parent.Children.Remove(cb);
                    break;
                case Decorator decorator:
                    decorator.Child = null;
                    break;
                case ContentControl contentControl:
                    contentControl.Content = null;
                    break;
            }

            wrap.Children.Add(cb);
        }

        WebhookDetailPanel.Children.Add(wrap);
    }

    protected internal override bool UpdateConfigValueSettings() {
        var hasUpdated = false;
        var config = AppServices.Provider.GetRequiredService<IConfig>();

        foreach (List<(string ConfigKey, CheckBox CheckBox)> checkboxes in
                 _groupCheckboxes.Values.SelectMany(sourceMap => sourceMap.Values))
        foreach ((string configKey, CheckBox cb) in checkboxes)
            hasUpdated |= config.Set("Discord", $"Events.Log.{configKey}", $"{cb.IsChecked}");

        hasUpdated |= config.Set("Discord", "WebhookUrl", ErrorWebhookUrlBx.Text ?? "");
        hasUpdated |= config.Set("Discord", "Events.WebhookUrl", EventWebhookUrlBx.Text ?? "");
        hasUpdated |= config.Set("Discord", "Wheel.WebhookUrl", WheelWebhookUrlBx.Text ?? "");
        hasUpdated |= config.Set("Discord", "Events.Log.Simulated", $"{LogSimEventsCbx.IsChecked}");
        hasUpdated |= config.Set("Discord", "Events.Log.RemoteConfig", $"{LogRemoteConfigCbx.IsChecked}");
        hasUpdated |= config.Set("Discord", "Wheel.Log.Enabled", $"{LogWheelSpinEventsCbx.IsChecked}");
        hasUpdated |= config.Set("Discord", "Wheel.Log.Triggers", $"{LogWheelTriggerEventsCbx.IsChecked}");
        return hasUpdated;
    }

    public override void UpdateCurrencyBoxes(List<string> currencies, string selected) {
        throw new NotImplementedException();
    }

    public override (string, string, TextBox?, TextBox?) GetValueBoxes(SubathonValue val) {
        throw new NotImplementedException();
    }

    private void TestWebhook_Click(object? sender, RoutedEventArgs e) {
        ErrorMessageEvents.RaiseErrorEvent("INFO", "Test", "This is a test of the Error Webhook", DateTime.Now);
        ErrorMessageEvents.RaiseCustomEvent("This is a test of the Event Webhook");
    }
}