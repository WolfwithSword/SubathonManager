using System.Windows.Controls;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using SubathonManager.Core.Enums;
using SubathonManager.Core;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Data;
using Wpf.Ui.Controls;
// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.UI.Views.SettingsViews;

public partial class WebhookLogSettings : SettingsControl
{
    private readonly Dictionary<string, List<(SubathonEventType EventType, CheckBox CheckBox)>> _groupCheckboxes = new();

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

    internal override void UpdateStatus(bool status, SubathonEventSource source, string name, string service)
        => throw new NotImplementedException();

    public override void LoadValues(AppDbContext db)
        => throw new NotImplementedException();

    public override bool UpdateValueSettings(AppDbContext db)
        => throw new NotImplementedException();

    private void InitWebhookSettings()
    {
        var config = AppServices.Provider.GetRequiredService<IConfig>();
        var groups = Enum.GetValues<SubathonEventType>()
            .Where(e => ((SubathonEventType?)e).IsEnabled())
            .GroupBy(e => ((SubathonEventType?)e).GetSource().GetGroupLabel())
            .OrderBy(g => g.Min(e => ((SubathonEventType?)e).GetSource().GetGroupLabelOrder()))
            // .OrderBy(g => g.Min(e => SubathonEventSourceHelper.GetSourceOrder(((SubathonEventType?)e).GetSource())))
            .Select(g => (
                Label: g.Key,
                Events: g.OrderBy(e => e.ToString(), StringComparer.OrdinalIgnoreCase).ToList()
            ))
            .ToList();

        _groupCheckboxes.Clear();
        WebhookGroupList.Children.Clear();

        foreach (var (label, events) in groups)
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
            _groupCheckboxes[label] = checkboxes;

            var navBtn = new Wpf.Ui.Controls.Button
            {
                Content = label,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(2, 1, 2, 1),
                Padding = new Thickness(10, 6, 10, 6),
                Appearance = ControlAppearance.Transparent,
                Tag = label
            };
            navBtn.Click += WebhookGroupNav_Click;
            WebhookGroupList.Children.Add(navBtn);
        }

        if (groups.Count > 0)
            SelectGroup(groups[0].Label);

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
        if (!_groupCheckboxes.TryGetValue(label, out var checkboxes)) return;
        foreach (var (_, cb) in checkboxes)
            WebhookDetailPanel.Children.Add(cb);
    }

    public override bool UpdateConfigValueSettings()
    {
        bool hasUpdated = false;
        var config = AppServices.Provider.GetRequiredService<IConfig>();

        foreach (var (_, checkboxes) in _groupCheckboxes)
            foreach (var (eventType, cb) in checkboxes)
                hasUpdated |= config.Set("Discord", $"Events.Log.{eventType}", $"{cb.IsChecked}");

        hasUpdated |= config.Set("Discord", "WebhookUrl", ErrorWebhookUrlBx.Text);
        hasUpdated |= config.Set("Discord", "Events.WebhookUrl", EventWebhookUrlBx.Text);
        hasUpdated |= config.Set("Discord", "Events.Log.Simulated", $"{LogSimEventsCbx.IsChecked}");
        hasUpdated |= config.Set("Discord", "Events.Log.RemoteConfig", $"{LogRemoteConfigCbx.IsChecked}");
        return hasUpdated;
    }

    private void TestWebhook_Click(object sender, RoutedEventArgs e)
    {
        ErrorMessageEvents.RaiseErrorEvent("INFO", "Test",
            "This is a test of the Error Webhook", DateTime.Now);
        ErrorMessageEvents.RaiseCustomEvent("This is a test of the Event Webhook");
    }
}