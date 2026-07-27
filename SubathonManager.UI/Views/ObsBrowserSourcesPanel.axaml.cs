using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Objects;
using SubathonManager.Data;
using SubathonManager.Integration;
using SubathonManager.UI.Controls;
using SubathonManager.UI.Services;

namespace SubathonManager.UI.Views;

public partial class ObsBrowserSourcesPanel : UserControl
{
    private bool _refreshing;
    private bool _refreshQueued;
    private bool? _lastScriptStatus;
    private List<ObsBrowserSourceCard> _lastCards = [];

    public ObsBrowserSourcesPanel()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            IntegrationEvents.ConnectionUpdated -= OnConnectionUpdated;
            IntegrationEvents.ConnectionUpdated += OnConnectionUpdated;
            ServiceManager.OBS.HelperScriptStatusChanged -= OnHelperScriptStatusChanged;
            ServiceManager.OBS.HelperScriptStatusChanged += OnHelperScriptStatusChanged;
            ServiceManager.OBS.BrowserSourcesChanged -= OnBrowserSourcesChanged;
            ServiceManager.OBS.BrowserSourcesChanged += OnBrowserSourcesChanged;
            _ = RefreshAsync();
        };
        Unloaded += (_, _) =>
        {
            IntegrationEvents.ConnectionUpdated -= OnConnectionUpdated;
            ServiceManager.OBS.HelperScriptStatusChanged -= OnHelperScriptStatusChanged;
            ServiceManager.OBS.BrowserSourcesChanged -= OnBrowserSourcesChanged;
        };
    }

    private void UpdatePanelStatusText(int count, bool scriptActive)
    {
        PanelStatusText.Text = count == 0
            ? "No overlay browser sources found"
            : scriptActive
                ? $"{count} source(s)"
                : $"{count} source(s) - helper script not loaded, SRGB control unavailable";
    }

    private void OnBrowserSourcesChanged()
        => Dispatcher.UIThread.Post(() => _ = RefreshAsync());

    private void OnHelperScriptStatusChanged(bool active)
    {
        if (_lastScriptStatus == active) return;
        Dispatcher.UIThread.Post(() => _ = RefreshAsync());
    }

    private void OnConnectionUpdated(IntegrationConnection? connection)
    {
        if (connection is not { Source: SubathonEventSource.OBS, Service: "OBS" }) return;
        Dispatcher.UIThread.Post(() => _ = RefreshAsync());
    }

    private void Refresh_Click(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
        => _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_refreshing)
        {
            _refreshQueued = true;
            return;
        }
        _refreshing = true;
        try
        {
            if (!ServiceManager.OBS.Connected)
            {
                CardsStack.Children.Clear();
                _lastCards = [];
                _lastScriptStatus = null;
                PanelStatusText.Text = "OBS not connected";
                return;
            }

            PanelStatusText.Text = "Loading...";

            if (!ServiceManager.OBS.HelperScriptActive)
                ServiceManager.OBS.RecheckHelperScript();

            var config = AppServices.Provider.GetRequiredService<IConfig>();
            var port = config.Get("Server", "Port", "14040") ?? "14040";

            var cards = await Task.Run(() => ServiceManager.OBS.GetOverlayBrowserSourcesAsync(port));

            Dictionary<Guid, string> routeNames;
            var factory = AppServices.Provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
            await using (var db = await factory.CreateDbContextAsync())
            {
                routeNames = await db.Routes.ToDictionaryAsync(r => r.Id, r => r.Name);
            }

            bool scriptActive = ServiceManager.OBS.HelperScriptActive;
            var ordered = cards.OrderBy(c => c.SceneName).ThenBy(c => c.SourceName).ToList();

            if (scriptActive == _lastScriptStatus && ordered.SequenceEqual(_lastCards))
            {
                UpdatePanelStatusText(ordered.Count, scriptActive);
                return;
            }
            _lastScriptStatus = scriptActive;
            _lastCards = ordered;

            CardsStack.Children.Clear();
            foreach (var card in ordered)
            {
                string? overlayName = null;
                bool unknownOverlay = false;
                if (card.RouteId.HasValue)
                {
                    if (routeNames.TryGetValue(card.RouteId.Value, out var name)) overlayName = name;
                    else unknownOverlay = true;
                }
                CardsStack.Children.Add(BuildCard(card, overlayName, unknownOverlay));
            }

            UpdatePanelStatusText(ordered.Count, scriptActive);
        }
        catch (Exception)
        {
            PanelStatusText.Text = "Failed to query OBS";
        }
        finally
        {
            _refreshing = false;
        }

        if (_refreshQueued)
        {
            _refreshQueued = false;
            await RefreshAsync();
        }
    }

    private Control BuildCard(ObsBrowserSourceCard card, string? overlayName, bool unknownOverlay)
    {
        var border = new Border
        {
            BorderBrush = new SolidColorBrush(Color.Parse("#333")),
            BorderThickness = new global::Avalonia.Thickness(1),
            CornerRadius = new global::Avalonia.CornerRadius(6),
            Padding = new global::Avalonia.Thickness(10, 8, 12, 8),
            Margin = new global::Avalonia.Thickness(0, 0, 12, 8)
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        var titleGrid = new Grid();
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        var title = new TextBlock
        {
            Text = card.SourceName,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold
        };
        ToolTip.SetTip(title, card.Url);
        titleGrid.Children.Add(title);

        var overlayLabel = new TextBlock
        {
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new global::Avalonia.Thickness(8, 0, 0, 0)
        };
        if (unknownOverlay)
        {
            overlayLabel.Text = "Unknown Overlay";
            overlayLabel.Foreground = Brushes.Orange;
            ToolTip.SetTip(overlayLabel, "URL points at an overlay that no longer exists in SubathonManager");
        }
        else if (!string.IsNullOrEmpty(overlayName))
        {
            overlayLabel.Text = overlayName;
            overlayLabel.Foreground = Brushes.CornflowerBlue;
        }

        Grid.SetColumn(overlayLabel, 1);
        titleGrid.Children.Add(overlayLabel);
        grid.Children.Add(titleGrid);

        var sceneText = new TextBlock
        {
            FontSize = 11,
            Margin = new global::Avalonia.Thickness(0, 1, 0, 6),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        sceneText.Inlines?.Add(new Run("Scene: ") { Foreground = ThemeBrush("TextFillColorSecondaryBrush") });
        sceneText.Inlines?.Add(new Run(card.ScenePath) { Foreground = ThemeBrush("TextFillColorPrimaryBrush") });
        Grid.SetRow(sceneText, 1);
        grid.Children.Add(sceneText);

        var controlsRow = new Grid();
        controlsRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        controlsRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        Grid.SetRow(controlsRow, 2);

        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        controlsRow.Children.Add(controls);

        var widthBox = new TextBox
        {
            Text = card.Width.ToString(),
            Width = 62,
            Height = 30,
            FontSize = 12,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(widthBox, "Width");
        var heightBox = new TextBox
        {
            Text = card.Height.ToString(),
            Width = 62,
            Height = 30,
            FontSize = 12,
            Margin = new global::Avalonia.Thickness(4, 0, 0, 0),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(heightBox, "Height");
        var applySizeBtn = new Button
        {
            Content = "Set",
            Height = 30,
            Padding = new global::Avalonia.Thickness(10, 2, 10, 2),
            Margin = new global::Avalonia.Thickness(6, 0, 0, 0)
        };
        ToolTip.SetTip(applySizeBtn, "Apply resolution (not transform) to the browser source");
        applySizeBtn.Click += (_, _) =>
        {
            if (!int.TryParse(widthBox.Text?.Trim(), out int w) || w < 1 ||
                !int.TryParse(heightBox.Text?.Trim(), out int h) || h < 1) return;
            ServiceManager.OBS.SetBrowserSourceSize(card.SourceName, w, h);
        };

        controls.Children.Add(widthBox);
        controls.Children.Add(new TextBlock
        {
            Text = "x",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new global::Avalonia.Thickness(4, 0, 0, 0)
        });
        controls.Children.Add(heightBox);
        controls.Children.Add(applySizeBtn);

        bool scriptActive = ServiceManager.OBS.HelperScriptActive;
        var srgbCheck = new CheckBox
        {
            Content = "SRGB Off",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new global::Avalonia.Thickness(16, 0, 0, 0),
            IsChecked = card.SrgbOff,
            IsThreeState = false,
            IsEnabled = scriptActive
        };
        ToolTip.SetTip(srgbCheck, scriptActive
            ? "Fixes dull/grey transparent or glow visuals when checked"
            : "Fixes dull/grey transparent or glow visuals when checked. Requires the SubathonManager helper script to be loaded in OBS (see Settings -> External Software -> OBS)");
        srgbCheck.IsCheckedChanged += async (_, _) =>
            await ServiceManager.OBS.RequestBlendMethodAsync(
                card.SourceName, card.SceneName, card.SceneItemId, srgbCheck.IsChecked ?? false);
        controls.Children.Add(srgbCheck);

        var visibilityBtn = MakeIconButton(card.Visible ? "Eye24" : "EyeOff24",
            card.Visible ? "Visible" : "Hidden", leftMargin: 16);
        bool currentVisible = card.Visible;
        visibilityBtn.Click += (_, _) =>
        {
            bool newVisible = !currentVisible;
            ServiceManager.OBS.SetSceneItemVisible(card.SceneName, card.SceneItemId, newVisible);
            currentVisible = newVisible;
            if (visibilityBtn.Content is SymIcon icon)
            {
                icon.Glyph = newVisible ? "Eye24" : "EyeOff24";
                icon.FontSize = 16;
            }
            ToolTip.SetTip(visibilityBtn, newVisible ? "Visible" : "Hidden");
        };
        controls.Children.Add(visibilityBtn);

        var rightBtns = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        Grid.SetColumn(rightBtns, 1);

        if (!string.IsNullOrEmpty(overlayName) && card.RouteId.HasValue)
        {
            var editBtn = MakeIconButton("Edit24", "Edit Overlay", leftMargin: 12);
            editBtn.Click += async (_, _) => await OpenOverlayEditorAsync(card.RouteId.Value);
            rightBtns.Children.Add(editBtn);
        }

        var refreshBtn = MakeIconButton("ArrowClockwise24", "Refresh source inside OBS", leftMargin: 6);
        refreshBtn.Click += (_, _) => ServiceManager.OBS.RefreshBrowserSource(card.SourceName);
        rightBtns.Children.Add(refreshBtn);

        var deleteBtn = MakeIconButton("Delete24", "Remove browser source from OBS", leftMargin: 6, danger: true);
        deleteBtn.Click += async (_, _) => await ConfirmAndDeleteAsync(card);
        rightBtns.Children.Add(deleteBtn);

        controlsRow.Children.Add(rightBtns);

        grid.Children.Add(controlsRow);
        border.Child = grid;
        return border;
    }

    private Button MakeIconButton(string glyph, string tooltip, double leftMargin, bool danger = false)
    {
        var btn = new Button
        {
            Width = 32,
            Height = 30,
            Padding = new global::Avalonia.Thickness(2),
            Margin = new global::Avalonia.Thickness(leftMargin, 0, 0, 0),
            Content = new SymIcon { Glyph = glyph, FontSize = 16 }
        };
        btn.Classes.Add("iconbtn");
        btn.Classes.Add(danger ? "danger" : "opaquesecondary");
        ToolTip.SetTip(btn, tooltip);
        return btn;
    }

    private async Task OpenOverlayEditorAsync(Guid routeId)
    {
        var factory = AppServices.Provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var route = await db.Routes.FirstOrDefaultAsync(r => r.Id == routeId);
        if (route == null) return;

        (TopLevel.GetTopLevel(this) as MainWindow)?.OpenRouteEditor(route);
    }

    private async Task ConfirmAndDeleteAsync(ObsBrowserSourceCard card)
    {
        var dialog = new FAContentDialog
        {
            Title = "Delete Browser Source",
            Content = $"Delete source '{card.SourceName}' from OBS?\n",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = FAContentDialogButton.Close
        };

        if (await dialog.ShowAsync() != FAContentDialogResult.Primary) return;

        ServiceManager.OBS.RemoveBrowserSource(card.SourceName);
    }

    private IBrush ThemeBrush(string key)
        => this.TryFindResource(key, this.ActualThemeVariant, out var b) && b is IBrush brush
            ? brush
            : Brushes.Gray;
}
