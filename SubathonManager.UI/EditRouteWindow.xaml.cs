using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Media;
using System.IO;
using System.Windows;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SubathonManager.Core.Events;
using SubathonManager.Core.Models;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
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
            _logger?.LogError(ex, "Failed to load overlay editor");
            _loadedWebView = false;
        }
        finally
        {
            WidgetEvents.WidgetPositionUpdated += OnWidgetPositionUpdated;
            WidgetEvents.WidgetScaleUpdated += OnWidgetScaleUpdated;
            WidgetEvents.SelectEditorWidget += SelectWidgetFromEvent;
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
            _widgets.Add(w);
            widgetHelper.SyncCssVariables(w);
            widgetHelper.SyncJsVariables(w);
        }
        if (hasUpdatedZ) await db.SaveChangesAsync();

        WebViewContainer.SizeChanged += WebViewContainer_SizeChanged;
        try
        {
            PreviewWebView.Source = new Uri(_route.GetRouteUrl(App.AppConfig!,true));
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, $"WebView 2 failed to load: {ex.Message}");
        }
        UpdateWebViewScale();
    }

    private async void SaveRouteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_route == null) return;

        try
        {
            await using var db = await _factory.CreateDbContextAsync();
            await db.Entry(_route).ReloadAsync();

            _route.Name = RouteNameBox.Text.Trim();
            if (int.TryParse(RouteWidthBox.Text, out var w)) _route.Width = w;
            if (int.TryParse(RouteHeightBox.Text, out var h)) _route.Height = h;

            await db.SaveChangesAsync();
            UpdateWebViewScale();
            OverlayEvents.RaiseOverlayRefreshRequested(_route.Id);

            await Task.Run(async () =>
            {
                await Dispatcher.InvokeAsync(() => { SaveRouteButton.Content = "Saved!"; }
                );
                await Task.Delay(1500);
                await Dispatcher.InvokeAsync(() => { SaveRouteButton.Content = "Save"; }
                );
            });

        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save overlay");
        }
    }

    private void NumberOrNegativeOnly_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (char.IsDigit(e.Text, 0))
        {
            e.Handled = false;
            return;
        }

        if (sender is Wpf.Ui.Controls.TextBox tb)
        {
            if (e.Text == "-" && tb.SelectionStart == 0 && !tb.Text.Contains('-'))
            {
                e.Handled = false;
                return;
            }
            e.Handled = !int.TryParse(
                (tb).Text.Insert((tb).SelectionStart, e.Text), NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out _);
        }
        else if (sender is  System.Windows.Controls.TextBox tb2)
        {
            if (e.Text == "-" && tb2.SelectionStart == 0 && !tb2.Text.Contains('-'))
            {
                e.Handled = false;
                return;
            }
            e.Handled = !int.TryParse(
                (tb2).Text.Insert((tb2).SelectionStart, e.Text), NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out _);
        }
    }
    
    private void NumberOrDecimalOnly_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (char.IsDigit(e.Text, 0))
        {
            e.Handled = false;
            return;
        }

        if (sender is Wpf.Ui.Controls.TextBox tb)
        {
            if (e.Text == "." && !tb.Text.Contains('.'))
            {
                e.Handled = false;
                return;
            }
            
            e.Handled = !double.TryParse(
                (tb).Text.Insert((tb).SelectionStart, e.Text),
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out _);
        }
        else if (sender is System.Windows.Controls.TextBox tb2)
        {
            if (e.Text == "." && !tb2.Text.Contains('.'))
            {
                e.Handled = false;
                return;
            }
            
            e.Handled = !double.TryParse(
                (tb2).Text.Insert((tb2).SelectionStart, e.Text),
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out _);
        }
    }
    
    private void NumberOnly_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !int.TryParse(e.Text, out _);
    }
    
    private Widget? GetWidgetFromSender(object? sender)
    {
        if (sender is Wpf.Ui.Controls.Button { DataContext: Widget wi }) return wi;
        return null;
    }
    
    private async void DeleteWidget_Click(object sender, RoutedEventArgs e)
    {
        var wi = GetWidgetFromSender(sender);
        if (wi == null) return;
        Guid routeId = wi.RouteId;

        try
        {
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
            if (wi.Id == _selectedWidget?.Id)
                PopulateWidgetEditor(null);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to delete widget");
        }
    }

    private async void CopyWidget_Click(object sender, RoutedEventArgs e)
    {
        try
        {    
            var w = GetWidgetFromSender(sender);
            if (w == null) return;
            await using var db = await _factory.CreateDbContextAsync();
            await db.Entry(w).ReloadAsync();
            
            // clone widget
            var clone = w.Clone(w.RouteId, w.Name + " (Copy)", _widgets.Count + 1);
            db.Widgets.Add(clone);
            db.CssVariables.AddRange(clone.CssVariables);
            db.JsVariables.AddRange(clone.JsVariables);
            await db.SaveChangesAsync();
            
            _widgets.Insert(0, clone);
            await RefreshWidgetZIndicesAsync();
            RefreshWebView();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to duplicate widget");
        }
    }

    private async void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var wi = GetWidgetFromSender(sender);
            if (wi == null) return;

            int idx = _widgets.IndexOf(wi);
            if (idx <= 0) return;

            var above = _widgets[idx - 1];
            await SwapWidgetZAsync(wi, above);
            _widgets.Move(idx, idx - 1);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to move widget Z-Index up");
        }
    }

    private async void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var wi = GetWidgetFromSender(sender);
            if (wi == null) return;

            int idx = _widgets.IndexOf(wi);
            if (idx < 0 || idx >= _widgets.Count - 1) return;

            var below = _widgets[idx + 1];
            await SwapWidgetZAsync(wi, below);
            _widgets.Move(idx, idx + 1);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to move widget Z-Index down");
        }
    }

    private void SelectWidgetFromEvent(Guid widgetId)
    {
        if (widgetId == _selectedWidget?.Id && WidgetEditPanel.Visibility == Visibility.Visible) return;
        using var db = _factory.CreateDbContext();
        var widget = db.Widgets.Include(wX => wX.JsVariables)
            .Include(wX => wX.CssVariables).FirstOrDefault(wX => wX.Id == widgetId);
        Dispatcher.Invoke(() =>
        {
            PopulateWidgetEditor(widget);
        });
    }

    private void EditWidget_Click(object sender, RoutedEventArgs e)
    {    
        var widget = GetWidgetFromSender(sender);
        if (widget != null)
            PopulateWidgetEditor(widget);
    }
    
    private void WidgetCard_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Widget widget })
            PopulateWidgetEditor(widget);
    }
    
    
    private void PopulateWidgetEditor(Widget? widget)
    {
        if (widget == null)
        {
            WidgetEditPanel.Visibility = Visibility.Collapsed;
            EmptyEditorPanel.Visibility = Visibility.Visible;
            _editingCssVars = new ObservableCollection<CssVariable>();
            _selectedWidget = null;
            return;
        }
        
        WidgetEntityHelper widgetHelper = new WidgetEntityHelper(_factory, null);
        using var db = _factory.CreateDbContext();
        widgetHelper.SyncCssVariables(widget);
        widgetHelper.SyncJsVariables(widget);
        _selectedWidget = db.Widgets.Include(wX => wX.CssVariables)
            .Include(wX => wX.JsVariables)
            .FirstOrDefault(wX => wX.Id == widget.Id);
        widget = _selectedWidget!;

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
        
        _editingCssVars = new ObservableCollection<CssVariable>(widget.CssVariables);
        CssVarsList.ItemsSource = _editingCssVars;
        PopulateJsVars();
    }

    private async void OpenWidgetDocumentation_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedWidget == null || string.IsNullOrWhiteSpace(_selectedWidget.DocsUrl)
            || !Uri.IsWellFormedUriString(_selectedWidget.DocsUrl, UriKind.Absolute)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _selectedWidget.DocsUrl,
                UseShellExecute = true
            });
        }
        catch {/**/}
    }
    
    private async void ToggleVisibility_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var widget = GetWidgetFromSender(sender);
            if (widget == null) return;

            await using var db = await _factory.CreateDbContextAsync();
            var wa = await db.Widgets.Include(w => w.CssVariables)
                .Include(w => w.JsVariables)
                .FirstOrDefaultAsync(w => w.Id == widget.Id);
            if (wa == null) return;

            widget = wa;
            widget.Visibility = !widget.Visibility;

            await db.SaveChangesAsync();
            RefreshWebView();
            if (sender is Wpf.Ui.Controls.Button button && button.Content is SymbolIcon icon)
            {
                Dispatcher.Invoke(() =>
                {
                    icon.Symbol = widget.Visibility ? SymbolRegular.Eye20 : SymbolRegular.EyeOff20;
                });
            }

            OverlayEvents.RaiseOverlayRefreshRequested(widget.RouteId);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to update widget visibility");
        }
    }

    private void PopulateJsVars()
    {
        // call in a dispatch
        JsVarsList.Items.Clear();
        if (_selectedWidget == null) return;

        var containerPanel = new StackPanel { Orientation = Orientation.Vertical };
        foreach (var jsVar in _selectedWidget.JsVariables) 
        {
            var outerPanel = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 4, 0, 4)};
            var itemRow = new StackPanel { Orientation = Orientation.Horizontal };

            var nameBlock = new Wpf.Ui.Controls.TextBlock
            {
                Text = jsVar.Name,
                ToolTip = $"{jsVar.Name} - {jsVar.Type}",
                Width = 172,
                TextAlignment = TextAlignment.Left,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = jsVar.Type == WidgetVariableType.EventTypeList ?
                    VerticalAlignment.Top : VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            
            itemRow.Children.Add(nameBlock);

            if (jsVar.Type == WidgetVariableType.EventTypeList)
            {
                var panelValues = (jsVar.Value ?? "").Split(',',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var border = new Border
                {
                    BorderBrush = Brushes.Gray,
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(4),
                    Width = 282,
                    CornerRadius = new CornerRadius(4),
                    Margin = new Thickness(0, 4, 0, 2)
                };
                
                var chkboxList = new StackPanel { Orientation = Orientation.Vertical };
                foreach (var eType in Enum.GetNames(typeof(SubathonEventType)).OrderBy(x => x))
                {
                    if (eType == nameof(SubathonEventType.Command) ||
                        eType == nameof(SubathonEventType.Unknown)) continue;
                    var chkBox = new CheckBox
                    {
                        Content = new Wpf.Ui.Controls.TextBlock
                        {
                            Text = eType,
                            TextWrapping =  TextWrapping.Wrap,
                            MaxWidth = 278
                        },
                        IsChecked = panelValues.Contains(eType),
                        Margin = new Thickness(2)
                    };
                    chkBox.Checked += (_, __) => UpdateEventListValues(jsVar, chkboxList);
                    chkBox.Unchecked += (_, __) => UpdateEventListValues(jsVar, chkboxList);
                    chkboxList.Children.Add(chkBox);
                }

                border.Child = chkboxList;
                outerPanel.Children.Add(itemRow);
                outerPanel.Children.Add(border);
            }
            else if (jsVar.Type == WidgetVariableType.Boolean)
            {
                bool.TryParse(jsVar.Value, out bool isChecked);
                var chkBox = new CheckBox
                {
                    Content = new Wpf.Ui.Controls.TextBlock
                    {
                        Text = string.Empty,
                        TextWrapping =  TextWrapping.Wrap,
                        MaxWidth = 150
                    },
                    IsChecked = isChecked,
                    Margin = new Thickness(2)
                };
                chkBox.Checked += (_, __) => jsVar.Value = "True";
                chkBox.Unchecked += (_, __) => jsVar.Value = "False";
                itemRow.Children.Add(chkBox);
                outerPanel.Children.Add(itemRow);
            }
            else if (jsVar.Type == WidgetVariableType.EventTypeSelect)
            {
                var values = Enum.GetValues<SubathonEventType>()
                    .Where(x => ((SubathonEventType?)x).HasNoValueConfig())
                    .Select(x=>x.ToString()).ToList().OrderBy(x => x);
                var selected = string.IsNullOrWhiteSpace(jsVar.Value) ? string.Empty : jsVar.Value;
                var typeList = new ComboBox
                {
                    Margin = new Thickness(2),
                    IsEditable=true, IsTextSearchEnabled=true, 
                    StaysOpenOnEdit=true,
                    MaxWidth = 146,
                    Width = 146
                };
                typeList.Items.Add(string.Empty);
                foreach (var val in values)
                    typeList.Items.Add(val);
                typeList.SelectedValue = selected;
                typeList.SelectionChanged += (_, __) =>
                {
                    jsVar.Value = $"{typeList.SelectedValue}";
                };
                itemRow.Children.Add(typeList);
                outerPanel.Children.Add(itemRow);
            }
            else if (jsVar.Type == WidgetVariableType.StringSelect)
            {
                var values = jsVar.Value!.Trim().Split(',');
                var selected = values.Length > 0 ? values[0] : string.Empty;
                var stringList = new ComboBox
                {
                    Margin = new Thickness(2),
                    MaxWidth = 146,
                    Width = 146
                };
                foreach (var val in values)
                    stringList.Items.Add(val);
                stringList.SelectedValue = selected;
                stringList.SelectionChanged += (_, __) =>
                {
                    if (!jsVar.Value.Contains(',')) return;
                    if (jsVar.Value.Contains(',') && jsVar.Value.StartsWith($"{stringList.SelectedValue},")) return;
                    var newVal = new List<string>
                    {
                        $"{stringList.SelectedValue}"
                    };
                    foreach (var v in values)
                    {
                        if (newVal.Contains(v)) continue;
                        newVal.Add(v);
                    }
                    jsVar.Value = string.Join(',', newVal);

                };
                itemRow.Children.Add(stringList);
                outerPanel.Children.Add(itemRow);
            }
            else if (((WidgetVariableType?)jsVar.Type).IsFileVariable())
            {
                var shortContent = string.IsNullOrWhiteSpace(jsVar.Value) ? "Empty" : jsVar.Value.Split('/').Last();
                var valueBtn = new Wpf.Ui.Controls.Button
                {
                    Content = shortContent,
                    Width = 150,
                    ToolTip = jsVar.Value,
                    Margin = new Thickness(0,0, 0, 4),
                };
                valueBtn.Click += (_, __) =>
                {
                    var path = SelectFileVarPathDialog(jsVar.Type);
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        path = path.Replace('\\', '/');
                        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
                        dir = dir!.Replace('\\', '/');
                        var widgetDir = Path.GetDirectoryName(_selectedWidget.HtmlPath);
                        widgetDir = widgetDir!.Replace('\\', '/');
                        
                        if (widgetDir!.Contains(dir!))
                        {
                            path = path.Replace(dir!, "./").Replace("//", "/");
                        }
                        jsVar.Value = path;
                        valueBtn.Content = path.Split('/').Last();
                        valueBtn.ToolTip = path;
                    }
                };
                
                var openBtn = new Wpf.Ui.Controls.Button
                {
                    Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Open24 },
                    ToolTip = "Open File",
                    Width = 40,
                    Height = 30,
                    Margin = new Thickness(0,0, 55, 2),
                    Padding = new Thickness(2)
                };
                
                openBtn.Click  += (_, __) =>
                {
                    var file = jsVar.Value;
                    if (string.IsNullOrWhiteSpace(file)) return;
                    if (file.StartsWith('.'))
                    {
                        var widgetDir = Path.GetDirectoryName(_selectedWidget.HtmlPath);
                        file = Path.Join(widgetDir, file.Replace("./", ""));
                    }

                    Process.Start(new ProcessStartInfo
                    {
                        FileName = file,
                        UseShellExecute = true
                    });
                };

                var removeBtn = new Wpf.Ui.Controls.Button
                {
                    Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Delete24 },
                    Width = 40,
                    Height = 30,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    ToolTip = "Clear File",
                    Foreground = System.Windows.Media.Brushes.Red,
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Margin = new Thickness(15, 0, 0, 0),
                    Padding = new Thickness(2)
                };

                removeBtn.Click  += (_, __) =>
                {
                    jsVar.Value = string.Empty;
                    valueBtn.Content = "Empty";
                    valueBtn.ToolTip = string.Empty;
                    
                };
                var btnRow = new StackPanel { Orientation = Orientation.Vertical };
                var btnRow2 = new StackPanel { Orientation = Orientation.Horizontal };
                btnRow.Children.Add(valueBtn);
                btnRow2.Children.Add(openBtn);
                btnRow2.Children.Add(removeBtn);
                btnRow.Children.Add(btnRow2);
                itemRow.Children.Add(btnRow);
                
                outerPanel.Children.Add(itemRow);
            }
            else
            {
                var txtBox = new Wpf.Ui.Controls.TextBox
                {
                    Text = jsVar.Value,
                    Width = 150
                };

                if (jsVar.Type == WidgetVariableType.Int)
                {
                    txtBox.PreviewTextInput += (s, e) =>
                    {
                        var tb = (Wpf.Ui.Controls.TextBox)s;

                        if (char.IsDigit(e.Text, 0))
                        {
                            e.Handled = false;
                            return;
                        }

                        if (e.Text == "-" && tb.SelectionStart == 0 && !tb.Text.Contains('-'))
                        {
                            e.Handled = false;
                            return;
                        }

                        e.Handled = true;
                    };
                }
                else if (jsVar.Type == WidgetVariableType.Percent)
                {
                    txtBox.PreviewTextInput += (s, e) =>
                    {
                        var tb = (Wpf.Ui.Controls.TextBox)s;

                        var newText = tb.Text.Remove(tb.SelectionStart, tb.SelectionLength)
                            .Insert(tb.SelectionStart, e.Text);

                        if (!int.TryParse(newText, out var value) || value < 0 || value > 100)
                        {
                            e.Handled = true;
                        }
                    };
                }
                else if (jsVar.Type == WidgetVariableType.Float)
                {
                    txtBox.PreviewTextInput += (s, e) =>
                    {
                        var tb = (Wpf.Ui.Controls.TextBox)s;
                        string incoming = e.Text;

                        if (char.IsDigit(incoming, 0))
                        {
                            e.Handled = false;
                            return;
                        }

                        if (incoming == "-" && tb.SelectionStart == 0 && !tb.Text.Contains('-'))
                        {
                            e.Handled = false;
                            return;
                        }

                        if (incoming == "." && !tb.Text.Contains('.'))
                        {
                            e.Handled = false;
                            return;
                        }

                        e.Handled = true;
                    };

                }
                txtBox.Text = jsVar.Value;

                txtBox.TextChanged += (s, e) =>
                {
                    if (jsVar.Type == WidgetVariableType.Percent && string.IsNullOrWhiteSpace(txtBox.Text))
                        txtBox.Text = "0";
                    jsVar.Value = txtBox.Text!;
                };
                
                itemRow.Children.Add(txtBox);
                
                outerPanel.Children.Add(itemRow);
            }
            outerPanel.Children.Add(new Separator
            {
                Margin = new Thickness(16, 2, 16, 2)
            });
            containerPanel.Children.Add(outerPanel);
        }
        JsVarsList.Items.Add(containerPanel);
    }

    private string SelectFileVarPathDialog(WidgetVariableType type)
    {
        var path = string.Empty;
        try
        {
            // get filter by type
            var filter = "File|*.*";
            if (type == WidgetVariableType.ImageFile)
                filter = "Image|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.avif;*.bmp;*.svg;*.ico";
            else if (type == WidgetVariableType.SoundFile)
                filter = "Sound|*.wav;*.mp3;*.ogg;*.oga;*.opus;*.m4a;";
            else if (type == WidgetVariableType.VideoFile)
                filter = "Video|*.mp4;*.m4v;*.webm;*.ogm";
            
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select file",
                Filter = filter
            };

            if (dlg.ShowDialog() == true)
            {
                path = dlg.FileName;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, $"Failed to parse filepath");
        }

        return path;
    }

    private void UpdateEventListValues(JsVariable variable, StackPanel dropdown)
    {
        var selected = dropdown.Children.OfType<CheckBox>().Where(c => c.IsChecked == true)
            .Select(c => ((Wpf.Ui.Controls.TextBlock)c.Content).Text).ToList();
        variable.Value = string.Join(',', selected);
    }
    
    private void ReloadVars_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedWidget == null) return;
        WidgetEntityHelper widgetHelper = new WidgetEntityHelper(_factory, null);
        widgetHelper.SyncCssVariables(_selectedWidget);
        widgetHelper.SyncJsVariables(_selectedWidget);
        
        using var db = _factory.CreateDbContext();
        var widget = db.Widgets.Include(wX => wX.CssVariables)
            .Include(wX => wX.JsVariables)
            .FirstOrDefault(wX => wX.Id == _selectedWidget.Id);
        _selectedWidget = widget;
        
        _editingCssVars = new ObservableCollection<CssVariable>(_selectedWidget!.CssVariables);
        CssVarsList.ItemsSource = _editingCssVars;
        PopulateJsVars();
        //RefreshWebView();
    }
    
    private async void SaveWidgetButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_selectedWidget == null) return;
            WidgetEntityHelper widgetHelper = new WidgetEntityHelper(_factory, null);
            
            
            widgetHelper.SyncCssVariables(_selectedWidget);
            widgetHelper.SyncJsVariables(_selectedWidget);
            
            await using var db = await _factory.CreateDbContextAsync();
            _selectedWidget.Name = WidgetNameBox.Text;
            _selectedWidget.Width = int.TryParse(WidgetWidthBox.Text, out int w) ? w : _selectedWidget.Width;
            _selectedWidget.Height = int.TryParse(WidgetHeightBox.Text, out int h) ? h : _selectedWidget.Height;
            _selectedWidget.X = float.TryParse(WidgetXBox.Text, out float x) ? x : _selectedWidget.X;
            _selectedWidget.Y = float.TryParse(WidgetYBox.Text, out float y) ? y : _selectedWidget.Y;
            _selectedWidget.ScaleX = float.TryParse(WidgetScaleXBox.Text, out float sx) ? sx : (_selectedWidget.ScaleX == 0 ? 1 : _selectedWidget.ScaleX);
            _selectedWidget.ScaleY = float.TryParse(WidgetScaleYBox.Text, out float sy) ? sy : (_selectedWidget.ScaleY == 0 ? 1 : _selectedWidget.ScaleY);

            foreach (var cssVar in _editingCssVars)
            {
                var cssVarToUpdate = _selectedWidget.CssVariables.Find(csv => csv.Name == cssVar.Name);
                if (cssVarToUpdate != null)
                    cssVarToUpdate.Value = cssVar.Value;
            }
            
            db.Entry(_selectedWidget).State = EntityState.Modified;
            db.Widgets.Update(_selectedWidget);
            db.CssVariables.UpdateRange(_selectedWidget.CssVariables);
            db.JsVariables.UpdateRange(_selectedWidget.JsVariables);
            await db.SaveChangesAsync();
            _editingCssVars.Clear();
            foreach(var cssVar in _selectedWidget.CssVariables)
                _editingCssVars.Add(cssVar);

            await LoadRouteAsync();
            RefreshWebView();

            WidgetsList.Items.Refresh();
            OverlayEvents.RaiseOverlayRefreshRequested(_selectedWidget.RouteId);
            
            PopulateWidgetEditor(_selectedWidget);
            await Task.Run(async () =>
            {
                await Dispatcher.InvokeAsync(() => { SaveWidgetButton.Content = "Saved!"; }
                );
                await Task.Delay(1500);
                await Dispatcher.InvokeAsync(() => { SaveWidgetButton.Content = "Save"; }
                );
            });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save widget");
        }

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

    private async void ImportWidgetButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select widget HTML file(s)",
                Filter = "HTML Files|*.html;*.htm",
                Multiselect = true
            };

            if (!string.IsNullOrEmpty(_lastFolder) && Directory.Exists(_lastFolder))
                dlg.InitialDirectory = _lastFolder;
            else if (Directory.Exists(Path.GetFullPath("./presets")))
                dlg.InitialDirectory = Path.GetFullPath("./presets");

            if (dlg.ShowDialog() == true)
            {
                await using var db = await _factory.CreateDbContextAsync();
                WidgetEntityHelper helper = new WidgetEntityHelper(_factory, null);
                _lastFolder = Path.GetDirectoryName(dlg.FileNames[0])!;
                foreach (var path in dlg.FileNames)
                {
                    try
                    {
                        await ImportSingleWidgetAsync(path, db, helper);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, $"Failed to import widget HTML file {path}");
                    }
                    _lastFolder = Path.GetDirectoryName(path)!;
                }
                await RefreshWidgetZIndicesAsync();
                RefreshWebView();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, $"Failed to import widget HTML file(s)");
        }
    }
    
    private async Task ImportSingleWidgetAsync(string path, AppDbContext db, WidgetEntityHelper helper)
    {
        var newWidget = new Widget(Path.GetFileNameWithoutExtension(path), path);

        var metadata = await helper.ExtractWidgetMetadata(path);

        newWidget.RouteId = _route!.Id;
        newWidget.X = 0;
        newWidget.Y = 0;
        newWidget.Z = _widgets.Count > 0 ? _widgets.Max(x => x.Z) + 1 : 1;

        newWidget.Width = metadata.TryGetValue("Width", out var w) &&
                          int.TryParse(w, out var parsedW)
            ? parsedW
            : 400;

        newWidget.Height = metadata.TryGetValue("Height", out var h) &&
                           int.TryParse(h, out var parsedH)
            ? parsedH
            : 200;

        newWidget.DocsUrl = metadata.TryGetValue("Url", out var u) && !string.IsNullOrWhiteSpace(u)
            && Uri.IsWellFormedUriString(u, UriKind.Absolute)
            ? u.Trim()
            : string.Empty;

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

    private void WebViewContainer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateWebViewScale();
    }
    
    private async void CopyOverlayUrl_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_route == null) return;
            await UiUtils.UiUtils.TrySetClipboardTextAsync(_route.GetRouteUrl(App.AppConfig!));
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, $"Failed to copy overlay URL");
        }
    }

    private void OpenOverlayInBrowser_Click(object sender, RoutedEventArgs e)
    {
        if (_route == null) return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _route.GetRouteUrl(App.AppConfig!),
                UseShellExecute = true
            });
        }
        catch {/**/}
    }
    protected override void OnClosed(EventArgs e)
    {
        PreviewWebView?.Dispose();
        
        WidgetEvents.WidgetPositionUpdated -= OnWidgetPositionUpdated;
        WidgetEvents.WidgetScaleUpdated -= OnWidgetScaleUpdated;
        WidgetEvents.SelectEditorWidget -= SelectWidgetFromEvent;
        WebViewContainer.SizeChanged -= WebViewContainer_SizeChanged;
        Loaded -= EditRouteWindow_Loaded;
        base.OnClosed(e);
    }
}