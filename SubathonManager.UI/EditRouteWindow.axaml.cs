using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;
using SubathonManager.Data;
using SubathonManager.Data.Widgets;
using SubathonManager.UI.Views;
using SubathonManager.UI.Services;

namespace SubathonManager.UI;

public partial class EditRouteWindow : Window
{
    public static readonly StyledProperty<bool> ObsConnectedProperty =
        AvaloniaProperty.Register<EditRouteWindow, bool>(nameof(ObsConnected));

    public bool ObsConnected
    {
        get => GetValue(ObsConnectedProperty);
        set => SetValue(ObsConnectedProperty, value);
    }

    public readonly Guid EditorRouteId;
    private Route? _route;
    private readonly ObservableCollection<Widget> _widgets = new();
    private Widget? _selectedWidget;
    private readonly ObservableCollection<CssVariable> _editingCssVars = [];
    private readonly ObservableCollection<JsVariable> _editingJsVars = [];
    private readonly Dictionary<Guid, Border> _widgetCardBorders = new();
    private readonly Dictionary<Guid, List<CssVariable>> _unsavedCssVars = new();
    private readonly Dictionary<Guid, List<JsVariable>> _unsavedJsVars = new();
    private readonly HashSet<Guid> _erroredWidgets = new();
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly ILogger? _logger = AppServices.Provider.GetService<ILogger<EditRouteWindow>>();
    private string _lastFolder = string.Empty;
    private bool _loadedWebView;
    private string _editUrl = string.Empty;
    private double _currentScale = 1;
    private bool _webViewLightBg = true;
    private readonly Timer? _cssLivePreviewTimer;
    private bool _hasPendingCssChanges;
    private bool _hasPendingJsChanges;
    private int _suppressCount;

    [GeneratedRegex(@"^-?[\d.]+")]
    private static partial Regex IsNumberRegex();

    public EditRouteWindow() : this(Guid.Empty) { }

    public EditRouteWindow(Guid routeId)
    {
        Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", "--autoplay-policy=no-user-gesture-required");
        _factory = AppServices.Provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        InitializeComponent();
        EditorRouteId = routeId;
        WidgetsList.ItemsSource = _widgets;
        BrowserEditorButton.IsVisible = OperatingSystem.IsLinux();
        WebViewWarningButton.IsVisible = OperatingSystem.IsLinux();
        UiUtils.UiHelpers.EnableClickAwayUnfocus(this);
        LoadPreviewBgPreference();
        TryEnableEditorFileDrop();

        PreviewWebView.EnvironmentRequested += (_, e) =>
        {
            if (e is WindowsWebView2EnvironmentRequestedEventArgs win)
                win.AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required";
            if (e is LinuxWpeWebViewEnvironmentRequestedEventArgs linux) 
                linux.PreferWebKitGtkInstead = true;
            // if (e is GtkWebViewEnvironmentRequestedEventArgs gtkArgs)
            // {
            //     gtkArgs.EphemeralDataManager = true;
            //     gtkArgs.DisableCache = true;
            //     gtkArgs.ExperimentalOffscreen = true;
            // }
        };

        PreviewWebView.AdapterCreated += (_, _) =>
        {
            PreviewWebView.IsVisible = false;
            PreviewWebView.IsVisible = true;
        };


        PreviewWebView.NavigationCompleted += (_, _) =>
            Dispatcher.UIThread.Post(() =>
            {
                ApplyWebViewZoom(_currentScale);
                ApplyWebViewBackground(_webViewLightBg);
            });
        
        Loaded += EditRouteWindow_Loaded;
        ObsConnected = ServiceManager.OBS.Connected;
        IntegrationEvents.ConnectionUpdated += OnObsConnectionUpdated;
        Closed += (_, _) =>
        {
            IntegrationEvents.ConnectionUpdated -= OnObsConnectionUpdated;
            WidgetEvents.WidgetActionRequested -= OnWidgetActionRequested;
        };

        _cssLivePreviewTimer = new Timer(_ =>
        {
            if (_selectedWidget == null) return;
            OverlayEvents.RaiseWidgetVarsUpdated(_selectedWidget.Id, _editingCssVars, []);
        }, null, Timeout.Infinite, Timeout.Infinite);
    }

    private async void EditRouteWindow_Loaded(object? sender, RoutedEventArgs e)
    {
        try
        {

            // if (OperatingSystem.IsLinux())
            // {
            //     _loadedWebView = false;
            //     PreviewWebView.IsVisible = false;
            //     WebViewFallbackPanel.IsVisible = true;
            //     ConfigureFallbackForPlatform();
            //     await LoadRouteAsync();
            // }
            // else
            // {
                _loadedWebView = true;
                await LoadRouteAsync();
            // }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load overlay editor. The embedded WebView is not available");
            _loadedWebView = false;
            PreviewWebView.IsVisible = false;
            WebViewFallbackPanel.IsVisible = true;
            ConfigureFallbackForPlatform();
            await LoadRouteAsync();
        }
        finally
        {
            WidgetEvents.WidgetPositionUpdated += OnWidgetPositionUpdated;
            WidgetEvents.WidgetScaleUpdated += OnWidgetScaleUpdated;
            WidgetEvents.WidgetSizeUpdated += OnWidgetSizeUpdated;
            WidgetEvents.SelectEditorWidget += SelectWidgetFromEvent;
            WidgetEvents.WidgetActionRequested += OnWidgetActionRequested;
        }
    }

    private void OnWidgetPositionUpdated(Widget updatedWidget)
    {
        if (_selectedWidget == null || _selectedWidget.Id != updatedWidget.Id) return;
        Dispatcher.UIThread.Post(() =>
        {
            _selectedWidget.X = updatedWidget.X;
            _selectedWidget.Y = updatedWidget.Y;
            if (WidgetXBox.Text != $"{updatedWidget.X}") WidgetXBox.Text = $"{updatedWidget.X}";
            if (WidgetYBox.Text != $"{updatedWidget.Y}") WidgetYBox.Text = $"{updatedWidget.Y}";
        });
    }

    private void OnWidgetScaleUpdated(Widget updatedWidget)
    {
        if (_selectedWidget == null || _selectedWidget.Id != updatedWidget.Id) return;
        Dispatcher.UIThread.Post(() =>
        {
            _selectedWidget.ScaleX = updatedWidget.ScaleX;
            _selectedWidget.ScaleY = updatedWidget.ScaleY;
            if (WidgetScaleXBox.Text != $"{updatedWidget.ScaleX}") WidgetScaleXBox.Text = $"{updatedWidget.ScaleX}";
            if (WidgetScaleYBox.Text != $"{updatedWidget.ScaleY}") WidgetScaleYBox.Text = $"{updatedWidget.ScaleY}";
        });
    }

    private void OnWidgetSizeUpdated(Widget updatedWidget)
    {
        if (_selectedWidget == null || _selectedWidget.Id != updatedWidget.Id) return;
        Dispatcher.UIThread.Post(() =>
        {
            _selectedWidget.X = updatedWidget.X;
            _selectedWidget.Y = updatedWidget.Y;
            _selectedWidget.Width = updatedWidget.Width;
            _selectedWidget.Height = updatedWidget.Height;
            if (WidgetXBox.Text != $"{updatedWidget.X}") WidgetXBox.Text = $"{updatedWidget.X}";
            if (WidgetYBox.Text != $"{updatedWidget.Y}") WidgetYBox.Text = $"{updatedWidget.Y}";
            if (WidgetWidthBox.Text != $"{updatedWidget.Width}") WidgetWidthBox.Text = $"{updatedWidget.Width}";
            if (WidgetHeightBox.Text != $"{updatedWidget.Height}") WidgetHeightBox.Text = $"{updatedWidget.Height}";
        });
    }

    private async Task LoadRouteAsync()
    {
        WidgetPackPaths.InvalidateVersionCache();
        await WidgetCatalog.LoadIndexAsync(_factory);

        await using var db = await _factory.CreateDbContextAsync();
        _route = await db.Routes
            .Include(r => r.Widgets).ThenInclude(w => w.CssVariables)
            .Include(r => r.Widgets).ThenInclude(w => w.JsVariables)
            .FirstOrDefaultAsync(r => r.Id == EditorRouteId);

        if (_route == null)
        {
            Close();
            return;
        }

        if (RouteNameBox.Text != _route.Name) RouteNameBox.Text = _route.Name;
        if (RouteWidthBox.Text != _route.Width.ToString()) RouteWidthBox.Text = _route.Width.ToString();
        if (RouteHeightBox.Text != _route.Height.ToString()) RouteHeightBox.Text = _route.Height.ToString();

        _widgets.Clear();
        _erroredWidgets.Clear();
        var sorted = _route.Widgets.OrderByDescending(w => w.Z).ToList();

        int index = sorted.Count;
        bool hasUpdatedZ = false;
        var widgetHelper = new WidgetEntityHelper(_factory, null);
        foreach (var w in sorted)
        {
            if (w.Z != index)
            {
                hasUpdatedZ = true;
                w.Z = index;
            }
            index -= 1;
            if (!WidgetFiles.Current.Exists(w.HtmlPath))
            {
                _erroredWidgets.Add(w.Id);
                _logger?.LogWarning("Widget {Name} ({Id}) file not found: {Path}", w.Name, w.Id, w.HtmlPath);
            }
            else if (w.Type == WidgetType.Html)
            {
                widgetHelper.SyncCssVariables(w);
                widgetHelper.SyncJsVariables(w);
            }
            await db.Entry(w).ReloadAsync();
            await db.Entry(w).Collection(x => x.CssVariables).LoadAsync();
            await db.Entry(w).Collection(x => x.JsVariables).LoadAsync();
            _widgets.Add(w);
        }

        if (hasUpdatedZ) await db.SaveChangesAsync();

        try
        {
            var config = AppServices.Provider.GetRequiredService<IConfig>();
            _editUrl = _route.GetRouteUrl(config, true);
            if (_loadedWebView)
                PreviewWebView.Source = new Uri(_editUrl);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "WebView failed to load: {Message}", ex.Message);
        }
        UpdateWebViewScale();
    }


    private Widget? GetWidgetFromSender(object? sender)
        => sender is Control { DataContext: Widget wi } ? wi : null;

    private void SelectWidgetFromEvent(Guid widgetId)
    {
        Dispatcher.UIThread.Post(async void () =>
        {
            if (widgetId == _selectedWidget?.Id && WidgetEditPanel.IsVisible) return;

            StashCurrentWidgetEdits();

            await using var db = await _factory.CreateDbContextAsync();
            var widget = await db.Widgets.Include(wX => wX.JsVariables)
                .Include(wX => wX.CssVariables).FirstOrDefaultAsync(wX => wX.Id == widgetId);

            PopulateWidgetEditor(widget);
        });
    }

    private void PopulateWidgetEditor(Widget? widget)
    {
        CssVarsList.ItemsSource = null;
        JsVarsList.ItemsSource = null;
        JsVarFontList.ItemsSource = null;
        StashCurrentWidgetEdits();
        UnsubscribeCssVarChanges();
        _editingCssVars.Clear();
        _editingJsVars.Clear();
        UpdateSaveButtonBorder(SaveButtonBorder, false);
        _hasPendingCssChanges = false;
        if (widget == null)
        {
            WidgetEditPanel.IsVisible = false;
            EmptyEditorPanel.IsVisible = true;
            _selectedWidget = null;
            return;
        }

        if (_erroredWidgets.Contains(widget.Id)) return;

        if (!widget.Type.IsAsset())
        {
            var widgetHelper = new WidgetEntityHelper(_factory, null);
            widgetHelper.SyncCssVariables(widget);
            widgetHelper.SyncJsVariables(widget);
        }

        using var db = _factory.CreateDbContext();
        _selectedWidget = db.Widgets.Include(wX => wX.CssVariables)
            .Include(wX => wX.JsVariables)
            .FirstOrDefault(wX => wX.Id == widget.Id);
        if (_selectedWidget == null) return;
        widget = _selectedWidget;

        WidgetEditPanel.IsVisible = true;
        EmptyEditorPanel.IsVisible = false;
        
        _suppressCount++;

        bool isAsset = widget.Type.IsAsset();
        bool showVars = !isAsset;
        WidgetVarsSeparator.IsVisible = showVars;
        WidgetVarsScrollViewer.IsVisible = showVars;
        ReloadVarsButton.IsVisible = showVars;
        ResetVarsButton.IsVisible = showVars;
        AssetFilePanel.IsVisible = isAsset;
        if (isAsset) AssetFilePathText.Text = widget.HtmlPath;

        if (WidgetNameBox.Text != widget.Name) WidgetNameBox.Text = widget.Name;
        if (WidgetWidthBox.Text != widget.Width.ToString()) WidgetWidthBox.Text = widget.Width.ToString();
        if (WidgetHeightBox.Text != widget.Height.ToString()) WidgetHeightBox.Text = widget.Height.ToString();
        if (WidgetXBox.Text != $"{widget.X}") WidgetXBox.Text = $"{widget.X}";
        if (WidgetYBox.Text != $"{widget.Y}") WidgetYBox.Text = $"{widget.Y}";
        if (widget.ScaleX == 0) widget.ScaleX = 1;
        if (widget.ScaleY == 0) widget.ScaleY = 1;
        if (WidgetScaleXBox.Text != $"{widget.ScaleX}") WidgetScaleXBox.Text = $"{widget.ScaleX}";
        if (WidgetScaleYBox.Text != $"{widget.ScaleY}") WidgetScaleYBox.Text = $"{widget.ScaleY}";

        if (string.IsNullOrWhiteSpace(widget.DocsUrl) || isAsset)
        {
            DocsLinkBtn.IsVisible = false;
            WidgetNameBox.Width = 355;
        }
        else
        {
            DocsLinkBtn.IsVisible = true;
            WidgetNameBox.Width = 315;
        }

        foreach (var vr in widget.CssVariables) _editingCssVars.Add(vr);
        CssVarsList.ItemsSource = _editingCssVars;
        if (_unsavedCssVars.TryGetValue(widget.Id, out var stashed))
        {
            foreach (var stashedVar in stashed)
            {
                var live = _editingCssVars.FirstOrDefault(vr => vr.Name == stashedVar.Name);
                if (live != null) live.Value = stashedVar.Value;
            }
            _hasPendingCssChanges = true;
        }
        SubscribeCssVarChanges();
        PopulateJsVars();
        bool hasStash = _unsavedCssVars.ContainsKey(widget.Id) || _unsavedJsVars.ContainsKey(widget.Id);
        _hasPendingCssChanges = _unsavedCssVars.ContainsKey(widget.Id);
        _hasPendingJsChanges = _unsavedJsVars.ContainsKey(widget.Id);
        bool realDirty = hasStash || _hasPendingCssChanges || _hasPendingJsChanges;

        Dispatcher.UIThread.Post(() =>
        {
            _suppressCount--;
            UiUtils.UiHelpers.UpdateButtonPendingBorder(SaveButtonBorder, realDirty);
        }, DispatcherPriority.Background);
    }

    private void PopulateJsVars()
    {
        _hasPendingJsChanges = false;
        if (_selectedWidget == null) return;
        _editingJsVars.Clear();
        JsVarsList.ItemsSource = _selectedWidget.JsVariables.Where(x => !x.Type.IsFontVariable()).ToList();
        JsVarFontList.ItemsSource = _selectedWidget.JsVariables.Where(x => x.Type.IsFontVariable()).ToList();

        foreach (var vr in _selectedWidget.JsVariables) _editingJsVars.Add(vr);
        bool hasJsStash = _unsavedJsVars.TryGetValue(_selectedWidget.Id, out var stashedJs);
        if (hasJsStash)
        {
            foreach (var stashedVar in stashedJs!)
            {
                var live = _editingJsVars.FirstOrDefault(vr => vr.Name == stashedVar.Name);
                if (live != null) live.Value = stashedVar.Value;
            }
        }

        _hasPendingJsChanges = hasJsStash;
    }

    private async Task<string> SelectFileVarPathDialog(WidgetVariableType type)
    {
        try
        {
            if (type == WidgetVariableType.FolderPath)
            {
                var folders = await StorageProvider.OpenFolderPickerAsync(new global::Avalonia.Platform.Storage.FolderPickerOpenOptions
                {
                    Title = "Select Folder",
                    AllowMultiple = false
                });
                return folders.FirstOrDefault()?.Path.LocalPath ?? string.Empty;
            }

            var patterns = FileVarPatterns(type);

            var files = await StorageProvider.OpenFilePickerAsync(new global::Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = "Select file",
                AllowMultiple = false,
                FileTypeFilter = new[] { new global::Avalonia.Platform.Storage.FilePickerFileType(type.ToString()) { Patterns = patterns } }
            });
            return files.FirstOrDefault()?.Path.LocalPath ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to parse filepath");
            return string.Empty;
        }
    }

    private static string[] FileVarPatterns(WidgetVariableType type) => type switch
    {
        WidgetVariableType.ImageFile => ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp", "*.avif", "*.bmp", "*.svg", "*.ico"],
        WidgetVariableType.SoundFile => ["*.wav", "*.mp3", "*.ogg", "*.oga", "*.opus", "*.m4a"],
        WidgetVariableType.VideoFile => ["*.mp4", "*.m4v", "*.webm", "*.ogm", "*.mkv", "*.mov"],
        _ => ["*.*"]
    };

    private static bool FileVarAccepts(WidgetVariableType type, string path)
    {
        if (type == WidgetVariableType.FolderPath) return Directory.Exists(path);
        if (!File.Exists(path)) return false;

        var patterns = FileVarPatterns(type);
        if (patterns.Contains("*.*")) return true;

        string ext = Path.GetExtension(path);
        return patterns.Any(p => string.Equals(p[1..], ext, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<CheckBox> GetAllCheckBoxes(Panel panel)
    {
        foreach (var child in panel.Children)
        {
            if (child is CheckBox cb)
                yield return cb;
            else if (child is Expander { Content: Panel innerPanel })
                foreach (var nested in GetAllCheckBoxes(innerPanel))
                    yield return nested;
            else if (child is Panel childPanel)
                foreach (var nested in GetAllCheckBoxes(childPanel))
                    yield return nested;
        }
    }

    private void UpdateEventListValues(JsVariable variable, Panel container)
    {
        var selected = GetAllCheckBoxes(container)
            .Where(c => c.IsChecked == true)
            .Select(c => ((TextBlock)c.Content!).Tag)
            .ToList();
        variable.Value = string.Join(',', selected);
    }

    private async Task SwapWidgetZAsync(Widget a, Widget b)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var wa = await db.Widgets.Include(w => w.CssVariables)
            .Include(w => w.JsVariables).FirstOrDefaultAsync(w => w.Id == a.Id);
        var wb = await db.Widgets.Include(w => w.CssVariables)
            .Include(w => w.JsVariables).FirstOrDefaultAsync(w => w.Id == b.Id);
        if (wa == null || wb == null) return;

        (wa.Z, wb.Z) = (wb.Z, wa.Z);

        await db.SaveChangesAsync();

        _widgets[_widgets.IndexOf(a)] = wa;
        _widgets[_widgets.IndexOf(b)] = wb;
        RefreshWebView();
    }

    private async Task RefreshWidgetZIndicesAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        int start = _widgets.Count;
        for (int i = 0; i < _widgets.Count; i++)
        {
            var wi = _widgets[i];
            var w = await db.Widgets.FirstOrDefaultAsync(x => x.Id == wi.Id);
            w?.Z = start - i;
        }
        await db.SaveChangesAsync();
        await LoadRouteAsync();
    }

    private void RefreshWebView()
    {
        if (!_loadedWebView || string.IsNullOrEmpty(_editUrl)) return;
        try
        {
            var sep = _editUrl.Contains('?') ? "&" : "?";
            PreviewWebView.Source = new Uri($"{_editUrl}{sep}_ts={DateTime.Now.Ticks}");
        }
        catch { /**/ }
    }
    
    private async void BrowseWidgetsButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_route == null) return;

        var dialog = new WidgetBrowserDialog(async entry =>
        {
            string file = WidgetCatalog.ToAbsolutePath(entry.PackPath);
            return File.Exists(file) && await AddWidgetPackInPlaceAsync(file);
        });

        await dialog.ShowDialog(this);
    }

    public async Task<bool> AddWidgetPackInPlaceAsync(string packFile)
    {
        if (_route == null) return false;

        try
        {
            var mounted = WidgetPackInstaller.MountInPlace(packFile);
            if (mounted == null)
            {
                _logger?.LogError("Could not read widget package {Path}", packFile);
                return false;
            }

            await using var db = await _factory.CreateDbContextAsync();
            var helper = new WidgetEntityHelper(_factory, null);

            var manifest = mounted.Manifest;
            await ImportSingleWidgetAsync(mounted.HtmlPath, db, helper,
                manifest.Name, manifest.ScaleX, manifest.ScaleY);

            foreach (var existing in _widgets.ToList())
                ReseatWidgetCard(existing);

            await RefreshWidgetZIndicesAsync();
            OverlayEvents.RaiseOverlayRefreshRequested(_route.Id);
            RefreshWebView();

            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to add widget package in place {Path}", packFile);
            return false;
        }
    }

    public async Task<bool> AddWidgetPackAsync(string packPath)
    {
        if (_route == null) return false;

        try
        {
            await using var db = await _factory.CreateDbContextAsync();
            var helper = new WidgetEntityHelper(_factory, null);

            bool added = packPath.EndsWith(WidgetCollectionInstaller.CollectionExtension, StringComparison.OrdinalIgnoreCase)
                ? await ImportWidgetCollectionAsync(packPath, db, helper)
                : await ImportWidgetPackAsync(packPath, db, helper);

            foreach (var existing in _widgets.ToList())
                ReseatWidgetCard(existing);

            await RefreshWidgetZIndicesAsync();
            OverlayEvents.RaiseOverlayRefreshRequested(_route.Id);
            RefreshWebView();

            return added;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to add widget package {Path}", packPath);
            return false;
        }
    }

    private async Task<bool> ImportWidgetCollectionAsync(string smwcPath, AppDbContext db, WidgetEntityHelper helper)
    {
        var collection = WidgetCollectionInstaller.InstallAll(smwcPath);
        if (collection == null)
        {
            _logger?.LogError("Could not read widget collection {Path}", smwcPath);
            return false;
        }

        if (collection.Failed > 0)
            _logger?.LogWarning("Skipped {Count} unreadable package(s) in {Path}", collection.Failed, smwcPath);

        foreach (var pack in collection.Packs)
        {
            var manifest = pack.Manifest;
            await ImportSingleWidgetAsync(pack.HtmlPath, db, helper, manifest.Name, manifest.ScaleX, manifest.ScaleY);
        }

        return collection.Packs.Count > 0;
    }

    private async Task<bool> ImportWidgetPackAsync(string smwPath, AppDbContext db, WidgetEntityHelper helper)
    {
        var installed = WidgetPackInstaller.Install(smwPath);
        if (installed == null)
        {
            _logger?.LogError("Could not read widget package {Path}", smwPath);
            return false;
        }

        var manifest = installed.Manifest;
        await ImportSingleWidgetAsync(installed.HtmlPath, db, helper, manifest.Name, manifest.ScaleX, manifest.ScaleY);
        return true;
    }

    private async Task ImportSingleWidgetAsync(string path, AppDbContext db, WidgetEntityHelper helper,
        string? displayName = null, float scaleX = 1f, float scaleY = 1f)
    {
        var widgetType = WidgetTypeHelper.DetectFromPath(path);
        var newWidget = new Widget(
            string.IsNullOrWhiteSpace(displayName) ? Path.GetFileNameWithoutExtension(path) : displayName, path)
        {
            Type = widgetType,
            RouteId = _route!.Id,
            X = 0,
            Y = 0,
            ScaleX = scaleX > 0 ? scaleX : 1f,
            ScaleY = scaleY > 0 ? scaleY : 1f,
            Z = _widgets.Count > 0 ? _widgets.Max(x => x.Z) + 1 : 1
        };

        if (widgetType == WidgetType.Html)
        {
            var metadata = await helper.ExtractWidgetMetadata(path);
            newWidget.Width = metadata.Width > 0 ? metadata.Width : 400;
            newWidget.Height = metadata.Height > 0 ? metadata.Height : 400;
            newWidget.DocsUrl = metadata.Url;

            db.Widgets.Add(newWidget);
            await db.SaveChangesAsync();

            (List<JsVariable> jsVars, _, _) = helper.LoadNewJsVariables(newWidget, metadata);

            var allVarTypes = jsVars.Select(j => j.Type).Distinct().ToList();
            var missingFontTypes = WidgetVariableTypeHelper.FontVariables.ToList().Where(x => !allVarTypes.Contains(x)).ToList();
            foreach (var fontVar in missingFontTypes)
            {
                jsVars.Add(new JsVariable
                {
                    WidgetId = newWidget.Id,
                    Type = fontVar,
                    Name = $"{fontVar}s",
                    Description = $"Custom font names to include from {fontVar}s, comma separated",
                    Value = string.Empty
                });
            }

            if (jsVars.Count > 0)
            {
                newWidget.JsVariables = jsVars;
                db.JsVariables.AddRange(jsVars);
            }

            await db.SaveChangesAsync();

            newWidget.ScanCssVariables();
            db.CssVariables.AddRange(newWidget.CssVariables);
            await db.SaveChangesAsync();
        }
        else
        {
            if (widgetType == WidgetType.Video)
            {
                newWidget.Width = 1280;
                newWidget.Height = 720;
            }
            else
            {
                var (naturalW, naturalH) = DetectImageDimensions(path);
                const int anchorHeight = 400;
                float factor = naturalH > 0 ? anchorHeight / (float)naturalH : 1f;
                newWidget.Width = (int)Math.Round(naturalW * factor);
                newWidget.Height = anchorHeight;
            }
            db.Widgets.Add(newWidget);
            await db.SaveChangesAsync();
        }

        _route.Widgets.Add(newWidget);
        _widgets.Insert(0, newWidget);
    }

    private static (int width, int height) DetectImageDimensions(string path)
    {
        try
        {
            using var bmp = new Bitmap(path);
            return (bmp.PixelSize.Width > 0 ? bmp.PixelSize.Width : 400,
                    bmp.PixelSize.Height > 0 ? bmp.PixelSize.Height : 400);
        }
        catch
        {
            return (400, 400);
        }
    }

    private string? _webViewRequirementUrl;
    
    private void ConfigureFallbackForPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            WebViewFallbackText.Text = "The embedded WebView2 runtime isn't installed on your system.";
            WebViewRequirementHint.Text = "Install the Microsoft Edge WebView2 Runtime, then reopen this editor.";
            _webViewRequirementUrl = "https://developer.microsoft.com/microsoft-edge/webview2/";
            InstallWebViewButton.Content = "Get WebView2 Runtime";
            WebViewRequirementHint.IsVisible = true;
            InstallWebViewButton.IsVisible = true;
        }
        else if (OperatingSystem.IsLinux())
        {
            WebViewFallbackText.Text = "The embedded WebView needs WebKitGTK or WPEWebKit, but may still not be supported.";
            WebViewRequirementHint.Text =
                "Use \"Browser Editor\" button " +
                "to edit this overlay in your web browser.";
            _webViewRequirementUrl = "https://github.com/AvaloniaUI/Avalonia.Controls.WebView/pull/38";
            InstallWebViewButton.Content = "Track upstream fix/problem";
            WebViewRequirementHint.IsVisible = true;
            InstallWebViewButton.IsVisible = true;
        }
        else
        {
            WebViewFallbackText.Text = "The embedded WebView couldn't be initialized on this system.";
            WebViewRequirementHint.IsVisible = false;
            InstallWebViewButton.IsVisible = false;
        }
    }

    private void OpenWebViewRequirement_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_webViewRequirementUrl)) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = _webViewRequirementUrl, UseShellExecute = true });
        }
        catch { /**/ }
    }

    private void UpdateWebViewScale()
    {
        if (PreviewWebView == null || _route == null || WebViewContainer == null) return;
        if (WebViewContainer.Bounds.Width <= 0 || WebViewContainer.Bounds.Height <= 0) return;

        double scaleX = WebViewContainer.Bounds.Width / _route.Width;
        double scaleY = WebViewContainer.Bounds.Height / _route.Height;
        double scale = Math.Min(scaleX, scaleY);
        if (scale <= 0) return;

        _currentScale = scale;
        PreviewWebView.Height = _route.Height * scale;
        PreviewWebView.Width = _route.Width * scale;
        ApplyWebViewZoom(scale);
    }

    private async void ApplyWebViewZoom(double scale)
    {
        if (!_loadedWebView) return;
        try
        {
            var s = scale.ToString(CultureInfo.InvariantCulture);
            await PreviewWebView.InvokeScript(
                $"(function(){{var z={s};if(window.__setPreviewZoom){{" +
                $"document.documentElement.style.zoom='';window.__setPreviewZoom(z);}}" +
                $"else{{document.documentElement.style.zoom=z;window.__previewZoom=z;}}}})();");
        }
        catch { /**/ }
    }

    private async void ApplyWebViewBackground(bool light)
    {
        if (!_loadedWebView) return;
        var color = light ? "#ffffff" : "#1e1e1e";
        try
        {
            await PreviewWebView.InvokeScript($"document.documentElement.style.background='{color}';");
        }
        catch { /**/ }
    }

    private void WebViewBgToggle_Click(object? sender, RoutedEventArgs e)
    {
        _webViewLightBg = WebViewBgToggle.IsChecked != true;
        ApplyWebViewBgToggle();
        _ = StateValueHelper.SetAsync(_factory, StateKeys.EditorPreviewLightBg, _webViewLightBg);
    }

    private void LoadPreviewBgPreference()
    {
        try
        {
            using var db = _factory.CreateDbContext();
            _webViewLightBg = StateValueHelper.Get(db, StateKeys.EditorPreviewLightBg, true);
            ApplyWebViewBgToggle();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not read the editor preview background preference");
        }
    }

    private void ApplyWebViewBgToggle()
    {
        WebViewBgToggle.IsChecked = !_webViewLightBg;
        WebViewBgToggle.Content = _webViewLightBg ? "Light" : "Dark";
        ApplyWebViewBackground(_webViewLightBg);
    }


    protected override void OnClosed(EventArgs e)
    {
        WidgetEvents.WidgetPositionUpdated -= OnWidgetPositionUpdated;
        WidgetEvents.WidgetScaleUpdated -= OnWidgetScaleUpdated;
        WidgetEvents.SelectEditorWidget -= SelectWidgetFromEvent;
        WidgetEvents.WidgetSizeUpdated -= OnWidgetSizeUpdated;
        Loaded -= EditRouteWindow_Loaded;

        if (_loadedWebView)
        {
            try { PreviewWebView.Source = new Uri("about:blank"); }
            catch { /**/ }
        }

        _cssLivePreviewTimer?.Dispose();
        UnsubscribeCssVarChanges();
        base.OnClosed(e);
    }

    private void UpdateSaveButtonBorder(Border border, bool hasPendingChanges)
        => Dispatcher.UIThread.Post(() => UiUtils.UiHelpers.UpdateButtonPendingBorder(border, hasPendingChanges));

    private void OnObsConnectionUpdated(IntegrationConnection? connection)
    {
        if (connection is not { Source: SubathonEventSource.OBS, Service: "OBS" }) return;
        Dispatcher.UIThread.Post(() => ObsConnected = connection.Status);
    }

    private async void AddToObs_Click(object? sender, RoutedEventArgs e)
    {
        if (_route == null) return;
        try
        {
            var scenes = ServiceManager.OBS.GetScenes();
            string currentScene = ServiceManager.OBS.GetCurrentScene();
            var config = AppServices.Provider.GetRequiredService<IConfig>();
            string url = _route.GetRouteUrl(config);

            var dialog = new ObsAddSourceDialog(_route, url, scenes, currentScene);
            await dialog.ShowDialog(this);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[OBS] Failed to open add source dialog for overlay {Name}", _route.Name);
        }
    }
}
