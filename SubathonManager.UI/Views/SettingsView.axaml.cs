using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Objects;
using SubathonManager.UI.Views.SettingsViews;
using SubathonManager.UI.Services;

namespace SubathonManager.UI.Views;

public partial class SettingsView : SettingsControl
{
    private DateTime? _lastUpdatedTimerAt;
    private bool _initialised;
    private readonly ILogger? _logger = AppServices.Provider.GetRequiredService<ILogger<SettingsView>>();

    public SettingsView()
    {
        InitializeComponent();

        var config = AppServices.Provider.GetRequiredService<IConfig>();

        SubathonEvents.SubathonDataUpdate += UpdateTimerValue;
        Loaded += (_, _) =>
        {
            WebServerEvents.WebServerStatusChanged += UpdateServerStatus;
            UpdateServerStatus(ServiceManager.Server?.Running ?? false);
            SubathonEvents.SubathonValueConfigUpdatedRemote += RefreshSubathonValues;

            if (_initialised) return;
            _initialised = true;
            SettingsEvents.SettingsUnsavedChanges += UpdateSaveButtonBorder;
            RegisterUnsavedChangeHandlers();
            InitCurrencySelects();
        };

        StreamingSettingsControl.Init(this);
        WebhookLogSettingsControl.Init(this);
        ExternalServiceSettingsControl.Init(this);
        CommandsSettingsControl.Init(this);
        ExtensionSettingsControl.Init(this);
        ExternalSoftwareSettingsControl.Init(this);

        SettingsEvents.HotLinkToSourceRequested -= HotLinkToSource;
        SettingsEvents.HotLinkToSourceRequested += HotLinkToSource;

        ServerPortTextBox.Text = config.Get("Server", "Port", string.Empty) ?? string.Empty;
        LoadValues();
        InitCurrencySelects();

        Unloaded += (_, _) =>
        {
            WebServerEvents.WebServerStatusChanged -= UpdateServerStatus;
            SubathonEvents.SubathonValueConfigUpdatedRemote -= RefreshSubathonValues;
        };

        Task.Run(CheckForUpdateOnBoot);
    }

    private void HotLinkToSource(SubathonEventSource source, string? detail)
    {
        Dispatcher.UIThread.Post(() =>
        {
            SettingsGroupControl? group = source.GetGroup() switch
            {
                SubathonSourceGroup.Stream => StreamingSettingsControl,
                SubathonSourceGroup.StreamExtension => ExtensionSettingsControl,
                SubathonSourceGroup.ExternalService => ExternalServiceSettingsControl,
                SubathonSourceGroup.ExternalSoftware => ExternalSoftwareSettingsControl,
                _ => null
            };
            if (group == null) return;
            group.TryHotLinkToSource(source);
            if (!string.IsNullOrWhiteSpace(detail)
                && group.GetControlForSource(source) is SettingsViews.External.GoAffProSettings goAffPro)
                goAffPro.TrySelectStore(detail);
        });
    }

    private async void CheckForUpdateOnBoot()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15));
            _logger?.LogDebug("Checking for updates on boot...");
            (bool hasUpdate, string? newVersion, string? _) = await AppServices.CheckForUpdate(_logger);
            if (hasUpdate && !string.IsNullOrEmpty(newVersion))
                await Dispatcher.UIThread.InvokeAsync(() => UpdateBtn.Content = "Update Available!");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error checking for updates on boot");
        }
    }

    private void GoToHelp_Click(object? sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://docs.subathonmanager.app",
            UseShellExecute = true
        });
    }

    private async void Updater_Click(object? sender, RoutedEventArgs e)
    {
        (bool hasUpdate, string? newVersion, string? url) = await AppServices.CheckForUpdate(_logger);
        if (hasUpdate && !string.IsNullOrEmpty(newVersion))
        {
            var panel = new StackPanel { Orientation = Orientation.Vertical };
            panel.Children.Add(new TextBlock { Text = "Update available!", TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBlock { Text = "v" + newVersion, TextWrapping = TextWrapping.Wrap });

            if (!string.IsNullOrEmpty(url))
            {
                var navUrl = url.Replace("/" + newVersion, "/v" + newVersion);
                var link = new Button
                {
                    Content = "Latest Version",
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(0),
                    Cursor = new Cursor(StandardCursorType.Hand),
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                link.Click += (_, _) => Process.Start(new ProcessStartInfo(navUrl) { UseShellExecute = true });
                panel.Children.Add(link);
            }

            panel.Children.Add(new TextBlock { Text = "Download and install now?", Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBlock { Text = "You will need to start the app manually once finished.", TextWrapping = TextWrapping.Wrap });

            var dialog = new FAContentDialog
            {
                Title = "Updater",
                PrimaryButtonText = "Update",
                CloseButtonText = "Cancel",
                Content = panel
            };
            var result = await dialog.ShowAsync();
            if (result != FAContentDialogResult.Primary) return;

            await AppServices.DownloadAndInstall(_logger);
        }
        else
        {
            await Dispatcher.UIThread.InvokeAsync(() => UpdateBtn.Content = "No Updates Found");
            await Task.Delay(5000);
            await Dispatcher.UIThread.InvokeAsync(() => UpdateBtn.Content = "Check for Updates");
        }
    }

    internal override void UpdateStatus(IntegrationConnection? connection) { }

    private async void ShowTelemetryPromptAsync(object? sender, RoutedEventArgs routedEventArgs)
    {
        try
        {
            var config = AppServices.Provider.GetRequiredService<IConfig>();

            var panel = new StackPanel { Orientation = Orientation.Vertical, Width = 340 };
            panel.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 4, 4, 12),
                Text = "Would you like to send anonymous usage data to help guide development?\n\n" +
                       "Only information on which integrations are active is collected - no usernames, keys, or personal information of any kind."
            });

            var checkBox = new CheckBox
            {
                Content = "Enable anonymous data collection",
                IsChecked = config.GetBool("Telemetry", "Enabled", false),
                Margin = new Thickness(4, 0, 4, 4)
            };
            panel.Children.Add(checkBox);

            var dialog = new FAContentDialog
            {
                Title = "Help Improve Subathon Manager",
                PrimaryButtonText = "Confirm",
                CloseButtonText = "Cancel",
                Content = panel
            };

            var result = await dialog.ShowAsync();
            if (result != FAContentDialogResult.Primary) return;
            bool enabled = checkBox.IsChecked ?? false;
            if (config.SetBool("Telemetry", "Enabled", enabled))
                config.Save();
        }
        catch { /**/ }
    }
}
