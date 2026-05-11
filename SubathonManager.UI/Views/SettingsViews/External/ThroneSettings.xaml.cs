using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Extensions.DependencyInjection;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;
using SubathonManager.Data;
using SubathonManager.UI.Services;
using Button = Wpf.Ui.Controls.Button;
using PasswordBox = Wpf.Ui.Controls.PasswordBox;
using TextBlock = Wpf.Ui.Controls.TextBlock;

namespace SubathonManager.UI.Views.SettingsViews.External;

public partial class ThroneSettings: DevTunnelSettingsControl
{
    // TODO
    protected override StackPanel? _MembershipsPanel => null;
    protected override PasswordBox _WebhookUrlBox => WebhookUrlBox;
    protected override TextBlock _WebhookStatusText => ThroneWebhookStatusText;
    protected override SubathonEventSource _EventSource => SubathonEventSource.Throne;
    protected override StackPanel _WebhookUrlRow => WebhookUrlRow;
    protected override TextBlock _TunnelPrereqStatusText => TunnelPrereqStatusText;
    protected override Button _TunnelPrereqHint => TunnelPrereqHint;
    protected override Wpf.Ui.Controls.TextBox? _WebhookForwardUrlsBox => null;
    protected override Popup? _ForwardUrlsPopup => null;
    protected override Wpf.Ui.Controls.TextBox? _ForwardUrlsMultiBox => null;
    protected override SubathonEventType? _membershipEventType => null;
    protected override Button? _ConnectBtn => ConnectBtn;
    protected override bool allowMembershipDelete => false;

    private readonly string configSection = "Throne";
    
    public ThroneSettings()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            IntegrationEvents.ConnectionUpdated += UpdateStatus;
            RegisterUnsavedChangeHandlers();
            RefreshFromStoredState();
        };

        Unloaded += (_, _) =>
        {
            IntegrationEvents.ConnectionUpdated -= UpdateStatus;
        };
    }
    
    public override void Init(SettingsView host)
    {
        Host = host;
        RegisterUnsavedChangeHandlers();
    }
    
    internal override void UpdateStatus(IntegrationConnection? conn)
    {
        if (conn == null) return;
        base.UpdateStatus(conn);
        if (conn.Source != SubathonEventSource.Throne) return;
        Dispatcher.Invoke(() =>
            {
                DisconnBtn.Visibility = conn.Status ? Visibility.Visible : Visibility.Collapsed; 
                ConnectBtn.Visibility = conn.Status ? Visibility.Collapsed :  Visibility.Visible;
            }
        );
    }

    public override bool UpdateValueSettings(AppDbContext db)
    {
        return false;
        // throw new NotImplementedException();
    }

    public override void UpdateCurrencyBoxes(List<string> currencies, string selected)
    {
        return;
        // throw new NotImplementedException();
    }

    public override (string seconds, string points, TextBox? timeBox, TextBox? pointsBox) GetValueBoxes(SubathonValue val)
    {
        return ("", "", null, null);
        // throw new NotImplementedException();
    }
    private void TestThroneShopGiftOrder_Click(object sender, RoutedEventArgs e)
    {
        throw new NotImplementedException();
    }

    private void TestThroneShopOrder_Click(object sender, RoutedEventArgs e)
    {
        throw new NotImplementedException();
    }

    private void TestThroneTip_Click(object sender, RoutedEventArgs e)
    {
        throw new NotImplementedException();
    }

    private async void DisconnectThrone_Click(object sender, RoutedEventArgs e)
    {
        var config = AppServices.Provider.GetRequiredService<IConfig>();
        bool hasUpdated = config.SetBool(configSection, "Enabled", false);
        if (hasUpdated) config.Save();
        
        await ServiceManager.Throne.StopAsync();
    }

    private async void ConnectThrone_Click(object sender, RoutedEventArgs e)
    {
        var config = AppServices.Provider.GetRequiredService<IConfig>();
        bool hasUpdated = config.SetBool(configSection, "Enabled", true);
        if (hasUpdated) config.Save();
        
        await ServiceManager.Throne.Initialize();
    }

    private async void OpenThroneLink_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://throne.com/profile/integrations/webhook",
                UseShellExecute = true
            });
        }
        catch { /**/ }
    }
}