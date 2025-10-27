using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using SubathonManager.Core.Events;
using SubathonManager.Core;
using SubathonManager.Data;

namespace SubathonManager.UI.Views;

public partial class SettingsView
{
    private DateTime? _lastUpdatedTimerAt;
    private readonly IDbContextFactory<AppDbContext> _factory;
    public SettingsView()
    {
        _factory = App.AppServices.GetRequiredService<IDbContextFactory<AppDbContext>>();
        TwitchEvents.TwitchConnected += UpdateTwitchStatus;
        StreamElementsEvents.StreamElementsConnectionChanged += UpdateSEStatus;
        InitializeComponent();
        
        SaveSettingsButton.Click += SaveSettingsButton_Click;
        DataFolderText.Text = $"Data Folder: {Config.DataFolder}";
        SEJWTTokenBox.Text = Config.Data["StreamElements"]["JWT"];
        
        ServerPortTextBox.Text = Config.Data["Server"]["Port"];
        LoadValues();
        InitWebhookSettings();
        InitTwitchAutoSettings();
        
        SubathonEvents.SubathonDataUpdate += UpdateTimerValue;
    }
}