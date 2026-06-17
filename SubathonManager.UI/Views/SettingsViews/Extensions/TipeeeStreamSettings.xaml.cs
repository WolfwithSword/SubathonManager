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

// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.UI.Views.SettingsViews.Extensions;

public partial class TipeeeStreamSettings : SettingsControl
{
    private readonly ILogger? _logger = AppServices.Provider.GetRequiredService<ILogger<TipeeeStreamSettings>>();

    public TipeeeStreamSettings()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            IntegrationEvents.ConnectionUpdated += UpdateStatus;
            RegisterUnsavedChangeHandlers();
            UpdateStatus(Utils.GetConnection(SubathonEventSource.TipeeeStream, nameof(SubathonEventSource.TipeeeStream)));
        };
        Unloaded += (_, _) =>
        {
            IntegrationEvents.ConnectionUpdated -= UpdateStatus;
        };
    }

    public override void Init(SettingsView host)
    {
        Host = host;
        UpdateStatus(Utils.GetConnection(SubathonEventSource.TipeeeStream, nameof(SubathonEventSource.TipeeeStream)));
    }

    internal override void UpdateStatus(IntegrationConnection? connection)
    {
        if (connection is not { Source: SubathonEventSource.TipeeeStream }) return;
        Dispatcher.Invoke(() =>
        {
            string username = (connection.Status && !string.IsNullOrEmpty(connection.Name))
                ? connection.Name
                : "Disconnected";
            if (TipeeeStatusText.Text != username) TipeeeStatusText.Text = username;

            string connectLabel = connection.Status ? "Reconnect" : "Connect";
            if (ConnectBtn.Content.ToString() != connectLabel) ConnectBtn.Content = connectLabel;

            DisconnBtn.Visibility = connection.Status ? Visibility.Visible : Visibility.Collapsed;
        });
    }

    protected internal override void LoadValues(AppDbContext db) { }

    public override bool UpdateValueSettings(AppDbContext db)
    {
        bool hasUpdated = false;

        var tipValue = db.SubathonValues.FirstOrDefault(sv =>
            sv.EventType == SubathonEventType.TipeeeStreamDonation && sv.Meta == "");
        if (tipValue != null)
        {
            if (double.TryParse(DonoBox.Text, out var s) && !s.Equals(tipValue.Seconds)) { tipValue.Seconds = s; hasUpdated = true; }
            if (double.TryParse(DonoBox2.Text, out var p) && !p.Equals(tipValue.Points)) { tipValue.Points = p; hasUpdated = true; }
        }

        return hasUpdated;
    }

    public override void UpdateCurrencyBoxes(List<string> currencies, string selected)
    {
        CurrencyBox.ItemsSource = currencies;
        CurrencyBox.SelectedItem = selected;
    }

    public override (string, string, TextBox?, TextBox?) GetValueBoxes(SubathonValue val)
    {
        string v = $"{val.Seconds}";
        string p = $"{val.Points}";
        return val.EventType switch
        {
            SubathonEventType.TipeeeStreamDonation => (v, p, DonoBox, DonoBox2),
            _ => (v, p, null, null)
        };
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ServiceManager.TipeeeStream.ConnectAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to connect TipeeeStream");
        }
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        await ServiceManager.TipeeeStream.StopAsync();
        ServiceManager.TipeeeStream.RevokeTokens();
    }

    private void TestDonation_Click(object sender, RoutedEventArgs e)
    {
        TipeeeStreamService.SimulateDonation(SimulateAmountBox.Text, CurrencyBox.Text);
    }
}
