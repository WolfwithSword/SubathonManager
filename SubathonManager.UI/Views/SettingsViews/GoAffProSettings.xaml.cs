using System.Windows;
using System.Windows.Controls;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Data;
using SubathonManager.UI.Services;

namespace SubathonManager.UI.Views.SettingsViews;

public partial class GoAffProSettings : SettingsControl
{
    private readonly ILogger? _logger = AppServices.Provider.GetRequiredService<ILogger<GoAffProSettings>>();
    private string _configSection = "GoAffPro";
    
    private readonly Dictionary<GoAffProSource, List<object>> _sourceUiElements = new();
    public GoAffProSettings()
    {
        InitializeComponent();
        
        _sourceUiElements[GoAffProSource.GamerSupps] = [GSStatusText, GSCurrency, GSSecondsBox, GSPointsBox,
            GSMode, GSCommission, GSTotalSim, GSCommSim, GSQuantitySim, GSEnabled];
        _sourceUiElements[GoAffProSource.UwUMarket] = [UMStatusText, UMCurrency, UMSecondsBox, UMPointsBox,
            UMMode, UMCommission, UMTotalSim, UMCommSim, UMQuantitySim, UMEnabled];
        
        Dispatcher.Invoke(() =>
        {
            foreach (var elementSet in _sourceUiElements.Values)
            {
                if (elementSet[6] is TextBox box) box.ToolTip = "Order Total $";
                if (elementSet[7] is TextBox box2) box2.ToolTip = "Commission Total $";
                if (elementSet[8] is TextBox box3) box3.ToolTip = "Items Ordered";
            }
        });
        
        Loaded += (_, _) =>
        {
            IntegrationEvents.ConnectionUpdated += UpdateStatus;
        };

        Unloaded += (_, _) =>
        {
            IntegrationEvents.ConnectionUpdated -= UpdateStatus;
        };
    }

    internal override void UpdateStatus(bool status, SubathonEventSource source, string name, string service)
    {
        if (source != SubathonEventSource.GoAffPro) return;
        
        Host.UpdateConnectionStatus(status, StatusText, ConnectBtn);
        if (!Enum.TryParse(service, out GoAffProSource sourceEnum)) return;
        if (sourceEnum == GoAffProSource.Unknown) return;
        
        TextBlock? statusLabel = null;
        Dispatcher.Invoke(() =>
        {
            statusLabel = _sourceUiElements[sourceEnum][0] as TextBlock;
            if (_sourceUiElements[sourceEnum][1] is TextBlock block)
            {
                block.Text = string.IsNullOrWhiteSpace(name) ? string.Empty : $"[{name}]";
            }
        });
        
        if (statusLabel != null)
            Host.UpdateConnectionStatus(status, statusLabel, null);
    }

    public override void LoadValues(AppDbContext db)
    {
        var config = AppServices.Provider.GetRequiredService<IConfig>();
        foreach (var goAffProSource in _sourceUiElements.Keys)
        {
            var eventType = goAffProSource.GetOrderEvent();
            if (eventType == SubathonEventType.Unknown) continue;
            
            TextBox secondsBox = (_sourceUiElements[goAffProSource][2] as TextBox)!;
            TextBox pointsBox = (_sourceUiElements[goAffProSource][3] as TextBox)!;
            
            var value = db.SubathonValues.AsNoTracking().FirstOrDefault(v => v.EventType == eventType);
            if (value == null) continue;
            
            string v = $"{value.Seconds}"; 
            string p = $"{value.Points}";
            if (!string.IsNullOrWhiteSpace(v) && !string.IsNullOrWhiteSpace(p))
            {
                Host.UpdateTimePointsBoxes(secondsBox, pointsBox, v, p);
            }

            ComboBox modeBox = (_sourceUiElements[goAffProSource][4] as ComboBox)!;

            
            modeBox.ItemsSource = Enum.GetNames<GoAffProModes>().ToList();
            modeBox.SelectedItem = config.Get(_configSection, $"{goAffProSource}.Mode", "Dollar")?.Trim() ?? "Dollar";

            CheckBox asComm = (_sourceUiElements[goAffProSource][5] as CheckBox)!;
            asComm.IsChecked = config.GetBool(_configSection, $"{goAffProSource}.CommissionAsDonation", false);

            CheckBox enabledBox = (_sourceUiElements[goAffProSource][9] as CheckBox)!;
            enabledBox.IsChecked = config.GetBool(_configSection, $"{goAffProSource}.Enabled", true);
        }
    }

    public override bool UpdateValueSettings(AppDbContext db)
    {
        bool hasUpdated = false;
        
        foreach (var goAffProSource in _sourceUiElements.Keys)
        {
            var eventType = goAffProSource.GetOrderEvent();
            if (eventType == SubathonEventType.Unknown) continue;
            
            TextBox secondsBox = (_sourceUiElements[goAffProSource][2] as TextBox)!;
            TextBox pointsBox = (_sourceUiElements[goAffProSource][3] as TextBox)!;
            
            var value = db.SubathonValues.FirstOrDefault(x => x.EventType == eventType && x.Meta == "");
            if (value != null && double.TryParse(secondsBox.Text, out var seconds) && !value.Seconds.Equals(seconds))
            {
                value.Seconds = seconds;
                hasUpdated = true;
            }
            if (value != null && double.TryParse(pointsBox.Text, out var points) && !value.Points.Equals(points))
            {
                value.Points = points;
                hasUpdated = true;
            }
        }
        return hasUpdated;
    }

    public override bool UpdateConfigValueSettings()
    {
        var config = AppServices.Provider.GetRequiredService<IConfig>();
        bool hasUpdated = false;
        foreach (var source in _sourceUiElements.Keys)
        {
            string selectedModeTxt = $"{((_sourceUiElements[source][4] as ComboBox)?.SelectedItem)}";
            hasUpdated |= config.Set(_configSection, $"{source}.Mode", selectedModeTxt);
            bool asDono = (_sourceUiElements[source][5] as CheckBox)?.IsChecked ?? false; 
            hasUpdated |= config.SetBool(_configSection, $"{source}.CommissionAsDonation", asDono);
            bool enabled = (_sourceUiElements[source][9] as CheckBox)?.IsChecked ?? true; 
            hasUpdated |= config.SetBool(_configSection, $"{source}.Enabled", enabled);
        }
        return hasUpdated;
    }

    private void TestOrder_Click(object sender, RoutedEventArgs routedEventArgs)
    {
        GoAffProSource source = GoAffProSource.Unknown;
        if (sender is not Button) return;
        if (Equals(sender, GSOrderTest)) source = GoAffProSource.GamerSupps;
        else if (Equals(sender, UMOrderTest)) source = GoAffProSource.UwUMarket;
        else return;
        
        var elements = _sourceUiElements[source];
        //6 total 7 comm 8 quant 
        decimal total = Decimal.TryParse((elements[6] as TextBox)?.Text, out decimal result) ? result : 0;
        decimal commTotal = Decimal.TryParse((elements[7] as TextBox)?.Text, out decimal result2) ? result2 : 0;
        int itemCount = Int32.TryParse((elements[8] as TextBox)?.Text, out int result3) ? result3 : 0;
        string currency = (elements[1] as TextBlock)?.Text.Replace("[", "").Replace("]", "") ?? "USD";
        
        ServiceManager.GoAffPro.SimulateOrder(total, itemCount, commTotal, source, currency);
    }

    private async void OpenLogin_Click(object sender, RoutedEventArgs routedEventArgs)
    {
        try
        {
            var config = AppServices.Provider.GetRequiredService<IConfig>();
            
            var msgBox = new Wpf.Ui.Controls.MessageBox
            {
                Title = "Login to GoAffPro",
                CloseButtonText = "Cancel",
                Owner = Application.Current.Windows
                    .OfType<Window>()
                    .FirstOrDefault(w => w.IsActive),
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                PrimaryButtonText = "Confirm"
            };

            var userLabel = new Wpf.Ui.Controls.TextBlock
            {
                Text = "Email: ",
                Width = 76,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(16, 4, 8, 0)
            };
            var userBox = new Wpf.Ui.Controls.TextBox
            {
                Text = config.GetFromEncoded(_configSection, "Email", string.Empty) ?? string.Empty,
                Width = 240,
                Margin = new Thickness(2, 4, 0, 0)
            };
            
            var pwLabel = new Wpf.Ui.Controls.TextBlock
            {
                Text = "Password: ",
                Width = 76,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(16, 4, 8, 0)
            };
            var pwBox = new Wpf.Ui.Controls.PasswordBox
            {
                Password = config.GetFromEncoded(_configSection, "Password", string.Empty) ?? string.Empty,
                Width = 240,
                Margin = new Thickness(2, 4, 0, 0)
            };

            var panel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            var panel2 = new StackPanel
            {
                Orientation = Orientation.Horizontal
            };
            var panel3 = new StackPanel
            {
                Orientation = Orientation.Horizontal
            };
            
            panel2.Children.Add(userLabel);
            panel2.Children.Add(userBox);
            panel3.Children.Add(pwLabel);
            panel3.Children.Add(pwBox);
            panel.Children.Add(panel2);
            panel.Children.Add(panel3);
            msgBox.Content = panel;
            
            var result = await msgBox.ShowDialogAsync();
            bool confirm = result == Wpf.Ui.Controls.MessageBoxResult.Primary;
            if (!confirm) return;
            
            await ServiceManager.GoAffPro.StopAsync();

            bool setData = false;
            setData |= config.SetEncoded(_configSection, "Email", userBox.Text);
            setData |= config.SetEncoded(_configSection, "Password", pwBox.Password);
            if (setData) config.Save();
            
            
            if (string.IsNullOrWhiteSpace(config.GetFromEncoded(_configSection, "Email", string.Empty))
                || string.IsNullOrWhiteSpace(config.GetFromEncoded(_configSection, "Password", string.Empty)))
            {
                return;
            }
            await ServiceManager.GoAffPro.StartAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error logging into GoAffPro");
        }
    }

}