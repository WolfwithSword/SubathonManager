using System.Windows.Controls;
using System.Windows;
using SubathonManager.Core.Events;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Data;

namespace SubathonManager.UI.Views.SettingsViews;

public partial class YouTubeSettings : UserControl
{
    public required SettingsView Host { get; set; }
    public YouTubeSettings()
    {
        InitializeComponent();
    }

    public void Init(SettingsView host)
    {
        Host = host;
        YouTubeEvents.YouTubeConnectionUpdated += UpdateYoutubeStatus;
        YTUserHandle.Text = Config.Data["YouTube"]["Handle"] ?? "";
    }
    
    private void UpdateYoutubeStatus(bool status,  string name)
    {
        Dispatcher.Invoke(() =>
        {
            if (YTUserHandle.Text != name && name != "None") YTUserHandle.Text = name; 
            Host!.UpdateConnectionStatus(status, YTStatusText, ConnectYTBtn);
        });
    }

    public void UpdateValueSettings(AppDbContext db)
    {
        var superchatValue = db.SubathonValues.FirstOrDefault(sv =>
            sv.EventType == SubathonEventType.YouTubeSuperChat
            && sv.Meta == "");
        if (superchatValue != null && double.TryParse(DonoBox.Text, out var scSeconds))
            superchatValue.Seconds = scSeconds;
        if (superchatValue != null && int.TryParse(DonoBox2.Text, out var scPoints))
            superchatValue.Points = scPoints;
            
        Host!.SaveSubTier(db, SubathonEventType.YouTubeMembership, "DEFAULT", MemberT1TextBox, MemberT1TextBox2);
        Host!.SaveSubTier(db, SubathonEventType.YouTubeGiftMembership, "DEFAULT", GiftMemberT1TextBox, GiftMemberT1TextBox2);
    }
    
    private void ConnectYouTubeButton_Click(object sender, RoutedEventArgs e)
    {
        string user = YTUserHandle.Text.Trim();
        if (!user.StartsWith("@") && !string.IsNullOrEmpty(user))
            user = "@" + user;
        Config.Data["YouTube"]["Handle"] = user;
        Config.Save();

        App.AppYouTubeService!.Start(user);
    }
}