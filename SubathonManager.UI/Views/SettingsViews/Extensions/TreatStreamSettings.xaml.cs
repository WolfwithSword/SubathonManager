using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;
using SubathonManager.Data;
using SubathonManager.Integration;
using SubathonManager.UI.Services;

namespace SubathonManager.UI.Views.SettingsViews.Extensions;

public partial class TreatStreamSettings : SettingsControl
{
    private readonly ILogger? _logger = AppServices.Provider.GetRequiredService<ILogger<TreatStreamSettings>>();

    public TreatStreamSettings()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            IntegrationEvents.ConnectionUpdated += UpdateStatus;
            RegisterUnsavedChangeHandlers();
            UpdateStatus(Utils.GetConnection(SubathonEventSource.TreatStream, "Socket"));
        };
        Unloaded += (_, _) =>
        {
            IntegrationEvents.ConnectionUpdated -= UpdateStatus;
        };
    }

    public override void Init(SettingsView host)
    {
        Host = host;
        UpdateStatus(Utils.GetConnection(SubathonEventSource.TreatStream, "Socket"));
    }

    internal override void UpdateStatus(IntegrationConnection? connection)
    {
        if (connection is not { Source: SubathonEventSource.TreatStream, Service: "Socket" }) return;
        Host.UpdateConnectionStatus(connection.Status, TreatStatusText, null);
        Dispatcher.Invoke(() =>
        {
            DisconnectTreatBtn.Visibility = connection.Status ? Visibility.Visible : Visibility.Collapsed;
            ConnectTreatBtn.Content = connection.Status ? "Reconnect" : "Connect";
        });
    }

    private async void ConnectTreatStream_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ServiceManager.TreatStream.ConnectAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to connect TreatStream Service");
        }
    }

    private async void DisconnectTreatStream_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ServiceManager.TreatStream.StopAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to disconnect TreatStream Service");
        }
    }

    private void TestTreat_Click(object sender, RoutedEventArgs e)
    {
        TreatStreamService.SimulateTreat(SimulateTreatTitleBox.Text);
    }

    public override bool UpdateValueSettings(AppDbContext db)
    {
        bool hasUpdated = false;
        var treatValue = db.SubathonValues.FirstOrDefault(sv =>
            sv.EventType == SubathonEventType.TreatStreamOrder && sv.Meta == "");
        if (treatValue != null && double.TryParse(TreatBox.Text, out var seconds)
                               && !seconds.Equals(treatValue.Seconds))
        {
            treatValue.Seconds = seconds;
            hasUpdated = true;
        }
        if (treatValue != null && double.TryParse(TreatBox2.Text, out var points)
                               && !points.Equals(treatValue.Points))
        {
            treatValue.Points = points;
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
            SubathonEventType.TreatStreamOrder => (v, p, TreatBox, TreatBox2),
            _ => (v, p, null, null)
        };
    }
}
