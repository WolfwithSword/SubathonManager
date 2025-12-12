using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Windows;
using System.Diagnostics;
using SubathonManager.Core.Events;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Data;
using SubathonManager.Integration;


namespace SubathonManager.UI.Views.SettingsViews;

public partial class StreamLabsSettings : UserControl
{
    public required SettingsView Host { get; set; }
    
    private readonly ILogger? _logger = AppServices.Provider.GetRequiredService<ILogger<StreamLabsSettings>>();
    public StreamLabsSettings()
    {
        InitializeComponent();
    }

    public void Init(SettingsView host)
    {
        Host = host;
        StreamLabsEvents.StreamLabsConnectionChanged += UpdateSLStatus;
        SLTokenBox.Text = App.AppConfig!.Get("StreamLabs", "SocketToken", string.Empty)!;

        if (App.AppStreamLabsService != null)
            Host!.UpdateConnectionStatus(App.AppStreamLabsService.Connected, SLStatusText, ConnectSLBtn);
    }
    public void UpdateValueSettings(AppDbContext db)
    {
        var slTipValue =
            db.SubathonValues.FirstOrDefault(sv =>
                sv.EventType == SubathonEventType.StreamLabsDonation && sv.Meta == "");
        if (slTipValue != null && double.TryParse(DonoBox.Text, out var slTipSeconds))
            slTipValue.Seconds = slTipSeconds;
        if (slTipValue != null && int.TryParse(DonoBox2.Text, out var slTipPoints))
            slTipValue.Points = slTipPoints;
    }
    
    private void UpdateSLStatus(bool status)
    {
        Host!.UpdateConnectionStatus(status, SLStatusText, ConnectSLBtn);
    }
    
    private async void ConnectSLButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await App.AppStreamLabsService!.DisconnectAsync();
            App.AppStreamLabsService!.SetSocketToken(SLTokenBox.Password);
            await Task.Delay(100);
            await App.AppStreamLabsService!.InitClientAsync();
            if (App.AppStreamLabsService.IsTokenEmpty())
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