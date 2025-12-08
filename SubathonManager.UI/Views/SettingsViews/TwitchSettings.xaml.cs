using System.Windows.Controls;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubathonManager.Core.Events;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Data;

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
        TwitchEvents.TwitchConnected += UpdateTwitchStatus;
    }
    
    private void UpdateTwitchStatus()
    {
        Dispatcher.Invoke(() =>
        {
            string username = App.AppTwitchService!.UserName != string.Empty ? App.AppTwitchService.UserName! : "Disconnected";
            if (TwitchStatusText.Text != username) TwitchStatusText.Text = username; 
            string connectBtn = App.AppTwitchService!.UserName != string.Empty ? "Reconnect" : "Connect";
            if (ConnectTwitchBtn.Content.ToString() != connectBtn) ConnectTwitchBtn.Content = connectBtn;
        });
    }

    public void UpdateValueSettings(AppDbContext db)
    {
        var cheerValue =
            db.SubathonValues.FirstOrDefault(sv => sv.EventType == SubathonEventType.TwitchCheer && sv.Meta == "");
        // divide by 100 since UI shows "per 100 bits"
        if (cheerValue != null && double.TryParse(CheerTextBox.Text, out var cheerSeconds))
            cheerValue.Seconds = cheerSeconds / 100.0;
        if (cheerValue != null && int.TryParse(Cheer2TextBox.Text, out var cheerPoints))
            cheerValue.Points = cheerPoints;

        var raidValue =
            db.SubathonValues.FirstOrDefault(sv => sv.EventType == SubathonEventType.TwitchRaid && sv.Meta == "");
        if (raidValue != null && double.TryParse(RaidTextBox.Text, out var raidSeconds))
            raidValue.Seconds = raidSeconds;
        if (raidValue != null && int.TryParse(Raid2TextBox.Text, out var raidPoints))
            raidValue.Points = raidPoints;

        var followValue =
            db.SubathonValues.FirstOrDefault(sv => sv.EventType == SubathonEventType.TwitchFollow && sv.Meta == "");
        if (followValue != null && double.TryParse(FollowTextBox.Text, out var followSeconds))
            followValue.Seconds = followSeconds;
        if (followValue != null && int.TryParse(Follow2TextBox.Text, out var followPoints))
            followValue.Points = followPoints;
        
        var tcdTipValue =
            db.SubathonValues.FirstOrDefault(sv =>
                sv.EventType == SubathonEventType.TwitchCharityDonation && sv.Meta == "");
        if (tcdTipValue != null && double.TryParse(DonoBox.Text, out var tcdTipSeconds))
            tcdTipValue.Seconds = tcdTipSeconds;
        if (tcdTipValue != null && int.TryParse(DonoBox2.Text, out var tcdTipPoints))
            tcdTipValue.Points = tcdTipPoints;

        Host!.SaveSubTier(db, SubathonEventType.TwitchSub, "1000", SubT1TextBox, SubT1TextBox2);
        Host!.SaveSubTier(db, SubathonEventType.TwitchSub, "2000", SubT2TextBox, SubT2TextBox2);
        Host!.SaveSubTier(db, SubathonEventType.TwitchSub, "3000", SubT3TextBox, SubT3TextBox2);
        Host!.SaveSubTier(db, SubathonEventType.TwitchGiftSub, "1000", GiftSubT1TextBox, GiftSubT1TextBox2);
        Host!.SaveSubTier(db, SubathonEventType.TwitchGiftSub, "2000", GiftSubT2TextBox, GiftSubT2TextBox2);
        Host!.SaveSubTier(db, SubathonEventType.TwitchGiftSub, "3000", GiftSubT3TextBox, GiftSubT3TextBox2);
        
        Config.Data["Twitch"]["PauseOnEnd"] = TwitchPauseOnEndBx.IsChecked.ToString();
        Config.Data["Twitch"]["LockOnEnd"] = TwitchLockOnEndBx.IsChecked.ToString();
        Config.Data["Twitch"]["ResumeOnStart"] = TwitchResumeOnStartBx.IsChecked.ToString();
        Config.Data["Twitch"]["UnlockOnStart"] = TwitchUnlockOnStartBx.IsChecked.ToString();
        Config.Data["Twitch"]["HypeTrainMultiplier.Enabled"] = $"{HypeTrainMultBox.IsChecked}";
        Config.Data["Twitch"]["HypeTrainMultiplier.Points"] = $"{HypeTrainMultPointsBox.IsChecked}";
        Config.Data["Twitch"]["HypeTrainMultiplier.Time"] = $"{HypeTrainMultTimeBox.IsChecked}";
        Config.Data["Twitch"]["HypeTrainMultiplier.Multiplier"] = HypeTrainMultAmt.Text;
        Config.Save();
    }
    
    private async void ConnectTwitchButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            try
            {
                var cts = new CancellationTokenSource(5000);
                await App.AppTwitchService!.StopAsync(cts.Token);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error in Twitch connection");
            }

            await App.AppTwitchService!.InitializeAsync();
            _logger?.LogInformation("Twitch connection established");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to initialize TwitchService");
        }
    }
    
    private void InitTwitchAutoSettings()
    {
        bool.TryParse(Config.Data["Twitch"]["PauseOnEnd"] ?? "false", out var pauseOnEnd);
        if (TwitchPauseOnEndBx.IsChecked != pauseOnEnd) TwitchPauseOnEndBx.IsChecked = pauseOnEnd;
            
        bool.TryParse(Config.Data["Twitch"]["LockOnEnd"] ?? "false", out var lockOnEnd);
        if (TwitchLockOnEndBx.IsChecked != lockOnEnd) TwitchLockOnEndBx.IsChecked = lockOnEnd;
            
        bool.TryParse(Config.Data["Twitch"]["ResumeOnStart"] ?? "false", out var resumeOnStart);
        if (TwitchResumeOnStartBx.IsChecked != resumeOnStart) TwitchResumeOnStartBx.IsChecked = resumeOnStart;
            
        bool.TryParse(Config.Data["Twitch"]["UnlockOnStart"] ?? "false", out var unlockOnStart);
        if (TwitchUnlockOnStartBx.IsChecked != unlockOnStart) TwitchUnlockOnStartBx.IsChecked = unlockOnStart;
    }
    
    private void LoadHypeTrainValues()
    {
        bool.TryParse(Config.Data["Twitch"]["HypeTrainMultiplier.Enabled"] ?? "false", out bool enabled);
        if (HypeTrainMultBox.IsChecked != enabled)
            HypeTrainMultBox.IsChecked = enabled;
        double.TryParse(Config.Data["Twitch"]["HypeTrainMultiplier.Multiplier"] ?? "1",
            out var parsedAmt);
            
        if (HypeTrainMultAmt.Text != parsedAmt.ToString("0.00"))
            HypeTrainMultAmt.Text = parsedAmt.ToString("0.00");

        bool.TryParse(Config.Data["Twitch"]["HypeTrainMultiplier.Points"] ?? "false",
            out var applyPts);
        bool.TryParse(Config.Data["Twitch"]["HypeTrainMultiplier.Time"] ?? "false",
            out var applyTime);
            
        if (HypeTrainMultTimeBox.IsChecked != applyTime)
            HypeTrainMultTimeBox.IsChecked = applyTime;
        if (HypeTrainMultPointsBox.IsChecked != applyPts)
            HypeTrainMultPointsBox.IsChecked = applyPts;
    }
    
    
}