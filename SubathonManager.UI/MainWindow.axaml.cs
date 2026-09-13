using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Data.Widgets;
using SubathonManager.UI.Services;
using SubathonManager.UI.UiUtils;
using ActivationKind = SubathonManager.UI.Platform.ActivationKind;

namespace SubathonManager.UI;

public partial class MainWindow : Window {
    private readonly string _fullVersion = ServiceManager.AppVersion ?? string.Empty;

    public MainWindow() {
        InitializeComponent();
        WindowIcons.Apply(this);

        VersionText.Text = _fullVersion.Length > 8 ? _fullVersion[..8] + "‥" : _fullVersion;
        ToolTip.SetTip(VersionLabelBtn, _fullVersion);

        UiHelpers.EnableClickAwayUnfocus(this);

        InitConnectionStatus();
        InitHome();
        InitOverlays();

        Loaded += async (_, _) => {
            await MaybeShowTelemetryPromptAsync();
            await ImportPendingOverlayAsync();
            await CollectPendingWidgetPackAsync();
        };
    }

    private async void CopyVersion_Click(object? sender, RoutedEventArgs e) {
        if (string.IsNullOrEmpty(_fullVersion)) return;
        await UiHelpers.TrySetClipboardTextAsync(_fullVersion);
    }

    public void HandlePendingActivation(ActivationKind kind) {
        switch (kind) {
            case ActivationKind.SmoFile:
                _ = ImportPendingOverlayAsync();
                break;
            case ActivationKind.SmwFile:
                _ = CollectPendingWidgetPackAsync();
                break;
        }
    }

    private async Task ImportPendingOverlayAsync() {
        string? path = Utils.PendingOverlayImportPath;
        if (string.IsNullOrWhiteSpace(path)) return;

        MainWindowTabs.SelectedItem = OverlayTabItem;
        await Task.Delay(300);
        await ImportRouteFromFile(path);
        Utils.PendingOverlayImportPath = null;
    }

    private async Task CollectPendingWidgetPackAsync() {
        string? path = Utils.PendingWidgetPackImportPath;
        Utils.PendingWidgetPackImportPath = null;
        if (string.IsNullOrWhiteSpace(path)) return;

        if (path.StartsWith("http", StringComparison.OrdinalIgnoreCase)) {
            string? downloaded = await DownloadWidgetPackAsync(path);
            if (downloaded == null) return;
            path = downloaded;
        }

        EditRouteWindow? editor = FindOpenOverlayEditor();
        if (editor != null) {
            editor.Activate();
            _ = editor.AddWidgetPackAsync(path);
            return;
        }

        bool isCollection = path.EndsWith(WidgetCollectionInstaller.CollectionExtension,
            StringComparison.OrdinalIgnoreCase);

        string? installed = isCollection
            ? WidgetCollectionInstaller.InstallAll(path) != null ? path : null
            : WidgetPackInstaller.DropIntoImports(path);

        if (installed == null)
            _logger?.LogWarning("Failed to collect widget package {Path}", path);
        else
            _logger?.LogDebug("Collected widget package {Path}", installed);
    }

    private async Task<string?> DownloadWidgetPackAsync(string url) {
        try {
            using var client = new HttpClient();
            using HttpResponseMessage response = await client.GetAsync(url);
            response.EnsureSuccessStatusCode();

            string fileName = response.Content.Headers.ContentDisposition?.FileNameStar
                              ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                              ?? Uri.UnescapeDataString(Path.GetFileName(new Uri(url).AbsolutePath));

            if (string.IsNullOrWhiteSpace(fileName)) fileName = "imported_widget";
            foreach (char invalid in Path.GetInvalidFileNameChars())
                fileName = fileName.Replace(invalid, '_');

            if (!fileName.EndsWith(WidgetPackPaths.PackExtension, StringComparison.OrdinalIgnoreCase) &&
                !fileName.EndsWith(WidgetCollectionInstaller.CollectionExtension, StringComparison.OrdinalIgnoreCase))
                fileName += WidgetPackPaths.PackExtension;

            string tempFile = Path.Combine(Path.GetTempPath(), fileName);
            await File.WriteAllBytesAsync(tempFile, await response.Content.ReadAsByteArrayAsync());
            _logger?.LogDebug("Downloaded widget package {Url} to {Path}", url, tempFile);
            return tempFile;
        }
        catch (Exception ex) {
            _logger?.LogError(ex, "Failed to download widget package {Url}", url);
            return null;
        }
    }

    private static EditRouteWindow? FindOpenOverlayEditor() {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return null;

        List<EditRouteWindow> editors = desktop.Windows.OfType<EditRouteWindow>().ToList();
        return editors.FirstOrDefault(w => w.IsActive) ?? editors.FirstOrDefault();
    }
}