using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Data;
using SubathonManager.Data.Overlays;
using SubathonManager.Data.Widgets;
using SubathonManager.UI.UiUtils;
using SubathonManager.UI.Views;

namespace SubathonManager.UI;

public partial class MainWindow
{
    // ReSharper disable once InconsistentNaming
    internal readonly ILogger? _logger =
        AppServices.Provider.GetService<ILogger<MainWindow>>();

    public ObservableCollection<Route> Overlays { get; } = new();

    private void InitOverlays()
    {
        InitObsIntegration();
        OverlaysList.ItemsSource = Overlays;
        LoadRoutes();
    }

    private void LoadRoutes()
    {
        Overlays.Clear();
        using var db = _factory.CreateDbContext();
        var routes = db.Routes.OrderByDescending(r => r.UpdatedTimestamp).ToList();

        foreach (var route in routes)
            Overlays.Add(route);
    }

    private void OpenMarketplace_Click(object? sender, RoutedEventArgs e) {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://docs.subathonmanager.app/latest/browse/",
            UseShellExecute = true
        });
    }
    private void OpenBrowsePresets_Click(object? sender, RoutedEventArgs e) {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://docs.subathonmanager.app/latest/widgets/presets/Presets/",
            UseShellExecute = true
        });
    }

    private async void BrowseWidgets_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            await new WidgetBrowserDialog().ShowDialog(this);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to open the widget browser");
        }
    }

    private void OpenPresets_Click(object? sender, RoutedEventArgs e) => OpenRelativeFolder("presets");
    private void OpenImports_Click(object? sender, RoutedEventArgs e) => OpenRelativeFolder("imports");
    private void OpenExports_Click(object? sender, RoutedEventArgs e) => OpenRelativeFolder("exports");
    private void OpenResources_Click(object? sender, RoutedEventArgs e) => OpenRelativeFolder("resources");

    private void OpenRelativeFolder(string name)
    {
        string path = Path.GetFullPath($"./{name}");
        try
        {
            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);
            if (!UiHelpers.OpenFolder(path))
                _logger?.LogWarning("Unable to locate {Name} folder: {Path}", name, path);
        }
        catch
        {
            _logger?.LogWarning("Unable to locate {Name} folder: {Path}", name, path);
        }
    }

    private async void CopyRouteUrl_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is Control { DataContext: Route route })
            {
                var config = AppServices.Provider.GetRequiredService<IConfig>();
                await UiUtils.UiHelpers.TrySetClipboardTextAsync(route.GetRouteUrl(config));
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to copy overlay URL");
        }
    }

    private void AddRoute_Click(object? sender, RoutedEventArgs e)
    {
        using var db = _factory.CreateDbContext();
        var newRoute = new Route
        {
            Name = $"New Overlay {Overlays.Count + 1}",
            Width = 1920,
            Height = 1080
        };
        db.Routes.Add(newRoute);
        db.SaveChanges();

        Overlays.Insert(0, newRoute);
    }

    private void DeleteRoute_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: Route route })
        {
            using var db = _factory.CreateDbContext();
            var found = db.Routes.FirstOrDefault(r => r.Id == route.Id);
            if (found != null)
            {
                db.Routes.Remove(found);
                db.SaveChanges();
            }

            Overlays.Remove(route);
        }
    }

    private void DuplicateRoute_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: Route route })
        {
            using var db = _factory.CreateDbContext();
            var dbRoute = db.Routes.Include(r => r.Widgets)
                .ThenInclude(w => w.CssVariables).Include(r => r.Widgets)
                .ThenInclude(w => w.JsVariables).FirstOrDefault(r => r.Id == route.Id);

            if (dbRoute == null) return;
            var clone = new Route
            {
                Name = $"{dbRoute.Name} (Copy)",
                Width = dbRoute.Width,
                Height = dbRoute.Height
            };

            db.Routes.Add(clone);

            foreach (var widget in dbRoute.Widgets.ToArray())
            {
                var cloneWidget = widget.Clone(clone.Id, widget.Name, widget.Z);
                db.Widgets.Add(cloneWidget);
                db.CssVariables.AddRange(cloneWidget.CssVariables);
                db.JsVariables.AddRange(cloneWidget.JsVariables);
            }
            db.SaveChanges();
            Overlays.Insert(0, clone);
        }
    }

    private void RouteCard_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: Route route })
            OpenRouteEditor(route);
    }

    private void EditRoute_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: Route route })
            OpenRouteEditor(route);
    }

    private void ExportRoute_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: Route route }) return;

        var dialog = new ExportOverlayDialog(route);
        _ = dialog.ShowDialog(this);
    }

    private async void ImportRoute_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Overlay",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Subathon Manager Overlay (*.smo)") { Patterns = new[] { "*.smo" } }
            }
        });

        var file = files.FirstOrDefault();
        if (file == null) return;

        await ImportRouteFromFile(file.Path.LocalPath);
    }

    private async Task<bool> ImportRouteFromFile(string filePath)
    {
        if (filePath.StartsWith("http"))
        {
            using var client = new HttpClient();
            using var response = await client.GetAsync(filePath);
            response.EnsureSuccessStatusCode();

            string fileName = response.Content.Headers.ContentDisposition?.FileNameStar
                              ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                              ?? Uri.UnescapeDataString(Path.GetFileNameWithoutExtension(new Uri(filePath).AbsolutePath));

            if (string.IsNullOrWhiteSpace(fileName)) fileName = "imported_overlay";
            if (!fileName.EndsWith(".smo", StringComparison.OrdinalIgnoreCase)) fileName += ".smo";

            string tempFile = Path.Combine(Path.GetTempPath(), fileName);
            await File.WriteAllBytesAsync(tempFile, await response.Content.ReadAsByteArrayAsync());
            filePath = tempFile;
        }

        try
        {
            var installed = OverlayPackInstaller.Install(filePath);
            if (installed == null)
            {
                _logger?.LogError("Import failed: could not read overlay archive {Path}", filePath);
                return false;
            }

            WidgetPackPaths.InvalidateResolveCache();
            WidgetPackPaths.InvalidateVersionCache();

            var manifest = installed.Manifest;
            var result = await OverlayPorter.ImportExtractedRouteAsync(
                installed.UnpackDir,
                _factory,
                manifest.Name,
                OverlayPackPaths.RouteName(manifest.Name, manifest.Version));

            if (result.Failed)
            {
                _logger?.LogError("Import failed: {Reason}", result.FailReason);
                return false;
            }

            if (!result.HasAnythingNew)
            {
                _logger?.LogInformation("Import: everything already exists, nothing to add");
                return false;
            }

            await using var db = await _factory.CreateDbContextAsync();

            if (result is { RouteIsNew: true, Route: not null })
                db.Routes.Add(result.Route);

            if (result.NewWidgets.Count > 0)
                db.Widgets.AddRange(result.NewWidgets);

            if (result.NewCssVariables.Count > 0)
                db.CssVariables.AddRange(result.NewCssVariables);

            if (result.NewJsVariables.Count > 0)
                db.JsVariables.AddRange(result.NewJsVariables);

            foreach (var repointed in result.RepointedWidgets)
            {
                var tracked = await db.Widgets.FirstOrDefaultAsync(w => w.Id == repointed.Id);
                tracked?.HtmlPath = repointed.HtmlPath;
            }

            if (result is { MergedRouteId: { } mergedId, MergedRouteName: { } mergedName })
            {
                var existing = await db.Routes.FirstOrDefaultAsync(r => r.Id == mergedId);
                existing?.Name = mergedName;
            }

            await db.SaveChangesAsync();

            if (result.RepointedWidgets.Count > 0)
            {
                var helper = new WidgetEntityHelper(_factory, null);
                foreach (var repointed in result.RepointedWidgets)
                {
                    helper.SyncCssVariables(repointed);
                    helper.SyncJsVariables(repointed);
                }
            }
            await Dispatcher.UIThread.InvokeAsync(LoadRoutes);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Import of overlay failed");
            return false;
        }
    }

    public void CloseEditor()
    {
        if (_editWindow != null)
        {
            try
            {
                _editWindow.Close();
            } finally {/**/}
        }
    }

    private EditRouteWindow? _editWindow;

    internal void OpenRouteEditor(Route route)
    {
        if (_editWindow != null)
        {
            if (_editWindow.EditorRouteId == route.Id)
            {
                _editWindow.Activate();
                return;
            }
            _editWindow.Close();
        }

        _editWindow = new EditRouteWindow(route.Id);
        _editWindow.Closed += (_, _) =>
        {
            LoadRoutes();
            _editWindow = null;
        };
        
        _editWindow.Show();
        _editWindow.Activate();
    }
}
