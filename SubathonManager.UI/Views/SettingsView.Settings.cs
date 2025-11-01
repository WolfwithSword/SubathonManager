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
            if (TwitchPauseOnEndBx.IsChecked != pauseOnEnd) TwitchPauseOnEndBx.IsChecked = pauseOnEnd;
            
            bool.TryParse(Config.Data["Twitch"]["LockOnEnd"] ?? "false", out var lockOnEnd);
            if (TwitchLockOnEndBx.IsChecked != lockOnEnd) TwitchLockOnEndBx.IsChecked = lockOnEnd;
            
            bool.TryParse(Config.Data["Twitch"]["ResumeOnStart"] ?? "false", out var resumeOnStart);
            if (TwitchResumeOnStartBx.IsChecked != resumeOnStart) TwitchResumeOnStartBx.IsChecked = resumeOnStart;
            
            bool.TryParse(Config.Data["Twitch"]["UnlockOnStart"] ?? "false", out var unlockOnStart);
            if (TwitchUnlockOnStartBx.IsChecked != unlockOnStart) TwitchUnlockOnStartBx.IsChecked = unlockOnStart;
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
                string username = App.AppTwitchService!.UserName != string.Empty ? App.AppTwitchService.UserName! : "Disconnected";
                if (TwitchStatusText.Text != username) TwitchStatusText.Text = username; 
                string connectBtn = App.AppTwitchService!.UserName != string.Empty ? "Reconnect" : "Connect";
                if (ConnectTwitchBtn.Content.ToString() != connectBtn) ConnectTwitchBtn.Content = connectBtn;
            });
        }

        private void UpdateConnectionStatus(bool status, TextBlock? textBlock, Button? button)
        {
            Dispatcher.Invoke(() =>
            {
                if (textBlock == null || button == null) return;
                if (status && textBlock.Text != "Connected") textBlock.Text = "Connected";
                else if (!status && textBlock.Text != "Disconnected") textBlock.Text = "Disconnected";
                
                if (status && button.Content.ToString() != "Reconnect") button.Content = "Reconnect";
                else if (!status && button.Content.ToString() != "Connect") button.Content = "Connect";
            });
        }
        private void UpdateSEStatus(bool status)
        {
            UpdateConnectionStatus(status, SEStatusText, ConnectSEBtn);
        }

        private void UpdateSLStatus(bool status)
        {
            UpdateConnectionStatus(status, SLStatusText, ConnectSLBtn);
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
        
        private async void ConnectSLButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await App.AppStreamLabsService!.DisconnectAsync();
                App.AppStreamLabsService!.SetSocketToken(SLTokenBox.Password);
                await Task.Delay(100);
                await App.AppStreamLabsService!.InitClientAsync();
                if (App.AppStreamLabsService.IsTokenEmpty())
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "https://streamlabs.com/dashboard#/settings/api-settings",
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
            using var db = _factory.CreateDbContext();

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
            
            var slTipValue =
                db.SubathonValues.FirstOrDefault(sv =>
                    sv.EventType == SubathonEventType.StreamLabsDonation && sv.Meta == "");
            if (slTipValue != null && double.TryParse(SLTipBox.Text, out var slTipSeconds))
                slTipValue.Seconds = slTipSeconds;
            if (slTipValue != null && int.TryParse(SLTipBox2.Text, out var slTipPoints))
                slTipValue.Points = slTipPoints;
            
            db.SaveChanges();

            Config.Data["Twitch"]["PauseOnEnd"] = TwitchPauseOnEndBx.IsChecked.ToString();
            Config.Data["Twitch"]["LockOnEnd"] = TwitchLockOnEndBx.IsChecked.ToString();
            Config.Data["Twitch"]["ResumeOnStart"] = TwitchResumeOnStartBx.IsChecked.ToString();
            Config.Data["Twitch"]["UnlockOnStart"] = TwitchUnlockOnStartBx.IsChecked.ToString();
                
            UpdateWebhookSettings(); // also calls save
            
        }

        private void LoadValues()
        {
            using var db = _factory.CreateDbContext();

            // one minor possible issue
            // TODO UI will show these values even if not saved, so maybe have a textblock *after* each for "Current Value"?
            // reduce confusion for which values are active
            // also TODO: Add a "simulate" button next to each, which will simulate textbox value and type Simulated Event

            var values = db.SubathonValues.ToList();
            foreach (var val in values)
            {
                var v = $"{val.Seconds}";
                var p = $"{val.Points}";
                
                TextBox? box = null;
                TextBox? box2 = null;
                switch (val.EventType) 
                { 
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
                        break;
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
                    case SubathonEventType.StreamElementsDonation:
                        box = SETipBox;
                        box2 = SETipBox2;
                        break;
                    case SubathonEventType.StreamLabsDonation:
                        box = SLTipBox;
                        box2 = SLTipBox2;
                        break;
                }
                
                if (box != null && box.Text != v) box.Text = v;
                if (box2 != null && box2.Text != p) box2.Text = p;
            }
        }
    }
}
