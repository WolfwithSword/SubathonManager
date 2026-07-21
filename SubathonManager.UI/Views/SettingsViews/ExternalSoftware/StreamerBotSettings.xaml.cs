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

public partial class StreamerBotSettings : SettingsControl
{
    public StreamerBotSettings()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            IntegrationEvents.ConnectionUpdated -= UpdateStatus;
            IntegrationEvents.ConnectionUpdated += UpdateStatus;
            UpdateStatus(Utils.GetConnection(SubathonEventSource.StreamerBot, "Socket"));
        };
        Unloaded += (_, _) =>
        {
            IntegrationEvents.ConnectionUpdated -= UpdateStatus;
        };
    }

    internal override void UpdateStatus(IntegrationConnection? connection)
    {
        if (connection is not { Source: SubathonEventSource.StreamerBot, Service: "Socket" }) return;
        Host.UpdateConnectionStatus(connection.Status, StreamerBotStatusText, null);
    }

    private void OpenStreamerBotExtension_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://extensions.wolfwithsword.com/extensions/subathonmanager-extension/",
                UseShellExecute = true
            });
        }
        catch { /**/ }
    }

    public override bool UpdateValueSettings(AppDbContext db) => false;
    public override void UpdateCurrencyBoxes(List<string> currencies, string selected) { }
    public override (string, string, TextBox?, TextBox?) GetValueBoxes(SubathonValue val) => ("", "", null, null);
}
