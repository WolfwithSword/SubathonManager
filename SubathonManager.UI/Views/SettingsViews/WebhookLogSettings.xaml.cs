using System.Windows.Controls;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using SubathonManager.Core.Enums;
using SubathonManager.Core;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Data;
// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.UI.Views.SettingsViews;

public partial class WebhookLogSettings : SettingsControl
{
    public WebhookLogSettings()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            RegisterUnsavedChangeHandlers();
        };
    }

    public override void Init(SettingsView host)
    {
        Host = host;
        SuppressUnsavedChanges(InitWebhookSettings);
    }

    internal override void UpdateStatus(bool status, SubathonEventSource source, string name, string service)
    {
        throw new NotImplementedException();
    }

    public override void LoadValues(AppDbContext db)
    {
        throw new NotImplementedException();
    }

    public override bool UpdateValueSettings(AppDbContext db)
    {
        throw new NotImplementedException();
    }

    private void InitWebhookSettings()
    {
        var config = AppServices.Provider.GetRequiredService<IConfig>();
        var groups = Enum.GetValues<SubathonEventType>()
            .Where(e => ((SubathonEventType?)e).IsEnabled())
            .GroupBy(e => ((SubathonEventType?)e).GetSource().GetGroupLabel())
            .OrderBy(g => g.Min(e => SubathonEventSourceHelper.GetSourceOrder(((SubathonEventType?)e).GetSource())))
            .Select(g => (
                Label: g.Key,
                Events: g.OrderBy(e => e.ToString(), StringComparer.OrdinalIgnoreCase).ToList()
            ));
        
                const int columns = 4;
        var groupList = groups.ToList();
 
        for (int c = 0; c < columns; c++)
            EventWebhookListPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
 
        int rowCount = (int)Math.Ceiling(groupList.Count / (double)columns);
        for (int r = 0; r < rowCount; r++)
            EventWebhookListPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
 
        for (int i = 0; i < groupList.Count; i++)
        {
            var (label, events) = groupList[i];
            int row = i / columns;
            int col = i % columns;
 
            var wrap = new WrapPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(4, 4, 4, 4)
            };
 
            foreach (var eventType in events)
            {
                bool isChecked = config.GetBool("Discord", $"Events.Log.{eventType}", false);
                var checkBox = new CheckBox
                {
                    Content = eventType.ToString(),
                    IsChecked = isChecked,
                    Margin = new Thickness(0, 4, 8, 4),
                    VerticalAlignment = VerticalAlignment.Center,
                    Width = 240
                };
                wrap.Children.Add(checkBox);
                WireControl(checkBox);
            }
 
            var expander = new Expander
            {
                Header = label,
                IsExpanded = false,
                Margin = new Thickness(0, 2, 4, 2),
                VerticalAlignment = VerticalAlignment.Top,
                Content = wrap
            };
 
            Grid.SetRow(expander, row);
            Grid.SetColumn(expander, col);
            EventWebhookListPanel.Children.Add(expander);
        }
 
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
    
    public override bool UpdateConfigValueSettings()
    {
        bool hasUpdated = false;
        var config = AppServices.Provider.GetRequiredService<IConfig>();
        foreach (var child in EventWebhookListPanel.Children)
        {
            if (child is not Expander { Content: WrapPanel wrap }) continue;
            foreach (var inner in wrap.Children)
            {
                if (inner is CheckBox checkbox)
                    hasUpdated |= config.Set("Discord", $"Events.Log.{checkbox.Content}", $"{checkbox.IsChecked}");
            }
        }
 
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