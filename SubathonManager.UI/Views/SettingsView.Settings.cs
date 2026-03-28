using System.Windows;
using System.Windows.Controls;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using SubathonManager.Data;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.UI.Services;
using Button = System.Windows.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;

namespace SubathonManager.UI.Views
{
    public partial class SettingsView
    {

        private void InitCurrencySelects()
        {
            var currencies = ServiceManager.Events.ValidEventCurrencies().OrderBy(x => x).ToList();
            DefaultCurrencyBox.ItemsSource = currencies;
            
            var config = AppServices.Provider.GetRequiredService<IConfig>();
            DefaultCurrencyBox.SelectedItem = config.Get("Currency", "Primary", "USD")?.Trim().ToUpperInvariant() ?? "USD";
            
            ExtensionSettingsControl.UpdateCurrencyBoxes(currencies, DefaultCurrencyBox.Text);
            ExternalServiceSettingsControl.UpdateCurrencyBoxes(currencies, DefaultCurrencyBox.Text);
            StreamingSettingsControl.UpdateCurrencyBoxes(currencies, DefaultCurrencyBox.Text);
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
            var config = AppServices.Provider.GetRequiredService<IConfig>();
            Process.Start(new ProcessStartInfo
            {
                FileName = $"http://localhost:{config.Get("Server", "Port", "14040")}/api/data/amounts",
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
        
        private bool SaveTopAppSettings()
        {
            bool hasUpdated = false;
            string selectedCurrency = DefaultCurrencyBox.Text;
            
            var config = AppServices.Provider.GetRequiredService<IConfig>();
            hasUpdated |= config.Set("Currency", "BitsLikeAsDonation", $"{BitsAsCurrencyBox.IsChecked}");
            

            if (selectedCurrency.Length >= 3)
            {
                if (config.Get("Currency", "Primary", string.Empty) != selectedCurrency)
                {
                    hasUpdated |= config.Set("Currency", "Primary", selectedCurrency);
                    ServiceManager.Events.ReInitCurrencyService();
                }
            }
            if (int.TryParse(ServerPortTextBox.Text, out var port))
            {
                hasUpdated |= config.Set("Server", "Port", port.ToString());
            }
            
            string selectedTheme = (ThemeBox.SelectedItem is ComboBoxItem item) 
                ? item.Content?.ToString() ?? "" 
                : "";
            if (!string.IsNullOrEmpty(selectedTheme))
            {
                hasUpdated |= config.Set("App", "Theme", selectedTheme);
            }
            hasUpdated |= ExtensionSettingsControl.SaveConfigValues();
            return hasUpdated;
        }

        public void UpdateConnectionStatus(bool status, TextBlock? textBlock, Button? button)
        {
            Dispatcher.Invoke(() =>
            {
                if (textBlock != null)
                {
                    if (status && textBlock.Text != "Connected") textBlock.Text = "Connected";
                    else if (!status && textBlock.Text != "Disconnected") textBlock.Text = "Disconnected";
                }

                if (button == null) return;
                if (status && button.Content.ToString() != "Reconnect") button.Content = "Reconnect";
                else if (!status && button.Content.ToString() != "Connect") button.Content = "Connect";
            });
        }
        
        public bool SaveSubTier(AppDbContext db, SubathonEventType type, string meta, Wpf.Ui.Controls.TextBox tb,
            Wpf.Ui.Controls.TextBox tb2)
        {
            bool hasUpdated = false;
            var val = db.SubathonValues.FirstOrDefault(sv => sv.EventType == type && sv.Meta == meta);
            if (val != null && double.TryParse(tb.Text, out var seconds) && !seconds.Equals(val.Seconds))
            {
                val.Seconds = seconds;
                hasUpdated = true;
            }

            if (val != null && int.TryParse(tb2.Text, out var points) && !points.Equals((int)val.Points))
            {
                val.Points = points;
                hasUpdated = true;
            }

            return hasUpdated;
        }

        private void UpdateSubathonValues()
        {
            using var db = _factory.CreateDbContext();

            var updaters = new Func<AppDbContext, bool>[]
            {
                StreamingSettingsControl.UpdateValueSettings,
                ExtensionSettingsControl.UpdateValueSettings,
                ExternalServiceSettingsControl.UpdateValueSettings
            };
            
            bool hasUpdated = updaters.Aggregate(false, (current, updater) => current | updater(db));

            db.SaveChanges();

            if (!hasUpdated) return;
            SubathonValueConfigHelper helper = new SubathonValueConfigHelper(null, null);
            var newData = helper.GetAllAsJson();
            SubathonEvents.RaiseSubathonValueConfigRequested(newData);
        }

        private void UpdateSaveButtonBorder( bool hasPendingChanges)
        {
            Dispatcher.InvokeAsync(() =>
            {
                UiUtils.UiUtils.UpdateButtonPendingBorder(SaveButtonBorder, hasPendingChanges);
            });
        }
        
        private void SaveAllSubathonValuesButton_Click(object sender, RoutedEventArgs e)
        {
            bool hasUpdated = false;
            hasUpdated |= SaveTopAppSettings();
            UpdateSubathonValues();
            hasUpdated |= StreamingSettingsControl.UpdateConfigValueSettings();
            ExternalServiceSettingsControl.RefreshTierCombo(SubathonEventSource.KoFi); 
            ExternalServiceSettingsControl.RefreshTierCombo(SubathonEventSource.External);
            StreamingSettingsControl.RefreshTierCombo(SubathonEventSource.YouTube);
            hasUpdated |= ExternalServiceSettingsControl.UpdateConfigValueSettings();
            hasUpdated |= CommandsSettingsControl.UpdateConfigValueSettings();
            hasUpdated |= WebhookLogSettingsControl.UpdateConfigValueSettings();

            if (hasUpdated)
            {
                var config = AppServices.Provider.GetRequiredService<IConfig>();
                config.Save();
            }

            Task.Run(async () =>
            {
                await Dispatcher.InvokeAsync(() => 
                    { 
                        SaveAllSubathonValuesButton.Content = "Saved!";
                        UpdateSaveButtonBorder(false);
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

        private void RefreshSubathonValues()
        {
            Dispatcher.Invoke(() =>LoadValues(false));
        }

        private void LoadValues(bool doConfigLoad = true)
        {
            using var db = _factory.CreateDbContext();
            var values = db.SubathonValues.ToList();
            foreach (var val in values)
            {
                var v = $"{val.Seconds}";
                var p = $"{val.Points}";
                
                TextBox? box = null;
                TextBox? box2 = null;
                var source = val.EventType.GetSource();
                if (source.GetGroup() == SubathonSourceGroup.Stream)
                {
                    
                    (v, p, box, box2) = StreamingSettingsControl.GetValueBoxes(val);
                }
                else if (source.GetGroup() == SubathonSourceGroup.StreamExtension)
                {
                    (v, p, box, box2) = ExtensionSettingsControl.GetValueBoxes(val);
                }
                else if (source.GetGroup() == SubathonSourceGroup.ExternalService)
                {
                    (v, p, box, box2) = ExternalServiceSettingsControl.GetValueBoxes(val);
                }

                if (box != null && box2 != null)
                    UpdateTimePointsBoxes(box, box2, v, p);
            }

            if (doConfigLoad)
            {
                var config = AppServices.Provider.GetRequiredService<IConfig>();
                bool bitsAsDonation = config.GetBool("Currency", "BitsLikeAsDonation", false);
                BitsAsCurrencyBox.IsChecked = bitsAsDonation;
                
                var theme = config.Get("App", "Theme", "Dark")!;
                foreach (ComboBoxItem item in ThemeBox.Items)
                {
                    if (theme.Equals((string)item.Content, StringComparison.OrdinalIgnoreCase))
                    {
                        ThemeBox.SelectedItem = item;
                        break;
                    }
                }
                ExtensionSettingsControl.LoadConfigValues();
            }

            StreamingSettingsControl.LoadValues(db);
            ExternalServiceSettingsControl.LoadValues(db);
        }

        public override void UpdateCurrencyBoxes(List<string> currencies, string selected)
        {
            return;
        }
    }
}
