using System.Windows.Controls;
using System.Windows;
using SubathonManager.Core.Events;
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
        YTUserHandle.Text = App.AppConfig!.Get("YouTube", "Handle", string.Empty)!;
    }
    
    private void UpdateYoutubeStatus(bool status,  string name)
    {
        Dispatcher.Invoke(() =>
        {
            if (YTUserHandle.Text != name && name != "None") YTUserHandle.Text = name; 
            Host!.UpdateConnectionStatus(status, YTStatusText, ConnectYTBtn);
        });
    }

    public bool UpdateValueSettings(AppDbContext db)
    {
        bool hasUpdated = false;
        var superchatValue = db.SubathonValues.FirstOrDefault(sv =>
            sv.EventType == SubathonEventType.YouTubeSuperChat
            && sv.Meta == "");
        if (superchatValue != null && double.TryParse(DonoBox.Text, out var scSeconds)
            && !scSeconds.Equals(superchatValue.Seconds))
        {
            superchatValue.Seconds = scSeconds;
            hasUpdated = true;
        }

        if (superchatValue != null && double.TryParse(DonoBox2.Text, out var scPoints)
            && !scPoints.Equals(superchatValue.Points))
        {
            superchatValue.Points = scPoints;
            hasUpdated = true;
        }

        hasUpdated |= Host!.SaveSubTier(db, SubathonEventType.YouTubeMembership, "DEFAULT", MemberDefaultTextBox, MemberRenameTextBox2);
        hasUpdated |= Host!.SaveSubTier(db, SubathonEventType.YouTubeGiftMembership, "DEFAULT", GiftMemberDefaultTextBox, GiftMemberDefaultTextBox2);
        return hasUpdated;
    }

    private void ConnectYouTubeButton_Click(object sender, RoutedEventArgs e)
    {
        string user = YTUserHandle.Text.Trim();
        if (!user.StartsWith("@") && !string.IsNullOrEmpty(user))
            user = "@" + user;
        App.AppConfig!.Set("YouTube", "Handle", user);
        App.AppConfig!.Save();

        App.AppYouTubeService!.Start(user);
    }
}