using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using Microsoft.EntityFrameworkCore;
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
using SubathonManager.UI.UiUtils;
using SubathonManager.UI.Views.SettingsViews.External.GoAffPro;

namespace SubathonManager.UI.Views.SettingsViews.External;

public partial class GoAffProSettings : SettingsControl {
    private readonly string _configSection = "GoAffPro";
    private readonly Dictionary<string, bool> _connectedStatus = new();

    private readonly ILogger? _logger = AppServices.Provider.GetRequiredService<ILogger<GoAffProSettings>>();

    // keyed by store InternalName
    private readonly Dictionary<string, GoAffProSourceControl> _sourceControls = new();
    private string _activeSource = string.Empty;

    public GoAffProSettings() {
        InitializeComponent();

        Loaded += (_, _) => {
            IntegrationEvents.ConnectionUpdated += UpdateStatus;
            RegisterUnsavedChangeHandlers();
            UpdateStatus(Utils.GetConnection(SubathonEventSource.GoAffPro, nameof(SubathonEventSource.GoAffPro)));
        };
        Unloaded += (_, _) => { IntegrationEvents.ConnectionUpdated -= UpdateStatus; };
    }

    // ReSharper disable once InconsistentNaming
    private static IEnumerable<GoAffProStore> _stores => GoAffProStoreRegistry.All().Where(s => s.Enabled);

    public override void Init(SettingsView host) {
        Host = host;
        Dispatcher.UIThread.Invoke(() => {
            UpdateStatus(Utils.GetConnection(SubathonEventSource.GoAffPro, nameof(SubathonEventSource.GoAffPro)));
            foreach (GoAffProStore store in _stores)
                AddStoreTab(store);
        });

        GoAffProStoreRegistry.StoreDiscovered -= OnStoreDiscovered;
        GoAffProStoreRegistry.StoreDiscovered += OnStoreDiscovered;

        Dispatcher.UIThread.InvokeAsync(async () => {
            await Task.Delay(5000);
            foreach (GoAffProStore store in _stores)
                SetNavButtonStatus(store.InternalName,
                    Utils.GetConnection(SubathonEventSource.GoAffPro, store.InternalName).Status);
        });
    }

    private void OnStoreDiscovered(GoAffProStore store) {
        Dispatcher.UIThread.Post(() => {
            if (_sourceControls.ContainsKey(store.InternalName) || !store.Enabled) return;
            AddStoreTab(store);
            var config = AppServices.Provider.GetRequiredService<IConfig>();
            SuppressUnsavedChanges(() => {
                var factory = AppServices.Provider
                    .GetRequiredService<IDbContextFactory<AppDbContext>>();
                using AppDbContext db = factory.CreateDbContext();
                _sourceControls[store.InternalName].LoadValues(db, config, _configSection);
            });
        });
    }

    private void AddStoreTab(GoAffProStore store) {
        if (_sourceControls.ContainsKey(store.InternalName)) return;
        var control = new GoAffProSourceControl(Host, store);
        _sourceControls[store.InternalName] = control;
        UpdateStatus(Utils.GetConnection(SubathonEventSource.GoAffPro, store.InternalName));

        var navBtn = new Button {
            Content = store.StoreName,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(2, 1, 2, 1),
            Height = 34,
            Tag = store.InternalName
        };
        navBtn.StyleAsTab(false);
        navBtn.SetTabActive(false);
        navBtn.Click += GroupNav_Click;
        SourceList?.Children.Add(navBtn);

        if (string.IsNullOrEmpty(_activeSource))
            SelectGroup(store.InternalName);
    }

    private void GroupNav_Click(object? sender, RoutedEventArgs e) {
        if (sender is Button { Tag: string label })
            SelectGroup(label);
    }

    public void TrySelectStore(string internalName) {
        Dispatcher.UIThread.Post(() => SelectGroup(internalName));
    }

    private void SelectGroup(string label) {
        if (SourceList == null) return;
        foreach (Control? child in SourceList.Children) {
            if (child is not Button btn) continue;
            btn.SetTabActive(btn.Tag as string == label);
        }

        if (label == _activeSource) return;

        _sourceControls.TryGetValue(label, out GoAffProSourceControl? control);

        SourcesPanel?.Children.Clear();
        if (control != null)
            SourcesPanel?.Children.Add(control);
        _activeSource = label;
    }

    internal override void UpdateStatus(IntegrationConnection? connection) {
        if (connection is not { Source: SubathonEventSource.GoAffPro }) return;
        if (connection.Service == nameof(SubathonEventSource.GoAffPro)) {
            Host.UpdateConnectionStatus(connection.Status, StatusText, ConnectBtn);
            return;
        }

        _sourceControls.TryGetValue(connection.Service, out GoAffProSourceControl? control);
        control?.UpdateStatus(connection.Status, connection.Name);
        SetNavButtonStatus(connection.Service, connection.Status);
    }

    private void SetNavButtonStatus(string internalName, bool status) {
        if (!_sourceControls.ContainsKey(internalName)) return;
        _connectedStatus[internalName] = status;

        Button? btn = SourceList?.Children
            .OfType<Button>()
            .FirstOrDefault(b => Equals(b.Tag, internalName));
        if (btn == null) return;
        btn.Opacity = status ? 1.0 : 0.6;

        Dispatcher.UIThread.Post(SortSourceList);
    }

    private void SortSourceList() {
        if (SourceList == null) return;

        Dictionary<string, int> originalOrder = _stores
            .Select((s, i) => (Key: s.InternalName, Index: i))
            .ToDictionary(x => x.Key, x => x.Index);

        List<Button> buttons = SourceList.Children.OfType<Button>().ToList();
        List<Button> sorted = buttons
            .OrderByDescending(b => _connectedStatus.GetValueOrDefault(b.Tag as string ?? ""))
            .ThenBy(b => originalOrder.GetValueOrDefault(b.Tag as string ?? "", int.MaxValue))
            .ToList();

        SourceList.Children.Clear();
        foreach (Button b in sorted)
            SourceList.Children.Add(b);
    }

    protected internal override void LoadValues(AppDbContext db) {
        var config = AppServices.Provider.GetRequiredService<IConfig>();
        foreach (GoAffProSourceControl control in _sourceControls.Values)
            SuppressUnsavedChanges(() => control.LoadValues(db, config, _configSection));

        if (!int.TryParse(config.Get(_configSection, "DaysOffset", "0"), out int offsetDays)) offsetDays = 0;
        LookbackDaysBox.Text = offsetDays.ToString();
    }

    public override bool UpdateValueSettings(AppDbContext db) {
        return _sourceControls.Values.Aggregate(false, (acc, c) => acc | c.UpdateValueSettings(db));
    }

    protected internal override bool UpdateConfigValueSettings() {
        var config = AppServices.Provider.GetRequiredService<IConfig>();
        var offsetDays = 0;
        if (string.IsNullOrWhiteSpace(LookbackDaysBox.Text) ||
            !int.TryParse(LookbackDaysBox.Text, out offsetDays)) offsetDays = 0;
        bool hasUpdated = config.Set(_configSection, "DaysOffset", offsetDays.ToString());
        hasUpdated |=
            _sourceControls.Values.Aggregate(false, (acc, c) => acc | c.UpdateConfigSettings(config, _configSection));
        return hasUpdated;
    }

    public override void UpdateCurrencyBoxes(List<string> currencies, string selected) {
    }

    public override (string, string, TextBox?, TextBox?) GetValueBoxes(SubathonValue val) {
        return ("", "", null, null);
    }

    private async void OpenLogin_Click(object? sender, RoutedEventArgs routedEventArgs) {
        try {
            var config = AppServices.Provider.GetRequiredService<IConfig>();
            var secureStorage = AppServices.Provider.GetRequiredService<ISecureStorage>();

            var userBox = new TextBox {
                Text = secureStorage.GetOrDefault(StorageKeys.GoAffProEmail, string.Empty) ?? string.Empty,
                Width = 240,
                Margin = new Thickness(2, 4, 0, 0)
            };
            var pwBox = new TextBox {
                Text = secureStorage.GetOrDefault(StorageKeys.GoAffProPassword, string.Empty) ?? string.Empty,
                PasswordChar = '●',
                Width = 240,
                Margin = new Thickness(2, 4, 0, 0)
            };

            var row1 = new StackPanel { Orientation = Orientation.Horizontal };
            row1.Children.Add(new TextBlock {
                Text = "Email: ", Width = 76, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(16, 4, 8, 0)
            });
            row1.Children.Add(userBox);

            var row2 = new StackPanel { Orientation = Orientation.Horizontal };
            row2.Children.Add(new TextBlock {
                Text = "Password: ", Width = 76, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(16, 4, 8, 0)
            });
            row2.Children.Add(pwBox);

            var panel = new StackPanel
                { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Center };
            panel.Children.Add(row1);
            panel.Children.Add(row2);

            var dialog = new FAContentDialog {
                Title = "Login to GoAffPro",
                PrimaryButtonText = "Confirm",
                CloseButtonText = "Cancel",
                Content = panel
            };

            FAContentDialogResult result = await dialog.ShowAsync();
            if (result != FAContentDialogResult.Primary) return;

            await ServiceManager.GoAffPro.StopAsync();

            var setData = false;
            setData |= secureStorage.Set(StorageKeys.GoAffProEmail, userBox.Text ?? string.Empty);
            setData |= secureStorage.Set(StorageKeys.GoAffProPassword, pwBox.Text ?? string.Empty);
            if (setData) config.Save();

            if (string.IsNullOrWhiteSpace(secureStorage.GetOrDefault(StorageKeys.GoAffProPassword, string.Empty))
                || string.IsNullOrWhiteSpace(secureStorage.GetOrDefault(StorageKeys.GoAffProEmail, string.Empty)))
                return;
            await ServiceManager.GoAffPro.StartAsync();
        }
        catch (Exception ex) {
            _logger?.LogError(ex, "Error logging into GoAffPro");
        }
    }
}