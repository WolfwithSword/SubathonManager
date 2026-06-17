using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;
using SubathonManager.Core.Security;
using SubathonManager.Core.Security.Interfaces;
using SubathonManager.Data;
using SubathonManager.Integration;
using SubathonManager.UI.Services;

// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.UI.Views.SettingsViews.Extensions;

public partial class TangiaSettings : SettingsControl
{
    private readonly ILogger? _logger = AppServices.Provider.GetRequiredService<ILogger<TangiaSettings>>();

    public TangiaSettings()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            IntegrationEvents.ConnectionUpdated += UpdateStatus;
            RegisterUnsavedChangeHandlers();
            UpdateStatus(Utils.GetConnection(SubathonEventSource.Tangia, $"{SubathonEventSource.Tangia}"));
        };
        Unloaded += (_, _) =>
        {
            IntegrationEvents.ConnectionUpdated -= UpdateStatus;
        };
    }

    public override void Init(SettingsView host)
    {
        Host = host;
        UpdateStatus(Utils.GetConnection(SubathonEventSource.Tangia, $"{SubathonEventSource.Tangia}"));
    }

    internal override void UpdateStatus(IntegrationConnection? connection)
    {
        if (connection is not { Source: SubathonEventSource.Tangia }) return;
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = connection.Status ? "Connected" : "Disconnected";
            DisconnectBtn.Visibility = connection.Status ? Visibility.Visible : Visibility.Collapsed;
        });
    }

    protected internal override void LoadValues(AppDbContext db)
    {
        var secureStorage = AppServices.Provider.GetRequiredService<ISecureStorage>();
        var key = secureStorage.GetOrDefault(StorageKeys.TangiaEventKey, string.Empty) ?? string.Empty;
        SuppressUnsavedChanges(() => EventKeyBox.Password = key);

        var tokensValue = db.SubathonValues.FirstOrDefault(sv =>
            sv.EventType == SubathonEventType.TangiaTokens && sv.Meta == "");
        if (tokensValue == null) return;
        SuppressUnsavedChanges(() =>
        {
            TokensSecondsBox.Text = $"{Math.Round(tokensValue.Seconds * 100)}";
            TokensPointsBox.Text = $"{tokensValue.Points}";
        });
    }

    public override bool UpdateValueSettings(AppDbContext db)
    {
        bool hasUpdated = false;

        var tokensValue = db.SubathonValues.FirstOrDefault(sv =>
            sv.EventType == SubathonEventType.TangiaTokens && sv.Meta == "");

        if (tokensValue != null && double.TryParse(TokensSecondsBox.Text, out var seconds) &&
            !seconds.Equals(Math.Round(tokensValue.Seconds * 100)))
        {
            tokensValue.Seconds = seconds / 100.0;
            hasUpdated = true;
        }

        if (tokensValue != null && double.TryParse(TokensPointsBox.Text, out var points) &&
            !points.Equals(tokensValue.Points))
        {
            tokensValue.Points = points;
            hasUpdated = true;
        }

        return hasUpdated;
    }

    public override void UpdateCurrencyBoxes(List<string> currencies, string selected) { }

    public override (string, string, TextBox?, TextBox?) GetValueBoxes(SubathonValue val)
    {
        if (val.EventType != SubathonEventType.TangiaTokens) return ("", "", null, null);
        return ($"{Math.Round(val.Seconds * 100)}", $"{val.Points}", TokensSecondsBox, TokensPointsBox);
    }

    private async void GetKey_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var secureStorage = AppServices.Provider.GetRequiredService<ISecureStorage>();

            var urlLabel = new Wpf.Ui.Controls.TextBlock
            {
                Text = "Overlay URL:",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(16, 4, 8, 0)
            };
            var urlBox = new Wpf.Ui.Controls.TextBox
            {
                Width = 300,
                PlaceholderText = "https://overlays.tangia.co/stream-overlay/fullscreen/evt_...",
                Margin = new Thickness(2, 4, 0, 0)
            };

            var linkText = new TextBlock { Margin = new Thickness(16, 8, 0, 4) };
            var link = new Hyperlink
            {
                NavigateUri = new Uri("https://app.tangia.co/twitch/settings/fullscreen-overlay")
            };
            link.Inlines.Add("Get overlay URL from Tangia settings");
            link.RequestNavigate += (_, args) =>
            {
                Process.Start(new ProcessStartInfo { FileName = args.Uri.ToString(), UseShellExecute = true });
                args.Handled = true;
            };
            linkText.Inlines.Add(link);

            var urlRow = new StackPanel { Orientation = Orientation.Horizontal };
            urlRow.Children.Add(urlLabel);
            urlRow.Children.Add(urlBox);

            var panel = new StackPanel { Orientation = Orientation.Vertical };
            panel.Children.Add(urlRow);
            panel.Children.Add(linkText);

            var msgBox = new Wpf.Ui.Controls.MessageBox
            {
                Title = "Enter Tangia Overlay URL",
                CloseButtonText = "Cancel",
                PrimaryButtonText = "Confirm",
                Content = panel,
                Owner = Application.Current.Windows
                    .OfType<Window>()
                    .FirstOrDefault(w => w.IsActive),
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };

            var result = await msgBox.ShowDialogAsync();
            if (result != Wpf.Ui.Controls.MessageBoxResult.Primary) return;

            if (!TangiaService.TryParseEventKey(urlBox.Text.Trim(), out var eventKey))
            {
                _logger?.LogWarning("[Tangia] Could not extract event key from URL: {Url}", urlBox.Text);
                return;
            }

            secureStorage.Set(StorageKeys.TangiaEventKey, eventKey);
            SuppressUnsavedChanges(() => EventKeyBox.Password = eventKey);

            await ServiceManager.Tangia.StopAsync();
            await ServiceManager.Tangia.StartAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[Tangia] Error setting event key");
        }
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        var secureStorage = AppServices.Provider.GetRequiredService<ISecureStorage>();
        await ServiceManager.Tangia.StopAsync();
        secureStorage.Delete(StorageKeys.TangiaEventKey);
        SuppressUnsavedChanges(() => EventKeyBox.Password = string.Empty);
    }

    private void TestTangia_Click(object sender, RoutedEventArgs e)
    {
        if (!long.TryParse(SimulateTangiaAmt.Text, out var amount)) return;
        TangiaService.SimulateTangiaTokens(amount);
    }
}
