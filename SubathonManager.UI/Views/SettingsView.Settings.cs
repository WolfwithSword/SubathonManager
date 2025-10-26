using System.Windows;
using System.Windows.Controls;
using System.Diagnostics;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Data;

namespace SubathonManager.UI.Views
{
    public partial class SettingsView
    {

        private void InitWebhookSettings()
        {
            foreach (SubathonEventType eventType in Enum.GetValues(typeof(SubathonEventType)))
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

        private void UpdateWebhookSettings()
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

        private void InitTwitchAutoSettings()
        {
            bool.TryParse(Config.Data["Twitch"]["PauseOnEnd"] ?? "false", out var pauseOnEnd);
            TwitchPauseOnEndBx.IsChecked = pauseOnEnd;
            
            bool.TryParse(Config.Data["Twitch"]["LockOnEnd"] ?? "false", out var lockOnEnd);
            TwitchLockOnEndBx.IsChecked = lockOnEnd;
            
            bool.TryParse(Config.Data["Twitch"]["ResumeOnStart"] ?? "false", out var resumeOnStart);
            TwitchResumeOnStartBx.IsChecked = resumeOnStart;
            
            bool.TryParse(Config.Data["Twitch"]["UnlockOnStart"] ?? "false", out var unlockOnStart);
            TwitchUnlockOnStartBx.IsChecked = unlockOnStart;
        }
        
        private void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            // only top level ones, not subathon ones
            if (int.TryParse(ServerPortTextBox.Text, out var port))
            {
                Config.Data["Server"]["Port"] = port.ToString();
                Config.Save();
            }
        }
        
        private void UpdateTwitchStatus()
        {
            Dispatcher.Invoke(() =>
            {
                TwitchStatusText.Text = App.AppTwitchService!.UserName != string.Empty ? App.AppTwitchService.UserName : "Disconnected";
                ConnectTwitchBtn.Content = App.AppTwitchService!.UserName != string.Empty ? "Reconnect" : "Connect";
            });
        }

        private void UpdateSEStatus(bool status)
        {
            Dispatcher.Invoke(() =>
            {
                SEStatusText.Text = status ? "Connected" : "Disconnected";
                ConnectSEBtn.Content = status? "Reconnect" : "Connect";
            });
        }

        private async void ConnectTwitchButton_Click(object sender, RoutedEventArgs e)
        {
            // TODO: Smartly stop everything inside before initialize, i think i do rn?
            try
            {
                try
                {
                    var cts = new CancellationTokenSource(5000);
                    await App.AppTwitchService!.StopAsync(cts.Token);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex); //
                }

                await App.AppTwitchService!.InitializeAsync();
                Console.WriteLine("Twitch connection established");
            }
            catch
            {
                Console.WriteLine("Failed to initialize twitch service.");
            }
        }

        private async void ConnectSEButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                App.AppStreamElementsService!.Disconnect();
                App.AppStreamElementsService!.SetJwtToken(SEJWTTokenBox.Password);
                await Task.Delay(100);
                App.AppStreamElementsService!.InitClient();
                if (App.AppStreamElementsService.IsTokenEmpty())
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "https://streamelements.com/dashboard/account/channels",
                        UseShellExecute = true
                    });
                }
            }
            catch
            {
                Console.WriteLine("Failed to initialize streamelements service."); 
            }
        }
        
        private void SaveAllSubathonValuesButton_Click(object sender, RoutedEventArgs e)
        {

            using var db = new AppDbContext();

            // Twitch values
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

            void SaveSubTier(SubathonEventType type, string meta, Wpf.Ui.Controls.TextBox tb,
                Wpf.Ui.Controls.TextBox tb2)
            {
                var val = db.SubathonValues.FirstOrDefault(sv => sv.EventType == type && sv.Meta == meta);
                if (val != null && double.TryParse(tb.Text, out var seconds))
                    val.Seconds = seconds;
                if (val != null && int.TryParse(tb2.Text, out var points))
                    val.Points = points;

            }

            SaveSubTier(SubathonEventType.TwitchSub, "1000", SubT1TextBox, SubT1TextBox2);
            SaveSubTier(SubathonEventType.TwitchSub, "2000", SubT2TextBox, SubT2TextBox2);
            SaveSubTier(SubathonEventType.TwitchSub, "3000", SubT3TextBox, SubT3TextBox2);
            SaveSubTier(SubathonEventType.TwitchGiftSub, "1000", GiftSubT1TextBox, GiftSubT1TextBox2);
            SaveSubTier(SubathonEventType.TwitchGiftSub, "2000", GiftSubT2TextBox, GiftSubT2TextBox2);
            SaveSubTier(SubathonEventType.TwitchGiftSub, "3000", GiftSubT3TextBox, GiftSubT3TextBox2);

            var seTipValue =
                db.SubathonValues.FirstOrDefault(sv =>
                    sv.EventType == SubathonEventType.StreamElementsDonation && sv.Meta == "");
            if (seTipValue != null && double.TryParse(SETipBox.Text, out var seTipSeconds))
                seTipValue.Seconds = seTipSeconds;
            if (seTipValue != null && int.TryParse(SETipBox2.Text, out var seTipPoints))
                seTipValue.Points = seTipPoints;

            db.SaveChanges();

            Config.Data["Twitch"]["PauseOnEnd"] = TwitchPauseOnEndBx.IsChecked.ToString();
            Config.Data["Twitch"]["LockOnEnd"] = TwitchLockOnEndBx.IsChecked.ToString();
            Config.Data["Twitch"]["ResumeOnStart"] = TwitchResumeOnStartBx.IsChecked.ToString();
            Config.Data["Twitch"]["UnlockOnStart"] = TwitchUnlockOnStartBx.IsChecked.ToString();
                
            UpdateWebhookSettings(); // also calls save
            
        }

        private void LoadValues()
        {
            using var db = new AppDbContext();

            // one minor possible issue
            // TODO UI will show these values even if not saved, so maybe have a textblock *after* each for "Current Value"?
            // reduce confusion for which values are active
            // also TODO: Add a "simulate" button next to each, which will simulate textbox value and type Simulated Event

            var values = db.SubathonValues.ToList();
            foreach (var val in values)
            {
                var v = $"{val.Seconds}";
                var p = $"{val.Points}";
                switch (val.EventType) 
                { 
                    case SubathonEventType.TwitchFollow:
                        FollowTextBox.Text = v;
                        Follow2TextBox.Text = p;
                        break;
                    case SubathonEventType.TwitchCheer:
                        CheerTextBox.Text = $"{Math.Round(val.Seconds * 100)}";
                        Cheer2TextBox.Text =  $"{val.Points}"; // in backend when adding, need to round down when adding for odd bits
                        break;
                    case SubathonEventType.TwitchSub:
                        switch (val.Meta)
                        {
                            case "1000":
                                SubT1TextBox.Text = v;
                                SubT1TextBox2.Text = p;
                                break;
                            case "2000":
                                SubT2TextBox.Text = v;
                                SubT2TextBox2.Text = p;
                                break;
                            case "3000":
                                SubT3TextBox.Text = v;
                                SubT3TextBox2.Text = p;
                                break;
                        }

                        break;
                    case SubathonEventType.TwitchGiftSub:
                        switch (val.Meta)
                        {
                            case "1000":
                                GiftSubT1TextBox.Text = v;
                                GiftSubT1TextBox2.Text = p;
                                break;
                            case "2000":
                                GiftSubT2TextBox.Text = v;
                                GiftSubT2TextBox2.Text = p;
                                break;
                            case "3000":
                                GiftSubT3TextBox.Text = v;
                                GiftSubT3TextBox2.Text = p;
                                break;
                        }
                        break;
                    case SubathonEventType.TwitchRaid:
                        RaidTextBox.Text = v;
                        Raid2TextBox.Text = p;
                        break;
                    case SubathonEventType.StreamElementsDonation:
                        SETipBox.Text = v;
                        SETipBox2.Text = p;
                        break;
                }
            }
        }
    }
}
