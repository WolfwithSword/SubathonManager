using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PicartoEventsLib.Abstractions.Models;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;
using SubathonManager.Data;
using SubathonManager.Integration;
using SubathonManager.UI.Services;

namespace SubathonManager.UI.Views.SettingsViews.Streaming;

public partial class PicartoSettings : SettingsControl
{
    private readonly ILogger? _logger = AppServices.Provider.GetRequiredService<ILogger<PicartoSettings>>();
    
    public PicartoSettings()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            IntegrationEvents.ConnectionUpdated += UpdateStatus;
            RegisterUnsavedChangeHandlers();
            UpdateStatus(Utils.GetConnection(SubathonEventSource.Picarto, "Chat"));
            UpdateStatus(Utils.GetConnection(SubathonEventSource.Picarto, "Alerts"));
        };

        Unloaded += (_, _) =>
        {
            IntegrationEvents.ConnectionUpdated -= UpdateStatus;
        };
    }
    
    public override void Init(SettingsView host)
    {
        Host = host;
        var config = AppServices.Provider.GetRequiredService<IConfig>();
        PicartoUserBox.Text = config.Get("Picarto", "Username", string.Empty)!;
                
        Dispatcher.Invoke(() =>
        {
            UpdateStatus(Utils.GetConnection(SubathonEventSource.Picarto, "Chat"));
            UpdateStatus(Utils.GetConnection(SubathonEventSource.Picarto, "Alerts"));
        });
    }

    internal override void UpdateStatus(IntegrationConnection? connection)
    {
        if (connection is not { Source: SubathonEventSource.Picarto }) return;
        Dispatcher.Invoke(() =>
        { 
            if (connection.Service == "Chat")
                Host.UpdateConnectionStatus(connection.Status, PicartoChatStatusText, ConnectPicartoBtn);
            else if (connection.Service == "Alerts")
                Host.UpdateConnectionStatus(connection.Status, PicartoClientStatusText, ConnectPicartoBtn);
        });
    }
    protected internal override bool UpdateConfigValueSettings()
    {
        bool hasUpdated = false;
        var config = AppServices.Provider.GetRequiredService<IConfig>();
        hasUpdated |= config.Set("Picarto", "Username", $"{PicartoUserBox.Text}");
        return hasUpdated;
    }

    public override void UpdateCurrencyBoxes(List<string> currencies, string selected)
    {
        return;
    }

    public override (string, string, TextBox?, TextBox?) GetValueBoxes(SubathonValue val)
    {
        string v = $"{val.Seconds}";
        string p = $"{val.Points}";
        TextBox? box = null;
        TextBox? box2 = null;
        switch (val.EventType)
        {
            case SubathonEventType.PicartoFollow:
                box = FollowTextBox;
                box2 = Follow2TextBox;
                break;
            case SubathonEventType.PicartoTip:
                v = $"{Math.Round(val.Seconds * 100)}";
                box = KudosTextBox;
                box2 = Kudos2TextBox;
                break;
            case SubathonEventType.PicartoSub:
                switch (val.Meta)
                {
                    case "T1":
                        box = SubT1TextBox;
                        box2 = SubT1TextBox2;
                        break;
                    case "T2":
                        box = SubT2TextBox;
                        box2 = SubT2TextBox2;
                        break;
                    case "T3":
                        box = SubT3TextBox;
                        box2 = SubT3TextBox2;
                        break;
                }

                break;
            case SubathonEventType.PicartoGiftSub:
                switch (val.Meta)
                {
                    case "T1":
                        box = GiftSubTextBox;
                        box2 = GiftSubTextBox2;
                        break;
                }

                break;
        }
        return (v, p, box, box2);
    }

    private async void ConnectPicartoButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var updated = UpdateConfigValueSettings();
            if (updated)
            {
                var config = AppServices.Provider.GetRequiredService<IConfig>();
                config.Save();
            }
            await ServiceManager.Picarto.UpdateChannel();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to initialize PicartoService");
        }
    }
    
    public override bool UpdateValueSettings(AppDbContext db)
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
        
        hasUpdated |= Host.SaveSubTier(db, SubathonEventType.PicartoSub, "T1", SubT1TextBox, SubT1TextBox2);
        hasUpdated |= Host.SaveSubTier(db, SubathonEventType.PicartoSub, "T2", SubT2TextBox, SubT2TextBox2);
        hasUpdated |= Host.SaveSubTier(db, SubathonEventType.PicartoSub, "T3", SubT3TextBox, SubT3TextBox2);
        hasUpdated |= Host.SaveSubTier(db, SubathonEventType.PicartoGiftSub, "T1", GiftSubTextBox, GiftSubTextBox2); 
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

        decimal amount = tier switch
        {
            2 => 9.99m,
            3 => 14.99m,
            _ => 4.99m
        };
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