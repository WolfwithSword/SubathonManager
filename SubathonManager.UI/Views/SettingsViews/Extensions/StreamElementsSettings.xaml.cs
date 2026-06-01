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
using SubathonManager.Data;
using SubathonManager.Integration;
using SubathonManager.UI.Services;
// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.UI.Views.SettingsViews.Extensions;

public partial class StreamElementsSettings : SettingsControl
{
    
    private readonly ILogger? _logger = AppServices.Provider.GetRequiredService<ILogger<StreamElementsSettings>>();
    public StreamElementsSettings()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            IntegrationEvents.ConnectionUpdated += UpdateStatus;
            RegisterUnsavedChangeHandlers();
            UpdateStatus(Utils.GetConnection(SubathonEventSource.StreamElements, "Socket"));
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
        SEJWTTokenBox.Text = secureStorage.GetOrDefault(StorageKeys.StreamElementsJwt, string.Empty)!;
        UpdateStatus(Utils.GetConnection(SubathonEventSource.StreamElements, "Socket"));
    }

    public override bool UpdateValueSettings(AppDbContext db)
    {
        bool hasUpdated = false;
        var seTipValue =
            db.SubathonValues.FirstOrDefault(sv =>
                sv.EventType == SubathonEventType.StreamElementsDonation && sv.Meta == "");
        if (seTipValue != null && double.TryParse(DonoBox.Text, out var seTipSeconds) &&
            !seTipSeconds.Equals(seTipValue.Seconds))
        {
            seTipValue.Seconds = seTipSeconds;
            hasUpdated = true;
        }

        if (seTipValue != null && double.TryParse(DonoBox2.Text, out var seTipPoints) &&
            !seTipPoints.Equals(seTipValue.Points))
        {
            seTipValue.Points = seTipPoints;
            hasUpdated = true;
        }

        return hasUpdated;
    }
    
    internal override void UpdateStatus(IntegrationConnection? connection)
    {
        if (connection is not {Source: SubathonEventSource.StreamElements}) return;
        Host.UpdateConnectionStatus(connection.Status, SEStatusText, ConnectSEBtn);
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
            case SubathonEventType.StreamElementsDonation:
                box = DonoBox;
                box2 = DonoBox2;
                break;
        }
        return (v, p, box, box2);
    }


    private async void ConnectSEButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ServiceManager.StreamElements.Disconnect();
            ServiceManager.StreamElements.SetJwtToken(SEJWTTokenBox.Password);
            await Task.Delay(100);
            ServiceManager.StreamElements.InitClient();
            if (ServiceManager.StreamElements.IsTokenEmpty())
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://streamelements.com/dashboard/account/channels",
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to initialize StreamElements Service");
        }
    }
    
    
    private void TestSETip_Click(object sender, RoutedEventArgs e)
    {
        var value = SimulateSETipAmountBox.Text;
        var currency = CurrencyBox.Text;
        StreamElementsService.SimulateTip(value, currency);
    }
}