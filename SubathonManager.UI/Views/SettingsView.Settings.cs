using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Data;
using SubathonManager.UI.UiUtils;
using SubathonManager.UI.Services;

namespace SubathonManager.UI.Views;

public partial class SettingsView
{
    private void InitCurrencySelects()
    {
        var currencies = ServiceManager.Events.ValidEventCurrencies().OrderBy(x => x).ToList();
        DefaultCurrencyBox.ItemsSource = currencies;

        var config = AppServices.Provider.GetRequiredService<IConfig>();
        DefaultCurrencyBox.SelectedItem = config.Get("Currency", "Primary", "USD")?.Trim().ToUpperInvariant() ?? "USD";

        ExtensionSettingsControl.UpdateCurrencyBoxes(currencies, (DefaultCurrencyBox.SelectedItem as string) ?? "USD");
        ExternalServiceSettingsControl.UpdateCurrencyBoxes(currencies, (DefaultCurrencyBox.SelectedItem as string) ?? "USD");
        StreamingSettingsControl.UpdateCurrencyBoxes(currencies, (DefaultCurrencyBox.SelectedItem as string) ?? "USD");
    }

    private void OpenDataFolder_Click(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        UiHelpers.OpenFolder(Config.DataFolder);
    }

    private void EventsSummary_Click(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        var config = AppServices.Provider.GetRequiredService<IConfig>();
        Process.Start(new ProcessStartInfo
        {
            FileName = $"http://localhost:{config.Get("Server", "Port", "14040")}/api/data/amounts",
            UseShellExecute = true
        });
    }

    private async void ExportEvents_Click(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await AppDbContext.ActiveEventsToCsv(db);
    }

    private void UpdateServerStatus(bool status)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (ServerStatusText == null) return;
            if (status && ServerStatusText.Text != "Running") ServerStatusText.Text = "Running";
            else if (!status && ServerStatusText.Text != "Not Running") ServerStatusText.Text = "Not Running";
        });
    }

    private bool SaveTopAppSettings()
    {
        bool hasUpdated = false;
        string selectedCurrency = (DefaultCurrencyBox.SelectedItem as string) ?? "";

        var config = AppServices.Provider.GetRequiredService<IConfig>();
        hasUpdated |= config.Set("Currency", "BitsLikeAsDonation", $"{BitsAsCurrencyBox.IsChecked}");
        hasUpdated |= config.Set("App", "OtherValuesWhenLocked", $"{AddOtherWhenLockedBox.IsChecked}");

        bool updatedLockVisibility = config.Set("App", "ShowLockedEvents", $"{ShowEventsWhenLockedBox.IsChecked}");
        hasUpdated |= updatedLockVisibility;
        if (updatedLockVisibility)
            SettingsEvents.RaiseEventVisibilityChanged();

        if (selectedCurrency.Length >= 3)
        {
            if (config.Get("Currency", "Primary", string.Empty) != selectedCurrency)
            {
                hasUpdated |= config.Set("Currency", "Primary", selectedCurrency);
                ServiceManager.Events.ReInitCurrencyService();
            }
        }
        if (int.TryParse(ServerPortTextBox.Text, out var port))
            hasUpdated |= config.Set("Server", "Port", port.ToString());

        string selectedTheme = (ThemeBox.SelectedItem is ComboBoxItem item) ? item.Content?.ToString() ?? "" : "";
        if (!string.IsNullOrEmpty(selectedTheme))
            hasUpdated |= config.Set("App", "Theme", selectedTheme);

        hasUpdated |= ExtensionSettingsControl.SaveConfigValues();
        return hasUpdated;
    }

    public void UpdateConnectionStatus(bool status, TextBlock? textBlock, Button? button)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (textBlock != null)
            {
                if (status && textBlock.Text != "Connected") textBlock.Text = "Connected";
                else if (!status && textBlock.Text != "Disconnected") textBlock.Text = "Disconnected";
            }

            if (button == null) return;
            if (status && button.Content?.ToString() != "Reconnect") button.Content = "Reconnect";
            else if (!status && button.Content?.ToString() != "Connect") button.Content = "Connect";
        });
    }

    public bool SaveSubTier(AppDbContext db, SubathonEventType type, string meta, TextBox tb, TextBox tb2)
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

    private void UpdateSaveButtonBorder(bool hasPendingChanges)
    {
        Dispatcher.UIThread.Post(() => UiHelpers.UpdateButtonPendingBorder(SaveButtonBorder, hasPendingChanges));
    }

    private void SaveAllSubathonValuesButton_Click(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
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
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                SaveAllSubathonValuesButton.Content = "Saved!";
                UpdateSaveButtonBorder(false);
            });
            await Task.Delay(1500);
            await Dispatcher.UIThread.InvokeAsync(() => SaveAllSubathonValuesButton.Content = "Save All");
        });
    }

    public void UpdateTimePointsBoxes(TextBox? boxTime, TextBox? boxPoints, string time, string points)
    {
        if (boxTime != null && boxTime.Text != time) boxTime.Text = time;
        if (boxPoints != null && boxPoints.Text != points) boxPoints.Text = points;
    }

    private void RefreshSubathonValues()
    {
        Dispatcher.UIThread.Post(() => LoadValues(false));
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
            var group = val.EventType.GetSource().GetGroup();
            if (group == SubathonSourceGroup.Stream)
                (v, p, box, box2) = StreamingSettingsControl.GetValueBoxes(val);
            else if (group == SubathonSourceGroup.StreamExtension)
                (v, p, box, box2) = ExtensionSettingsControl.GetValueBoxes(val);
            else if (group == SubathonSourceGroup.ExternalService)
                (v, p, box, box2) = ExternalServiceSettingsControl.GetValueBoxes(val);

            if (box != null && box2 != null)
                UpdateTimePointsBoxes(box, box2, v, p);
        }

        if (doConfigLoad)
        {
            var config = AppServices.Provider.GetRequiredService<IConfig>();
            BitsAsCurrencyBox.IsChecked = config.GetBool("Currency", "BitsLikeAsDonation", false);
            AddOtherWhenLockedBox.IsChecked = config.GetBool("App", "OtherValuesWhenLocked", true);
            ShowEventsWhenLockedBox.IsChecked = config.GetBool("App", "ShowLockedEvents", false);

            var theme = config.Get("App", "Theme", "System")!;
            foreach (var obj in ThemeBox.Items)
            {
                if (obj is ComboBoxItem item && theme.Equals(item.Content?.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    ThemeBox.SelectedItem = item;
                    break;
                }
            }
            ExtensionSettingsControl.LoadConfigValues();
        }

        StreamingSettingsControl.LoadValues(db);
        ExtensionSettingsControl.LoadValues(db);
        ExternalServiceSettingsControl.LoadValues(db);
    }

    public override bool UpdateValueSettings(AppDbContext db) => false;
    public override void UpdateCurrencyBoxes(List<string> currencies, string selected) { }
    public override (string, string, TextBox?, TextBox?) GetValueBoxes(SubathonValue val) => ("", "", null, null);
}
