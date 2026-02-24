using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Windows;
using System.Diagnostics;
using SubathonManager.Core.Events;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Interfaces;
using SubathonManager.Integration;
using SubathonManager.Data;
using SubathonManager.UI.Services;

namespace SubathonManager.UI.Views.SettingsViews;

public partial class StreamElementsSettings : UserControl
{
    public required SettingsView Host { get; set; }
    
    private readonly ILogger? _logger = AppServices.Provider.GetRequiredService<ILogger<StreamElementsSettings>>();
    public StreamElementsSettings()
    {
        InitializeComponent();
    }

    public void Init(SettingsView host)
    {
        Host = host;

        var config = AppServices.Provider.GetRequiredService<IConfig>();
        IntegrationEvents.ConnectionUpdated += UpdateSEStatus;
        SEJWTTokenBox.Text = config!.Get("StreamElements", "JWT", string.Empty)!;    
        if (ServiceManager.StreamElementsOrNull != null)
            Host!.UpdateConnectionStatus(ServiceManager.StreamElementsOrNull.Connected, SEStatusText, ConnectSEBtn);
    }

    public bool UpdateValueSettings(AppDbContext db)
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
    
    private void UpdateSEStatus(bool status, SubathonEventSource source, string name, string service)
    {
        if (source != SubathonEventSource.StreamElements) return;
        Host!.UpdateConnectionStatus(status, SEStatusText, ConnectSEBtn);
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