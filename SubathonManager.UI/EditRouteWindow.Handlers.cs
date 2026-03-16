using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Data;
using Wpf.Ui.Controls;
using TextBox = Wpf.Ui.Controls.TextBox;
// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.UI;

public partial class EditRouteWindow
{
    
#region GeneralHandlers
    private void WebViewContainer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateWebViewScale();
    }
    
    private async void CopyOverlayUrl_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_route == null) return;
            var config = AppServices.Provider.GetRequiredService<IConfig>();
            await UiUtils.UiUtils.TrySetClipboardTextAsync(_route.GetRouteUrl(config!));
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
            var config = AppServices.Provider.GetRequiredService<IConfig>();
            Process.Start(new ProcessStartInfo
            {
                FileName = _route.GetRouteUrl(config!),
                UseShellExecute = true
            });
        }
        catch {/**/}
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
                        _logger?.LogError(ex, "Failed to import widget HTML file {Path}", path);
                    }
                    _lastFolder = Path.GetDirectoryName(path)!;
                }
                await RefreshWidgetZIndicesAsync();
                RefreshWebView();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to import widget HTML file(s)");
        }
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
                {
                    cssVarToUpdate.Value = cssVar.Value;
                    cssVarToUpdate.Type = cssVar.Type;
                    cssVarToUpdate.Description = cssVar.Description;
                }
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
            
            await db.Entry(_selectedWidget).ReloadAsync();
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
            if (sender is Wpf.Ui.Controls.Button { Content: SymbolIcon icon })
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
    
    private void OpenEditorInBrowser_Click(object sender, RoutedEventArgs e)
    {
        if (_route == null) return;
        try
        {
            var config = AppServices.Provider.GetRequiredService<IConfig>();
            Process.Start(new ProcessStartInfo
            {
                FileName = _route.GetRouteUrl(config, true),
                UseShellExecute = true
            });
        }
        catch { /**/ }
    }
#endregion GeneralHandlers
    
    
#region CSSHandlers
    private void SizeValueBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox { Tag: CssVariable cssVar } tb) return;
        tb.TextChanged += SizeValueBox_TextChanged;
    }

    private void SizeValueBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox { Tag: CssVariable cssVar } tb) return;
        var unit = FindSiblingUnitBox(tb)?.SelectedItem as string ?? "px";
        cssVar.Value = tb.Text + unit;
    }

    private void SizeUnitBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is ComboBox cb)
            cb.SelectionChanged += SizeUnitBox_SelectionChanged;
    }
    
    private void SizeUnitBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { Tag: CssVariable cssVar } cb) return;
        if (e.AddedItems.Count == 0) return;
    
        var unit = cb.SelectedItem as string ?? "px";
        if ((cssVar.Value ?? "").EndsWith(unit)) return;
    
        var numericPart = IsNumberRegex().Match(cssVar.Value ?? "").Value;
        cssVar.Value = numericPart + unit;
    }

    private ComboBox? FindSiblingUnitBox(TextBox tb)
    {
        if (tb.Parent is not Panel parent) return null;
        return parent.Children.OfType<ComboBox>().FirstOrDefault();
    }
#endregion CSSHandlers  

#region JSHandlers
    private void JsIntBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox tb) return;
        tb.PreviewTextInput += (s, ev) =>
        {
            var box = (System.Windows.Controls.TextBox)s;
            if (char.IsDigit(ev.Text, 0) || ev.Text == "-" && box.SelectionStart == 0 && !box.Text.Contains('-'))
                ev.Handled = false;
            else
                ev.Handled = true;
        };
    }

    private void JsFloatBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox tb) return;
        tb.PreviewTextInput += (s, ev) =>
        {
            var box = (System.Windows.Controls.TextBox)s;
            if (char.IsDigit(ev.Text, 0) ||
                ev.Text == "-" && box.SelectionStart == 0 && !box.Text.Contains('-') ||
                ev.Text == "." && !box.Text.Contains('.'))
                ev.Handled = false;
            else
                ev.Handled = true;
        };
    }

    private void JsBoolBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: JsVariable jsVar } cb) return;
        cb.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            cb.Checked += (_, __) => jsVar.Value = "True";
            cb.Unchecked += (_, __) => jsVar.Value = "False";
        });
    }

    private void JsEventTypeSelectBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ComboBox { Tag: JsVariable jsVar } cb) return;
        var values = Enum.GetValues<SubathonEventType>()
            .Where(x => ((SubathonEventType?)x).IsEnabled())
            .Where(x => ((SubathonEventType?)x).HasNoValueConfig())
            .Select(x => x.ToString()).OrderBy(x => x);
        cb.Items.Add(string.Empty);
        foreach (var val in values) cb.Items.Add(val);
        cb.SelectedValue = string.IsNullOrWhiteSpace(jsVar.Value) ? string.Empty : jsVar.Value;
        cb.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            cb.SelectionChanged += (_, __) => jsVar.Value = $"{cb.SelectedValue}";
        });
    }

    private void JsEventSubTypeSelectBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ComboBox { Tag: JsVariable jsVar } cb) return;
        var values = Enum.GetValues<SubathonEventSubType>()
            .Where(x => ((SubathonEventSubType?)x).IsTrueEvent())
            .Select(x => x.ToString()).OrderBy(x => x);
        cb.Items.Add(string.Empty);
        foreach (var val in values) cb.Items.Add(val);
        cb.SelectedValue = string.IsNullOrWhiteSpace(jsVar.Value) ? string.Empty : jsVar.Value;
        cb.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            cb.SelectionChanged += (_, __) => jsVar.Value = $"{cb.SelectedValue}";
        });
    }

    private void JsStringSelectBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ComboBox { Tag: JsVariable jsVar } cb) return;
        var values = jsVar.Value?.Trim().Split(',') ?? [];
        foreach (var val in values) cb.Items.Add(val);
        cb.SelectedValue = values.Length > 0 ? values[0] : string.Empty;
        cb.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            cb.SelectionChanged += (_, __) =>
            {
                if (!jsVar.Value?.Contains(',') ?? true) return;
                if (jsVar.Value.StartsWith($"{cb.SelectedValue},")) return;
                var newVal = new List<string> { $"{cb.SelectedValue}" };
                foreach (var v in values)
                    if (!newVal.Contains(v)) newVal.Add(v);
                jsVar.Value = string.Join(',', newVal);
            };
        });
    }

    private void JsFileVar_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not StackPanel { Tag: JsVariable jsVar } panel) return;

        var shortContent = string.IsNullOrWhiteSpace(jsVar.Value) ? "Empty" : jsVar.Value.Split('/').Last();
        if (string.IsNullOrWhiteSpace(shortContent)) shortContent = "./";

        var valueBtn = new Wpf.Ui.Controls.Button
        {
            Content = shortContent, Width = 150, ToolTip = jsVar.Value, Margin = new Thickness(0, 0, 0, 4)
        };
        valueBtn.Click += (_, __) =>
        {
            var path = SelectFileVarPathDialog(jsVar.Type);
            if (string.IsNullOrWhiteSpace(path)) return;
            path = Path.GetFullPath(path).Replace('\\', '/');
            var widgetDir = Path.GetDirectoryName(_selectedWidget!.HtmlPath)!.Replace('\\', '/');
            if (path.Contains(widgetDir))
                path = path.Replace(widgetDir, "./").Replace("//", "/");
            jsVar.Value = path;
            valueBtn.Content = path == "./" ? "./" : path.Split('/').Last();
            valueBtn.ToolTip = path;
        };

        var openBtn = new Wpf.Ui.Controls.Button
        {
            Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Open24 },
            ToolTip = "Open", Width = 40, Height = 30, Margin = new Thickness(0, 0, 55, 2), Padding = new Thickness(2)
        };
        openBtn.Click += (_, __) =>
        {
            var file = jsVar.Value;
            if (string.IsNullOrWhiteSpace(file)) return;
            if (file.StartsWith('.'))
                file = Path.Join(Path.GetDirectoryName(_selectedWidget!.HtmlPath), file.Replace("./", ""));
            Process.Start(new ProcessStartInfo { FileName = file, UseShellExecute = true });
        };

        var removeBtn = new Wpf.Ui.Controls.Button
        {
            Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Delete24 },
            Width = 40, Height = 30, ToolTip = "Clear Value",
            Foreground = Brushes.Red, Cursor = Cursors.Hand,
            Margin = new Thickness(15, 0, 0, 0), Padding = new Thickness(2)
        };
        removeBtn.Click += (_, __) =>
        {
            jsVar.Value = string.Empty;
            valueBtn.Content = "Empty";
            valueBtn.ToolTip = string.Empty;
        };

        var btnRow2 = new StackPanel { Orientation = Orientation.Horizontal };
        btnRow2.Children.Add(openBtn);
        btnRow2.Children.Add(removeBtn);
        panel.Children.Add(valueBtn);
        panel.Children.Add(btnRow2);
    }

    private void JsEventTypeList_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Expander { Tag: JsVariable jsVar } expander) return;

        var panelValues = (jsVar.Value ?? "").Split(',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var outerPanel = new StackPanel { Orientation = Orientation.Vertical };
        var groupValues = Enum.GetValues<SubathonEventType>()
            .Where(x => ((SubathonEventType?)x).IsEnabled())
            .Where(x => x is not SubathonEventType.Command and not SubathonEventType.Unknown)
            .GroupBy(x => ((SubathonEventType?)x).GetSource())
            .OrderBy(g => SubathonEventSourceHelper.GetSourceOrder(g.Key))
            .ThenBy(g => g.Key.ToString());

        foreach (var group in groupValues)
        {
            var groupExpander = new Expander
            {
                BorderBrush = Brushes.DarkGray, BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(4, 2, 4, 2), Margin = new Thickness(0, 2, 0, 2),
                IsExpanded = false, Header = group.Key.ToString()
            };
            
            var chkboxList = new StackPanel { Orientation = Orientation.Vertical };
            foreach (var eType in group.Select(x => x.ToString()).OrderBy(x => x))
            {
                var chkBox = new CheckBox
                {
                    Content = new Wpf.Ui.Controls.TextBlock { Text = eType, TextWrapping = TextWrapping.Wrap, MaxWidth = 240 },
                    IsChecked = panelValues.Contains(eType),
                    Margin = new Thickness(2)
                };
                chkBox.Checked += (_, __) => UpdateEventListValues(jsVar, outerPanel);
                chkBox.Unchecked += (_, __) => UpdateEventListValues(jsVar, outerPanel);
                chkboxList.Children.Add(chkBox);
            }
            groupExpander.Content = chkboxList;
            outerPanel.Children.Add(groupExpander);
        }
        expander.Content = outerPanel;
    }

    private void JsEventSubTypeList_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Expander { Tag: JsVariable jsVar } expander) return;

        var panelValues = (jsVar.Value ?? "").Split(',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var chkboxList = new StackPanel { Orientation = Orientation.Vertical };
        foreach (var eType in Enum.GetNames<SubathonEventSubType>().OrderBy(x => x))
        {
            if (eType is nameof(SubathonEventSubType.CommandLike) or nameof(SubathonEventSubType.Unknown)) continue;
            var chkBox = new CheckBox
            {
                Content = new Wpf.Ui.Controls.TextBlock { Text = eType, TextWrapping = TextWrapping.Wrap, MaxWidth = 278 },
                IsChecked = panelValues.Contains(eType),
                Margin = new Thickness(2)
            };
            chkBox.Checked += (_, __) => UpdateEventListValues(jsVar, chkboxList);
            chkBox.Unchecked += (_, __) => UpdateEventListValues(jsVar, chkboxList);
            chkboxList.Children.Add(chkBox);
        }
        expander.Content = chkboxList;
    }
    
    private void JsPercentSlider_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Slider { Tag: JsVariable jsVar } slider) return;
        if (int.TryParse(jsVar.Value, out var initial))
            slider.Value = initial;

        slider.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            slider.ValueChanged += (_, args) =>
            {
                var intVal = (int)args.NewValue;
                jsVar.Value = intVal.ToString();
                // sync
                if (FindPercentSiblingBox(slider) is { } tb && tb.Text != intVal.ToString())
                    tb.Text = intVal.ToString();
            };
        });
    }

    private void JsPercentBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox { Tag: JsVariable jsVar } tb) return;
        tb.Text = int.TryParse(jsVar.Value, out var initial) ? initial.ToString() : "0";

        tb.PreviewTextInput += (_, ev) =>
        {
            var newText = tb.Text.Remove(tb.SelectionStart, tb.SelectionLength)
                .Insert(tb.SelectionStart, ev.Text);
            if (!int.TryParse(newText, out var val) || val < 0 || val > 100)
                ev.Handled = true;
        };

        tb.TextChanged += (_, __) =>
        {
            if (string.IsNullOrWhiteSpace(tb.Text)) return;
            if (!int.TryParse(tb.Text, out var val)) return;
            val = Math.Clamp(val, 0, 100);
            jsVar.Value = val.ToString();
            if (FindPercentSiblingSlider(tb) is { } slider && (int)slider.Value != val)
                slider.Value = val;
        };
    }

    private Slider? FindPercentSiblingSlider(System.Windows.Controls.TextBox tb)
    {
        return tb.Parent is not Panel parent ? null : parent.Children.OfType<Slider>().FirstOrDefault();
    }

    private System.Windows.Controls.TextBox? FindPercentSiblingBox(Slider slider)
    {
        return slider.Parent is not Panel parent ? null : parent.Children.OfType<System.Windows.Controls.TextBox>().FirstOrDefault();
    }
#endregion JSHandlers

}