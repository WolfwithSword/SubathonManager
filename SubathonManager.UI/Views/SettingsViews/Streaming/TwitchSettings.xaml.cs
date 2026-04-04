using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;
using SubathonManager.Data;
using SubathonManager.UI.Services;

namespace SubathonManager.UI.Views.SettingsViews.Streaming;

public partial class TwitchSettings : SettingsControl
{
    private readonly ILogger? _logger = AppServices.Provider.GetRequiredService<ILogger<TwitchSettings>>();
    public TwitchSettings()
    {
        InitializeComponent();     
        
        Loaded += (_, _) =>
        {
            IntegrationEvents.ConnectionUpdated += UpdateStatus;
            RegisterUnsavedChangeHandlers();
            UpdateStatus(Utils.GetConnection(SubathonEventSource.Twitch, "Chat"));
            UpdateStatus(Utils.GetConnection(SubathonEventSource.Twitch, "EventSub"));
            UpdateStatus(Utils.GetConnection(SubathonEventSource.Twitch, "API"));
        };

        Unloaded += (_, _) =>
        {
            IntegrationEvents.ConnectionUpdated -= UpdateStatus;
        };
    }
    public override void Init(SettingsView host)
    {
        Host = host;
        
        Dispatcher.Invoke(() =>
        {
            UpdateStatus(Utils.GetConnection(SubathonEventSource.Twitch, "Chat"));
            UpdateStatus(Utils.GetConnection(SubathonEventSource.Twitch, "EventSub"));
            UpdateStatus(Utils.GetConnection(SubathonEventSource.Twitch, "API"));
        });
        
        InitTwitchAutoSettings();
        LoadHypeTrainValues();
    }

    internal override void UpdateStatus(IntegrationConnection? connection)
    {
        if (connection is not { Source: SubathonEventSource.Twitch })
            return;
        // we only care to show for event sub mostly, if it fails, the rest may also fail
        Dispatcher.Invoke(() =>
        {
            string conStat = connection.Status ? "Connected" : "Disconnected";
            switch (connection.Service)
            {
                case "EventSub":
                {
                    string username = connection.Name != string.Empty ? connection.Name : "Disconnected";
                    if (!connection.Status)
                        username = "Disconnected";
                    if (TwitchStatusText.Text != username) TwitchStatusText.Text = username;
                    string connectBtn = connection.Name != string.Empty ? "Reconnect" : "Connect";
                    if (!connection.Status) connectBtn = "Connect";
                    if (ConnectTwitchBtn.Content.ToString() != connectBtn) ConnectTwitchBtn.Content = connectBtn;
                    if (EventSubStatusText.Text != conStat) EventSubStatusText.Text = conStat;
                    break;
                }
                case "Chat":
                {
                    if (ChatStatusText.Text != conStat) ChatStatusText.Text = conStat;
                    break;
                }
            }
        });
    }

    public override bool UpdateValueSettings(AppDbContext db)
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
        
        hasUpdated |= Host.SaveSubTier(db, SubathonEventType.TwitchSub, "1000", SubT1TextBox, SubT1TextBox2);
        hasUpdated |= Host.SaveSubTier(db, SubathonEventType.TwitchSub, "2000", SubT2TextBox, SubT2TextBox2) ;
        hasUpdated |= Host.SaveSubTier(db, SubathonEventType.TwitchSub, "3000", SubT3TextBox, SubT3TextBox2) ;
        hasUpdated |= Host.SaveSubTier(db, SubathonEventType.TwitchGiftSub, "1000", GiftSubT1TextBox, GiftSubT1TextBox2) ;
        hasUpdated |= Host.SaveSubTier(db, SubathonEventType.TwitchGiftSub, "2000", GiftSubT2TextBox, GiftSubT2TextBox2) ;
        hasUpdated |= Host.SaveSubTier(db, SubathonEventType.TwitchGiftSub, "3000", GiftSubT3TextBox, GiftSubT3TextBox2) ;
        return hasUpdated;
    }

    protected internal override bool UpdateConfigValueSettings()
    {
        bool hasUpdated = false;
        var config = AppServices.Provider.GetRequiredService<IConfig>();
        hasUpdated |= config.SetBool("Twitch", "PauseOnEnd", TwitchPauseOnEndBx.IsChecked);
        hasUpdated |= config.SetBool("Twitch", "LockOnEnd",  TwitchLockOnEndBx.IsChecked);
        hasUpdated |= config.SetBool("Twitch", "ResumeOnStart",  TwitchResumeOnStartBx.IsChecked);
        hasUpdated |= config.SetBool("Twitch", "UnlockOnStart",  TwitchUnlockOnStartBx.IsChecked);
        hasUpdated |= config.SetBool("Twitch", "HypeTrainMultiplier.Enabled",  HypeTrainMultBox.IsChecked);
        hasUpdated |= config.SetBool("Twitch", "HypeTrainMultiplier.Points",  HypeTrainMultPointsBox.IsChecked);
        hasUpdated |= config.SetBool("Twitch", "HypeTrainMultiplier.Time",  HypeTrainMultTimeBox.IsChecked);
        hasUpdated |= config.Set("Twitch", "HypeTrainMultiplier.Multiplier",  HypeTrainMultAmt.Text);
        return hasUpdated;
    }

    public override void UpdateCurrencyBoxes(List<string> currencies, string selected)
    {
        CurrencyBox.ItemsSource = currencies;
        CurrencyBox.SelectedItem = selected;
    }

    public override (string, string, TextBox?, TextBox?) GetValueBoxes(SubathonValue val)
    {
        string v = $"{val.Seconds}";
        string p = $"{val.Points}";
        TextBox? box = null;
        TextBox? box2 = null;
        switch (val.EventType)
        {
            case SubathonEventType.TwitchCharityDonation:
                box = DonoBox;
                box2 = DonoBox2;
                break;
            case SubathonEventType.TwitchFollow:
                box = FollowTextBox;
                box2 = Follow2TextBox;
                break;
            case SubathonEventType.TwitchCheer:
                v = $"{Math.Round(val.Seconds * 100)}";
                box = CheerTextBox;
                box2 = Cheer2TextBox; // in backend when adding, need to round down when adding for odd bits
                break;
            
            case SubathonEventType.TwitchSub:
                switch (val.Meta)
                {
                    case "1000":
                        box = SubT1TextBox;
                        box2 = SubT1TextBox2;
                        break;
                    case "2000":
                        box = SubT2TextBox;
                        box2 = SubT2TextBox2;
                        break;
                    case "3000":
                        box = SubT3TextBox;
                        box2 = SubT3TextBox2;
                        break;
                }
                break;;
            case SubathonEventType.TwitchGiftSub:
                switch (val.Meta)
                {
                    case "1000":
                        box = GiftSubT1TextBox;
                        box2 = GiftSubT1TextBox2;
                        break;
                    case "2000":
                        box = GiftSubT2TextBox;
                        box2 = GiftSubT2TextBox2;
                        break;
                    case "3000":
                        box = GiftSubT3TextBox;
                        box2 = GiftSubT3TextBox2;
                        break;
                }
                break;
            case SubathonEventType.TwitchRaid:
                box = RaidTextBox;
                box2 = Raid2TextBox;
                break;
        }
        return (v, p, box, box2);
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
        var pauseOnEnd = config.GetBool("Twitch", "PauseOnEnd", false);
        if (TwitchPauseOnEndBx.IsChecked != pauseOnEnd) TwitchPauseOnEndBx.IsChecked = pauseOnEnd;

        var lockOnEnd = config.GetBool("Twitch", "LockOnEnd", false);
        if (TwitchLockOnEndBx.IsChecked != lockOnEnd) TwitchLockOnEndBx.IsChecked = lockOnEnd;

        var resumeOnStart = config.GetBool("Twitch", "ResumeOnStart", false);
        if (TwitchResumeOnStartBx.IsChecked != resumeOnStart) TwitchResumeOnStartBx.IsChecked = resumeOnStart;

        var unlockOnStart = config.GetBool("Twitch", "UnlockOnStart", false);
        if (TwitchUnlockOnStartBx.IsChecked != unlockOnStart) TwitchUnlockOnStartBx.IsChecked = unlockOnStart;
    }
    
    private void LoadHypeTrainValues()
    {
        var config = AppServices.Provider.GetRequiredService<IConfig>();
        bool enabled = config.GetBool("Twitch", "HypeTrainMultiplier.Enabled", false);
        if (HypeTrainMultBox.IsChecked != enabled)
            HypeTrainMultBox.IsChecked = enabled;
        double.TryParse(config.Get("Twitch", "HypeTrainMultiplier.Multiplier", "1"),
            out var parsedAmt);
            
        if (HypeTrainMultAmt.Text != parsedAmt.ToString("0.00"))
            HypeTrainMultAmt.Text = parsedAmt.ToString("0.00");

        bool applyPts = config.GetBool("Twitch", "HypeTrainMultiplier.Points", false);
        bool applyTime = config.GetBool("Twitch", "HypeTrainMultiplier.Time", false);
            
        if (HypeTrainMultTimeBox.IsChecked != applyTime)
            HypeTrainMultTimeBox.IsChecked = applyTime;
        if (HypeTrainMultPointsBox.IsChecked != applyPts)
            HypeTrainMultPointsBox.IsChecked = applyPts;
    }
}