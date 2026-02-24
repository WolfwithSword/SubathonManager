using System.Windows.Controls;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubathonManager.Core.Events;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Interfaces;
using SubathonManager.Data;
using SubathonManager.UI.Services;

namespace SubathonManager.UI.Views.SettingsViews;

public partial class TwitchSettings : UserControl
{
    public required SettingsView Host { get; set; }
    private readonly ILogger? _logger = AppServices.Provider.GetRequiredService<ILogger<TwitchSettings>>();
    public TwitchSettings()
    {
        InitializeComponent();
    }
    public void Init(SettingsView host)
    {
        Host = host;
        InitTwitchAutoSettings();
        LoadHypeTrainValues();
        IntegrationEvents.ConnectionUpdated += UpdateTwitchStatus;
    }
    
    private void UpdateTwitchStatus(bool status, SubathonEventSource source, string name, string service)
    {
        if (source != SubathonEventSource.Twitch)
            return;
        // we only care to show for event sub mostly, if it fails, the rest may also fail
        Dispatcher.Invoke(() =>
        {
            string conStat = status ? "Connected" : "Disconnected";
            if (service == "EventSub")
            {
                string username = name != string.Empty ? name : "Disconnected";
                if (!status)
                    username = "Disconnected";
                if (TwitchStatusText.Text != username) TwitchStatusText.Text = username;
                string connectBtn = name != string.Empty ? "Reconnect" : "Connect";
                if (!status) connectBtn = "Connect";
                if (ConnectTwitchBtn.Content.ToString() != connectBtn) ConnectTwitchBtn.Content = connectBtn;
                if (EventSubStatusText.Text != conStat) EventSubStatusText.Text = conStat;
            }
            else if (service == "Chat")
            {
                if (ChatStatusText.Text != conStat) ChatStatusText.Text = conStat;
            }
        });
    }

    public bool UpdateValueSettings(AppDbContext db)
    {
        bool hasUpdated = false;
        var cheerValue =
            db.SubathonValues.FirstOrDefault(sv => sv.EventType == SubathonEventType.TwitchCheer && sv.Meta == "");
        // divide by 100 since UI shows "per 100 bits"
        if (cheerValue != null && double.TryParse(CheerTextBox.Text, out var cheerSeconds)
            && !cheerValue.Seconds.Equals(cheerSeconds / 100.0))
        {
            cheerValue.Seconds = cheerSeconds / 100.0;
            hasUpdated = true;
        }

        if (cheerValue != null && double.TryParse(Cheer2TextBox.Text, out var cheerPoints) &&
            !cheerValue.Points.Equals(cheerPoints))
        {
            cheerValue.Points = cheerPoints;
            hasUpdated = true;
        }

        var raidValue =
            db.SubathonValues.FirstOrDefault(sv => sv.EventType == SubathonEventType.TwitchRaid && sv.Meta == "");
        if (raidValue != null && double.TryParse(RaidTextBox.Text, out var raidSeconds)
            && !raidValue.Seconds.Equals(raidSeconds))
        {
            raidValue.Seconds = raidSeconds;
            hasUpdated = true;
        }

        if (raidValue != null && double.TryParse(Raid2TextBox.Text, out var raidPoints) &&
            !raidValue.Points.Equals(raidPoints))
        {
            raidValue.Points = raidPoints;
            hasUpdated = true;
        }

        var followValue =
            db.SubathonValues.FirstOrDefault(sv => sv.EventType == SubathonEventType.TwitchFollow && sv.Meta == "");
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

        var tcdTipValue =
            db.SubathonValues.FirstOrDefault(sv =>
                sv.EventType == SubathonEventType.TwitchCharityDonation && sv.Meta == "");
        if (tcdTipValue != null && double.TryParse(DonoBox.Text, out var tcdTipSeconds)
            && !tcdTipValue.Seconds.Equals(tcdTipSeconds))
        {
            tcdTipValue.Seconds = tcdTipSeconds;
            hasUpdated = true;
        }

        if (tcdTipValue != null && double.TryParse(DonoBox2.Text, out var tcdTipPoints)
            && !tcdTipValue.Points.Equals(tcdTipPoints))
        {
            tcdTipValue.Points = tcdTipPoints;
            hasUpdated = true;
        }
        
        hasUpdated |= Host!.SaveSubTier(db, SubathonEventType.TwitchSub, "1000", SubT1TextBox, SubT1TextBox2);
        hasUpdated |= Host!.SaveSubTier(db, SubathonEventType.TwitchSub, "2000", SubT2TextBox, SubT2TextBox2) ;
        hasUpdated |= Host!.SaveSubTier(db, SubathonEventType.TwitchSub, "3000", SubT3TextBox, SubT3TextBox2) ;
        hasUpdated |= Host!.SaveSubTier(db, SubathonEventType.TwitchGiftSub, "1000", GiftSubT1TextBox, GiftSubT1TextBox2) ;
        hasUpdated |= Host!.SaveSubTier(db, SubathonEventType.TwitchGiftSub, "2000", GiftSubT2TextBox, GiftSubT2TextBox2) ;
        hasUpdated |= Host!.SaveSubTier(db, SubathonEventType.TwitchGiftSub, "3000", GiftSubT3TextBox, GiftSubT3TextBox2) ;
        return hasUpdated;
    }

    public void UpdateConfigValueSettings()
    {
        var config = AppServices.Provider.GetRequiredService<IConfig>();
        config!.Set("Twitch", "PauseOnEnd", $"{TwitchPauseOnEndBx.IsChecked}");
        config!.Set("Twitch", "LockOnEnd",  $"{TwitchLockOnEndBx.IsChecked}");
        config!.Set("Twitch", "ResumeOnStart",  $"{TwitchResumeOnStartBx.IsChecked}");
        config!.Set("Twitch", "UnlockOnStart",  $"{TwitchUnlockOnStartBx.IsChecked}");
        config!.Set("Twitch", "HypeTrainMultiplier.Enabled",  $"{HypeTrainMultBox.IsChecked}");
        config!.Set("Twitch", "HypeTrainMultiplier.Points",  $"{HypeTrainMultPointsBox.IsChecked}");
        config!.Set("Twitch", "HypeTrainMultiplier.Time",  $"{HypeTrainMultTimeBox.IsChecked}");
        config!.Set("Twitch", "HypeTrainMultiplier.Multiplier",  HypeTrainMultAmt.Text);
        config!.Save();
    }
    
    private async void ConnectTwitchButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            try
            {
                var cts = new CancellationTokenSource(5000);
                await ServiceManager.Twitch.StopAsync(cts.Token);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error in Twitch connection");
            }

            await ServiceManager.Twitch.InitializeAsync();
            _logger?.LogInformation("Twitch connection established");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to initialize TwitchService");
        }
    }
    
    private void InitTwitchAutoSettings()
    {
        var config = AppServices.Provider.GetRequiredService<IConfig>();
        bool.TryParse(config!.Get("Twitch", "PauseOnEnd", "false"), out var pauseOnEnd);
        if (TwitchPauseOnEndBx.IsChecked != pauseOnEnd) TwitchPauseOnEndBx.IsChecked = pauseOnEnd;
            
        bool.TryParse(config!.Get("Twitch", "LockOnEnd", "false"), out var lockOnEnd);
        if (TwitchLockOnEndBx.IsChecked != lockOnEnd) TwitchLockOnEndBx.IsChecked = lockOnEnd;
            
        bool.TryParse(config!.Get("Twitch", "ResumeOnStart", "false"), out var resumeOnStart);
        if (TwitchResumeOnStartBx.IsChecked != resumeOnStart) TwitchResumeOnStartBx.IsChecked = resumeOnStart;
            
        bool.TryParse(config!.Get("Twitch", "UnlockOnStart", "false"), out var unlockOnStart);
        if (TwitchUnlockOnStartBx.IsChecked != unlockOnStart) TwitchUnlockOnStartBx.IsChecked = unlockOnStart;
    }
    
    private void LoadHypeTrainValues()
    {
        var config = AppServices.Provider.GetRequiredService<IConfig>();
        bool.TryParse(config!.Get("Twitch", "HypeTrainMultiplier.Enabled", "false"),
            out bool enabled);
        if (HypeTrainMultBox.IsChecked != enabled)
            HypeTrainMultBox.IsChecked = enabled;
        double.TryParse(config!.Get("Twitch", "HypeTrainMultiplier.Multiplier", "1"),
            out var parsedAmt);
            
        if (HypeTrainMultAmt.Text != parsedAmt.ToString("0.00"))
            HypeTrainMultAmt.Text = parsedAmt.ToString("0.00");

        bool.TryParse(config!.Get("Twitch", "HypeTrainMultiplier.Points", "false"),
            out var applyPts);
        bool.TryParse(config!.Get("Twitch", "HypeTrainMultiplier.Time", "false"),
            out var applyTime);
            
        if (HypeTrainMultTimeBox.IsChecked != applyTime)
            HypeTrainMultTimeBox.IsChecked = applyTime;
        if (HypeTrainMultPointsBox.IsChecked != applyPts)
            HypeTrainMultPointsBox.IsChecked = applyPts;
    }
}