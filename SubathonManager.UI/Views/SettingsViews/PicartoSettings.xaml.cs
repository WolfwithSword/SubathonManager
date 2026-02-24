using System.Windows.Controls;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PicartoEventsLib.Abstractions.Models;
using SubathonManager.Core.Events;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Interfaces;
using SubathonManager.Data;
using SubathonManager.Integration;
using SubathonManager.UI.Services;

namespace SubathonManager.UI.Views.SettingsViews;

public partial class PicartoSettings : UserControl
{
    public required SettingsView Host { get; set; }
    private readonly ILogger? _logger = AppServices.Provider.GetRequiredService<ILogger<TwitchSettings>>();
    
    public PicartoSettings()
    {
        InitializeComponent();
    }
    
    public void Init(SettingsView host)
    {
        Host = host;
        var config = AppServices.Provider.GetRequiredService<IConfig>();
        PicartoUserBox.Text = config!.Get("Picarto", "Username", string.Empty)!;
        IntegrationEvents.ConnectionUpdated += UpdateConnectionStatus;
    }
    
    private void UpdateConnectionStatus(bool status, SubathonEventSource source, string name, string service)
    {
        if (source != SubathonEventSource.Picarto) return;
        Dispatcher.Invoke(() =>
        { 
            if (service == "Chat")
                Host!.UpdateConnectionStatus(status, PicartoChatStatusText, ConnectPicartoBtn);
            else if (service == "Alerts")
                Host!.UpdateConnectionStatus(status, PicartoClientStatusText, ConnectPicartoBtn);
        });
    }
    
    public void UpdateConfigValueSettings()
    {
        var config = AppServices.Provider.GetRequiredService<IConfig>();
        config!.Set("Picarto", "Username", $"{PicartoUserBox.Text}");
        config!.Save();
    }
    
    private async void ConnectPicartoButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            UpdateConfigValueSettings();
            await ServiceManager.Picarto.UpdateChannel();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to initialize PicartoService");
        }
    }
    
    public bool UpdateValueSettings(AppDbContext db)
    {
        bool hasUpdated = false;
        var cheerValue =
            db.SubathonValues.FirstOrDefault(sv => sv.EventType == SubathonEventType.PicartoTip && sv.Meta == "");
        // divide by 100 since UI shows "per 100 kudos"
        if (cheerValue != null && double.TryParse(KudosTextBox.Text, out var cheerSeconds)
            && !cheerValue.Seconds.Equals(cheerSeconds / 100.0))
        {
            cheerValue.Seconds = cheerSeconds / 100.0;
            hasUpdated = true;
        }

        if (cheerValue != null && double.TryParse(Kudos2TextBox.Text, out var cheerPoints) &&
            !cheerValue.Points.Equals(cheerPoints))
        {
            cheerValue.Points = cheerPoints;
            hasUpdated = true;
        }

        var followValue =
            db.SubathonValues.FirstOrDefault(sv => sv.EventType == SubathonEventType.PicartoFollow && sv.Meta == "");
        if (followValue != null && double.TryParse(FollowTextBox.Text, out var followSeconds)
            && !followValue.Seconds.Equals(followSeconds))
        {
            followValue.Seconds = followSeconds;
            hasUpdated = true;
        }

        if (followValue != null && double.TryParse(Follow2TextBox.Text, out var followPoints)
            && !followValue.Points.Equals(followPoints))
        {
            followValue.Points = followPoints;
            hasUpdated = true;
        }
        
        hasUpdated |= Host!.SaveSubTier(db, SubathonEventType.PicartoSub, "T1", SubT1TextBox, SubT1TextBox2);
        hasUpdated |= Host!.SaveSubTier(db, SubathonEventType.PicartoSub, "T2", SubT2TextBox, SubT2TextBox2);
        hasUpdated |= Host!.SaveSubTier(db, SubathonEventType.PicartoSub, "T3", SubT3TextBox, SubT3TextBox2);
        hasUpdated |= Host!.SaveSubTier(db, SubathonEventType.PicartoGiftSub, "T1", GiftSubTextBox, GiftSubTextBox2); 
        return hasUpdated;
    }
    
    
    private void TestPicartoTip_Click(object sender, RoutedEventArgs e)
    {
        var value = SimulateTipAmt.Text;
        if (string.IsNullOrWhiteSpace(value)) return;
        
        PicartoTip tip = new PicartoTip
        {
            Channel = string.IsNullOrWhiteSpace(PicartoUserBox.Text) ? "SYSTEM" : PicartoUserBox.Text,
            Amount = decimal.Parse(value),
            Username = "SYSTEM"
        };
        PicartoService.ProcessAlert(tip);
    }
    
    private void TestPicartoFollow_Click(object sender, RoutedEventArgs e)
    {
        PicartoFollow follow = new PicartoFollow
        {
            Channel = string.IsNullOrWhiteSpace(PicartoUserBox.Text) ? "SYSTEM" : PicartoUserBox.Text,
            Username = "SYSTEM"
        };
        PicartoService.ProcessAlert(follow); 
    }

    private void TestPicartoSub_Click(object sender, RoutedEventArgs e)
    {
        // recurring but tiered
        int tier = 1;
        string selectedTier = (SimSubTierSelection.SelectedItem is System.Windows.Controls.ComboBoxItem item) 
            ? item.Content?.ToString() ?? "" 
            : "";
        switch (selectedTier)
        {
            case "Tier 1":
                tier = 1;
                break;
            case "Tier 2":
                tier = 2;
                break;
            case "Tier 3":
                tier = 3;
                break;
        }

        decimal amount = 4.99m;
        if (tier == 2) amount = 9.99m;
        if (tier == 3) amount = 14.99m;
        PicartoSubscription sub = new PicartoSubscription
        {
            Channel = string.IsNullOrWhiteSpace(PicartoUserBox.Text) ? "SYSTEM" : PicartoUserBox.Text,
            Username = "SYSTEM",
            Amount = amount,
            IsGift = false
        };
        PicartoService.ProcessAlert(sub); 
    }

    private void TestPicartoSubMonths_Click(object sender, RoutedEventArgs e)
    {
        // t1 but month selection, one time
        int months = 1;
        string selectedMonths = (SimSubMonthSelection.SelectedItem is System.Windows.Controls.ComboBoxItem item) 
            ? item.Content?.ToString() ?? "" 
            : "";
        int.TryParse(selectedMonths.Replace(" Months", "").Replace(" Month", "").Trim(), out months);
        
        decimal amount = 4.99m * months;
        PicartoSubscription sub = new PicartoSubscription
        {
            Channel = string.IsNullOrWhiteSpace(PicartoUserBox.Text) ? "SYSTEM" : PicartoUserBox.Text,
            Username = "SYSTEM",
            Amount = amount,
            IsGift = false
        };
        PicartoService.ProcessAlert(sub); 
    }
    
    private void TestPicartoGiftSub_Click(object sender, RoutedEventArgs e)
    {
        // gift but can be multiple months
        
        var value = SimGiftSubAmtInput.Text;
        if (string.IsNullOrWhiteSpace(value)) return;
        
        int months = 1;
        string selectedMonths = (SimGiftSubMonthSelection.SelectedItem is System.Windows.Controls.ComboBoxItem item) 
            ? item.Content?.ToString() ?? "" 
            : "";
        int.TryParse(selectedMonths.Replace(" Months", "").Replace(" Month", "").Trim(), out months);
        int.TryParse(value, out int amt);
        decimal amount = 4.99m * months * amt;
        PicartoSubscription sub = new PicartoSubscription
        {
            Channel = string.IsNullOrWhiteSpace(PicartoUserBox.Text) ? "SYSTEM" : PicartoUserBox.Text,
            Username = "SYSTEM",
            Amount = amount,
            Quantity = amt,
            IsGift = true
        };
        PicartoService.ProcessAlert(sub); 
    }
}