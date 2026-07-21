using System.Diagnostics;
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
using SubathonManager.Integration;
using SubathonManager.UI.Services;
using SubathonManager.Data;

namespace SubathonManager.UI.Views.SettingsViews.External;

public partial class PallySettings : SettingsControl
{
    private const string ConfigSection = "PallyGG";
    private readonly ILogger? _logger = AppServices.Provider.GetRequiredService<ILogger<PallySettings>>();

    public PallySettings()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            IntegrationEvents.ConnectionUpdated += UpdateStatus;
            RegisterUnsavedChangeHandlers();
            UpdateStatus(Utils.GetConnection(SubathonEventSource.PallyGG, "Socket"));
        };
        Unloaded += (_, _) =>
        {
            IntegrationEvents.ConnectionUpdated -= UpdateStatus;
        };
    }

    public override void Init(SettingsView host)
    {
        Host = host;
        var secureStorage = AppServices.Provider.GetRequiredService<ISecureStorage>();
        var config = AppServices.Provider.GetRequiredService<IConfig>();
        PallyApiKeyBox.Password = secureStorage.GetOrDefault(StorageKeys.PallyApiKey, string.Empty)!;
        PallyRoomBox.Text = config.Get(ConfigSection, "Room", string.Empty) ?? string.Empty;
        UpdateStatus(Utils.GetConnection(SubathonEventSource.PallyGG, "Socket"));
    }

    internal override void UpdateStatus(IntegrationConnection? connection)
    {
        if (connection is not { Source: SubathonEventSource.PallyGG, Service: "Socket" }) return;
        Host.UpdateConnectionStatus(connection.Status, PallyStatusText, null);
        Dispatcher.Invoke(() =>
        {
            DisconnBtn.Visibility = connection.Status ? Visibility.Visible : Visibility.Collapsed;
            ConnectBtn.Visibility = connection.Status ? Visibility.Collapsed : Visibility.Visible;
        });
    }

    private async void ConnectPally_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var config = AppServices.Provider.GetRequiredService<IConfig>();
            ServiceManager.Pally.SaveConfig(PallyApiKeyBox.Password.Trim(), PallyRoomBox.Text.Trim());
            config.SetBool(ConfigSection, "Enabled", true);
            config.Save();

            if (ServiceManager.Pally.IsKeyEmpty())
            {
                OpenDashboard();
                return;
            }
            await ServiceManager.Pally.RestartAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to connect PallyGG Service");
        }
    }

    private async void DisconnectPally_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var config = AppServices.Provider.GetRequiredService<IConfig>();
            if (config.SetBool(ConfigSection, "Enabled", false)) config.Save();
            await ServiceManager.Pally.StopAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to disconnect PallyGG Service");
        }
    }

    private void GetApiKey_Click(object sender, RoutedEventArgs e) => OpenDashboard();

    private static void OpenDashboard()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://pally.gg/dashboard/settings/api-keys",
                UseShellExecute = true
            });
        }
        catch { /**/ }
    }

    private void TestPallyGGDonation_Click(object sender, RoutedEventArgs e)
    {
        PallyService.SimulateTip(string.IsNullOrWhiteSpace(SimulatePallyGGDonationAmountBox.Text)
            ? "10.00" : SimulatePallyGGDonationAmountBox.Text);
    }

    public override bool UpdateValueSettings(AppDbContext db)
    {
        bool hasUpdated = false;
        var tipValue = db.SubathonValues.FirstOrDefault(sv =>
            sv.EventType == SubathonEventType.PallyGGDonation && sv.Meta == "");
        if (tipValue != null && double.TryParse(TipBox.Text, out var seconds)
                             && !seconds.Equals(tipValue.Seconds))
        {
            tipValue.Seconds = seconds;
            hasUpdated = true;
        }
        if (tipValue != null && double.TryParse(TipBox2.Text, out var points)
                             && !points.Equals(tipValue.Points))
        {
            tipValue.Points = points;
            hasUpdated = true;
        }
        return hasUpdated;
    }

    public override void UpdateCurrencyBoxes(List<string> currencies, string selected) { }

    public override (string, string, TextBox?, TextBox?) GetValueBoxes(SubathonValue val)
    {
        string v = $"{val.Seconds}";
        string p = $"{val.Points}";
        return val.EventType switch
        {
            SubathonEventType.PallyGGDonation => (v, p, TipBox, TipBox2),
            _ => (v, p, null, null)
        };
    }
}
