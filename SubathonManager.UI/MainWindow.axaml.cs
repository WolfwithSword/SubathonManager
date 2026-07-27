using Avalonia.Controls;
using Avalonia.Interactivity;
using SubathonManager.Core;
using SubathonManager.UI.Platform;
using SubathonManager.UI.UiUtils;
using SubathonManager.UI.Services;

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
        if (kind == ActivationKind.SmoFile)
            _ = ImportPendingOverlayAsync();
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
}
