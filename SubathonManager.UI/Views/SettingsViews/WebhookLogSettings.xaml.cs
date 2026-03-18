using System.Windows.Controls;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using SubathonManager.Core.Enums;
using SubathonManager.Core;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Data;

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
        
        foreach (SubathonEventType eventType in Enum.GetValues<SubathonEventType>()
                     .Cast<SubathonEventType>().Where(x => 
                         ((SubathonEventType?)x).IsEnabled()).OrderBy(x => x.ToString(), 
                         StringComparer.OrdinalIgnoreCase))
        {
            bool check = config.GetBool("Discord", $"Events.Log.{eventType}", false);
            CheckBox typeCheckBox = new()
            {
                Content = eventType.ToString(),
                IsChecked = check,
                Margin = new Thickness(0, 4, 8 ,4),
                VerticalAlignment = VerticalAlignment.Center
            };
            EventWebhookListPanel.Children.Add(typeCheckBox);
            WireControl(typeCheckBox);
        }
            
        bool logSim = config.GetBool("Discord", $"Events.Log.Simulated", false);
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
            if (child is CheckBox checkbox)
                hasUpdated |= config.Set("Discord", $"Events.Log.{checkbox.Content}", $"{checkbox.IsChecked}");
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