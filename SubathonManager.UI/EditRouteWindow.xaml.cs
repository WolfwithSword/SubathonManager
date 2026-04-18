using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Text.RegularExpressions;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SubathonManager.Core.Events;
using SubathonManager.Core.Models;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Interfaces;
using SubathonManager.Data;
using Wpf.Ui.Controls;

namespace SubathonManager.UI;

public partial class EditRouteWindow
{
    public readonly Guid EditorRouteId;
    private Route? _route;
    private ObservableCollection<Widget> _widgets = new();
    private Widget? _selectedWidget;
    private ObservableCollection<CssVariable> _editingCssVars = new();
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly ILogger? _logger = AppServices.Provider.GetRequiredService<ILogger<EditRouteWindow>>();
    private string _lastFolder = string.Empty;
    private bool _loadedWebView = false;
    private int _suppressCount = 0;
    
    [GeneratedRegex(@"^-?[\d.]+")]
    private static partial Regex IsNumberRegex();
    
    public EditRouteWindow(Guid routeId)
    {
        Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", "--autoplay-policy=no-user-gesture-required");
        _factory = AppServices.Provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        InitializeComponent();
        EditorRouteId = routeId;
        WidgetsList.ItemsSource = _widgets;
        Loaded += EditRouteWindow_Loaded;
    }
    private async void EditRouteWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // we do require webview2 windows runtime to be installed. Native on win11, win10 it might not be
            await PreviewWebView.EnsureCoreWebView2Async();
            PreviewWebView.CoreWebView2.Settings.AreDevToolsEnabled = true; // yeah, handy for technical users
            PreviewWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _loadedWebView = true;
            await LoadRouteAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load overlay editor. WebView2 is not available");
            _loadedWebView = false;
            Dispatcher.Invoke(() => 
            {
                PreviewWebView.Visibility = Visibility.Collapsed;
                WebViewFallbackPanel.Visibility = Visibility.Visible;
            });
            await LoadRouteAsync();
        }
        finally
        {
            WidgetEvents.WidgetPositionUpdated += OnWidgetPositionUpdated;
            WidgetEvents.WidgetScaleUpdated += OnWidgetScaleUpdated;
            WidgetEvents.WidgetSizeUpdated += OnWidgetSizeUpdated;
            WidgetEvents.SelectEditorWidget += SelectWidgetFromEvent;
            if (WebViewContainer != null)
                WebViewContainer.SizeChanged += WebViewContainer_SizeChanged;
        }
    }
    
    private void OnWidgetPositionUpdated(Widget updatedWidget)
    {
        if (_selectedWidget != null && _selectedWidget.Id == updatedWidget.Id)
        {
            Dispatcher.Invoke(() =>
            {
                _selectedWidget.X = updatedWidget.X;
                _selectedWidget.Y = updatedWidget.Y;
                if (WidgetXBox.Text != $"{updatedWidget.X}") WidgetXBox.Text = $"{updatedWidget.X}";
                if (WidgetYBox.Text != $"{updatedWidget.Y}") WidgetYBox.Text = $"{updatedWidget.Y}";
            });
        }

    } 
    
    private void OnWidgetScaleUpdated(Widget updatedWidget)
    {
        if (_selectedWidget != null && _selectedWidget.Id == updatedWidget.Id)
        {
            Dispatcher.Invoke(() =>
            {
                _selectedWidget.ScaleX = updatedWidget.ScaleX;
                _selectedWidget.ScaleY = updatedWidget.ScaleY;
                if (WidgetScaleXBox.Text != $"{updatedWidget.ScaleX}") WidgetScaleXBox.Text = $"{updatedWidget.ScaleX}";
                if (WidgetScaleYBox.Text != $"{updatedWidget.ScaleY}") WidgetScaleYBox.Text = $"{updatedWidget.ScaleY}";
            });
        }
    }
    
    private void OnWidgetSizeUpdated(Widget updatedWidget)
    {
        if (_selectedWidget != null && _selectedWidget.Id == updatedWidget.Id)
        {
            Dispatcher.Invoke(() =>
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
    }
    
    private async Task LoadRouteAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        _route = await db.Routes
            .Include(r => r.Widgets)
                .ThenInclude(w => w.CssVariables)
            .Include(r => r.Widgets)
                .ThenInclude( w => w.JsVariables)
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
        var sorted = _route.Widgets.OrderByDescending(w => w.Z).ToList();

        int index = sorted.Count;
        bool hasUpdatedZ = false;
        WidgetEntityHelper widgetHelper = new WidgetEntityHelper(_factory, null);
        foreach (var w in sorted)
        {
            if (w.Z != index)
            {
                hasUpdatedZ = true; 
                w.Z = index;
            }
            index -= 1;
            widgetHelper.SyncCssVariables(w);
            widgetHelper.SyncJsVariables(w);
            await db.Entry(w).ReloadAsync();
            await db.Entry(w).Collection(x => x.CssVariables).LoadAsync();
            await db.Entry(w).Collection(x => x.JsVariables).LoadAsync();
            _widgets.Add(w);
        }

        if (hasUpdatedZ)
        {
            await db.SaveChangesAsync();
        }

        try
        {
            var config = AppServices.Provider.GetRequiredService<IConfig>();
            PreviewWebView.Source = new Uri(_route.GetRouteUrl(config,true));
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, $"WebView 2 failed to load: {ex.Message}");
        }
        UpdateWebViewScale();
    }
    
    private Widget? GetWidgetFromSender(object? sender)
    {
        if (sender is Wpf.Ui.Controls.Button { DataContext: Widget wi }) return wi;
        return null;
    }

    private void SelectWidgetFromEvent(Guid widgetId)
    {
        Dispatcher.InvokeAsync(async () =>
        {
            if (widgetId == _selectedWidget?.Id && WidgetEditPanel.Visibility == Visibility.Visible) return;
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
        _editingCssVars.Clear();
        UpdateSaveButtonBorder(SaveButtonBorder, false);
        if (widget == null)
        {
            WidgetEditPanel.Visibility = Visibility.Collapsed;
            EmptyEditorPanel.Visibility = Visibility.Visible;
            _selectedWidget = null;
            return;
        }
        
        WidgetEntityHelper widgetHelper = new WidgetEntityHelper(_factory, null);
        widgetHelper.SyncCssVariables(widget);
        widgetHelper.SyncJsVariables(widget);
        
        using var db = _factory.CreateDbContext();
        _selectedWidget = db.Widgets.Include(wX => wX.CssVariables)
            .Include(wX => wX.JsVariables)
            .FirstOrDefault(wX => wX.Id == widget.Id);
        if (_selectedWidget == null) return;
        widget = _selectedWidget;

        WidgetEditPanel.Visibility = Visibility.Visible;
        EmptyEditorPanel.Visibility = Visibility.Collapsed;

        if (WidgetNameBox.Text != widget.Name) WidgetNameBox.Text = widget.Name;
        if (WidgetWidthBox.Text != widget.Width.ToString()) WidgetWidthBox.Text = widget.Width.ToString();
        if (WidgetHeightBox.Text != widget.Height.ToString()) WidgetHeightBox.Text = widget.Height.ToString();
        if (WidgetXBox.Text != $"{widget.X}") WidgetXBox.Text = $"{widget.X}";
        if (WidgetYBox.Text != $"{widget.Y}") WidgetYBox.Text = $"{widget.Y}";
        if (widget.ScaleX == 0) widget.ScaleX = 1;
        if (widget.ScaleY == 0) widget.ScaleY = 1;
        if (WidgetScaleXBox.Text != $"{widget.ScaleX}") WidgetScaleXBox.Text = $"{widget.ScaleX}";
        if (WidgetScaleYBox.Text != $"{widget.ScaleY}") WidgetScaleYBox.Text = $"{widget.ScaleY}";

        if (string.IsNullOrWhiteSpace(widget.DocsUrl))
        {
            DocsLinkBtn.Visibility = Visibility.Hidden;
            WidgetNameBox.Width = 355;
        }
        else
        {
           DocsLinkBtn.Visibility = Visibility.Visible;
           DocsLinkBtn.Icon = new SymbolIcon { Symbol = SymbolRegular.Globe24 };
           WidgetNameBox.Width = 315;
        }
        
        foreach (var v in widget.CssVariables) _editingCssVars.Add(v);
        CssVarsList.ItemsSource = _editingCssVars;
        PopulateJsVars();
        UpdateSaveButtonBorder(SaveButtonBorder, false);
    }

    private void PopulateJsVars()
    {
        if (_selectedWidget == null) return;
        JsVarsList.ItemsSource = _selectedWidget.JsVariables;
    }
    
    private string SelectFileVarPathDialog(WidgetVariableType type)
    {
        var path = string.Empty;
        try
        {
            if (type == WidgetVariableType.FolderPath)
            {
                var dlg = new Microsoft.Win32.OpenFolderDialog()
                {
                    Title = "Select Folder",
                    Multiselect = false
                };
                
                if (dlg.ShowDialog() == true)
                {
                    path = dlg.FolderName;
                }
            }
            else
            {
                // get filter by type
                var filter = type switch
                {
                    WidgetVariableType.ImageFile => "Image|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.avif;*.bmp;*.svg;*.ico",
                    WidgetVariableType.SoundFile => "Sound|*.wav;*.mp3;*.ogg;*.oga;*.opus;*.m4a;",
                    WidgetVariableType.VideoFile => "Video|*.mp4;*.m4v;*.webm;*.ogm",
                    _ => "File|*.*"
                };

                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Select file",
                    Filter = filter,
                    Multiselect = false
                };

                if (dlg.ShowDialog() == true)
                {
                    path = dlg.FileName;
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, $"Failed to parse filepath");
        }

        return path;
    }

    private IEnumerable<CheckBox> GetAllCheckBoxes(Panel panel)
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
    
    private void UpdateEventListValues(JsVariable variable, StackPanel container)
    {
        var selected = GetAllCheckBoxes(container)
            .Where(c => c.IsChecked == true)
            .Select(c => ((Wpf.Ui.Controls.TextBlock)c.Content).Tag)
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
        // _widgets is sorted descending
        await using var db = await _factory.CreateDbContextAsync();
        int start = _widgets.Count;
        for (int i = 0; i < _widgets.Count; i++)
        {
            var wi = _widgets[i];
            var w = await db.Widgets.FirstOrDefaultAsync(x => x.Id == wi.Id);
            if (w == null) continue;
            w.Z = start - i;
        }
        await db.SaveChangesAsync();
        await LoadRouteAsync();
    }
    
    private void RefreshWebView()
    {
        if (_loadedWebView) PreviewWebView.CoreWebView2?.Reload();
    }
    
    private async Task ImportSingleWidgetAsync(string path, AppDbContext db, WidgetEntityHelper helper)
    {
        var newWidget = new Widget(Path.GetFileNameWithoutExtension(path), path);

        var metadata = await helper.ExtractWidgetMetadata(path);

        newWidget.RouteId = _route!.Id;
        newWidget.X = 0;
        newWidget.Y = 0;
        newWidget.Z = _widgets.Count > 0 ? _widgets.Max(x => x.Z) + 1 : 1;

        newWidget.Width = metadata.Width;

        newWidget.Height = metadata.Height;

        newWidget.DocsUrl = metadata.Url;

        db.Widgets.Add(newWidget);
        await db.SaveChangesAsync();

        (List<JsVariable> jsVars, _, _) =
            helper.LoadNewJsVariables(newWidget, metadata);

        if (jsVars.Count > 0)
        {
            newWidget.JsVariables = jsVars;
            db.JsVariables.AddRange(jsVars);
        }

        await db.SaveChangesAsync();

        newWidget.ScanCssVariables();
        db.CssVariables.AddRange(newWidget.CssVariables);
        await db.SaveChangesAsync();

        _route.Widgets.Add(newWidget);
        _widgets.Insert(0, newWidget);
    }

    
    private void UpdateWebViewScale()
    {
        if (PreviewWebView == null || _route == null || WebViewContainer == null)
            return;

        double scaleX = WebViewContainer.ActualWidth / _route.Width;
        double scaleY = WebViewContainer.ActualHeight / _route.Height;
        double scale = Math.Min(scaleX, scaleY);

        PreviewWebView.Height = _route.Height * scale;
        PreviewWebView.Width = _route.Width * scale;
        PreviewWebView.ZoomFactor = scale;
    }

    protected override void OnClosed(EventArgs e)
    {
        WidgetEvents.WidgetPositionUpdated -= OnWidgetPositionUpdated;
        WidgetEvents.WidgetScaleUpdated -= OnWidgetScaleUpdated;
        WidgetEvents.SelectEditorWidget -= SelectWidgetFromEvent;
        WidgetEvents.WidgetSizeUpdated -= OnWidgetSizeUpdated;
        if (WebViewContainer != null)
            WebViewContainer.SizeChanged -= WebViewContainer_SizeChanged;
        Loaded -= EditRouteWindow_Loaded;
        
        if (_loadedWebView)
        {    
            try
            {
                PreviewWebView.CoreWebView2.Stop();
                PreviewWebView.CoreWebView2.Navigate("about:blank"); 
                PreviewWebView.Source = new Uri("about:blank");
            }
            catch { /**/ }
            PreviewWebView?.Dispose();
        }

        base.OnClosed(e);
    }

    private void UpdateSaveButtonBorder(Border border, bool hasPendingChanges)
    {
        Dispatcher.InvokeAsync(() =>
        {
            UiUtils.UiUtils.UpdateButtonPendingBorder(border, hasPendingChanges);
        });
    }

}