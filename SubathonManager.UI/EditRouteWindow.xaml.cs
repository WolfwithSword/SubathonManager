using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using SubathonManager.Core.Events;
using SubathonManager.Core.Models;
using SubathonManager.Data;

namespace SubathonManager.UI;

public partial class EditRouteWindow
{
    // todo unpopulate widget on delete. fix ui in general
    private readonly Guid _routeId;
    private Route? _route;
    private ObservableCollection<Widget> _widgets = new();
    private Widget? _selectedWidget;
    private ObservableCollection<CssVariable> _editingCssVars = new();
    private readonly IDbContextFactory<AppDbContext> _factory;
    
    public EditRouteWindow(Guid routeId)
    {
        _factory = App.AppServices.GetRequiredService<IDbContextFactory<AppDbContext>>();
        InitializeComponent();
        _routeId = routeId;
        WidgetsList.ItemsSource = _widgets;
        Loaded += EditRouteWindow_Loaded;
    }
    private async void EditRouteWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // we do require webview2 windows runtime to be installed. Native on win11, win10 it might not be
        await PreviewWebView.EnsureCoreWebView2Async();
        PreviewWebView.CoreWebView2.Settings.AreDevToolsEnabled = true; // yeah, handy for technical users
        PreviewWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        await LoadRouteAsync();
        WidgetEvents.WidgetPositionUpdated += OnWidgetPositionUpdated;
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
    
    private async Task LoadRouteAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        _route = await db.Routes
            .Include(r => r.Widgets)
            .ThenInclude(w => w.CssVariables)
            .FirstOrDefaultAsync(r => r.Id == _routeId);

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
        foreach (var w in sorted)
        {
            if (w.Z != index)
            {
                hasUpdatedZ = true; 
                w.Z = index;
            }
            index -= 1;
            _widgets.Add(w);
        }
        if (hasUpdatedZ) await db.SaveChangesAsync();

        WebViewContainer.SizeChanged += WebViewContainer_SizeChanged;
        try
        {
            PreviewWebView.Source = new Uri(_route.GetRouteUrl(true));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WebView2 failed: {ex.Message}");
        }
        UpdateWebViewScale();
    }

    private async void SaveRouteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_route == null) return;
        await using var db = await _factory.CreateDbContextAsync();
        await db.Entry(_route).ReloadAsync();
        // var route = await db.Routes.FirstOrDefaultAsync(r => r.Id == _route.Id);
        // if (route == null) return;

        _route.Name = RouteNameBox.Text.Trim();
        if (int.TryParse(RouteWidthBox.Text, out var w)) _route.Width = w;
        if (int.TryParse(RouteHeightBox.Text, out var h)) _route.Height = h;

        await db.SaveChangesAsync();
        // _route = route;
        UpdateWebViewScale();
        OverlayEvents.RaiseOverlayRefreshRequested(_route.Id);
    }
    
    private void NumberOnly_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !int.TryParse(e.Text, out _);
    }
    
    private Widget? GetWidgetFromSender(object? sender)
    {
        if (sender is Wpf.Ui.Controls.Button btn && btn.DataContext is Widget wi) return wi;
        return null;
    }
    
    private async void DeleteWidget_Click(object sender, RoutedEventArgs e)
    {
        var wi = GetWidgetFromSender(sender);
        if (wi == null) return;
        Guid routeId = wi.RouteId;
        
        await using var db = await _factory.CreateDbContextAsync();
        var w = await db.Widgets.FirstOrDefaultAsync(x => x.Id == wi.Id);
        if (w != null)
        {
            db.Widgets.Remove(w);
            await db.SaveChangesAsync();
        }

        _widgets.Remove(wi);
        await RefreshWidgetZIndicesAsync();
        RefreshWebView();
        OverlayEvents.RaiseOverlayRefreshRequested(routeId);
    }

    private async void CopyWidget_Click(object sender, RoutedEventArgs e)
    {
        var w = GetWidgetFromSender(sender);
        if (w == null) return;

        await using var db = await _factory.CreateDbContextAsync();
        await db.Entry(w).ReloadAsync();
        // var w = await db.Widgets.Include(x => x.CssVariables).FirstOrDefaultAsync(x => x.Id == wi.Id);
        // if (w == null) return;

        // clone widget
        var clone = new Widget(w.Name + " (Copy)", w.HtmlPath);

        clone.X = w.X;
        clone.Y = w.Y;
        clone.Z = _widgets.Count + 1;
        clone.Width = w.Width;
        clone.Height = w.Height;
        clone.RouteId = w.RouteId;

        db.Widgets.Add(clone);
        await db.SaveChangesAsync();

        // clone css vars
        if (w.CssVariables.Any())
        {
            foreach (var cv in w.CssVariables)
            {
                var n = new CssVariable
                {
                    Name = cv.Name,
                    Value = cv.Value,
                    WidgetId = clone.Id
                };
                db.CssVariables.Add(n);
            }
            await db.SaveChangesAsync();
        }

        _widgets.Insert(0, clone);
        await RefreshWidgetZIndicesAsync();
        RefreshWebView();
    }

    private async void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        var wi = GetWidgetFromSender(sender);
        if (wi == null) return;

        int idx = _widgets.IndexOf(wi);
        if (idx <= 0) return;

        var above = _widgets[idx - 1];
        await SwapWidgetZAsync(wi, above);
        _widgets.Move(idx, idx - 1);
    }

    private async void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        var wi = GetWidgetFromSender(sender);
        if (wi == null) return;

        int idx = _widgets.IndexOf(wi);
        if (idx < 0 || idx >= _widgets.Count - 1) return;

        var below = _widgets[idx + 1];
        await SwapWidgetZAsync(wi, below);
        _widgets.Move(idx, idx + 1);
    }

    private void EditWidget_Click(object sender, RoutedEventArgs e)
    {    
        var widget = GetWidgetFromSender(sender);
        if (widget != null)
            PopulateWidgetEditor(widget);
    }
    
    private void WidgetCard_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is Widget widget)
        {
            PopulateWidgetEditor(widget);
        }
    }
    
    
    private void PopulateWidgetEditor(Widget widget)
    {
        
        WidgetEntityHelper widgetHelper = new WidgetEntityHelper();
        using var db = _factory.CreateDbContext();
        widgetHelper.SyncCssVariables(widget);
        _selectedWidget = db.Widgets.Include(wX => wX.CssVariables)
            .FirstOrDefault(wX => wX.Id == widget.Id);
        widget = _selectedWidget!;

        WidgetEditPanel.Visibility = Visibility.Visible;
        EmptyEditorPanel.Visibility = Visibility.Collapsed;

        if (WidgetNameBox.Text != widget.Name) WidgetNameBox.Text = widget.Name;
        if (WidgetWidthBox.Text != widget.Width.ToString()) WidgetWidthBox.Text = widget.Width.ToString();
        if (WidgetHeightBox.Text != widget.Height.ToString()) WidgetHeightBox.Text = widget.Height.ToString();
        if (WidgetXBox.Text != $"{widget.X}") WidgetXBox.Text = $"{widget.X}";
        if (WidgetYBox.Text != $"{widget.Y}") WidgetYBox.Text = $"{widget.Y}";

        _editingCssVars = new ObservableCollection<CssVariable>(widget.CssVariables);
        CssVarsList.ItemsSource = _editingCssVars;
    }

    private void ReloadCSS_Click(object sender, RoutedEventArgs e)
    {
        // todo, button to delete vars no longer found or tie in here
        if (_selectedWidget == null) return;
        WidgetEntityHelper widgetHelper = new WidgetEntityHelper();
        widgetHelper.SyncCssVariables(_selectedWidget);
        
        using var db = _factory.CreateDbContext();
        var widget = db.Widgets.Include(wX => wX.CssVariables)
            .FirstOrDefault(wX => wX.Id == _selectedWidget.Id);
        _selectedWidget = widget;
        
        _editingCssVars = new ObservableCollection<CssVariable>(_selectedWidget!.CssVariables);
        CssVarsList.ItemsSource = _editingCssVars;
    }
    
    private async void SaveWidgetButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedWidget == null) return;

        await using var db = await _factory.CreateDbContextAsync();
        var widget = await db.Widgets.Include(wX => wX.CssVariables)
            .FirstOrDefaultAsync(wX => wX.Id == _selectedWidget.Id);

        if (widget == null) return;
        widget.Name = WidgetNameBox.Text;
        widget.Width = int.TryParse(WidgetWidthBox.Text, out int w) ? w : _selectedWidget.Width;
        widget.Height = int.TryParse(WidgetHeightBox.Text, out int h) ? h : _selectedWidget.Height;
        widget.X = float.TryParse(WidgetXBox.Text, out float x) ? x : _selectedWidget.X;
        widget.Y = float.TryParse(WidgetYBox.Text, out float y) ? y : _selectedWidget.Y;
        widget.CssVariables = _editingCssVars.ToList();

        db.Widgets.Update(widget);
        await db.SaveChangesAsync();
        _selectedWidget = widget;
        _selectedWidget.CssVariables = _editingCssVars.ToList();

        await LoadRouteAsync();
        RefreshWebView();
        
        WidgetsList.Items.Refresh();
        OverlayEvents.RaiseOverlayRefreshRequested(_selectedWidget.RouteId);

    }
    
    private async Task SwapWidgetZAsync(Widget a, Widget b)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var wa = await db.Widgets.Include(w => w.CssVariables).FirstOrDefaultAsync(w => w.Id == a.Id);
        var wb = await db.Widgets.Include(w => w.CssVariables).FirstOrDefaultAsync(w => w.Id == b.Id);
        if (wa == null || wb == null) return;

        int tmp = wa.Z;
        wa.Z = wb.Z;
        wb.Z = tmp;

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
        if (PreviewWebView?.CoreWebView2 != null) PreviewWebView.CoreWebView2.Reload();
    }

    private async void ImportWidgetButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select widget HTML file",
            Filter = "HTML Files|*.html;*.htm"
        };
        
        if (dlg.ShowDialog() == true)
        {
            string path = dlg.FileName;
            await using var db = await _factory.CreateDbContextAsync();
            var newWidget = new Widget(System.IO.Path.GetFileNameWithoutExtension(path), path);
            newWidget.RouteId = _route!.Id;
            newWidget.X = 0;
            newWidget.Y = 0;
            newWidget.Z = _widgets.Count > 0 ? _widgets.Max(x => x.Z) + 1 : 1;
            newWidget.Width = 400;
            newWidget.Height = 200;
            db.Widgets.Add(newWidget);
            await db.SaveChangesAsync();
            newWidget.ScanCssVariables();
            db.CssVariables.AddRange(newWidget.CssVariables);
            await db.SaveChangesAsync();
            _route.Widgets.Add(newWidget);
            
            _widgets.Insert(0, newWidget);
            await RefreshWidgetZIndicesAsync();
            RefreshWebView();
        }
        
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

    private void WebViewContainer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateWebViewScale();
    }
    
    private void CopyOverlayUrl_Click(object sender, RoutedEventArgs e)
    {
        if (_route == null) return;
        Clipboard.SetText(_route.GetRouteUrl());
    }

    private void OpenOverlayInBrowser_Click(object sender, RoutedEventArgs e)
    {
        if (_route == null) return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _route.GetRouteUrl(),
                UseShellExecute = true
            });
        }
        catch
        {
            // 
        }
    }
    protected override void OnClosed(EventArgs e)
    {
        if (PreviewWebView != null)
        {
            PreviewWebView.Dispose();
        }
        WidgetEvents.WidgetPositionUpdated -= OnWidgetPositionUpdated;
        WebViewContainer.SizeChanged -= WebViewContainer_SizeChanged;
        Loaded -= EditRouteWindow_Loaded;
        base.OnClosed(e);
    }


}