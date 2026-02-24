using System.Windows.Controls;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using SubathonManager.Core.Enums;
using SubathonManager.Core;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;

namespace SubathonManager.UI.Views.SettingsViews;

public partial class WebhookLogSettings : UserControl
{
    public required SettingsView Host { get; set; }
    public WebhookLogSettings()
    {
        InitializeComponent();
    }

    public void Init(SettingsView host)
    {
        Host = host;
        InitWebhookSettings();
    }
    private void InitWebhookSettings()
    {
        var config = AppServices.Provider.GetRequiredService<IConfig>();
        foreach (SubathonEventType eventType in Enum.GetValues(typeof(SubathonEventType))
                     .Cast<SubathonEventType>().OrderBy(x => x.ToString(), 
                         StringComparer.OrdinalIgnoreCase))
        {
            bool.TryParse(config!.Get("Discord", $"Events.Log.{eventType}", "false"), out var check);
            CheckBox typeCheckBox = new()
            {
                Content = eventType.ToString(),
                IsChecked = check,
                Margin = new Thickness(0, 4, 8 ,4),
                VerticalAlignment = VerticalAlignment.Center
            };
            EventWebhookListPanel.Children.Add(typeCheckBox);
        }
            
        bool.TryParse(config!.Get("Discord", "Events.Log.Simulated", "false"), out var logSim);
        LogSimEventsCbx.IsChecked = logSim;
        bool.TryParse(config!.Get("Discord", "Events.Log.RemoteConfig", "false"), out var logRemote);
        LogRemoteConfigCbx.IsChecked = logRemote; 
        ErrorWebhookUrlBx.Text = config!.Get("Discord", "WebhookUrl", string.Empty)!;
        EventWebhookUrlBx.Text = config!.Get("Discord", "Events.WebhookUrl", string.Empty)!;
    }
    
    public void UpdateValueSettings()
    {
        var config = AppServices.Provider.GetRequiredService<IConfig>();
        foreach (var child in EventWebhookListPanel.Children)
        {
            if (child is CheckBox checkbox)
                config!.Set("Discord", $"Events.Log.{checkbox.Content}", $"{checkbox.IsChecked}");
        }

        config!.Set("Discord", "WebhookUrl", ErrorWebhookUrlBx.Text);
        config!.Set("Discord", "Events.WebhookUrl", EventWebhookUrlBx.Text);
        config!.Set("Discord", "Events.Log.Simulated", $"{LogSimEventsCbx.IsChecked}");
        config!.Set("Discord", "Events.Log.RemoteConfig", $"{LogRemoteConfigCbx.IsChecked}");
        config!.Save();
    }  
    
    private void TestWebhook_Click(object sender, RoutedEventArgs e)
    {
        ErrorMessageEvents.RaiseErrorEvent("INFO", "Test", 
            "This is a test of the Error Webhook", DateTime.Now);
        ErrorMessageEvents.RaiseCustomEvent("This is a test of the Event Webhook");
    }

}