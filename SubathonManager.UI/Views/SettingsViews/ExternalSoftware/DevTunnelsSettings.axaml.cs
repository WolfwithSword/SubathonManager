using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using DevTunnels.Client.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;
using SubathonManager.Data;
using SubathonManager.UI.UiUtils;
using SubathonManager.UI.Services;

namespace SubathonManager.UI.Views.SettingsViews.ExternalSoftware;

public partial class DevTunnelsSettings : SettingsControl
{
    private readonly ILogger? _logger = AppServices.Provider.GetService<ILogger<DevTunnelsSettings>>();

    public DevTunnelsSettings()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            IntegrationEvents.ConnectionUpdated += UpdateStatus;
            UpdateStatus(Utils.GetConnection(SubathonEventSource.DevTunnels, "Cli"));
            UpdateStatus(Utils.GetConnection(SubathonEventSource.DevTunnels, "Login"));
            UpdateStatus(Utils.GetConnection(SubathonEventSource.DevTunnels, "Tunnel"));
        };

        Unloaded += (_, _) =>
        {
            IntegrationEvents.ConnectionUpdated -= UpdateStatus;
        };
    }

    internal override void UpdateStatus(IntegrationConnection? connection)
    {
        if (connection is not { Source: SubathonEventSource.DevTunnels }) return;

        Dispatcher.UIThread.Post(() =>
        {
            switch (connection.Service)
            {
                case "Cli":
                    ApplyCliState(connection.Status, connection.Name);
                    break;
                case "Login":
                    ApplyLoginState(connection.Status, connection.Name);
                    break;
                case "Tunnel":
                    ApplyTunnelState(connection.Status, connection.Name);
                    break;
            }
        });
    }

    protected internal override void LoadValues(AppDbContext db) { }
    public override bool UpdateValueSettings(AppDbContext db) => false;
    public override void UpdateCurrencyBoxes(List<string> currencies, string selected) { }
    public override (string, string, TextBox?, TextBox?) GetValueBoxes(SubathonValue val) => ("", "", null, null);

    private void ApplyCliState(bool installed, string? version)
    {
        CliStatusText.Text = installed
            ? (string.IsNullOrWhiteSpace(version) ? "Installed" : $"Installed (v{version})")
            : "Not installed";
        if (installed)
        {
            CliStatusText.Foreground = Brushes.ForestGreen;
            InstallCliBtn.Content = "Installed";
            InstallCliBtn.IsEnabled = false;
            InstallCliBtn.IsVisible = false;
        }
        else
        {
            CliStatusText.SetDynamicResource(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
            InstallCliBtn.IsEnabled = true;
            InstallCliBtn.IsVisible = true;
            InstallCliBtn.Content = "Install";
        }

        LoginButtonsPanel.IsEnabled = installed;

        bool loggedIn = Utils.GetConnection(SubathonEventSource.DevTunnels, "Login").Status;
        bool tunnelRunning = Utils.GetConnection(SubathonEventSource.DevTunnels, "Tunnel").Status;
        StartTunnelBtn.IsEnabled = installed && loggedIn && !tunnelRunning;
    }

    private void ApplyLoginState(bool loggedIn, string? username)
    {
        LoginStatusText.Text = loggedIn ? "Logged in:" : "Not logged in";

        UsernamePanel.IsVisible = loggedIn && !string.IsNullOrWhiteSpace(username);

        if (!string.IsNullOrWhiteSpace(username))
            UsernameRevealed.Text = username;

        UsernameHidden.IsVisible = true;
        UsernameRevealed.IsVisible = false;
        ToggleUsernameIcon.Glyph = "Eye20";

        LoginMicrosoftBtn.IsVisible = !loggedIn;
        LoginGithubBtn.IsVisible    = !loggedIn;
        LogoutBtn.IsVisible         = loggedIn;

        bool cliInstalled  = Utils.GetConnection(SubathonEventSource.DevTunnels, "Cli").Status;
        bool tunnelRunning = Utils.GetConnection(SubathonEventSource.DevTunnels, "Tunnel").Status;
        StartTunnelBtn.IsEnabled = loggedIn && cliInstalled && !tunnelRunning;
        DeleteTunnelsBtn.IsVisible = loggedIn && !tunnelRunning;
    }

    private void ToggleUsername_Click(object? sender, RoutedEventArgs e)
    {
        bool currentlyRevealed = UsernameRevealed.IsVisible;
        UsernameHidden.IsVisible   = currentlyRevealed;
        UsernameRevealed.IsVisible = !currentlyRevealed;
        ToggleUsernameIcon.Glyph   = currentlyRevealed ? "Eye20" : "EyeOff20";
    }

    private void ApplyTunnelState(bool running, string? url)
    {
        if (url == "(starting...)")
        {
            TunnelStatusText.Text = "Starting...";
            StartTunnelBtn.IsEnabled = false;
            StopTunnelBtn.IsVisible = false;
            StartTunnelBtn.IsVisible = true;
            DeleteTunnelsBtn.IsVisible = false;
            TunnelUrlPanel.IsVisible = false;
            return;
        }

        if (url == "(stopping...)")
        {
            TunnelStatusText.Text = "Stopping...";
            StopTunnelBtn.IsEnabled = false;
            StopTunnelBtn.IsVisible = true;
            StartTunnelBtn.IsVisible = false;
            DeleteTunnelsBtn.IsVisible = false;
            TunnelUrlPanel.IsVisible = false;
            return;
        }

        TunnelStatusText.Text = running ? "Running" : "Stopped";

        StartTunnelBtn.IsVisible = !running;
        StopTunnelBtn.IsVisible = running;

        bool cliInstalled = Utils.GetConnection(SubathonEventSource.DevTunnels, "Cli").Status;
        bool loggedIn = Utils.GetConnection(SubathonEventSource.DevTunnels, "Login").Status;
        StartTunnelBtn.IsEnabled = !running && cliInstalled && loggedIn;
        StopTunnelBtn.IsEnabled = running;
        DeleteTunnelsBtn.IsVisible = !running && loggedIn;

        TunnelUrlPanel.IsVisible = running && !string.IsNullOrWhiteSpace(url);
        if (!string.IsNullOrWhiteSpace(url))
            TunnelUrlBox.Text = url;
    }

    private async void CheckCli_Click(object? sender, RoutedEventArgs e)
    {
        CheckCliBtn.IsEnabled = false;
        try
        {
            CliStatusText.SetDynamicResource(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
            CliStatusText.Text = "Checking...";
            await ServiceManager.DevTunnels.RefreshCliStatusAsync();
        }
        finally
        {
            CheckCliBtn.IsEnabled = true;
        }
    }

    private async void GetCli_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            InstallCliBtn.IsEnabled = false;
            InstallCliBtn.Content = "Installing...";
            bool result = await ServiceManager.DevTunnels.TryInstallAsync();
            if (result)
            {
                InstallCliBtn.Content = "Installed";
                await ServiceManager.DevTunnels.RefreshCliStatusAsync();
                await Task.Delay(2500);
                return;
            }
            InstallCliBtn.IsEnabled = true;
            InstallCliBtn.Content = "Install";
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://learn.microsoft.com/en-us/azure/developer/dev-tunnels/get-started?tabs=windows#install",
                UseShellExecute = true
            });
        }
        catch { /**/ }
    }

    private async void LoginMicrosoft_Click(object? sender, RoutedEventArgs e)
        => await RunLoginAsync(LoginProvider.Microsoft);

    private async void LoginGitHub_Click(object? sender, RoutedEventArgs e)
        => await RunLoginAsync(LoginProvider.GitHub);

    private async Task RunLoginAsync(LoginProvider provider)
    {
        LoginMicrosoftBtn.IsEnabled = false;
        LoginGithubBtn.IsEnabled = false;
        LoginStatusText.Text = "Opening browser...";
        try
        {
            await ServiceManager.DevTunnels.LoginAsync(provider);
        }
        catch (Exception ex)
        {
            LoginStatusText.Text = "Login failed";
            _logger?.LogError(ex, "DevTunnels {Provider} login failed", provider);
        }
        finally
        {
            LoginMicrosoftBtn.IsEnabled = true;
            LoginGithubBtn.IsEnabled = true;
        }
    }

    private async void Logout_Click(object? sender, RoutedEventArgs e)
    {
        LogoutBtn.IsEnabled = false;
        try
        {
            await ServiceManager.DevTunnels.LogoutAsync();
        }
        finally
        {
            LogoutBtn.IsEnabled = true;
        }
    }

    private async void StartTunnel_Click(object? sender, RoutedEventArgs e)
    {
        StartTunnelBtn.IsEnabled = false;
        try
        {
            await ServiceManager.DevTunnels.StartTunnelAsync();
        }
        finally { /**/ }
    }

    private async void StopTunnel_Click(object? sender, RoutedEventArgs e)
    {
        await ServiceManager.DevTunnels.StopTunnelAsync();
    }

    private async void DeleteTunnels_Click(object? sender, RoutedEventArgs e)
    {
        DeleteTunnelsBtn.IsEnabled = false;
        StartTunnelBtn.IsEnabled = false;
        try
        {
            await ServiceManager.DevTunnels.DeleteOldTunnelsAsync();
        }
        finally
        {
            DeleteTunnelsBtn.IsEnabled = true;
            bool cliInstalled = Utils.GetConnection(SubathonEventSource.DevTunnels, "Cli").Status;
            bool loggedIn = Utils.GetConnection(SubathonEventSource.DevTunnels, "Login").Status;
            StartTunnelBtn.IsEnabled = cliInstalled && loggedIn;
        }
    }

    private void RevealUrl_Click(object? sender, RoutedEventArgs e)
    {
        bool reveal = RevealUrlBtn.IsChecked == true;
        TunnelUrlBox.RevealPassword = reveal;
        RevealUrlIcon.Glyph = reveal ? "EyeOff20" : "Eye20";
    }

    private async void CopyTunnelUrl_Click(object? sender, RoutedEventArgs e)
    {
        var url = TunnelUrlBox.Text;
        if (string.IsNullOrWhiteSpace(url)) return;
        var result = await UiHelpers.TrySetClipboardTextAsync(url);
        if (!result) return;
        if (sender is not Button btn) return;
        var original = btn.Content;
        btn.Content = "Copied!";
        await Task.Delay(1500);
        btn.Content = original;
    }
}
