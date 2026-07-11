using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Objects;
using SubathonManager.Data;
using SubathonManager.Integration;
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
    {
        Dispatcher.InvokeAsync(async () => await RefreshAsync());
    }

    private void OnHelperScriptStatusChanged(bool active)
    {
        if (_lastScriptStatus == active) return;
        Dispatcher.InvokeAsync(async () => await RefreshAsync());
    }

    private void OnConnectionUpdated(IntegrationConnection? connection)
    {
        if (connection is not { Source: SubathonEventSource.OBS }) return;
        Dispatcher.InvokeAsync(async () => await RefreshAsync());
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        _ = RefreshAsync();
    }

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

    private UIElement BuildCard(ObsBrowserSourceCard card, string? overlayName, bool unknownOverlay)
    {
        var border = new Border
        {
            BorderBrush = (Brush)new BrushConverter().ConvertFromString("#333")!,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 12, 8),
            Margin = new Thickness(0, 0, 12, 8)
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var titleGrid = new Grid();
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock
        {
            Text = card.SourceName,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            ToolTip = card.Url
        };
        titleGrid.Children.Add(title);

        var overlayLabel = new TextBlock
        {
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        };
        if (unknownOverlay)
        {
            overlayLabel.Text = "Unknown Overlay";
            overlayLabel.Foreground = Brushes.Orange;
            overlayLabel.ToolTip = "URL points at an overlay that no longer exists in SubathonManager";
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
            Margin = new Thickness(0, 1, 0, 6),
            Foreground = (Brush)FindResource("TextFillColorSecondaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        sceneText.Inlines.Add(new System.Windows.Documents.Run("Scene: "));
        sceneText.Inlines.Add(new System.Windows.Documents.Run(card.ScenePath)
        {
            Foreground = Brushes.CornflowerBlue
        });
        Grid.SetRow(sceneText, 1);
        grid.Children.Add(sceneText);

        var controlsRow = new Grid();
        controlsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        controlsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetRow(controlsRow, 2);

        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        controlsRow.Children.Add(controls);

        var widthBox = new Wpf.Ui.Controls.TextBox
        {
            Text = card.Width.ToString(),
            Width = 62,
            Height = 30,
            FontSize = 12,
            ClearButtonEnabled = false,
            ToolTip = "Width"
        };
        var heightBox = new Wpf.Ui.Controls.TextBox
        {
            Text = card.Height.ToString(),
            Width = 62,
            Height = 30,
            FontSize = 12,
            ClearButtonEnabled = false,
            Margin = new Thickness(4, 0, 0, 0),
            ToolTip = "Height"
        };
        var applySizeBtn = new Wpf.Ui.Controls.Button
        {
            Content = "Set",
            Height = 30,
            Padding = new Thickness(10, 2, 10, 2),
            Margin = new Thickness(6, 0, 0, 0),
            ToolTip = "Apply resolution (not transform) to the browser source"
        };
        applySizeBtn.Click += (_, _) =>
        {
            if (!int.TryParse(widthBox.Text.Trim(), out int w) || w < 1 ||
                !int.TryParse(heightBox.Text.Trim(), out int h) || h < 1) return;
            ServiceManager.OBS.SetBrowserSourceSize(card.SourceName, w, h);
        };

        controls.Children.Add(widthBox);
        controls.Children.Add(new TextBlock
        {
            Text = "x",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0)
        });
        controls.Children.Add(heightBox);
        controls.Children.Add(applySizeBtn);

        bool scriptActive = ServiceManager.OBS.HelperScriptActive;
        var srgbCheck = new CheckBox
        {
            Content = "SRGB Off",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 0, 0),
            IsChecked = card.SrgbOff,
            IsThreeState = false,
            IsEnabled = scriptActive,
            ToolTip = scriptActive
                ? "Fixes dull/grey transparent or glow visuals when checked"
                : "Fixes dull/grey transparent or glow visuals when checked. Requires the SubathonManager helper script to be loaded in OBS (see Settings -> External Software -> OBS)"
        };
        srgbCheck.Checked += async (_, _) =>
            await ServiceManager.OBS.RequestBlendMethodAsync(card.SourceName, card.SceneName, card.SceneItemId, true);
        srgbCheck.Unchecked += async (_, _) =>
            await ServiceManager.OBS.RequestBlendMethodAsync(card.SourceName, card.SceneName, card.SceneItemId, false);
        controls.Children.Add(srgbCheck);

        var visibilityBtn = MakeIconButton(
            card.Visible ? Wpf.Ui.Controls.SymbolRegular.Eye24 : Wpf.Ui.Controls.SymbolRegular.EyeOff24,
            card.Visible ? "Visible" : "Hidden",
            leftMargin: 16);
        visibilityBtn.Tag = card.Visible;
        visibilityBtn.Click += (_, _) =>
        {
            bool newVisible = !(bool)visibilityBtn.Tag;
            ServiceManager.OBS.SetSceneItemVisible(card.SceneName, card.SceneItemId, newVisible);
            visibilityBtn.Tag = newVisible;
            ((Wpf.Ui.Controls.SymbolIcon)visibilityBtn.Content).Symbol =
                newVisible ? Wpf.Ui.Controls.SymbolRegular.Eye24 : Wpf.Ui.Controls.SymbolRegular.EyeOff24;
            visibilityBtn.ToolTip = newVisible ? "Visible" : "Hidden";
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
            var editBtn = MakeIconButton(Wpf.Ui.Controls.SymbolRegular.Edit24,
                "Edit Overlay", leftMargin: 12);
            editBtn.Click += async (_, _) => await OpenOverlayEditorAsync(card.RouteId.Value);
            rightBtns.Children.Add(editBtn);
        }

        var refreshBtn = MakeIconButton(Wpf.Ui.Controls.SymbolRegular.ArrowClockwise24,
            "Refresh source inside OBS", leftMargin: 6);
        refreshBtn.Click += (_, _) => ServiceManager.OBS.RefreshBrowserSource(card.SourceName);
        rightBtns.Children.Add(refreshBtn);

        var deleteBtn = MakeIconButton(Wpf.Ui.Controls.SymbolRegular.Delete24,
            "Remove browser source from OBS", leftMargin: 6, danger: true);
        deleteBtn.Click += async (_, _) => await ConfirmAndDeleteAsync(card);
        rightBtns.Children.Add(deleteBtn);

        controlsRow.Children.Add(rightBtns);

        grid.Children.Add(controlsRow);
        border.Child = grid;
        return border;
    }
    
    private Wpf.Ui.Controls.Button MakeIconButton(Wpf.Ui.Controls.SymbolRegular symbol, string tooltip,
        double leftMargin, bool danger = false)
    {
        var btn = new Wpf.Ui.Controls.Button
        {
            Width = 32,
            Height = 30,
            Padding = new Thickness(2),
            Margin = new Thickness(leftMargin, 0, 0, 0),
            ToolTip = tooltip
        };
        if (danger)
            btn.Appearance = Wpf.Ui.Controls.ControlAppearance.Danger;
        else if (TryFindResource("OpaqueSecondaryButton") is Style style)
            btn.Style = style;
        btn.SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");
        btn.Content = new Wpf.Ui.Controls.SymbolIcon { Symbol = symbol, Width = 16, Height = 16 };
        return btn;
    }

    private async Task OpenOverlayEditorAsync(Guid routeId)
    {
        var factory = AppServices.Provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var route = await db.Routes.FirstOrDefaultAsync(r => r.Id == routeId);
        if (route == null) return;

        (Window.GetWindow(this) as MainWindow)?.OpenRouteEditor(route);
    }

    private async Task ConfirmAndDeleteAsync(ObsBrowserSourceCard card)
    {
        var msgBox = new Wpf.Ui.Controls.MessageBox
        {
            Title = "Delete Browser Source",
            Content = $"Delete source '{card.SourceName}' from OBS?\n",
            PrimaryButtonText = "Delete",
            PrimaryButtonAppearance = Wpf.Ui.Controls.ControlAppearance.Danger,
            CloseButtonText = "Cancel",
            Owner = Window.GetWindow(this),
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        if (await msgBox.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary) return;

        ServiceManager.OBS.RemoveBrowserSource(card.SourceName);
    }
}
