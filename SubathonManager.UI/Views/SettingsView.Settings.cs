using System.Windows;
using System.Windows.Controls;
using System.Diagnostics;
using SubathonManager.Data;
using SubathonManager.Core;
using SubathonManager.Core.Enums;

namespace SubathonManager.UI.Views
{
    public partial class SettingsView
    {

        private void InitCurrencySelects()
        {
            var currencies = App.AppEventService!.ValidEventCurrencies().OrderBy(x => x).ToList();
            DefaultCurrencyBox.ItemsSource = currencies;
            DefaultCurrencyBox.SelectedItem = Config.Data["Currency"]["Primary"]?.Trim().ToUpperInvariant() ?? "USD";
            
            StreamElementsSettingsControl.CurrencyBox.ItemsSource = currencies;
            StreamElementsSettingsControl.CurrencyBox.SelectedItem = DefaultCurrencyBox.Text;
            KoFiSettingsControl.CurrencyBox.ItemsSource = currencies;
            KoFiSettingsControl.CurrencyBox.SelectedItem = DefaultCurrencyBox.Text;
            StreamLabsSettingsControl.CurrencyBox.ItemsSource = currencies;
            StreamLabsSettingsControl.CurrencyBox.SelectedItem = DefaultCurrencyBox.Text;
            YouTubeSettingsControl.CurrencyBox.ItemsSource = currencies;
            YouTubeSettingsControl.CurrencyBox.SelectedItem = DefaultCurrencyBox.Text;
            TwitchSettingsControl.CurrencyBox.ItemsSource = currencies;
            TwitchSettingsControl.CurrencyBox.SelectedItem = DefaultCurrencyBox.Text;
            ExternalSettingsControl.CurrencyBox.ItemsSource = currencies;
            ExternalSettingsControl.CurrencyBox.SelectedItem = DefaultCurrencyBox.Text;
        }
        
        private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
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
        
        private void EventsSummary_Click(object sender, RoutedEventArgs e)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = $"http://localhost:{Config.Data["Server"]["Port"]}/api/data/amounts",
                UseShellExecute = true
            });
        }
        
        private async void ExportEvents_Click(object sender, RoutedEventArgs e)
        {
            await using var db = await _factory.CreateDbContextAsync();
            await AppDbContext.ActiveEventsToCsv(db);
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
        
        private void SaveTopAppSettings()
        {
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

        public void UpdateConnectionStatus(bool status, TextBlock? textBlock, Button? button)
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
        
        public void SaveSubTier(AppDbContext db, SubathonEventType type, string meta, Wpf.Ui.Controls.TextBox tb,
            Wpf.Ui.Controls.TextBox tb2)
        {
            var val = db.SubathonValues.FirstOrDefault(sv => sv.EventType == type && sv.Meta == meta);
            if (val != null && double.TryParse(tb.Text, out var seconds))
                val.Seconds = seconds;
            if (val != null && int.TryParse(tb2.Text, out var points))
                val.Points = points;
        }
        
        private void SaveAllSubathonValuesButton_Click(object sender, RoutedEventArgs e)
        {
            SaveTopAppSettings();
            using var db = _factory.CreateDbContext();
            
            ExternalSettingsControl.UpdateValueSettings(db);
            YouTubeSettingsControl.UpdateValueSettings(db);
            TwitchSettingsControl.UpdateValueSettings(db);
            StreamElementsSettingsControl.UpdateValueSettings(db);
            StreamLabsSettingsControl.UpdateValueSettings(db);
            KoFiSettingsControl.UpdateValueSettings(db);
            
            db.SaveChanges();
            KoFiSettingsControl.RefreshKoFiTierCombo();
            
            CommandsSettingsControl.UpdateValueSettings();
            WebhookLogSettingsControl.UpdateValueSettings();
            
            Config.Save();
            
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

        public void UpdateTimePointsBoxes(TextBox? boxTime, TextBox? boxPoints, string time, string points)
        {
            if (boxTime != null && boxTime.Text != time) boxTime.Text = time;
            if (boxPoints != null && boxPoints.Text != points) boxPoints.Text = points;
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
                    case SubathonEventType.ExternalDonation:
                        box = ExternalSettingsControl.DonoBox;
                        box2 = ExternalSettingsControl.DonoBox2;
                        break;
                    case SubathonEventType.KoFiDonation:
                        box = KoFiSettingsControl.DonoBox;
                        box2 = KoFiSettingsControl.DonoBox2;
                        break;
                    case SubathonEventType.YouTubeMembership:
                        box = YouTubeSettingsControl.MemberT1TextBox;
                        box2 = YouTubeSettingsControl.MemberT1TextBox2;
                        break;
                    case SubathonEventType.YouTubeGiftMembership:
                        box = YouTubeSettingsControl.GiftMemberT1TextBox;
                        box2 = YouTubeSettingsControl.GiftMemberT1TextBox2;
                        break;
                    case SubathonEventType.YouTubeSuperChat:
                        box = YouTubeSettingsControl.DonoBox;
                        box2 = YouTubeSettingsControl.DonoBox2;
                        break;
                    case SubathonEventType.TwitchCharityDonation:
                        box = TwitchSettingsControl.DonoBox;
                        box2 = TwitchSettingsControl.DonoBox2;
                        break;
                    case SubathonEventType.TwitchFollow:
                        box = TwitchSettingsControl.FollowTextBox;
                        box2 = TwitchSettingsControl.Follow2TextBox;
                        break;
                    case SubathonEventType.TwitchCheer:
                        v = $"{Math.Round(val.Seconds * 100)}";
                        box = TwitchSettingsControl.CheerTextBox;
                        box2 = TwitchSettingsControl.Cheer2TextBox; // in backend when adding, need to round down when adding for odd bits
                        break;
                    case SubathonEventType.TwitchSub:
                        switch (val.Meta)
                        {
                            case "1000":
                                box = TwitchSettingsControl.SubT1TextBox;
                                box2 = TwitchSettingsControl.SubT1TextBox2;
                                break;
                            case "2000":
                                box = TwitchSettingsControl.SubT2TextBox;
                                box2 = TwitchSettingsControl.SubT2TextBox2;
                                break;
                            case "3000":
                                box = TwitchSettingsControl.SubT3TextBox;
                                box2 = TwitchSettingsControl.SubT3TextBox2;
                                break;
                        }
                        break;
                    case SubathonEventType.TwitchGiftSub:
                        switch (val.Meta)
                        {
                            case "1000":
                                box = TwitchSettingsControl.GiftSubT1TextBox;
                                box2 = TwitchSettingsControl.GiftSubT1TextBox2;
                                break;
                            case "2000":
                                box = TwitchSettingsControl.GiftSubT2TextBox;
                                box2 = TwitchSettingsControl.GiftSubT2TextBox2;
                                break;
                            case "3000":
                                box = TwitchSettingsControl.GiftSubT3TextBox;
                                box2 = TwitchSettingsControl.GiftSubT3TextBox2;
                                break;
                        }
                        break;
                    case SubathonEventType.TwitchRaid:
                        box = TwitchSettingsControl.RaidTextBox;
                        box2 = TwitchSettingsControl.Raid2TextBox;
                        break;
                    case SubathonEventType.StreamElementsDonation:
                        box = StreamElementsSettingsControl.DonoBox;
                        box2 = StreamElementsSettingsControl.DonoBox2;
                        break;
                    case SubathonEventType.StreamLabsDonation:
                        box = StreamLabsSettingsControl.DonoBox;
                        box2 = StreamLabsSettingsControl.DonoBox2;
                        break;
                }
                if (box != null && box2 != null)
                    UpdateTimePointsBoxes(box, box2, v, p);
            }
            KoFiSettingsControl.LoadValues(db);
        }
    }
}
