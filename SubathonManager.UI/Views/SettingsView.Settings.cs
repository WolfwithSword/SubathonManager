using System.Windows;
using System.Windows.Controls;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SubathonManager.Data;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;

namespace SubathonManager.UI.Views
{
    public partial class SettingsView
    {

        private void InitCurrencySelects()
        {
            var currencies = App.AppEventService!.ValidEventCurrencies().OrderBy(x => x).ToList();
            DefaultCurrencyBox.ItemsSource = currencies;
            DefaultCurrencyBox.SelectedItem = Config.Data["Currency"]["Primary"]?.Trim().ToUpperInvariant() ?? "USD";
            SimulateSECurrencyBox.ItemsSource = currencies;
            SimulateSECurrencyBox.SelectedItem = DefaultCurrencyBox.Text;
            SimulateSLCurrencyBox.ItemsSource = currencies;
            SimulateSLCurrencyBox.SelectedItem = DefaultCurrencyBox.Text;
            SimulateSCCurrencyBox.ItemsSource = currencies;
            SimulateSCCurrencyBox.SelectedItem = DefaultCurrencyBox.Text;
        }
        
        
        private async void OpenDataFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = Config.DataFolder,
                    UseShellExecute = true,
                    Verb = "open"
                });
            } catch {/**/}
        }
        
        
        private async void ExportEvents_Click(object sender, RoutedEventArgs e)
        {
            await using var db = await _factory.CreateDbContextAsync();
            await AppDbContext.ActiveEventsToCsv(db);
        }
        
        private void InitCommandSettings()
        {
            foreach (SubathonCommandType commandType in Enum.GetValues(typeof(SubathonCommandType)))
            {
                if (commandType == SubathonCommandType.None || commandType == SubathonCommandType.Unknown) continue;
                // 200 | 30 blank | 200 | 120 | 120 | remain
                // enum / blank / name / mods / vips / whitelist 
                bool.TryParse(Config.Data["Twitch"][$"Commands.{commandType}.permissions.Mods"] ?? "false", out var checkMods);
                bool.TryParse(Config.Data["Twitch"][$"Commands.{commandType}.permissions.VIPs"] ?? "false", out var checkVips);
                string name = Config.Data["Twitch"][$"Commands.{commandType}.name"] ?? commandType.ToString().ToLower();
                string whitelist = (Config.Data["Twitch"][$"Commands.{commandType}.permissions.Whitelist"] ?? "");

                StackPanel entryPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Height = 40
                };
                
                TextBlock enumType = new TextBlock
                {
                    Text = commandType.ToString(),
                    Width = 200,
                    Margin = new Thickness(0, 0, 30, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Left,
                };

                TextBox enumName = new TextBox
                {
                    Text = name,
                    Width = 200,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Left,
                };

                CheckBox doMods = new CheckBox
                {
                    IsChecked = checkMods,
                    Width = 120,
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = "Allow Mods",
                    HorizontalAlignment = HorizontalAlignment.Center,
                };

                CheckBox doVips = new CheckBox
                {
                    IsChecked = checkVips,
                    Width = 120,
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = "Allow VIPs",
                    HorizontalAlignment = HorizontalAlignment.Center,
                };

                TextBox enumWhitelist = new TextBox
                {
                    Text = whitelist,
                    Width = 456,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Left,
                };

                entryPanel.Children.Add(enumType);
                entryPanel.Children.Add(enumName);
                entryPanel.Children.Add(doMods);
                entryPanel.Children.Add(doVips);
                entryPanel.Children.Add(enumWhitelist);

                CommandListPanel.Children.Add(entryPanel);
            }   
        }

        private void InitYoutubeSettings()
        {
            YTUserHandle.Text = Config.Data["YouTube"]["Handle"] ?? "";
        }
        
        private void UpdateCommandSettings()
        {
            bool updated = false;
            foreach (var child in CommandListPanel.Children)
            {
                if (child is StackPanel entry)

                    if (entry.Children[0] is TextBlock enumType)
                    {
                        string key = $"Commands.{enumType.Text}";
                        
                        if (entry.Children[1] is TextBox enumName &&
                            Config.Data["Twitch"][$"{key}.name"] != enumName.Text.Trim())
                        {
                            updated = true;
                            Config.Data["Twitch"][$"{key}.name"] = enumName.Text.Trim();
                        }
                    
                        if (entry.Children[2] is CheckBox doMods &&
                            Config.Data["Twitch"][$"{key}.permissions.Mods"] != $"{doMods.IsChecked}")
                        {
                            updated = true;
                            Config.Data["Twitch"][$"{key}.permissions.Mods"] = $"{doMods.IsChecked}";
                        }

                        if (entry.Children[3] is CheckBox doVips &&
                            Config.Data["Twitch"][$"{key}.permissions.VIPs"] != $"{doVips.IsChecked}")
                        {
                            updated = true;
                            Config.Data["Twitch"][$"{key}.permissions.VIPs"] = $"{doVips.IsChecked}";
                        }

                        if (entry.Children[4] is TextBox whitelist &&
                            Config.Data["Twitch"][$"{key}.permissions.Whitelist"] != whitelist.Text.Trim())
                        {
                            updated = true;
                            Config.Data["Twitch"][$"{key}.permissions.Whitelist"] = whitelist.Text.Trim();
                        }
                    }
            }
            Config.Save();
            if (updated)
            {
                TwitchEvents.RaiseCommandSettingsUpdated();
            }
        }

        
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

        private void UpdateServerStatus(bool status)
        {
            Dispatcher.Invoke(() =>
            {
                if (ServerStatusText == null) return;
                if (status && ServerStatusText.Text != "Running") ServerStatusText.Text = "Running";
                else if (!status && ServerStatusText.Text != "Not Running") ServerStatusText.Text = "Not Running";
            });
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
        
        private void SaveTopAppSettings()
        {
            // only top level ones, not subathon ones

            string selectedCurrency = DefaultCurrencyBox.Text;
            if (selectedCurrency.Length >= 3)
            {
                Config.Data["Currency"]["Primary"] = selectedCurrency;
                Config.Save();
                App.AppEventService!.ReInitCurrencyService();
            }
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

        private void UpdateYoutubeStatus(bool status,  string name)
        {
            Dispatcher.Invoke(() =>
            {
                if (YTUserHandle.Text != name && name != "None") YTUserHandle.Text = name; 
                UpdateConnectionStatus(status, YTStatusText, ConnectYTBtn);
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

        private void ConnectYouTubeButton_Click(object sender, RoutedEventArgs e)
        {
            string user = YTUserHandle.Text.Trim();
            if (!user.StartsWith("@"))
                user = "@" + user;
            Config.Data["YouTube"]["Handle"] = user;
            Config.Save();

            App.AppYouTubeService!.Start(user);
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
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to initialize StreamElements Service");
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
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to initialize StreamLabs Service");
            }
        }
        
        private void SaveAllSubathonValuesButton_Click(object sender, RoutedEventArgs e)
        {
            SaveTopAppSettings();
            using var db = _factory.CreateDbContext();
            
            void SaveSubTier(SubathonEventType type, string meta, Wpf.Ui.Controls.TextBox tb,
                Wpf.Ui.Controls.TextBox tb2)
            {
                var val = db.SubathonValues.FirstOrDefault(sv => sv.EventType == type && sv.Meta == meta);
                if (val != null && double.TryParse(tb.Text, out var seconds))
                    val.Seconds = seconds;
                if (val != null && int.TryParse(tb2.Text, out var points))
                    val.Points = points;
            }
            
            // YT Values
            var superchatValue = db.SubathonValues.FirstOrDefault(sv =>
                sv.EventType == SubathonEventType.YouTubeSuperChat
                && sv.Meta == "");
            if (superchatValue != null && double.TryParse(YTDonoBox.Text, out var scSeconds))
                superchatValue.Seconds = scSeconds;
            if (superchatValue != null && int.TryParse(YTDonoBox2.Text, out var scPoints))
                superchatValue.Points = scPoints;
            
            SaveSubTier(SubathonEventType.YouTubeMembership, "DEFAULT", MemberT1TextBox, MemberT1TextBox2);
            SaveSubTier(SubathonEventType.YouTubeGiftMembership, "DEFAULT", GiftMemberT1TextBox, GiftMemberT1TextBox2);
            
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
            
            UpdateCommandSettings();
            
            UpdateWebhookSettings();
            
            Task.Run(async () =>
            {
                await Dispatcher.InvokeAsync(() => 
                    { 
                        SaveAllSubathonValuesButton.Content = "Saved!";
                    } 
                );
                await Task.Delay(1500);
                await Dispatcher.InvokeAsync(() => 
                    { 
                        SaveAllSubathonValuesButton.Content = "Save All";
                    } 
                );
            });
        }

        private void LoadValues()
        {
            using var db = _factory.CreateDbContext();


            var values = db.SubathonValues.ToList();
            foreach (var val in values)
            {
                var v = $"{val.Seconds}";
                var p = $"{val.Points}";
                
                TextBox? box = null;
                TextBox? box2 = null;
                switch (val.EventType) 
                { 
                    case SubathonEventType.YouTubeMembership:
                        box = MemberT1TextBox;
                        box2 = MemberT1TextBox2;
                        break;
                    case SubathonEventType.YouTubeGiftMembership:
                        box = GiftMemberT1TextBox;
                        box2 = GiftMemberT1TextBox2;
                        break;
                    case SubathonEventType.YouTubeSuperChat:
                        box = YTDonoBox;
                        box2 = YTDonoBox2;
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
