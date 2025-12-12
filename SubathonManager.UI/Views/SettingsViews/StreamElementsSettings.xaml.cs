using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Windows;
using System.Diagnostics;
using SubathonManager.Core.Events;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Integration;
using SubathonManager.Data;

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
        
        StreamElementsEvents.StreamElementsConnectionChanged += UpdateSEStatus;
        SEJWTTokenBox.Text = App.AppConfig!.Get("StreamElements", "JWT", string.Empty)!;    
        if (App.AppStreamElementsService != null)
            Host!.UpdateConnectionStatus(App.AppStreamElementsService.Connected, SEStatusText, ConnectSEBtn);
    }

    public void UpdateValueSettings(AppDbContext db)
    {
        var seTipValue =
            db.SubathonValues.FirstOrDefault(sv =>
                sv.EventType == SubathonEventType.StreamElementsDonation && sv.Meta == "");
        if (seTipValue != null && double.TryParse(DonoBox.Text, out var seTipSeconds))
            seTipValue.Seconds = seTipSeconds;
        if (seTipValue != null && int.TryParse(DonoBox2.Text, out var seTipPoints))
            seTipValue.Points = seTipPoints;
    }
    
    private void UpdateSEStatus(bool status)
    {
        Host!.UpdateConnectionStatus(status, SEStatusText, ConnectSEBtn);
    }
    
    
    private async void ConnectSEButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            App.AppStreamElementsService!.Disconnect();
            App.AppStreamElementsService!.SetJwtToken(SEJWTTokenBox.Password);
            await Task.Delay(100);
            App.AppStreamElementsService!.InitClient();
            if (App.AppStreamElementsService.IsTokenEmpty())
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