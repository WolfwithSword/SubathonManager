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
using SubathonManager.Data;
using SubathonManager.Integration;
using SubathonManager.UI.Services;

// ReSharper disable NullableWarningSuppressionIsUsed


namespace SubathonManager.UI.Views.SettingsViews.Extensions;

public partial class StreamLabsSettings : SettingsControl
{
    
    private readonly ILogger? _logger = AppServices.Provider.GetRequiredService<ILogger<StreamLabsSettings>>();
    public StreamLabsSettings()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            IntegrationEvents.ConnectionUpdated += UpdateStatus;
            RegisterUnsavedChangeHandlers();
            UpdateStatus(Utils.GetConnection(SubathonEventSource.StreamLabs, "Socket"));
        };

        Unloaded += (_, _) =>
        {
            IntegrationEvents.ConnectionUpdated -= UpdateStatus;
        };
    }

    public override void Init(SettingsView host)
    {
        Host = host;
        var config = AppServices.Provider.GetRequiredService<IConfig>();
        SLTokenBox.Text = config.Get("StreamLabs", "SocketToken", string.Empty)!;
        UpdateStatus(Utils.GetConnection(SubathonEventSource.StreamLabs, "Socket"));
    }
    
    public override bool UpdateValueSettings(AppDbContext db)
    {
        bool hasUpdated = false;
        var slTipValue =
            db.SubathonValues.FirstOrDefault(sv =>
                sv.EventType == SubathonEventType.StreamLabsDonation && sv.Meta == "");
        if (slTipValue != null && double.TryParse(DonoBox.Text, out var slTipSeconds)
            && !slTipSeconds.Equals(slTipValue.Seconds))
        {
            slTipValue.Seconds = slTipSeconds;
            hasUpdated = true;
        }

        if (slTipValue != null && double.TryParse(DonoBox2.Text, out var slTipPoints)
            && !slTipPoints.Equals(slTipValue.Points))
        {
            slTipValue.Points = slTipPoints;
            hasUpdated = true;
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
        TextBox? box = null;
        TextBox? box2 = null;
        switch (val.EventType)
        {
            case SubathonEventType.StreamLabsDonation:
                box = DonoBox;
                box2 = DonoBox2;
                break;
        }
        return (v, p, box, box2);
    }

    internal override void UpdateStatus(IntegrationConnection? connection)
    {
        if (connection is not { Source: SubathonEventSource.StreamLabs }) return;
        Host.UpdateConnectionStatus(connection.Status, SLStatusText, ConnectSLBtn);
    }

    private async void ConnectSLButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ServiceManager.StreamLabs.DisconnectAsync();
            ServiceManager.StreamLabs.SetSocketToken(SLTokenBox.Password);
            await Task.Delay(100);
            await ServiceManager.StreamLabs.InitClientAsync();
            if (ServiceManager.StreamLabs.IsTokenEmpty())
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://streamlabs.com/dashboard#/settings/api-settings",
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to initialize StreamLabs Service");
        }
    }

    private void TestSLTip_Click(object sender, RoutedEventArgs e)
    {
        var value = SimulateSLTipAmountBox.Text;
        var currency = CurrencyBox.Text;
        StreamLabsService.SimulateTip(value, currency);
    }
}