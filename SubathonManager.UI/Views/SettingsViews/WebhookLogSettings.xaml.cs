using System.Windows.Controls;
using System.Windows;
using SubathonManager.Core.Enums;
using SubathonManager.Core;
using SubathonManager.Core.Events;

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
        foreach (SubathonEventType eventType in Enum.GetValues(typeof(SubathonEventType))
                     .Cast<SubathonEventType>().OrderBy(x => x.ToString(), 
                         StringComparer.OrdinalIgnoreCase))
        {
            bool.TryParse(App.AppConfig!.Get("Discord", $"Events.Log.{eventType}", "false"), out var check);
            CheckBox typeCheckBox = new()
            {
                Content = eventType.ToString(),
                IsChecked = check,
                Margin = new Thickness(0, 4, 8 ,4),
                VerticalAlignment = VerticalAlignment.Center
            };
            EventWebhookListPanel.Children.Add(typeCheckBox);
        }
            
        bool.TryParse(App.AppConfig!.Get("Discord", "Events.Log.Simulated", "false"), out var logSim);
        LogSimEventsCbx.IsChecked = logSim;
        bool.TryParse(App.AppConfig!.Get("Discord", "Events.Log.RemoteConfig", "false"), out var logRemote);
        LogRemoteConfigCbx.IsChecked = logRemote; 
        ErrorWebhookUrlBx.Text = App.AppConfig!.Get("Discord", "WebhookUrl", string.Empty)!;
        EventWebhookUrlBx.Text = App.AppConfig!.Get("Discord", "Events.WebhookUrl", string.Empty)!;
    }
    
    public void UpdateValueSettings()
    {
        foreach (var child in EventWebhookListPanel.Children)
        {
            if (child is CheckBox checkbox)
                App.AppConfig!.Set("Discord", $"Events.Log.{checkbox.Content}", $"{checkbox.IsChecked}");
        }

        App.AppConfig!.Set("Discord", "WebhookUrl", ErrorWebhookUrlBx.Text);
        App.AppConfig!.Set("Discord", "Events.WebhookUrl", EventWebhookUrlBx.Text);
        App.AppConfig!.Set("Discord", "Events.Log.Simulated", $"{LogSimEventsCbx.IsChecked}");
        App.AppConfig!.Set("Discord", "Events.Log.RemoteConfig", $"{LogRemoteConfigCbx.IsChecked}");
        App.AppConfig!.Save();
    }  
    
    private void TestWebhook_Click(object sender, RoutedEventArgs e)
    {
        ErrorMessageEvents.RaiseErrorEvent("INFO", "Test", 
            "This is a test of the Error Webhook", DateTime.Now);
        ErrorMessageEvents.RaiseCustomEvent("This is a test of the Event Webhook");
    }

}