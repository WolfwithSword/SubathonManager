using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace SubathonManager.UI.UiUtils;

public static class UiHelpers
{
    public static async Task<bool> TrySetClipboardTextAsync(string text)
    {
        var clipboard = GetClipboard();
        if (clipboard == null) return false;

        for (int i = 0; i < 5; i++)
        {
            try
            {
                await Dispatcher.UIThread.InvokeAsync(async () => await clipboard.SetTextAsync(text));
                return true;
            }
            catch { await Task.Delay(50); }
        }
        return false;
    }

    private static IClipboard? GetClipboard()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            return desktop.MainWindow?.Clipboard;
        return null;
    }
    
    public static void EnableClickAwayUnfocus(TopLevel topLevel)
    {
        topLevel.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (e.Source is Visual v && IsWithinTextInput(v)) return;

            topLevel.FocusManager?.Focus(null);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private static bool IsWithinTextInput(Visual? v)
    {
        while (v != null)
        {
            if (v is TextBox or AutoCompleteBox) return true;
            v = v.GetVisualParent();
        }
        return false;
    }

    public static bool IsInteractiveSource(object? source, Visual? stopAt = null)
    {
        var v = source as Visual;
        while (v != null && v != stopAt)
        {
            if (v is Button or CheckBox or RadioButton) return true;
            v = v.GetVisualParent();
        }
        return false;
    }

    public static void UpdateButtonPendingBorder(Border border, bool hasPendingChanges)
    {
        border.BorderBrush = hasPendingChanges
            ? new SolidColorBrush(Color.FromRgb(0xF5, 0xC5, 0x18))
            : new SolidColorBrush(Colors.Transparent);
    }

    public static bool RevealInFileManager(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (!File.Exists(path))
            return OpenFolder(Directory.Exists(path) ? path : Path.GetDirectoryName(path));

        try
        {
            // diff actions so not calling raw open folder, doesn't matter for linux
            if (OperatingSystem.IsWindows())
            {
                if (FileManagerFocus.TryFocusExplorer(Path.GetDirectoryName(path), path)) return true;
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }
            else if (OperatingSystem.IsMacOS())
                Process.Start(new ProcessStartInfo("open") { ArgumentList = { "-R", path } });
            else
            {
                if (FileManagerFocus.TryShowItemsOverDBus(path)) return true;
                return OpenFolder(Path.GetDirectoryName(path));
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool OpenFolder(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (FileManagerFocus.TryFocusExplorer(dir)) return true;
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
            }
            else if (OperatingSystem.IsMacOS())
                Process.Start(new ProcessStartInfo("open") { ArgumentList = { dir } });
            else
                Process.Start(new ProcessStartInfo("xdg-open") { ArgumentList = { dir } });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
