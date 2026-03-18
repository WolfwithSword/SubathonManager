using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Windows;
using System.Diagnostics;
using SubathonManager.Core.Events;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Interfaces;
using SubathonManager.Data;
using SubathonManager.Integration;
using SubathonManager.UI.Services;


namespace SubathonManager.UI.Views.SettingsViews;

public partial class StreamLabsSettings : SettingsControl
{
    
    private readonly ILogger? _logger = AppServices.Provider.GetRequiredService<ILogger<StreamLabsSettings>>();
    public StreamLabsSettings()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            IntegrationEvents.ConnectionUpdated += UpdateStatus;
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

        if (ServiceManager.StreamLabsOrNull != null)
            Host.UpdateConnectionStatus(ServiceManager.StreamLabsOrNull.Connected, SLStatusText, ConnectSLBtn);
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

    public override bool UpdateConfigValueSettings()
    {
        throw new NotImplementedException();
    }

    internal override void UpdateStatus(bool status, SubathonEventSource source, string name, string service)
    {
        if (source != SubathonEventSource.StreamLabs) return;
        Host.UpdateConnectionStatus(status, SLStatusText, ConnectSLBtn);
    }

    public override void LoadValues(AppDbContext db)
    {
        throw new NotImplementedException();
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