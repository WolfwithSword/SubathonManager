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
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;
using SubathonManager.Core.Security;
using SubathonManager.Core.Security.Interfaces;
using SubathonManager.Data;
using SubathonManager.Integration;
using SubathonManager.UI.Services;

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
        Dispatcher.UIThread.Post(() =>
        {
            StatusText.Text = connection.Status ? "Connected" : "Disconnected";
            DisconnectBtn.IsVisible = connection.Status;
        });
    }

    protected internal override void LoadValues(AppDbContext db)
    {
        var secureStorage = AppServices.Provider.GetRequiredService<ISecureStorage>();
        var key = secureStorage.GetOrDefault(StorageKeys.TangiaEventKey, string.Empty) ?? string.Empty;
        SuppressUnsavedChanges(() => EventKeyBox.Text = key);

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
        if (tokensValue != null && double.TryParse(TokensPointsBox.Text, out var points) && !points.Equals(tokensValue.Points))
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

    private async void GetKey_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var secureStorage = AppServices.Provider.GetRequiredService<ISecureStorage>();

            var urlBox = new TextBox
            {
                Width = 320,
                PlaceholderText = "https://overlays.tangia.co/stream-overlay/fullscreen/evt_...",
                Margin = new Thickness(0, 4, 0, 8)
            };
            var linkBtn = new Button
            {
                Content = "Get overlay URL from Tangia settings",
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                Cursor = new Cursor(StandardCursorType.Hand),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            linkBtn.Click += (_, _) =>
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://app.tangia.co/twitch/settings/fullscreen-overlay",
                    UseShellExecute = true
                });

            var panel = new StackPanel { Orientation = Orientation.Vertical };
            panel.Children.Add(new TextBlock { Text = "Overlay URL:" });
            panel.Children.Add(urlBox);
            panel.Children.Add(linkBtn);

            var dialog = new FAContentDialog
            {
                Title = "Enter Tangia Overlay URL",
                PrimaryButtonText = "Confirm",
                CloseButtonText = "Cancel",
                Content = panel
            };

            var result = await dialog.ShowAsync();
            if (result != FAContentDialogResult.Primary) return;

            if (!TangiaService.TryParseEventKey((urlBox.Text ?? "").Trim(), out var eventKey))
            {
                _logger?.LogWarning("[Tangia] Could not extract event key from URL: {Url}", urlBox.Text);
                return;
            }

            secureStorage.Set(StorageKeys.TangiaEventKey, eventKey);
            SuppressUnsavedChanges(() => EventKeyBox.Text = eventKey);

            await ServiceManager.Tangia.StopAsync();
            await ServiceManager.Tangia.StartAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[Tangia] Error setting event key");
        }
    }

    private async void Disconnect_Click(object? sender, RoutedEventArgs e)
    {
        var secureStorage = AppServices.Provider.GetRequiredService<ISecureStorage>();
        await ServiceManager.Tangia.StopAsync();
        secureStorage.Delete(StorageKeys.TangiaEventKey);
        SuppressUnsavedChanges(() => EventKeyBox.Text = string.Empty);
    }

    private void TestTangia_Click(object? sender, RoutedEventArgs e)
    {
        if (!long.TryParse(SimulateTangiaAmt.Text, out var amount)) return;
        TangiaService.SimulateTangiaTokens(amount);
    }
}
