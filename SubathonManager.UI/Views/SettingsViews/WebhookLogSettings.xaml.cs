using System.Windows.Controls;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using SubathonManager.Core.Enums;
using SubathonManager.Core;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;
using SubathonManager.Data;
using Wpf.Ui.Controls;
using TextBox = System.Windows.Controls.TextBox;

// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.UI.Views.SettingsViews;

public partial class WebhookLogSettings : SettingsControl
{
    private readonly Dictionary<string, Dictionary<string, List<(SubathonEventType EventType, CheckBox CheckBox)>>>
        _groupCheckboxes = new();
    private readonly Dictionary<string, List<string>> _subTabGroups = new();
    private readonly Dictionary<string, string> _activeSubTab = new();

    public WebhookLogSettings()
    {
        InitializeComponent();
        Loaded += (_, _) => RegisterUnsavedChangeHandlers();
    }

    public override void Init(SettingsView host)
    {
        Host = host;
        SuppressUnsavedChanges(InitWebhookSettings);
    }

    internal override void UpdateStatus(IntegrationConnection? connection)
        => throw new NotImplementedException();

    public override bool UpdateValueSettings(AppDbContext db)
        => throw new NotImplementedException();

    private void InitWebhookSettings()
    {
        var config = AppServices.Provider.GetRequiredService<IConfig>();

        var rawGroups = Enum.GetValues<SubathonEventType>()
            .Where(e => ((SubathonEventType?)e).IsEnabled())
            .GroupBy(e => ((SubathonEventType?)e).GetSource().GetGroupLabel())
            .OrderBy(g => g.Min(e => ((SubathonEventType?)e).GetSource().GetGroupLabelOrder()))
            .Select(g => (
                Label: g.Key,
                BySource: g
                    .GroupBy(e => ((SubathonEventType?)e).GetSource())
                    .OrderBy(sg => sg.Key.GetGroupLabelOrder())
                    .Select(sg => (
                        SourceName: sg.Key.GetDescription(),
                        Events: sg.OrderBy(e => e.ToString(), StringComparer.OrdinalIgnoreCase).ToList()
                    ))
                    .ToList()
            ))
            .ToList();

        _groupCheckboxes.Clear();
        _subTabGroups.Clear();
        _activeSubTab.Clear();
        WebhookGroupList.Children.Clear();

        foreach (var (label, bySource) in rawGroups)
        {
            var sourceMap = new Dictionary<string, List<(SubathonEventType, CheckBox)>>();

            foreach (var (sourceName, events) in bySource)
            {
                var checkboxes = new List<(SubathonEventType, CheckBox)>();
                foreach (var eventType in events)
                {
                    bool isChecked = config.GetBool("Discord", $"Events.Log.{eventType}", false);
                    var cb = new CheckBox
                    {
                        Content = eventType.ToString(),
                        IsChecked = isChecked,
                        Margin = new Thickness(0, 4, 8, 4),
                        VerticalAlignment = VerticalAlignment.Center,
                        Width = 224
                    };
                    WireControl(cb);
                    checkboxes.Add((eventType, cb));
                }
                sourceMap[sourceName] = checkboxes;
            }

            _groupCheckboxes[label] = sourceMap;

            if (bySource.Count > 1)
                _subTabGroups[label] = bySource.Select(s => s.SourceName).ToList();
            
            var navBtn = new Wpf.Ui.Controls.Button
            {
                Content = label,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(2, 1, 2, 1),
                Padding = new Thickness(10, 6, 10, 6),
                Appearance = ControlAppearance.Transparent,
                FontSize = 34,
                Height = 34,
                BorderThickness = new Thickness(2, 1, 1, 1),
                Tag = label
            };
            navBtn.Click += WebhookGroupNav_Click;
            WebhookGroupList.Children.Add(navBtn);
        }

        if (rawGroups.Count > 0)
            SelectGroup(rawGroups[0].Label);

        bool logSim = config.GetBool("Discord", "Events.Log.Simulated", false);
        LogSimEventsCbx.IsChecked = logSim;
        bool logRemote = config.GetBool("Discord", "Events.Log.RemoteConfig", false);
        LogRemoteConfigCbx.IsChecked = logRemote;
        ErrorWebhookUrlBx.Text = config.Get("Discord", "WebhookUrl", string.Empty)!;
        EventWebhookUrlBx.Text = config.Get("Discord", "Events.WebhookUrl", string.Empty)!;

        WireControl(LogSimEventsCbx);
        WireControl(LogRemoteConfigCbx);
        WireControl(ErrorWebhookUrlBx);
        WireControl(EventWebhookUrlBx);
    }

    private void WebhookGroupNav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Wpf.Ui.Controls.Button { Tag: string label })
            SelectGroup(label);
    }

    private void SelectGroup(string label)
    {
        foreach (var child in WebhookGroupList.Children)
        {
            if (child is Wpf.Ui.Controls.Button btn)
                btn.Appearance = btn.Tag as string == label
                    ? ControlAppearance.Secondary
                    : ControlAppearance.Transparent;
        }

        WebhookDetailPanel.Children.Clear();
        if (!_groupCheckboxes.TryGetValue(label, out var sourceMap)) return;

        if (_subTabGroups.TryGetValue(label, out var subTabs))
        {
            var subTabBar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 2)
            };

            if (!_activeSubTab.ContainsKey(label))
                _activeSubTab[label] = subTabs[0];

            foreach (var sourceName in subTabs)
            {
                var sn = sourceName;
                var subBtn = new Wpf.Ui.Controls.Button
                {
                    Content = sn,
                    Margin = new Thickness(0, 0, 0, 0),
                    Padding = new Thickness(10, 6, 10, 6),
                    Appearance = _activeSubTab[label].Equals(sn) ? ControlAppearance.Secondary : ControlAppearance.Transparent,
                    FontSize = 20,
                    MinWidth = 100,
                    Tag = sn,
                    BorderThickness = new Thickness(1, 1, 1, 2),
                    CornerRadius = new CornerRadius(4, 4, 0, 0)
                };
                
                subBtn.Click += (_, _) =>
                {
                    _activeSubTab[label] = sn;
                    SelectGroup(label);
                };
                subTabBar.Children.Add(subBtn);
            }

            WebhookDetailPanel.Children.Add(subTabBar);
            var sep = new Separator
            {
                Margin = new Thickness(0, -1, 0, 6),
                BorderThickness = new Thickness(2, 2, 2, 2)
            };
            WebhookDetailPanel.Children.Add(sep);
            var activeSource = _activeSubTab[label];
            if (sourceMap.TryGetValue(activeSource, out var activeCheckboxes))
                PopulateCheckboxWrap(activeCheckboxes);
        }
        else
        {
            var allCheckboxes = sourceMap.Values.SelectMany(x => x).ToList();
            PopulateCheckboxWrap(allCheckboxes);
        }
    }

    private void PopulateCheckboxWrap(IEnumerable<(SubathonEventType, CheckBox cb)> checkboxes)
    {
        var wrap = new WrapPanel
        {
            Orientation = Orientation.Horizontal
        };
        foreach (var (_, cb) in checkboxes)
        {
            switch (cb.Parent)
            {
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

    protected internal override bool UpdateConfigValueSettings()
    {
        bool hasUpdated = false;
        var config = AppServices.Provider.GetRequiredService<IConfig>();

        foreach (var checkboxes
                 in _groupCheckboxes.Values.SelectMany(sourceMap => sourceMap.Values))
            foreach (var (eventType, cb) in checkboxes)
                hasUpdated |= config.Set("Discord", $"Events.Log.{eventType}", $"{cb.IsChecked}");

        hasUpdated |= config.Set("Discord", "WebhookUrl", ErrorWebhookUrlBx.Text);
        hasUpdated |= config.Set("Discord", "Events.WebhookUrl", EventWebhookUrlBx.Text);
        hasUpdated |= config.Set("Discord", "Events.Log.Simulated", $"{LogSimEventsCbx.IsChecked}");
        hasUpdated |= config.Set("Discord", "Events.Log.RemoteConfig", $"{LogRemoteConfigCbx.IsChecked}");
        return hasUpdated;
    }

    public override void UpdateCurrencyBoxes(List<string> currencies, string selected)
    {
        throw new NotImplementedException();
    }

    public override (string, string, TextBox?, TextBox?) GetValueBoxes(SubathonValue val)
    {
        throw new NotImplementedException();
    }

    private void TestWebhook_Click(object sender, RoutedEventArgs e)
    {
        ErrorMessageEvents.RaiseErrorEvent("INFO", "Test",
            "This is a test of the Error Webhook", DateTime.Now);
        ErrorMessageEvents.RaiseCustomEvent("This is a test of the Event Webhook");
    }
}