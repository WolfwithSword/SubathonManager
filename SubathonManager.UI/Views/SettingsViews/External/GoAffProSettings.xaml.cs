using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
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
using SubathonManager.UI.Views.SettingsViews.External.GoAffPro;
using Wpf.Ui.Controls;
using TextBox = System.Windows.Controls.TextBox;

// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.UI.Views.SettingsViews.External;

public partial class GoAffProSettings : SettingsControl
{
    private readonly ILogger? _logger = AppServices.Provider.GetRequiredService<ILogger<GoAffProSettings>>();
    private readonly string _configSection = "GoAffPro";
    private readonly Dictionary<GoAffProSource, GoAffProSourceControl> _sourceControls = new();

    private IEnumerable<GoAffProSource> _sources => Enum.GetValues<GoAffProSource>()
        .Where(s => !s.IsDisabled());
    private GoAffProSource _activeSource = GoAffProSource.Unknown;
    

    public GoAffProSettings()
    {
        InitializeComponent();
        
        Loaded += (_, _) =>
        {
            IntegrationEvents.ConnectionUpdated += UpdateStatus;
            RegisterUnsavedChangeHandlers();
            UpdateStatus(Utils.GetConnection(SubathonEventSource.GoAffPro, nameof(SubathonEventSource.GoAffPro)));
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
            UpdateStatus(Utils.GetConnection(SubathonEventSource.GoAffPro, nameof(SubathonEventSource.GoAffPro)));
            foreach (var source in _sources)
            {
                var control = new GoAffProSourceControl(host, source);
                _sourceControls[source] = control;
                UpdateStatus(Utils.GetConnection(SubathonEventSource.GoAffPro, source.ToString()));
                
                var navBtn = new Wpf.Ui.Controls.Button
                {
                    Content = source.GetDescription(),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 1, 0, -12),
                    Padding = new Thickness(10, 6, 10, 6),
                    Appearance = ControlAppearance.Transparent,
                    FontSize = 20,
                    MinWidth = 100,
                    Tag = source.ToString(),
                    BorderThickness = new Thickness(1, 1, 1, 2),
                    CornerRadius = new CornerRadius(4, 4, 0, 0)
                };
                navBtn.Click += GroupNav_Click;
                SourceList?.Children.Add(navBtn);

                _sourceControls.TryGetValue(source, out var cachedControl);
                if (cachedControl != null && _activeSource == GoAffProSource.Unknown)
                {
                    SelectGroup(source.ToString());
                }
            }
        });

        Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(5000);
            foreach (var source in _sources)
            {
                SetNavButtonStatus(source,
                    Utils.GetConnection(SubathonEventSource.GoAffPro, source.ToString()).Status);
            }
        });
    }

    
    private void GroupNav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Wpf.Ui.Controls.Button { Tag: string label })
            SelectGroup(label);
    }
    
    private void SelectGroup(string label)
    {
        if (SourceList == null) return;
        foreach (var child in SourceList.Children)
        {
            if (child is not Wpf.Ui.Controls.Button btn) continue;
            
            btn.Appearance = btn.Tag as string == label
                ? ControlAppearance.Secondary
                : ControlAppearance.Transparent;
        }

        if (!Enum.TryParse(label, out GoAffProSource source)) return;
        if (source == _activeSource) return;

        _sourceControls.TryGetValue(source, out var control);
            
        SourcesPanel?.Children.Clear();
        if(control != null)
            SourcesPanel?.Children.Add(control);
        _activeSource = source;
    }

    
    internal override void UpdateStatus(IntegrationConnection? connection)
    {
        if (connection is not { Source: SubathonEventSource.GoAffPro }) return;
        Host.UpdateConnectionStatus(connection.Status, StatusText, ConnectBtn);

        if (!Enum.TryParse(connection.Service, out GoAffProSource goAffProSource)) return;

        _sourceControls.TryGetValue(goAffProSource, out var control);
        control?.UpdateStatus(connection.Status, connection.Name);
        SetNavButtonStatus(goAffProSource, connection.Status);
    }

    private void SetNavButtonStatus(GoAffProSource source, bool status)
    {
        var btn = SourceList?.Children
            .OfType<Wpf.Ui.Controls.Button>()
            .FirstOrDefault(b => Equals(b.Tag, source.ToString()));
        if (btn == null) return;
        btn.Opacity = status ? 1.0 : 0.6;
    }
    
    protected internal override void LoadValues(AppDbContext db)
    {
        var config = AppServices.Provider.GetRequiredService<IConfig>();
        foreach (var control in _sourceControls.Values)
        {
            SuppressUnsavedChanges(() => control.LoadValues(db, config, _configSection));
        }

        if(!int.TryParse(config.Get(_configSection, "DaysOffset", "0"), out var offsetDays)) offsetDays = 0;
        LookbackDaysBox.Text = offsetDays.ToString();
    }

    public override bool UpdateValueSettings(AppDbContext db) =>
        _sourceControls.Values.Aggregate(false, (acc, c) => acc | c.UpdateValueSettings(db));

    protected internal override bool UpdateConfigValueSettings()
    {
        var config = AppServices.Provider.GetRequiredService<IConfig>();
        var offsetDays = 0;
        if (string.IsNullOrWhiteSpace(LookbackDaysBox.Text) || !int.TryParse(LookbackDaysBox.Text, out offsetDays)) offsetDays = 0;
        bool hasUpdated = config.Set(_configSection, "DaysOffset", offsetDays.ToString());
        hasUpdated |=
            _sourceControls.Values.Aggregate(false, (acc, c) => acc | c.UpdateConfigSettings(config, _configSection));
        return hasUpdated;
    }

    public override void UpdateCurrencyBoxes(List<string> currencies, string selected)
    {
        return;
    }

    public override (string, string, TextBox?, TextBox?) GetValueBoxes(SubathonValue val)
    {
        return ("", "", null, null);
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