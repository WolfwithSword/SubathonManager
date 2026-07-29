using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Data.Widgets;
using SubathonManager.UI.UiUtils;
using SubathonManager.UI.Services;
using ActivationKind = SubathonManager.UI.Platform.ActivationKind;

namespace SubathonManager.UI;

public partial class MainWindow : Window
{
    private readonly string _fullVersion = ServiceManager.AppVersion ?? string.Empty;

    public MainWindow()
    {
        InitializeComponent();

        VersionText.Text = _fullVersion.Length > 8 ? _fullVersion[..8] + "‥" : _fullVersion;
        ToolTip.SetTip(VersionLabelBtn, _fullVersion);

        UiHelpers.EnableClickAwayUnfocus(this);

        InitConnectionStatus();
        InitHome();
        InitOverlays();

        Loaded += async (_, _) =>
        {
            await MaybeShowTelemetryPromptAsync();
            await ImportPendingOverlayAsync();
        };
    }

    private async void CopyVersion_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_fullVersion)) return;
        await UiHelpers.TrySetClipboardTextAsync(_fullVersion);
    }
    
    public void HandlePendingActivation(ActivationKind kind)
    {
        switch (kind)
        {
            case ActivationKind.SmoFile:
                _ = ImportPendingOverlayAsync();
                break;
            case ActivationKind.SmwFile:
                CollectPendingWidgetPack();
                break;
        }
    }

    private async Task ImportPendingOverlayAsync()
    {
        var path = Utils.PendingOverlayImportPath;
        if (string.IsNullOrWhiteSpace(path)) return;

        MainWindowTabs.SelectedItem = OverlayTabItem;
        await Task.Delay(300);
        await ImportRouteFromFile(path);
        Utils.PendingOverlayImportPath = null;
    }
    
    private void CollectPendingWidgetPack()
    {
        var path = Utils.PendingWidgetPackImportPath;
        Utils.PendingWidgetPackImportPath = null;
        if (string.IsNullOrWhiteSpace(path)) return;

        var editor = FindOpenOverlayEditor();
        if (editor != null)
        {
            editor.Activate();
            _ = editor.AddWidgetPackAsync(path);
            return;
        }

        bool isCollection = path.EndsWith(WidgetCollectionInstaller.CollectionExtension,
            StringComparison.OrdinalIgnoreCase);

        string? installed = isCollection
            ? (WidgetCollectionInstaller.InstallAll(path) != null ? path : null)
            : WidgetPackInstaller.DropIntoImports(path);

        if (installed == null)
            _logger?.LogWarning("Failed to collect widget package {Path}", path);
        else
            _logger?.LogDebug("Collected widget package {Path}", installed);
    }

    private static EditRouteWindow? FindOpenOverlayEditor()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return null;

        var editors = desktop.Windows.OfType<EditRouteWindow>().ToList();
        return editors.FirstOrDefault(w => w.IsActive) ?? editors.FirstOrDefault();
    }
}
