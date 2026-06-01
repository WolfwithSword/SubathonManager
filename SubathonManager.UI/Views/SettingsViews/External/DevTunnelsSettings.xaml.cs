using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using DevTunnels.Client.Authentication;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;
using SubathonManager.Data;
using SubathonManager.UI.Services;
using TextBox = System.Windows.Controls.TextBox;

namespace SubathonManager.UI.Views.SettingsViews.External;

public partial class DevTunnelsSettings : SettingsControl
{
    public DevTunnelsSettings()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            IntegrationEvents.ConnectionUpdated += UpdateStatus;
            // Re-read the last-known state from the shared connection dict on every
            // visit (including revisits after switching tabs), so we never show stale
            // status even if DevTunnels events fired while this control was detached.
            UpdateStatus(Utils.GetConnection(SubathonEventSource.DevTunnels, "Cli"));
            UpdateStatus(Utils.GetConnection(SubathonEventSource.DevTunnels, "Login"));
            UpdateStatus(Utils.GetConnection(SubathonEventSource.DevTunnels, "Tunnel"));
        };

        Unloaded += (_, _) =>
        {
            IntegrationEvents.ConnectionUpdated -= UpdateStatus;
        };
    }

    // ISettingsControl

    internal override void UpdateStatus(IntegrationConnection? connection)
    {
        if (connection is not { Source: SubathonEventSource.DevTunnels }) return;

        Dispatcher.Invoke(() =>
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

    // State helpers

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
            InstallCliBtn.Visibility = Visibility.Collapsed;
        }
        else
        {
            Brush brush = Brushes.Gray;
            if (Application.Current.Resources.Contains("TextFillColorPrimaryBrush"))
            {
                // ReSharper disable once NullableWarningSuppressionIsUsed
                brush = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"]!;
            }
            CliStatusText.Foreground = brush;
            InstallCliBtn.IsEnabled = true;
            InstallCliBtn.Visibility = Visibility.Visible;
            InstallCliBtn.Content = "Install";
        }

        LoginButtonsPanel.IsEnabled = installed;

        // Recompute Start button enabled state from stored connection dict to avoid
        // cross-referencing service properties that may lag behind event state.
        bool loggedIn = Utils.GetConnection(SubathonEventSource.DevTunnels, "Login").Status;
        bool tunnelRunning = Utils.GetConnection(SubathonEventSource.DevTunnels, "Tunnel").Status;
        StartTunnelBtn.IsEnabled = installed && loggedIn && !tunnelRunning;
    }

    private void ApplyLoginState(bool loggedIn, string? username)
    {
        LoginStatusText.Text = loggedIn ? "Logged in:" : "Not logged in";

        UsernamePanel.Visibility = loggedIn && !string.IsNullOrWhiteSpace(username)
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (!string.IsNullOrWhiteSpace(username))
            UsernameRevealed.Text = username;

        // Reset to hidden state whenever login state changes
        UsernameHidden.Visibility = Visibility.Visible;
        UsernameRevealed.Visibility = Visibility.Collapsed;

        LoginMicrosoftBtn.Visibility = loggedIn ? Visibility.Collapsed : Visibility.Visible;
        LoginGithubBtn.Visibility    = loggedIn ? Visibility.Collapsed : Visibility.Visible;
        LogoutBtn.Visibility         = loggedIn ? Visibility.Visible   : Visibility.Collapsed;

        bool cliInstalled  = Utils.GetConnection(SubathonEventSource.DevTunnels, "Cli").Status;
        bool tunnelRunning = Utils.GetConnection(SubathonEventSource.DevTunnels, "Tunnel").Status;
        StartTunnelBtn.IsEnabled = loggedIn && cliInstalled && !tunnelRunning;
    }

    private void ToggleUsername_Click(object sender, RoutedEventArgs e)
    {
        bool currentlyRevealed = UsernameRevealed.Visibility == Visibility.Visible;
        UsernameHidden.Visibility   = currentlyRevealed ? Visibility.Visible    : Visibility.Collapsed;
        UsernameRevealed.Visibility = currentlyRevealed ? Visibility.Collapsed  : Visibility.Visible;
        ToggleUsernameIcon.Symbol   = currentlyRevealed ? Wpf.Ui.Controls.SymbolRegular.Eye24 
            : Wpf.Ui.Controls.SymbolRegular.EyeOff24;
    }

    private void ApplyTunnelState(bool running, string? url)
    {
        if (url == "(starting…)")
        {
            TunnelStatusText.Text = "Starting…";
            StartTunnelBtn.IsEnabled = false;
            StopTunnelBtn.Visibility = Visibility.Collapsed;
            StartTunnelBtn.Visibility = Visibility.Visible;
            TunnelUrlPanel.Visibility = Visibility.Collapsed;
            return;
        }

        if (url == "(stopping…)")
        {
            TunnelStatusText.Text = "Stopping…";
            StopTunnelBtn.IsEnabled = false;
            StopTunnelBtn.Visibility = Visibility.Visible;
            StartTunnelBtn.Visibility = Visibility.Collapsed;
            TunnelUrlPanel.Visibility = Visibility.Collapsed;
            return;
        }

        TunnelStatusText.Text = running ? "Running" : "Stopped";

        StartTunnelBtn.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        StopTunnelBtn.Visibility = running ? Visibility.Visible : Visibility.Collapsed;

        bool cliInstalled = Utils.GetConnection(SubathonEventSource.DevTunnels, "Cli").Status;
        bool loggedIn = Utils.GetConnection(SubathonEventSource.DevTunnels, "Login").Status;
        StartTunnelBtn.IsEnabled = !running && cliInstalled && loggedIn;
        StopTunnelBtn.IsEnabled = running;

        TunnelUrlPanel.Visibility = running && !string.IsNullOrWhiteSpace(url) ? Visibility.Visible : Visibility.Collapsed;
        if (!string.IsNullOrWhiteSpace(url))
            TunnelUrlBox.Text = url;
    }

    // Button handlers

    private async void CheckCli_Click(object sender, RoutedEventArgs e)
    {
        CheckCliBtn.IsEnabled = false;
        try
        {
            Brush brush = Brushes.Gray;
            if (Application.Current.Resources.Contains("TextFillColorPrimaryBrush"))
                // ReSharper disable once NullableWarningSuppressionIsUsed
                brush = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"]!;
            CliStatusText.Foreground = brush;
            CliStatusText.Text = "Checking…";
            await ServiceManager.DevTunnels.RefreshCliStatusAsync();
        }
        finally
        {
            CheckCliBtn.IsEnabled = true;
        }
    }

    private async void GetCli_Click(object sender, RoutedEventArgs e)
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

    private async void LoginMicrosoft_Click(object sender, RoutedEventArgs e)
        => await RunLoginAsync(LoginProvider.Microsoft);

    private async void LoginGitHub_Click(object sender, RoutedEventArgs e)
        => await RunLoginAsync(LoginProvider.GitHub);

    private async Task RunLoginAsync(LoginProvider provider)
    {
        LoginMicrosoftBtn.IsEnabled = false;
        LoginGithubBtn.IsEnabled = false;
        LoginStatusText.Text = "Opening browser…";
        try
        {
            await ServiceManager.DevTunnels.LoginAsync(provider);
        }
        finally
        {
            LoginMicrosoftBtn.IsEnabled = true;
            LoginGithubBtn.IsEnabled = true;
        }
    }

    private async void Logout_Click(object sender, RoutedEventArgs e)
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

    private async void StartTunnel_Click(object sender, RoutedEventArgs e)
    {
        StartTunnelBtn.IsEnabled = false;
        try
        {
            await ServiceManager.DevTunnels.StartTunnelAsync();
        }
        finally
        {
            // State restored via connection update broadcast
        }
    }

    private async void StopTunnel_Click(object sender, RoutedEventArgs e)
    {
        await ServiceManager.DevTunnels.StopTunnelAsync();
    }

    private async void CopyTunnelUrl_Click(object sender, RoutedEventArgs e)
    {
        var url = TunnelUrlBox.Password;
        if (string.IsNullOrWhiteSpace(url)) return;
        var result = await UiUtils.UiUtils.TrySetClipboardTextAsync(url);
        if (!result) return;
        var btn = (sender as Wpf.Ui.Controls.Button)!;
        var original = btn.Content;
        btn.Content = "Copied!";
        await Task.Delay(1500);
        btn.Content = original;
    }
}
