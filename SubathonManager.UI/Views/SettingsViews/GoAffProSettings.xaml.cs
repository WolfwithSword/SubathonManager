using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Data;
using SubathonManager.UI.Services;
using SubathonManager.UI.Views.SettingsViews.GoAffPro;

// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.UI.Views.SettingsViews;

public partial class GoAffProSettings : SettingsControl
{
    private readonly ILogger? _logger = AppServices.Provider.GetRequiredService<ILogger<GoAffProSettings>>();
    private readonly string _configSection = "GoAffPro";
    private readonly List<GoAffProSourceControl> _sourceControls = [];

    public GoAffProSettings()
    {
        InitializeComponent();
        
        Loaded += (_, _) =>
        {
            IntegrationEvents.ConnectionUpdated += UpdateStatus;
            RegisterUnsavedChangeHandlers();
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
            foreach (var source in Enum.GetValues<GoAffProSource>()
                         .Where(s => !s.IsDisabled()))
            {
                var control = new GoAffProSourceControl(host, source);
                _sourceControls.Add(control);
                SourcesPanel.Children.Add(control);
            }
        });
    }

    internal override void UpdateStatus(bool status, SubathonEventSource source, string name, string service)
    {
        if (source != SubathonEventSource.GoAffPro) return;
        Host.UpdateConnectionStatus(status, StatusText, ConnectBtn);

        if (!Enum.TryParse(service, out GoAffProSource goAffProSource)) return;

        _sourceControls
            .FirstOrDefault(c => c.Source == goAffProSource)
            ?.UpdateStatus(status, name);
    }

    public override void LoadValues(AppDbContext db)
    {
        var config = AppServices.Provider.GetRequiredService<IConfig>();
        foreach (var control in _sourceControls)
            control.LoadValues(db, config, _configSection);
    }

    public override bool UpdateValueSettings(AppDbContext db) =>
        _sourceControls.Aggregate(false, (acc, c) => acc | c.UpdateValueSettings(db));

    public override bool UpdateConfigValueSettings()
    {
        var config = AppServices.Provider.GetRequiredService<IConfig>();
        return _sourceControls.Aggregate(false, (acc, c) => acc | c.UpdateConfigSettings(config, _configSection));
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
            _sourceControls.ForEach(c => c.StrikeThrough());
            await ServiceManager.GoAffPro.StartAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error logging into GoAffPro");
        }
    }
}