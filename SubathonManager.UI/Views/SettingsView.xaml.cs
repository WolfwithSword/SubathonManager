using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging; 
using SubathonManager.Core.Events;
using SubathonManager.Core;
using SubathonManager.Data;

namespace SubathonManager.UI.Views;

public partial class SettingsView
{
    private DateTime? _lastUpdatedTimerAt;
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly ILogger? _logger = AppServices.Provider.GetRequiredService<ILogger<SettingsView>>();
    public SettingsView()
    {
        _factory = AppServices.Provider!.GetRequiredService<IDbContextFactory<AppDbContext>>();
        TwitchEvents.TwitchConnected += UpdateTwitchStatus;
        YouTubeEvents.YouTubeConnectionUpdated += UpdateYoutubeStatus;
        StreamElementsEvents.StreamElementsConnectionChanged += UpdateSEStatus;
        StreamLabsEvents.StreamLabsConnectionChanged += UpdateSLStatus;
        InitializeComponent();
        
        SEJWTTokenBox.Text = Config.Data["StreamElements"]["JWT"];
        SLTokenBox.Text = Config.Data["StreamLabs"]["SocketToken"];

        if (App.AppStreamElementsService != null)
            UpdateConnectionStatus(App.AppStreamElementsService.Connected, SEStatusText, ConnectSEBtn);
        if (App.AppStreamLabsService != null)
            UpdateConnectionStatus(App.AppStreamLabsService.Connected, SLStatusText, ConnectSLBtn);
        
        ServerPortTextBox.Text = Config.Data["Server"]["Port"];
        LoadValues();
        InitWebhookSettings();
        InitTwitchAutoSettings();
        InitCommandSettings();
        InitYoutubeSettings();
        
        SubathonEvents.SubathonDataUpdate += UpdateTimerValue;
        WebServerEvents.WebServerStatusChanged += UpdateServerStatus;
        UpdateServerStatus(App.AppWebServer?.Running ?? false);

        InitCurrencySelects();
    }
}