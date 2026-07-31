using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Data;
using SubathonManager.Data.Widgets;
using SubathonManager.UI.Controls;
using SubathonManager.UI.UiUtils;
using SubathonManager.UI.Validation;
using SubathonManager.UI.Views;
// ReSharper disable NullableWarningSuppressionIsUsed
namespace SubathonManager.UI;

public partial class EditRouteWindow
{
#region GeneralHandlers

    private void WidgetDirtyBorder_Loaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not Border { Tag: Guid id } border) return;
        _widgetCardBorders[id] = border;
        if (_erroredWidgets.Contains(id))
        {
            border.Background = new SolidColorBrush(Color.FromArgb(60, 220, 53, 69));
            return;
        }
        UiUtils.UiHelpers.UpdateButtonPendingBorder(border,
            _unsavedCssVars.ContainsKey(id) || _unsavedJsVars.ContainsKey(id));
    }

    private void WidgetDirtyBorder_Unloaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not Border { Tag: Guid id }) return;
        _widgetCardBorders.Remove(id);
    }

    private void StashCurrentWidgetEdits()
    {
        if (_selectedWidget == null) return;

        if (_hasPendingCssChanges)
            _unsavedCssVars[_selectedWidget.Id] = _editingCssVars.Select(v => v.Clone(v.WidgetId)).ToList();
        if (_hasPendingJsChanges)
            _unsavedJsVars[_selectedWidget.Id] = _editingJsVars.Select(v => v.Clone(v.WidgetId)).ToList();

        if (_hasPendingCssChanges || _hasPendingJsChanges)
            UpdateWidgetCardDirty(_selectedWidget.Id, true);
    }

    private void UpdateWidgetCardDirty(Guid widgetId, bool isDirty)
    {
        if (!_widgetCardBorders.TryGetValue(widgetId, out var border)) return;
        if (!isDirty)
            isDirty = _unsavedCssVars.ContainsKey(widgetId) || _unsavedJsVars.ContainsKey(widgetId);
        UpdateSaveButtonBorder(border, isDirty);
    }

    private async Task SaveAllPendingWidgetChangesAsync()
    {
        StashCurrentWidgetEdits();

        var allDirtyIds = _unsavedCssVars.Keys.Union(_unsavedJsVars.Keys).ToList();
        if (allDirtyIds.Count == 0) return;

        await using var db = await _factory.CreateDbContextAsync();

        foreach (var widgetId in allDirtyIds)
        {
            var widget = await db.Widgets
                .Include(w => w.CssVariables)
                .Include(w => w.JsVariables)
                .FirstOrDefaultAsync(w => w.Id == widgetId);
            if (widget == null) continue;

            if (_unsavedCssVars.TryGetValue(widgetId, out var stashedCss))
            {
                foreach (var s in stashedCss)
                {
                    var t = widget.CssVariables.Find(v => v.Name == s.Name);
                    if (t != null) t.Value = s.Value;
                }
                db.CssVariables.UpdateRange(widget.CssVariables);
            }

            if (_unsavedJsVars.TryGetValue(widgetId, out var stashedJs))
            {
                foreach (var s in stashedJs)
                {
                    var t = widget.JsVariables.Find(v => v.Name == s.Name);
                    if (t != null) t.Value = s.Value;
                }
                db.JsVariables.UpdateRange(widget.JsVariables);
            }

            OverlayEvents.RaiseWidgetVarsUpdated(widgetId, widget.CssVariables, []);
        }

        await db.SaveChangesAsync();

        foreach (var widgetId in allDirtyIds)
        {
            _unsavedCssVars.Remove(widgetId);
            _unsavedJsVars.Remove(widgetId);
            UpdateWidgetCardDirty(widgetId, false);
        }

        _hasPendingCssChanges = false;
        _hasPendingJsChanges = false;

        if (_selectedWidget != null)
        {
            UnsubscribeCssVarChanges();
            _editingCssVars.Clear();
            _editingJsVars.Clear();
            var refreshed = await db.Widgets
                .Include(w => w.CssVariables)
                .Include(w => w.JsVariables)
                .FirstOrDefaultAsync(w => w.Id == _selectedWidget.Id);
            if (refreshed != null)
            {
                foreach (var v in refreshed.CssVariables) _editingCssVars.Add(v);
                foreach (var v in refreshed.JsVariables) _editingJsVars.Add(v);
            }
            SubscribeCssVarChanges();
        }
    }

    private void WebViewContainer_SizeChanged(object? sender, SizeChangedEventArgs e) => UpdateWebViewScale();

    private async void CopyOverlayUrl_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_route == null) return;
            var config = AppServices.Provider.GetRequiredService<IConfig>();
            await UiUtils.UiHelpers.TrySetClipboardTextAsync(_route.GetRouteUrl(config));
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to copy overlay URL");
        }
    }

    private void OpenOverlayInBrowser_Click(object? sender, RoutedEventArgs e)
    {
        if (_route == null) return;
        try
        {
            var config = AppServices.Provider.GetRequiredService<IConfig>();
            Process.Start(new ProcessStartInfo { FileName = _route.GetRouteUrl(config), UseShellExecute = true });
        }
        catch { /**/ }
    }

    private async void ImportWidgetButton_Click(object? sender, RoutedEventArgs e)
        => await RunImportAsync("Select widget file(s)", [
            new FilePickerFileType("Widgets") { Patterns = ["*.html", "*.htm", "*.smw", "*.smwc"] },
            new FilePickerFileType("Widget Packages (*.smw)") { Patterns = ["*.smw"] },
            new FilePickerFileType("Widget Collections (*.smwc)") { Patterns = ["*.smwc"] },
            new FilePickerFileType("HTML Widgets") { Patterns = ["*.html", "*.htm"] }
        ]);

    private async void ImportAssetButton_Click(object? sender, RoutedEventArgs e)
        => await RunImportAsync("Select asset file(s)", [
            new FilePickerFileType("Asset Files")
            {
                Patterns =
                [
                    "*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp", "*.avif", "*.bmp", "*.svg",
                    "*.mp4", "*.m4v", "*.webm", "*.ogm", "*.mkv", "*.mov"
                ]
            }
        ]);

    #region EDITOR FILE DROP

    private static readonly string[] DroppableExtensions =
    [
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".avif", ".bmp", ".svg",
        ".mp4", ".m4v", ".webm", ".ogm", ".mkv", ".mov",
        ".html", ".htm", WidgetPackPaths.PackExtension, WidgetCollectionInstaller.CollectionExtension
    ];

    private void TryEnableEditorFileDrop()
    {
        try
        {
            DragDrop.SetAllowDrop(this, true);
            AddHandler(DragDrop.DragOverEvent, EditorDragOver);
            AddHandler(DragDrop.DropEvent, EditorDrop);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Drag and drop unavailable for the overlay editor");
        }
    }

    private void EditorDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = AcceptedDropPaths(e).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void EditorDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;

        var paths = AcceptedDropPaths(e);
        if (paths.Count == 0) return;

        try
        {
            await ImportPathsAsync(paths);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to import dropped file(s)");
        }
    }

    private List<string> AcceptedDropPaths(DragEventArgs e)
    {
        var accepted = new List<string>();

        if (_route == null || IsOverWidgetEditorColumn(e)) return accepted;

        try
        {
            var files = e.DataTransfer.TryGetFiles();
            if (files == null) return accepted;

            accepted.AddRange(from item in files select item.TryGetLocalPath() into path where !string.IsNullOrWhiteSpace(path) && File.Exists(path) where DroppableExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) select path);
        }
        catch {/**/}

        return accepted;
    }

    private bool IsOverWidgetEditorColumn(DragEventArgs e)
    {
        try
        {
            var p = e.GetPosition(WidgetEditorColumn);
            var size = WidgetEditorColumn.Bounds.Size;

            return p is { X: >= 0, Y: >= 0 } && p.X <= size.Width && p.Y <= size.Height;
        }
        catch
        {
            return false;
        }
    }

    #endregion

    private async Task RunImportAsync(string title, FilePickerFileType[] filters)
    {
        try
        {
            IStorageFolder? start = null;
            if (!string.IsNullOrEmpty(_lastFolder) && Directory.Exists(_lastFolder))
                start = await StorageProvider.TryGetFolderFromPathAsync(_lastFolder);
            else if (Directory.Exists(Path.GetFullPath("./presets")))
                start = await StorageProvider.TryGetFolderFromPathAsync(Path.GetFullPath("./presets"));

            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = true,
                SuggestedStartLocation = start,
                FileTypeFilter = filters
            });

            if (files.Count == 0) return;

            await ImportPathsAsync(files.Select(f => f.Path.LocalPath).ToList());
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to import widget file(s)");
        }
    }

    private async Task ImportPathsAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;

        await using var db = await _factory.CreateDbContextAsync();
        var helper = new WidgetEntityHelper(_factory, null);
        bool importedPack = false;

        foreach (var path in paths)
        {
            _lastFolder = Path.GetDirectoryName(path)!;
            try
            {
                if (path.EndsWith(WidgetCollectionInstaller.CollectionExtension, StringComparison.OrdinalIgnoreCase))
                {
                    await ImportWidgetCollectionAsync(path, db, helper);
                    importedPack = true;
                }
                else if (path.EndsWith(WidgetPackPaths.PackExtension, StringComparison.OrdinalIgnoreCase))
                {
                    await ImportWidgetPackAsync(path, db, helper);
                    importedPack = true;
                }
                else
                {
                    await ImportSingleWidgetAsync(path, db, helper);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to import widget file {Path}", path);
            }
        }

        if (importedPack)
            foreach (var existing in _widgets.ToList())
                ReseatWidgetCard(existing);

        await RefreshWidgetZIndicesAsync();
        if (_route != null) OverlayEvents.RaiseOverlayRefreshRequested(_route.Id);
        RefreshWebView();
    }

    private void ReloadVars_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedWidget == null) return;
        _unsavedCssVars.Remove(_selectedWidget.Id);
        _hasPendingCssChanges = false;
        UpdateWidgetCardDirty(_selectedWidget.Id, false);
        var widgetHelper = new WidgetEntityHelper(_factory, null);
        widgetHelper.SyncCssVariables(_selectedWidget);
        widgetHelper.SyncJsVariables(_selectedWidget);

        using var db = _factory.CreateDbContext();
        _selectedWidget = db.Widgets.Include(wX => wX.CssVariables)
            .Include(wX => wX.JsVariables)
            .FirstOrDefault(wX => wX.Id == _selectedWidget.Id);

        CssVarsList.ItemsSource = null;
        UnsubscribeCssVarChanges();
        _editingCssVars.Clear();
        foreach (var v in _selectedWidget!.CssVariables) _editingCssVars.Add(v);
        SubscribeCssVarChanges();
        CssVarsList.ItemsSource = _editingCssVars;
        PopulateJsVars();
    }

    private async void SaveWidgetButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_selectedWidget == null) return;
            var widgetHelper = new WidgetEntityHelper(_factory, null);

            widgetHelper.SyncCssVariables(_selectedWidget);
            widgetHelper.SyncJsVariables(_selectedWidget);

            await using var db = await _factory.CreateDbContextAsync();
            _selectedWidget.Name = WidgetNameBox.Text ?? string.Empty;
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
            _unsavedCssVars.Remove(_selectedWidget.Id);
            _unsavedJsVars.Remove(_selectedWidget.Id);
            _hasPendingCssChanges = false;
            _hasPendingJsChanges = false;
            UpdateWidgetCardDirty(_selectedWidget.Id, false);
            UnsubscribeCssVarChanges();
            _editingCssVars.Clear();
            foreach (var cssVar in _selectedWidget.CssVariables) _editingCssVars.Add(cssVar);

            SubscribeCssVarChanges();
            var listEntry = _widgets.FirstOrDefault(wi => wi.Id == _selectedWidget.Id);
            if (listEntry != null)
            {
                int index = _widgets.IndexOf(listEntry);
                _widgets.Remove(listEntry);
                _widgets.Insert(index, _selectedWidget);
            }

            OverlayEvents.RaiseWidgetRefreshRequested(_selectedWidget.Id,
                _selectedWidget.X, _selectedWidget.Y, _selectedWidget.Width, _selectedWidget.Height,
                _selectedWidget.ScaleX, _selectedWidget.ScaleY);

            await db.Entry(_selectedWidget).ReloadAsync();
            UpdateSaveButtonBorder(SaveButtonBorder, false);
            SaveWidgetButton.Content = "Saved!";
            await Task.Delay(1500);
            SaveWidgetButton.Content = "Save";
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save widget");
        }
    }

    private void AssetOpenFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedWidget == null) return;
        string path = _selectedWidget.HtmlPath;
        if (!UiHelpers.RevealInFileManager(path))
            _logger?.LogError("Failed to open asset folder for {Path}", path);
    }

    private void OpenWidgetDocumentation_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedWidget == null || string.IsNullOrWhiteSpace(_selectedWidget.DocsUrl)
                                    || !Uri.IsWellFormedUriString(_selectedWidget.DocsUrl, UriKind.Absolute)) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = _selectedWidget.DocsUrl, UseShellExecute = true });
        }
        catch { /**/ }
    }

    private void VisibilityIcon_Loaded(object? sender, RoutedEventArgs e)
    {
        if (sender is SymIcon icon && icon.DataContext is Widget widget)
            icon.Glyph = widget.Visibility ? "Eye16" : "EyeOff16";
    }

    private void OnWidgetActionRequested(Guid widgetId, WidgetContextAction action)
    {
        Dispatcher.UIThread.Post(async void () =>
        {
            try
            {
                var widget = _widgets.FirstOrDefault(w => w.Id == widgetId);
                if (widget == null) return;

                switch (action)
                {
                    case WidgetContextAction.ToggleVisibility:
                        await SetWidgetVisibilityAsync(widget, !widget.Visibility);
                        break;
                    case WidgetContextAction.Clone:
                        await CloneWidgetAsync(widget);
                        break;
                    case WidgetContextAction.Delete:
                        await DeleteWidgetAsync(widget);
                        break;
                    case WidgetContextAction.ResetScale:
                        await ResetWidgetScaleAsync(widget);
                        break;
                    case WidgetContextAction.Refresh:
                        RefreshWidget(widget);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Widget context action {Action} failed", action);
            }
        });
    }

    private static void RefreshWidget(Widget widget)
        => OverlayEvents.RaiseWidgetRefreshRequested(widget.Id, widget.X, widget.Y,
            widget.Width, widget.Height, widget.ScaleX, widget.ScaleY);

    private async Task ResetWidgetScaleAsync(Widget widget)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var tracked = await db.Widgets.FirstOrDefaultAsync(w => w.Id == widget.Id);
        if (tracked == null) return;

        tracked.ScaleX = 1;
        tracked.ScaleY = 1;
        await db.SaveChangesAsync();

        widget.ScaleX = 1;
        widget.ScaleY = 1;
        if (_selectedWidget?.Id == widget.Id)
        {
            _selectedWidget.ScaleX = 1;
            _selectedWidget.ScaleY = 1;
            WidgetScaleXBox.Text = "1";
            WidgetScaleYBox.Text = "1";
        }

        RefreshWebView();
        if (_route != null) OverlayEvents.RaiseOverlayRefreshRequested(_route.Id);
    }

    private async void ToggleVisibility_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var widget = GetWidgetFromSender(sender);
            if (widget == null) return;

            bool visibility = await SetWidgetVisibilityAsync(widget, !widget.Visibility);
            if (sender is Button { Content: SymIcon icon })
                icon.Glyph = visibility ? "Eye16" : "EyeOff16";
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to update widget visibility");
        }
    }

    private async Task<bool> SetWidgetVisibilityAsync(Widget widget, bool visibility)
    {
        widget.Visibility = visibility;

        await using var db = await _factory.CreateDbContextAsync();
        var tracked = await db.Widgets.Include(w => w.CssVariables)
            .Include(w => w.JsVariables)
            .FirstOrDefaultAsync(w => w.Id == widget.Id);
        if (tracked == null) return visibility;

        tracked.Visibility = visibility;
        if (_selectedWidget?.Id == tracked.Id) _selectedWidget.Visibility = visibility;

        await db.SaveChangesAsync();
        RefreshWebView();
        ReseatWidgetCard(widget);
        OverlayEvents.RaiseOverlayRefreshRequested(tracked.RouteId);

        return visibility;
    }

    private void EditWidget_Click(object? sender, RoutedEventArgs e)
    {
        var widget = GetWidgetFromSender(sender);
        if (widget == null) return;
        if (_erroredWidgets.Contains(widget.Id))
        {
            ShowWidgetErrorPopup(widget);
            return;
        }
        PopulateWidgetEditor(widget);
    }

    private void WidgetCard_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: Widget widget }) return;
        if (_erroredWidgets.Contains(widget.Id))
        {
            ShowWidgetErrorPopup(widget);
            return;
        }
        PopulateWidgetEditor(widget);
    }

    private async void DeleteWidget_Click(object? sender, RoutedEventArgs e)
    {
        var wi = GetWidgetFromSender(sender);
        if (wi == null) return;
        await DeleteWidgetAsync(wi);
    }

    private async Task DeleteWidgetAsync(Widget wi)
    {
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
            _erroredWidgets.Remove(wi.Id);
            await RefreshWidgetZIndicesAsync();
            RefreshWebView();
            OverlayEvents.RaiseOverlayRefreshRequested(routeId);
            if (wi.Id == _selectedWidget?.Id) PopulateWidgetEditor(null);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to delete widget");
        }
    }

    private void ShowWidgetErrorPopup(Widget widget)
        => ShowConfirmFlyout(WidgetAnchor(widget),
            "Widget Error",
            $"Could not find the file for \"{widget.Name}\".",
            widget.HtmlPath,
            "Delete Widget",
            destructive: true,
            onConfirm: () => _ = DeleteWidgetAsync(widget));

    #region CARD POPUPS

    private Control WidgetAnchor(Widget widget)
        => _widgetCardBorders.TryGetValue(widget.Id, out var border) ? border : this;

    private StackPanel BuildFlyoutPanel(string title, string body, string? detail)
    {
        var panel = new StackPanel { Spacing = 6, MaxWidth = 320 };

        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });

        panel.Children.Add(new TextBlock
        {
            Text = body,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        });

        if (!string.IsNullOrWhiteSpace(detail))
        {
            var detailText = new TextBlock
            {
                Text = detail,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = TertiaryTextBrush()
            };
            ToolTip.SetTip(detailText, detail);
            panel.Children.Add(detailText);
        }

        return panel;
    }

    private IBrush TertiaryTextBrush()
        => this.TryFindResource("UiBrushTextTertiary", ActualThemeVariant, out var res) && res is IBrush brush
            ? brush
            : Brushes.Gray;

    private void ShowMessageFlyout(Control anchor, string title, string message, string? detail = null)
        => new Flyout
        {
            Placement = PlacementMode.Top,
            Content = BuildFlyoutPanel(title, message, detail)
        }.ShowAt(anchor);

    private void ShowConfirmFlyout(Control anchor, string title, string body, string? detail,
        string confirmText, bool destructive, Action onConfirm)
    {
        var flyout = new Flyout { Placement = PlacementMode.Top };
        var panel = BuildFlyoutPanel(title, body, detail);

        var confirmButton = new Button { Content = confirmText, MinWidth = 90 };
        confirmButton.Classes.Add(destructive ? "danger" : "accent");

        var cancelButton = new Button { Content = "Cancel", MinWidth = 90 };

        var row = new StackPanel
        {
            Orientation = global::Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            Margin = new global::Avalonia.Thickness(0, 6, 0, 0),
            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right
        };
        row.Children.Add(cancelButton);
        row.Children.Add(confirmButton);
        panel.Children.Add(row);

        cancelButton.Click += (_, _) => flyout.Hide();
        confirmButton.Click += (_, _) =>
        {
            flyout.Hide();
            onConfirm();
        };

        flyout.Content = panel;
        flyout.ShowAt(anchor);
    }

    #endregion

    private async void ExportWidget_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var widget = (sender as Control)?.Tag as Widget ?? GetWidgetFromSender(sender);
            if (widget == null || widget.Type.IsAsset()) return;

            if (!WidgetFiles.Current.Exists(widget.HtmlPath))
            {
                ShowWidgetErrorPopup(widget);
                return;
            }

            var dialog = new ExportWidgetDialog(widget);
            await dialog.ShowDialog(this);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to open widget export dialog");
        }
    }

    private void OpenWidgetFolder_Click(object? sender, RoutedEventArgs e)
    {
        var widget = (sender as Control)?.Tag as Widget ?? GetWidgetFromSender(sender);
        if (widget == null) return;

        string target = WidgetPackPaths.TryResolve(widget.HtmlPath, out var packFile, out _, out _, out _)
            ? packFile
            : widget.HtmlPath;

        if (!UiHelpers.RevealInFileManager(target))
            _logger?.LogError("Failed to open folder for {Path}", target);
    }

    private void UpdateWidgetVersion_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;

        var widget = button.Tag as Widget ?? GetWidgetFromSender(sender);
        if (widget == null) return;

        if (!WidgetPackPaths.TryResolve(widget.HtmlPath, out _, out var entry, out _, out var version))
            return;

        var update = WidgetPackInstaller.FindUpdate(widget.HtmlPath);
        if (update == null) return;

        ShowConfirmFlyout(button,
            $"Update \"{widget.Name}\"?",
            $"{WidgetPackPaths.DisplayVersion(version)}  -  {WidgetPackPaths.DisplayVersion(update.Version)}",
            "Your configured values are kept if the new version still has a matching variable. "
                + "Anything it dropped or renamed will be lost.",
            "Update",
            destructive: false,
            onConfirm: () => _ = ApplyWidgetVersionUpdateAsync(widget, entry, update, button));
    }

    private async Task ApplyWidgetVersionUpdateAsync(Widget widget, string entry,
        WidgetPackInstaller.PackUpdate update, Control anchor)
    {
        try
        {
            string newMountRoot = Path.Combine(
                Path.GetDirectoryName(update.PackFile)!,
                Path.GetFileNameWithoutExtension(update.PackFile));

            string newEntry = string.IsNullOrWhiteSpace(update.Entry) ? entry : update.Entry;
            string newPath = WidgetPackPaths.EntryPathIn(newMountRoot, newEntry);

            WidgetPackPaths.InvalidateResolveCache();

            if (!WidgetFiles.Current.Exists(newPath))
            {
                ShowMessageFlyout(anchor, "Update Failed",
                    $"Version {WidgetPackPaths.DisplayVersion(update.Version)} does not contain \"{newEntry}\".");
                return;
            }

            await using var db = await _factory.CreateDbContextAsync();
            var tracked = await db.Widgets.FirstOrDefaultAsync(w => w.Id == widget.Id);
            if (tracked == null) return;

            tracked.HtmlPath = newPath;
            await db.SaveChangesAsync();
            widget.HtmlPath = newPath;

            var helper = new WidgetEntityHelper(_factory, null);
            helper.SyncCssVariables(widget);
            helper.SyncJsVariables(widget);

            ReseatWidgetCard(widget);

            if (_route != null) OverlayEvents.RaiseOverlayRefreshRequested(_route.Id);
            RefreshWebView();
            if (_selectedWidget?.Id == widget.Id) PopulateWidgetEditor(widget);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to update widget {Name} to version {Version}", widget.Name, update.Version);
            ShowMessageFlyout(anchor, "Update Failed", ex.Message);
        }
    }

    private static void ShowMessageFlyout(Control anchor, string title, string message)
    {
        var panel = new StackPanel { Spacing = 6, MaxWidth = 300 };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(new TextBlock
        {
            Text = message,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        });

        new Flyout { Placement = PlacementMode.Top, Content = panel }.ShowAt(anchor);
    }

    private void ReseatWidgetCard(Widget widget)
    {
        int index = _widgets.IndexOf(widget);
        if (index < 0) return;
        _widgets.RemoveAt(index);
        _widgets.Insert(index, widget);
    }

    private void UnpackWidget_Click(object? sender, RoutedEventArgs e)
    {
        var widget = (sender as Control)?.Tag as Widget ?? GetWidgetFromSender(sender);
        if (widget == null) return;

        var location = WidgetPackPaths.Resolve(widget.HtmlPath);
        if (location == null) return;

        if (!WidgetPackPaths.TryResolve(widget.HtmlPath, out _, out var entry, out _, out _))
            return;

        var manifest = WidgetPackInstaller.ReadManifest(location.PackFileStr);
        string targetDir = WidgetPackPaths.UnpackRootFor(
            location,
            manifest?.Author ?? string.Empty,
            manifest?.Group ?? string.Empty,
            string.IsNullOrWhiteSpace(manifest?.Name) ? widget.Name : manifest.Name);

        bool wouldOverwrite = Directory.Exists(targetDir) &&
                              Directory.EnumerateFileSystemEntries(targetDir).Any();

        if (!wouldOverwrite)
        {
            _ = ApplyUnpackAsync(widget, entry, targetDir);
            return;
        }

        ShowConfirmFlyout(WidgetAnchor(widget),
            "Unpack Widget",
            "This folder already has content. Unpacking will overwrite the files in it.",
            targetDir,
            "Unpack",
            destructive: true,
            onConfirm: () => _ = ApplyUnpackAsync(widget, entry, targetDir));
    }

    private async Task ApplyUnpackAsync(Widget widget, string entry, string targetDir)
    {
        var anchor = WidgetAnchor(widget);

        try
        {
            if (WidgetFiles.Current is not WidgetPackFileSystem packs || !packs.Unpack(widget.HtmlPath, targetDir))
            {
                ShowMessageFlyout(anchor, "Unpack Failed", "Could not extract the widget package.");
                return;
            }

            string loosePath = Path.Combine(targetDir, entry.Replace('/', Path.DirectorySeparatorChar));

            await using var db = await _factory.CreateDbContextAsync();
            var tracked = await db.Widgets.FirstOrDefaultAsync(w => w.Id == widget.Id);
            if (tracked == null) return;

            tracked.HtmlPath = loosePath;
            await db.SaveChangesAsync();
            widget.HtmlPath = loosePath;

            var helper = new WidgetEntityHelper(_factory, null);
            helper.SyncCssVariables(widget);
            helper.SyncJsVariables(widget);

            ReseatWidgetCard(widget);

            if (_route != null) OverlayEvents.RaiseOverlayRefreshRequested(_route.Id);
            RefreshWebView();
            if (_selectedWidget?.Id == widget.Id) PopulateWidgetEditor(widget);

            UiHelpers.RevealInFileManager(loosePath);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to unpack widget {Name}", widget.Name);
            ShowMessageFlyout(anchor, "Unpack Failed", ex.Message);
        }
    }

    private async void CopyWidget_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var w = GetWidgetFromSender(sender);
            if (w == null) return;
            await CloneWidgetAsync(w);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to duplicate widget");
        }
    }

    private async Task CloneWidgetAsync(Widget widget)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Entry(widget).ReloadAsync();

        var clone = widget.Clone(widget.RouteId, widget.Name + " (Copy)", _widgets.Count + 1);
        db.Widgets.Add(clone);
        db.CssVariables.AddRange(clone.CssVariables);
        db.JsVariables.AddRange(clone.JsVariables);
        await db.SaveChangesAsync();

        _widgets.Insert(0, clone);
        await RefreshWidgetZIndicesAsync();
        RefreshWebView();

        if (_route != null) OverlayEvents.RaiseOverlayRefreshRequested(_route.Id);
    }

    private async void MoveUp_Click(object? sender, RoutedEventArgs e)
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

    private async void MoveDown_Click(object? sender, RoutedEventArgs e)
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

    private async void SaveRouteButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_route == null) return;
        try
        {
            await SaveAllPendingWidgetChangesAsync();

            await using var db = await _factory.CreateDbContextAsync();
            await db.Entry(_route).ReloadAsync();

            _route.Name = (RouteNameBox.Text ?? string.Empty).Trim();
            if (int.TryParse(RouteWidthBox.Text, out var w)) _route.Width = w;
            if (int.TryParse(RouteHeightBox.Text, out var h)) _route.Height = h;

            await db.SaveChangesAsync();
            UpdateWebViewScale();
            OverlayEvents.RaiseOverlayRefreshRequested(_route.Id);

            SaveRouteButton.Content = "Saved!";
            await Task.Delay(1500);
            SaveRouteButton.Content = "Save";
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save overlay");
        }
    }

    private void OpenEditorInBrowser_Click(object? sender, RoutedEventArgs e)
    {
        if (_route == null) return;
        try
        {
            var config = AppServices.Provider.GetRequiredService<IConfig>();
            Process.Start(new ProcessStartInfo { FileName = _route.GetRouteUrl(config, true), UseShellExecute = true });
        }
        catch { /**/ }
    }

    private void ExportRoute_Click(object? sender, RoutedEventArgs e)
    {
        if (_route == null) return;
        var dialog = new ExportOverlayDialog(_route);
        _ = dialog.ShowDialog(this);
    }

    private void SuppressUnsavedChanges(Action action)
    {
        _suppressCount++;
        try { action(); }
        finally { _suppressCount--; }
    }

    private void AttachChangeHandler(object? sender, RoutedEventArgs routedEventArgs)
    {
        void Attach()
        {
            switch (sender)
            {
                case Expander:
                    break;
                case TextBox tb:
                    tb.TextChanged -= Value_OnChanged;
                    tb.TextChanged += Value_OnChanged;
                    break;
                case ComboBox cb:
                    cb.SelectionChanged -= Value_OnChanged;
                    cb.SelectionChanged += Value_OnChanged;
                    break;
                case CheckBox chk:
                    chk.IsCheckedChanged -= Value_OnChanged;
                    chk.IsCheckedChanged += Value_OnChanged;
                    break;
                case Slider sld:
                    sld.ValueChanged -= Value_OnChanged;
                    sld.ValueChanged += Value_OnChanged;
                    break;
                case CssColorPicker csscp:
                    csscp.ColorChanged -= Value_OnChanged;
                    csscp.ColorChanged += Value_OnChanged;
                    break;
            }
        }
        SuppressUnsavedChanges(Attach);
    }

    private void Value_OnChanged(object? sender, RoutedEventArgs e)
    {
        if (_suppressCount > 0) return;
        if (sender is TextBox { IsFocused: false }) return;
        UpdateSaveButtonBorder(SaveButtonBorder, true);
        if (sender is Control { Tag: JsVariable }) _hasPendingJsChanges = true;
    }

    private void IntBox_Loaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox tb) return;
        if (string.IsNullOrWhiteSpace(tb.Text)) tb.Text = "0";
        NumericInputBehaviour.SetMode(tb, NumericInputBehaviour.NumericMode.SignedInteger);
        AttachChangeHandler(sender, e);
    }

    private void FloatBox_Loaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox tb) return;
        if (string.IsNullOrWhiteSpace(tb.Text)) tb.Text = "0";
        NumericInputBehaviour.SetMode(tb, NumericInputBehaviour.NumericMode.SignedDecimal);
        AttachChangeHandler(sender, e);
    }

#endregion GeneralHandlers

#region CSSHandlers

    private void SizeValueBox_Loaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox { Tag: CssVariable } tb) return;
        tb.TextChanged += SizeValueBox_TextChanged;
        AttachChangeHandler(sender, e);
    }

    private void SizeValueBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox { Tag: CssVariable cssVar } tb) return;
        if (string.IsNullOrWhiteSpace(tb.Text)) tb.Text = "0";
        var unit = FindSiblingUnitBox(tb)?.SelectedItem as string ?? "px";
        cssVar.Value = tb.Text + unit;
    }

    private void SizeUnitBox_Loaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not ComboBox cb) return;
        cb.SelectionChanged += SizeUnitBox_SelectionChanged;
        AttachChangeHandler(sender, e);
    }

    private void SizeUnitBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { Tag: CssVariable cssVar } cb) return;
        if (e.AddedItems.Count == 0) return;

        var unit = cb.SelectedItem as string ?? "px";
        if ((cssVar.Value ?? "").EndsWith(unit)) return;

        var numericPart = IsNumberRegex().Match(cssVar.Value ?? "").Value;
        cssVar.Value = numericPart + unit;
    }

    private ComboBox? FindSiblingUnitBox(TextBox tb)
        => tb.Parent is not Panel parent ? null : parent.Children.OfType<ComboBox>().FirstOrDefault();

    private void OpacitySlider_Loaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not Slider { Tag: CssVariable cssVar } slider) return;
        if (float.TryParse(cssVar.Value, out var initial)) slider.Value = initial;

        slider.ValueChanged += (_, args) =>
        {
            var floatVal = (float)args.NewValue;
            cssVar.Value = floatVal.ToString(CultureInfo.InvariantCulture);
            if (FindPercentSiblingBox(slider) is { } tb && tb.Text != floatVal.ToString(CultureInfo.InvariantCulture))
                tb.Text = floatVal.ToString(CultureInfo.InvariantCulture);
        };
        AttachChangeHandler(sender, e);
    }

    private void OpacityBox_Loaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox { Tag: CssVariable cssVar } tb) return;
        tb.Text = float.TryParse(cssVar.Value, out var initial) ? initial.ToString(CultureInfo.InvariantCulture) : "0";

        tb.TextChanged += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(tb.Text)) return;
            if (!float.TryParse(tb.Text, out var val)) return;
            val = Math.Clamp(val, 0, 1);
            cssVar.Value = val.ToString(CultureInfo.InvariantCulture);
            if (FindPercentSiblingSlider(tb) is { } slider && Math.Abs((float)slider.Value - val) > 0.001)
                slider.Value = val;
        };
        AttachChangeHandler(sender, e);
    }

    private void ResetVars_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedWidget == null) return;

        _unsavedCssVars.Remove(_selectedWidget.Id);
        _unsavedJsVars.Remove(_selectedWidget.Id);
        _hasPendingCssChanges = false;
        _hasPendingJsChanges = false;
        UpdateWidgetCardDirty(_selectedWidget.Id, false);
        UpdateSaveButtonBorder(SaveButtonBorder, false);

        using var db = _factory.CreateDbContext();
        var widget = db.Widgets
            .Include(w => w.CssVariables)
            .Include(w => w.JsVariables)
            .FirstOrDefault(w => w.Id == _selectedWidget.Id);
        if (widget == null) return;

        UnsubscribeCssVarChanges();
        _editingCssVars.Clear();
        _editingJsVars.Clear();
        foreach (var v in widget.CssVariables) _editingCssVars.Add(v);
        foreach (var v in widget.JsVariables) _editingJsVars.Add(v);
        SubscribeCssVarChanges();
        PopulateJsVars();

        OverlayEvents.RaiseWidgetVarsUpdated(_selectedWidget.Id, widget.CssVariables, []);
    }

    private void SubscribeCssVarChanges()
    {
        foreach (var v in _editingCssVars) v.PropertyChanged += OnEditingCssVarChanged;
        _editingCssVars.CollectionChanged += OnEditingCssVarsCollectionChanged;
    }

    private void UnsubscribeCssVarChanges()
    {
        foreach (var v in _editingCssVars) v.PropertyChanged -= OnEditingCssVarChanged;
        _editingCssVars.CollectionChanged -= OnEditingCssVarsCollectionChanged;
    }

    private void OnEditingCssVarsCollectionChanged(object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
            foreach (CssVariable v in e.OldItems) v.PropertyChanged -= OnEditingCssVarChanged;
        if (e.NewItems != null)
            foreach (CssVariable v in e.NewItems) v.PropertyChanged += OnEditingCssVarChanged;
    }

    private void OnEditingCssVarChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressCount > 0) return;
        if (e.PropertyName != nameof(CssVariable.Value) || _selectedWidget == null) return;
        _hasPendingCssChanges = true;
        _cssLivePreviewTimer?.Change(120, Timeout.Infinite);
        UpdateSaveButtonBorder(SaveButtonBorder, true);
    }

#endregion CSSHandlers

#region JSHandlers

    private void JsBoolBox_Loaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: JsVariable jsVar } cb) return;
        cb.IsCheckedChanged += (_, _) => jsVar.Value = cb.IsChecked == true ? "True" : "False";
        AttachChangeHandler(sender, e);
    }

    private void JsEventTypePickerBtn_Loaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: JsVariable jsVar } btn) return;
        SetJsEventTypeButtonLabel(btn, jsVar.Value);
    }

    private void JsEventTypePickerBtn_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: JsVariable jsVar } btn) return;

        var current = jsVar.Value ?? string.Empty;
        var entries = new List<EventTypeMenuEntry>();

        var values = Enum.GetValues<SubathonEventType>()
            .Where(x => x.IsEnabled())
            .Where(x => ((SubathonEventType?)x).HasNoValueConfig())
            .OrderBy(x => x.GetOrderNumber());

        foreach (var val in values)
        {
            if (val == SubathonEventType.GoAffProOrder)
            {
                foreach (var store in GoAffProStoreRegistry.All().Where(s => s.Enabled))
                {
                    var capturedStore = store;
                    entries.Add(new EventTypeMenuEntry(
                        val.GetSource(), store.EventName, current == store.InternalEventName,
                        () => SetJsEventTypeValue(btn, jsVar, capturedStore.InternalEventName)));
                }
                continue;
            }

            var captured = val;
            entries.Add(new EventTypeMenuEntry(
                val.GetSource(), val.GetLabel(), current == captured.ToString(),
                () => SetJsEventTypeValue(btn, jsVar, captured.ToString())));
        }

        EventTypeMenu.Show(btn, entries, groupBySourceType: false,
            clearLabel: "- none -", onClear: () => SetJsEventTypeValue(btn, jsVar, string.Empty));
    }

    private void SetJsEventTypeValue(Button btn, JsVariable jsVar, string value)
    {
        jsVar.Value = value;
        SetJsEventTypeButtonLabel(btn, value);
        Value_OnChanged(btn, new RoutedEventArgs());
    }

    private static void SetJsEventTypeButtonLabel(Button btn, string? value)
    {
        if (btn.Content is not Grid grid) return;
        var label = grid.Children.OfType<TextBlock>().FirstOrDefault();
        label?.Text = JsEventTypeDisplay(value);
    }

    private static string JsEventTypeDisplay(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "- none -";
        if (Enum.TryParse<SubathonEventType>(value, out var et))
            return $"{et.GetSource()} - {et.GetLabel()}";
        var store = GoAffProStoreRegistry.All().FirstOrDefault(s => s.InternalEventName == value);
        return store != null ? $"{SubathonEventSource.GoAffPro} - {store.EventName}" : value;
    }

    private void JsEventSubTypeSelectBox_Loaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not ComboBox { Tag: JsVariable jsVar } cb) return;
        var values = Enum.GetValues<SubathonEventSubType>()
            .Where(x => ((SubathonEventSubType?)x).IsTrueEvent())
            .OrderBy(x => x.GetOrderNumber());
        cb.Items.Add(string.Empty);
        foreach (var val in values) cb.Items.Add(val.ToString());
        cb.SelectedItem = string.IsNullOrWhiteSpace(jsVar.Value) ? string.Empty : jsVar.Value;
        Dispatcher.UIThread.Post(() =>
        {
            cb.SelectionChanged += (_, _) => jsVar.Value = $"{cb.SelectedItem}";
        }, DispatcherPriority.Loaded);
        AttachChangeHandler(sender, e);
    }

    private void JsStringSelectBox_Loaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not ComboBox { Tag: JsVariable jsVar } cb) return;
        var values = jsVar.Value?.Trim().Split(',') ?? [];
        foreach (var val in values) cb.Items.Add(val);
        cb.SelectedItem = values.Length > 0 ? values[0] : string.Empty;
        Dispatcher.UIThread.Post(() =>
        {
            cb.SelectionChanged += (_, _) =>
            {
                if (!jsVar.Value?.Contains(',') ?? true) return;
                if (jsVar.Value!.StartsWith($"{cb.SelectedItem},")) return;
                var newVal = new List<string> { $"{cb.SelectedItem}" };
                foreach (var v in values)
                    if (!newVal.Contains(v)) newVal.Add(v);
                jsVar.Value = string.Join(',', newVal);
            };
        }, DispatcherPriority.Loaded);
        AttachChangeHandler(sender, e);
    }

    private void JsFileVar_Loaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not StackPanel { Tag: JsVariable jsVar } panel) return;

        if (panel.Children.Count > 0) return;

        var shortContent = string.IsNullOrWhiteSpace(jsVar.Value) ? "Empty" : jsVar.Value.Split('/').Last();
        if (string.IsNullOrWhiteSpace(shortContent)) shortContent = "./";

        var valueBtn = new Button { Content = shortContent, Width = 150, Margin = new global::Avalonia.Thickness(0, 0, 0, 4) };
        ToolTip.SetTip(valueBtn, jsVar.Value);
        valueBtn.Click += async (_, _) =>
        {
            var path = await SelectFileVarPathDialog(jsVar.Type);
            if (string.IsNullOrWhiteSpace(path)) return;
            ApplyFileVarPath(jsVar, valueBtn, path);
        };

        var openBtn = new Button
        {
            Content = new SymIcon { Glyph = "Open24" },
            Width = 40, Height = 30, Margin = new global::Avalonia.Thickness(0, 0, 55, 2), Padding = new global::Avalonia.Thickness(2)
        };
        openBtn.Classes.Add("iconbtn");
        ToolTip.SetTip(openBtn, "Open");
        openBtn.Click += (_, _) =>
        {
            var file = jsVar.Value;
            if (string.IsNullOrWhiteSpace(file)) return;
            if (ResourcePaths.IsResourceUrl(file))
            {
                file = ResourcePaths.ToLocalPath(file) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(file)) return;
            }
            else if (file.StartsWith('.'))
                file = Path.Join(Path.GetDirectoryName(_selectedWidget!.HtmlPath), file.Replace("./", ""));
            Process.Start(new ProcessStartInfo { FileName = file, UseShellExecute = true });
        };

        var removeBtn = new Button
        {
            Content = new SymIcon { Glyph = "Delete24" },
            Width = 40, Height = 30, Foreground = Brushes.Red,
            Margin = new global::Avalonia.Thickness(15, 0, 0, 0), Padding = new global::Avalonia.Thickness(2)
        };
        removeBtn.Classes.Add("iconbtn");
        ToolTip.SetTip(removeBtn, "Clear Value");
        removeBtn.Click += (_, _) =>
        {
            jsVar.Value = string.Empty;
            valueBtn.Content = "Empty";
            ToolTip.SetTip(valueBtn, string.Empty);
        };

        var btnRow2 = new StackPanel { Orientation = global::Avalonia.Layout.Orientation.Horizontal };
        btnRow2.Children.Add(openBtn);
        btnRow2.Children.Add(removeBtn);
        panel.Children.Add(valueBtn);
        panel.Children.Add(btnRow2);

        TryEnableFileVarDrop(panel, valueBtn, jsVar);
    }

    private void ApplyFileVarPath(JsVariable jsVar, Button valueBtn, string path)
    {
        if (_selectedWidget == null) return;

        if (ResourcePaths.ToResourceUrl(path) is { } resourceUrl)
        {
            path = resourceUrl;
        }
        else
        {
            path = Path.GetFullPath(path).Replace('\\', '/');
            var widgetDir = Path.GetDirectoryName(_selectedWidget.HtmlPath)!.Replace('\\', '/');
            if (path.Contains(widgetDir))
                path = path.Replace(widgetDir, "./").Replace("//", "/");
        }

        jsVar.Value = path;
        valueBtn.Content = path == "./" ? "./" : path.Split('/').Last();
        ToolTip.SetTip(valueBtn, path);
        UpdateSaveButtonBorder(SaveButtonBorder, true);
    }

    private void TryEnableFileVarDrop(StackPanel panel, Button valueBtn, JsVariable jsVar)
    {
        try
        {
            panel.Background = Brushes.Transparent;
            DragDrop.SetAllowDrop(panel, true);

            panel.AddHandler(DragDrop.DragOverEvent, (_, e) =>
            {
                bool accepted = FirstAcceptedPath(e.DataTransfer, jsVar.Type) != null;

                e.DragEffects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
                SetFileVarDropHighlight(valueBtn, accepted);
                e.Handled = true;
            });

            panel.AddHandler(DragDrop.DragLeaveEvent, (_, _) =>
                SetFileVarDropHighlight(valueBtn, false));

            panel.AddHandler(DragDrop.DropEvent, (_, e) =>
            {
                SetFileVarDropHighlight(valueBtn, false);
                e.Handled = true;

                string? path = FirstAcceptedPath(e.DataTransfer, jsVar.Type);
                if (path != null) ApplyFileVarPath(jsVar, valueBtn, path);
            });
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Drag and drop unavailable for file variable {Name}", jsVar.Name);
        }
    }

    private static string? FirstAcceptedPath(IDataTransfer data, WidgetVariableType type)
    {
        try
        {
            var files = data.TryGetFiles();
            if (files == null) return null;

            foreach (var item in files)
            {
                string? path = item.TryGetLocalPath();
                if (!string.IsNullOrWhiteSpace(path) && FileVarAccepts(type, path)) return path;
            }
        }
        catch { /**/ }

        return null;
    }

    private void SetFileVarDropHighlight(Button valueBtn, bool active)
    {
        if (!active)
        {
            valueBtn.ClearValue(Button.BorderBrushProperty);
            return;
        }

        if (this.TryFindResource("AccentFillColorDefaultBrush", 
                ActualThemeVariant, out var res) && res is IBrush brush)
            valueBtn.BorderBrush = brush;
    }

    private void JsEventTypeList_Loaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not Expander { Tag: JsVariable jsVar } expander) return;
        if (expander.Content != null) return;

        var panelValues = (jsVar.Value ?? "").Split(',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var outerPanel = new StackPanel { Orientation = global::Avalonia.Layout.Orientation.Vertical };
        var groupValues = Enum.GetValues<SubathonEventType>()
            .Where(x => x.IsEnabled())
            .Where(x => x is not SubathonEventType.Command and not SubathonEventType.Unknown)
            .GroupBy(x => x.GetSource())
            .OrderBy(g => SubathonEventSourceHelper.GetSourceOrder(g.Key))
            .ThenBy(g => g.Key.GetOrderNumber());

        foreach (var group in groupValues)
        {
            var groupExpander = new Expander
            {
                BorderBrush = Brushes.DarkGray, BorderThickness = new global::Avalonia.Thickness(0, 0, 0, 1),
                Padding = new global::Avalonia.Thickness(4, 2, 4, 2), Margin = new global::Avalonia.Thickness(0, 2, 0, 2),
                IsExpanded = false, Header = group.Key.ToString()
            };

            var chkboxList = new StackPanel { Orientation = global::Avalonia.Layout.Orientation.Vertical };
            foreach (var (label, key) in BuildEventTypeEntries(group))
            {
                var chkBox = new CheckBox
                {
                    Content = new TextBlock { Text = label, Tag = key, TextWrapping = TextWrapping.Wrap, MaxWidth = 240 },
                    IsChecked = panelValues.Contains(key),
                    Margin = new global::Avalonia.Thickness(2)
                };
                chkBox.IsCheckedChanged += (_, _) => UpdateEventListValues(jsVar, outerPanel);
                chkboxList.Children.Add(chkBox);
                AttachChangeHandler(chkBox, e);
            }
            groupExpander.Content = chkboxList;
            outerPanel.Children.Add(groupExpander);
        }
        expander.Content = outerPanel;
    }

    private static IEnumerable<(string Label, string Key)> BuildEventTypeEntries(IEnumerable<SubathonEventType> group)
    {
        foreach (var eType in group.OrderBy(x => x.GetOrderNumber()))
        {
            if (eType == SubathonEventType.GoAffProOrder)
            {
                foreach (var store in GoAffProStoreRegistry.All().Where(s => s.Enabled))
                    yield return (store.EventName, store.InternalEventName);
                continue;
            }
            yield return (((SubathonEventType?)eType).GetLabel(), eType.ToString());
        }
    }

    private void JsEventSubTypeList_Loaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not Expander { Tag: JsVariable jsVar } expander) return;
        if (expander.Content != null) return;

        var panelValues = (jsVar.Value ?? "").Split(',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var chkboxList = new StackPanel { Orientation = global::Avalonia.Layout.Orientation.Vertical };
        foreach (var eType in Enum.GetValues<SubathonEventSubType>().OrderBy(x => x.GetOrderNumber()))
        {
            if (eType is SubathonEventSubType.CommandLike or SubathonEventSubType.Unknown) continue;
            var chkBox = new CheckBox
            {
                Content = new TextBlock { Text = eType.GetDescription(), Tag = eType, TextWrapping = TextWrapping.Wrap, MaxWidth = 278 },
                IsChecked = panelValues.Contains(eType.ToString()),
                Margin = new global::Avalonia.Thickness(2)
            };
            chkBox.IsCheckedChanged += (_, _) => UpdateEventListValues(jsVar, chkboxList);
            chkboxList.Children.Add(chkBox);
            AttachChangeHandler(chkBox, e);
        }
        expander.Content = chkboxList;
    }

    private void JsPercentSlider_Loaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not Slider { Tag: JsVariable jsVar } slider) return;
        if (int.TryParse(jsVar.Value, out var initial)) slider.Value = initial;

        Dispatcher.UIThread.Post(() =>
        {
            slider.ValueChanged += (_, args) =>
            {
                var intVal = (int)args.NewValue;
                jsVar.Value = intVal.ToString();
                if (FindPercentSiblingBox(slider) is { } tb && tb.Text != intVal.ToString())
                    tb.Text = intVal.ToString();
            };
        }, DispatcherPriority.Loaded);
        AttachChangeHandler(sender, e);
    }

    private void JsPercentBox_Loaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox { Tag: JsVariable jsVar } tb) return;
        tb.Text = int.TryParse(jsVar.Value, out var initial) ? initial.ToString() : "0";

        tb.TextChanged += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(tb.Text)) return;
            if (!int.TryParse(tb.Text, out var val)) return;
            val = Math.Clamp(val, 0, 100);
            jsVar.Value = val.ToString();
            if (FindPercentSiblingSlider(tb) is { } slider && (int)slider.Value != val)
                slider.Value = val;
        };
        AttachChangeHandler(sender, e);
    }

    private void JsFilteredEventTypeList_Loaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not Expander { Tag: JsVariable jsVar } expander) return;
        if (expander.Content != null) return;

        var allowedTypes = jsVar.Type.GetFilteredEventTypes().ToHashSet();

        var panelValues = (jsVar.Value ?? "").Split(',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var outerPanel = new StackPanel { Orientation = global::Avalonia.Layout.Orientation.Vertical };
        var groupValues = Enum.GetValues<SubathonEventType>()
            .Where(x => allowedTypes.Contains(x))
            .Where(x => x.IsEnabled())
            .Where(x => x is not SubathonEventType.Command and not SubathonEventType.Unknown)
            .GroupBy(x => x.GetSource())
            .OrderBy(g => SubathonEventSourceHelper.GetSourceOrder(g.Key))
            .ThenBy(g => g.Key.GetOrderNumber());

        foreach (var group in groupValues)
        {
            var groupExpander = new Expander
            {
                BorderBrush = Brushes.DarkGray, BorderThickness = new global::Avalonia.Thickness(0, 0, 0, 1),
                Padding = new global::Avalonia.Thickness(4, 2, 4, 2), Margin = new global::Avalonia.Thickness(0, 2, 0, 2),
                IsExpanded = false, Header = group.Key.ToString()
            };

            var chkboxList = new StackPanel { Orientation = global::Avalonia.Layout.Orientation.Vertical };
            foreach (var (label, key) in BuildEventTypeEntries(group))
            {
                var chkBox = new CheckBox
                {
                    Content = new TextBlock { Text = label, Tag = key, TextWrapping = TextWrapping.Wrap, MaxWidth = 240 },
                    IsChecked = panelValues.Contains(key),
                    Margin = new global::Avalonia.Thickness(2)
                };
                chkBox.IsCheckedChanged += (_, _) => UpdateEventListValues(jsVar, outerPanel);
                chkboxList.Children.Add(chkBox);
                AttachChangeHandler(chkBox, e);
            }
            groupExpander.Content = chkboxList;
            outerPanel.Children.Add(groupExpander);
        }
        expander.Content = outerPanel;
    }

    private Slider? FindPercentSiblingSlider(TextBox tb)
        => tb.Parent is not Panel parent ? null : parent.Children.OfType<Slider>().FirstOrDefault();

    private TextBox? FindPercentSiblingBox(Slider slider)
        => slider.Parent is not Panel parent ? null : parent.Children.OfType<TextBox>().FirstOrDefault();

#endregion JSHandlers
}
