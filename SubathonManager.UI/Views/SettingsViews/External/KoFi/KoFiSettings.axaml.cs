using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;
using SubathonManager.Data;
using SubathonManager.UI.UiUtils;

namespace SubathonManager.UI.Views.SettingsViews.External.KoFi;

[Obsolete("Legacy Ko-Fi via StreamerBot socket setup. DevTunnels method is recommended")]
public partial class KoFiSettings : SettingsControl
{
    private readonly ILogger? _logger = AppServices.Provider.GetRequiredService<ILogger<KoFiSettings>>();

    public KoFiSettings()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            IntegrationEvents.ConnectionUpdated += UpdateStatus;
            UpdateStatus(Utils.GetConnection(SubathonEventSource.KoFi, "Socket"));
        };
        Unloaded += (_, _) =>
        {
            IntegrationEvents.ConnectionUpdated -= UpdateStatus;
        };
    }

    internal override void UpdateStatus(IntegrationConnection? connection)
    {
        if (connection is not { Source: SubathonEventSource.KoFi } || connection.Service != "Socket") return;
        Host.UpdateConnectionStatus(connection.Status, KoFiStatusText, null);
    }

    private void OpenKoFiSetup_Click(object? sender, RoutedEventArgs e) => OpenDiscussion();

    private void OpenDiscussion()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://docs.subathonmanager.app/latest/config/setup/KoFi/",
                UseShellExecute = true
            });
        }
        catch { /**/ }
    }

    private async void CopyImportString_Click(object? sender, RoutedEventArgs e)
    {
        string version = AppServices.AppVersion;
        if (!version.StartsWith('v') || version.Length > 16) version = "nightly";
        string url = $"https://github.com/WolfwithSword/SubathonManager/releases/download/{version}/SubathonManager_KoFi.sb";
        try
        {
            using var http = new HttpClient();
            string content = await http.GetStringAsync(url);
            if (string.IsNullOrWhiteSpace(content)) { OpenDiscussion(); return; }

            var result = await UiHelpers.TrySetClipboardTextAsync(content);
            if (!result) return;
            if (sender is not Button button) return;
            var originalContent = button.Content;
            button.Content = "Copied!";
            await Task.Delay(1500);
            button.Content = originalContent;
        }
        catch { OpenDiscussion(); }
    }

    protected internal override void LoadValues(AppDbContext db) { }
    public override bool UpdateValueSettings(AppDbContext db) => false;
    public override void UpdateCurrencyBoxes(List<string> currencies, string selected) { }
    public override (string, string, TextBox?, TextBox?) GetValueBoxes(SubathonValue val) => ("", "", null, null);
}
