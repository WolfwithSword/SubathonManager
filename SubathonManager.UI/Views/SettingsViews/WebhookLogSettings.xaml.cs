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
            bool.TryParse(Config.Data["Discord"][$"Events.Log.{eventType}"] ?? "false", out var check);
            CheckBox typeCheckBox = new()
            {
                Content = eventType.ToString(),
                IsChecked = check,
                Margin = new Thickness(0, 4, 8 ,4),
                VerticalAlignment = VerticalAlignment.Center
            };
            EventWebhookListPanel.Children.Add(typeCheckBox);
        }
            
        bool.TryParse(Config.Data["Discord"][$"Events.Log.Simulated"] ?? "false", out var logSim);
        LogSimEventsCbx.IsChecked = logSim;
        ErrorWebhookUrlBx.Text = Config.Data["Discord"]["WebhookUrl"] ?? "";
        EventWebhookUrlBx.Text = Config.Data["Discord"]["Events.WebhookUrl"] ?? "";
    }
    
    public void UpdateValueSettings()
    {
        foreach (var child in EventWebhookListPanel.Children)
        {
            if (child is CheckBox checkbox)
                Config.Data["Discord"][$"Events.Log.{checkbox.Content}"] = checkbox.IsChecked.ToString();
        }

        Config.Data["Discord"]["WebhookUrl"] = ErrorWebhookUrlBx.Text;
        Config.Data["Discord"]["Events.WebhookUrl"] = EventWebhookUrlBx.Text;
        Config.Data["Discord"][$"Events.Log.Simulated"] = LogSimEventsCbx.IsChecked.ToString();
        Config.Save();
    }  
    
    private void TestWebhook_Click(object sender, RoutedEventArgs e)
    {
        ErrorMessageEvents.RaiseErrorEvent("INFO", "Test", 
            "This is a test of the Error Webhook", DateTime.Now);
        ErrorMessageEvents.RaiseCustomEvent("This is a test of the Event Webhook");
    }

}