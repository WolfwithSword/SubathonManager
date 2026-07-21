using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;
using SubathonManager.Data;

namespace SubathonManager.UI.Views.SettingsViews.ExternalSoftware;

public partial class StreamDeckSettings : SettingsControl
{
    public StreamDeckSettings()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            IntegrationEvents.ConnectionUpdated -= UpdateStatus;
            IntegrationEvents.ConnectionUpdated += UpdateStatus;
            UpdateStatus(Utils.GetConnection(SubathonEventSource.StreamDeck, "Socket"));
        };
        Unloaded += (_, _) =>
        {
            IntegrationEvents.ConnectionUpdated -= UpdateStatus;
        };
    }

    internal override void UpdateStatus(IntegrationConnection? connection)
    {
        if (connection is not { Source: SubathonEventSource.StreamDeck, Service: "Socket" }) return;
        Host.UpdateConnectionStatus(connection.Status, StreamDeckStatusText, null);
    }

    private void OpenStreamDeckSetup_Click(object sender, RoutedEventArgs e) => OpenDocs();

    private static void OpenDocs()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://docs.subathonmanager.app/latest/config/setup/StreamDeck/",
                UseShellExecute = true
            });
        }
        catch { /**/ }
    }

    private void DownloadPlugin_Click(object sender, RoutedEventArgs e)
    {
        string version = AppServices.AppVersion;
        if (!version.StartsWith('v') || version.Length > 16) version = "nightly";
        string url = $"https://github.com/WolfwithSword/SubathonManager/releases/download/{version}/SubathonManager_StreamDeck.streamDeckPlugin";
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch { OpenDocs(); }
    }

    public override bool UpdateValueSettings(AppDbContext db) => false;
    public override void UpdateCurrencyBoxes(List<string> currencies, string selected) { }
    public override (string, string, TextBox?, TextBox?) GetValueBoxes(SubathonValue val) => ("", "", null, null);
}
