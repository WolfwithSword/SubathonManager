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
using SubathonManager.Core.Security;
using SubathonManager.Core.Security.Interfaces;
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
    // keyed by store InternalName
    private readonly Dictionary<string, GoAffProSourceControl> _sourceControls = new();
    private readonly Dictionary<string, bool> _connectedStatus = new();

    private static IEnumerable<GoAffProStore> _stores => GoAffProStoreRegistry.All()
        .Where(s => s.Enabled);
    private string _activeSource = string.Empty;


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
            foreach (var store in _stores)
                AddStoreTab(store);
        });

        GoAffProStoreRegistry.StoreDiscovered -= OnStoreDiscovered;
        GoAffProStoreRegistry.StoreDiscovered += OnStoreDiscovered;

        Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(5000);
            foreach (var store in _stores)
            {
                SetNavButtonStatus(store.InternalName,
                    Utils.GetConnection(SubathonEventSource.GoAffPro, store.InternalName).Status);
            }
        });
    }

    private void OnStoreDiscovered(GoAffProStore store)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (_sourceControls.ContainsKey(store.InternalName) || !store.Enabled) return;
            AddStoreTab(store);
            var config = AppServices.Provider.GetRequiredService<IConfig>();
            SuppressUnsavedChanges(() =>
            {
                var factory = AppServices.Provider
                    .GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<AppDbContext>>();
                using var db = factory.CreateDbContext();
                _sourceControls[store.InternalName].LoadValues(db, config, _configSection);
            });
        });
    }

    private void AddStoreTab(GoAffProStore store)
    {
        if (_sourceControls.ContainsKey(store.InternalName)) return;
        var control = new GoAffProSourceControl(Host, store);
        _sourceControls[store.InternalName] = control;
        UpdateStatus(Utils.GetConnection(SubathonEventSource.GoAffPro, store.InternalName));

        var navBtn = new Wpf.Ui.Controls.Button
        {
            Content = store.StoreName,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(2, 1, 2, 1),
            Padding = new Thickness(6, 4, 6, 4),
            Appearance = ControlAppearance.Transparent,
            Height = 34,
            Tag = store.InternalName,
            BorderThickness = new Thickness(2, 1, 1, 1),
        };
        navBtn.Click += GroupNav_Click;
        SourceList?.Children.Add(navBtn);

        if (string.IsNullOrEmpty(_activeSource))
        {
            SelectGroup(store.InternalName);
        }
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

        if (label == _activeSource) return;

        _sourceControls.TryGetValue(label, out var control);

        SourcesPanel?.Children.Clear();
        if(control != null)
            SourcesPanel?.Children.Add(control);
        _activeSource = label;
    }


    internal override void UpdateStatus(IntegrationConnection? connection)
    {
        if (connection is not { Source: SubathonEventSource.GoAffPro }) return;
        if (connection.Service == nameof(SubathonEventSource.GoAffPro))
        {
            Host.UpdateConnectionStatus(connection.Status, StatusText, ConnectBtn);
            return;
        }

        _sourceControls.TryGetValue(connection.Service, out var control);
        control?.UpdateStatus(connection.Status, connection.Name);
        SetNavButtonStatus(connection.Service, connection.Status);
    }

    private void SetNavButtonStatus(string internalName, bool status)
    {
        if (!_sourceControls.ContainsKey(internalName)) return;
        _connectedStatus[internalName] = status;

        var btn = SourceList?.Children
            .OfType<Wpf.Ui.Controls.Button>()
            .FirstOrDefault(b => Equals(b.Tag, internalName));
        if (btn == null) return;
        btn.Opacity = status ? 1.0 : 0.6;

        Dispatcher.Invoke(SortSourceList);
    }

    private void SortSourceList()
    {
        if (SourceList == null) return;

        var originalOrder = _stores
            .Select((s, i) => (Key: s.InternalName, Index: i))
            .ToDictionary(x => x.Key, x => x.Index);

        var buttons = SourceList.Children.OfType<Wpf.Ui.Controls.Button>().ToList();
        var sorted = buttons
            .OrderByDescending(b => _connectedStatus.GetValueOrDefault(b.Tag as string ?? ""))
            .ThenBy(b => originalOrder.GetValueOrDefault(b.Tag as string ?? "", int.MaxValue))
            .ToList();

        SourceList.Children.Clear();
        foreach (var b in sorted)
            SourceList.Children.Add(b);
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
            var secureStorage = AppServices.Provider.GetRequiredService<ISecureStorage>();

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
                Text = secureStorage.GetOrDefault(StorageKeys.GoAffProEmail, string.Empty) ?? string.Empty,
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
                Password = secureStorage.GetOrDefault(StorageKeys.GoAffProPassword, string.Empty) ?? string.Empty,
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
            setData |= secureStorage.Set(StorageKeys.GoAffProEmail, userBox.Text);
            setData |= secureStorage.Set(StorageKeys.GoAffProPassword, pwBox.Password);
            if (setData) config.Save();


            if (string.IsNullOrWhiteSpace(secureStorage.GetOrDefault(StorageKeys.GoAffProPassword, string.Empty))
                || string.IsNullOrWhiteSpace(secureStorage.GetOrDefault(StorageKeys.GoAffProEmail, string.Empty)))
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
