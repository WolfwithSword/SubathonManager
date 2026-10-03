using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;
using SubathonManager.Data;
using SubathonManager.Integration;
using SubathonManager.UI.Services;

namespace SubathonManager.UI.Views.SettingsViews.ExternalSoftware;

public partial class StreamerBotSettings : SettingsControl {
    public StreamerBotSettings() {
        InitializeComponent();
        Loaded += (_, _) => {
            IntegrationEvents.ConnectionUpdated -= UpdateStatus;
            IntegrationEvents.ConnectionUpdated += UpdateStatus;
            UpdateStatus(Utils.GetConnection(SubathonEventSource.StreamerBot, "Socket"));
            UpdateStatus(Utils.GetConnection(SubathonEventSource.StreamerBot, StreamerBotService.HttpService));
        };
        Unloaded += (_, _) => { IntegrationEvents.ConnectionUpdated -= UpdateStatus; };
    }

    public override void Init(SettingsView host) {
        Host = host;
        var config = AppServices.Provider.GetRequiredService<IConfig>();
        SuppressUnsavedChanges(() => {
            HttpEnabledCheck.IsChecked = config.GetBool(StreamerBotService.ConfigSection, "Http.Enabled");
            HttpUrlBox.Text = config.Get(StreamerBotService.ConfigSection, "Http.Url", StreamerBotService.DefaultHttpUrl);
        });
        RegisterUnsavedChangeHandlers();
    }

    protected internal override bool UpdateConfigValueSettings() {
        var config = AppServices.Provider.GetRequiredService<IConfig>();
        var hasUpdated = false;
        hasUpdated |= config.SetBool(StreamerBotService.ConfigSection, "Http.Enabled", HttpEnabledCheck.IsChecked);
        hasUpdated |= config.Set(StreamerBotService.ConfigSection, "Http.Url", (HttpUrlBox.Text ?? "").Trim());
        if (hasUpdated) _ = ServiceManager.StreamerBot.CheckHttpAsync();
        return hasUpdated;
    }

    private async void CheckHttp_Click(object? sender, RoutedEventArgs e) {
        var config = AppServices.Provider.GetRequiredService<IConfig>();
        if (config.Set(StreamerBotService.ConfigSection, "Http.Url", (HttpUrlBox.Text ?? "").Trim()))
            config.Save();

        HttpStatusText.ClearValue(TextBlock.ForegroundProperty);
        HttpStatusText.Text = "Checking...";
        IReadOnlyList<StreamerBotActionInfo>? actions = await ServiceManager.StreamerBot.GetActionsAsync();
        await Dispatcher.UIThread.InvokeAsync(() => {
            if (actions == null) {
                HttpStatusText.Foreground = Brushes.OrangeRed;
                HttpStatusText.Text =
                    "Couldn't reach Streamer.bot. Check it is running, its HTTP Server is started, and the address is correct";
                return;
            }

            HttpStatusText.Text = HttpEnabledCheck.IsChecked == true
                ? $"Connected - {actions.Count} actions available"
                : $"Connected - {actions.Count} actions available (tick the box above and save to use them)";
        });
    }

    internal override void UpdateStatus(IntegrationConnection? connection) {
        if (connection is not { Source: SubathonEventSource.StreamerBot }) return;
        if (connection.Service == "Socket") {
            Host.UpdateConnectionStatus(connection.Status, StreamerBotStatusText, null);
            return;
        }

        if (connection.Service != StreamerBotService.HttpService) return;
        Dispatcher.UIThread.Post(() => HttpApiStatusText.Text =
            !connection.Configured ? "Disabled" : connection.Status ? "Connected" : "Disconnected");
    }

    private void OpenStreamerBotExtension_Click(object? sender, RoutedEventArgs e) {
        try {
            Process.Start(new ProcessStartInfo {
                FileName = "https://extensions.wolfwithsword.com/extensions/subathonmanager-extension/",
                UseShellExecute = true
            });
        }
        catch {
            /**/
        }
    }

    public override bool UpdateValueSettings(AppDbContext db) {
        return false;
    }

    public override void UpdateCurrencyBoxes(List<string> currencies, string selected) {
    }

    public override (string, string, TextBox?, TextBox?) GetValueBoxes(SubathonValue val) {
        return ("", "", null, null);
    }
}