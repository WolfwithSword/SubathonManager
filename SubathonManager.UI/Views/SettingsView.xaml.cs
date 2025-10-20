using SubathonManager.Core.Events;
using SubathonManager.Core;

namespace SubathonManager.UI.Views;

public partial class SettingsView
{
    private DateTime? _lastUpdatedTimerAt;
    public SettingsView()
    {
        TwitchEvents.TwitchConnected += UpdateTwitchStatus;
        StreamElementsEvents.StreamElementsConnectionChanged += UpdateSEStatus;
        InitializeComponent();
        
        SaveSettingsButton.Click += SaveSettingsButton_Click;
        DataFolderText.Text = $"Data Folder: {Config.DataFolder}";
        SEJWTTokenBox.Text = Config.Data["StreamElements"]["JWT"];
        
        ServerPortTextBox.Text = Config.Data["Server"]["Port"];
        LoadValues();
        
        SubathonEvents.SubathonDataUpdate += UpdateTimerValue;
    }
}