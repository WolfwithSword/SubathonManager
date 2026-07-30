using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SubathonManager.UI.UiUtils;

public static partial class FileManagerFocus
{
    #region WINDOWS

    private const int SwRestore = 9;

    [DllImport("user32")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32")]
    private static extern bool IsIconic(IntPtr hWnd);

    [SupportedOSPlatform("windows")]
    public static bool TryFocusExplorer(string? folder, string? selectFile = null)
    {
        if (string.IsNullOrWhiteSpace(folder)) return false;

        string target;
        try { target = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar); }
        catch { return false; }

        object? shell = null;
        object? windows = null;

        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null) return false;

            shell = Activator.CreateInstance(shellType);
            if (shell == null) return false;

            windows = Invoke(shell, "Windows");
            if (windows == null) return false;

            if (Invoke(windows, "Count") is not int count) return false;

            for (int i = 0; i < count; i++)
            {
                object? window = null;
                try
                {
                    window = Invoke(windows, "Item", i);
                    if (window == null) continue;

                    object? document = GetProperty(window, "Document");
                    string? path = GetProperty(GetProperty(GetProperty(document, "Folder"), "Self"), "Path") as string;
                    if (string.IsNullOrWhiteSpace(path)) continue;

                    if (!string.Equals(path.TrimEnd(Path.DirectorySeparatorChar), target,
                            StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (GetProperty(window, "HWND") is not { } hwndValue) continue;

                    var hwnd = new IntPtr(Convert.ToInt64(hwndValue));
                    if (hwnd == IntPtr.Zero) continue;

                    if (IsIconic(hwnd)) ShowWindow(hwnd, SwRestore);
                    SetForegroundWindow(hwnd);

                    TrySelectItem(document, selectFile);
                    return true;
                }
                catch {/**/}
                finally
                {
                    Release(window);
                }
            }
        }
        catch
        {
            return false;
        }
        finally
        {
            Release(windows);
            Release(shell);
        }

        return false;
    }

    private static void TrySelectItem(object? document, string? selectFile)
    {
        if (document == null || string.IsNullOrWhiteSpace(selectFile)) return;

        try
        {
            object? folder = GetProperty(document, "Folder");
            if (folder == null) return;

            object? item = Invoke(folder, "ParseName", Path.GetFileName(selectFile));
            if (item == null) return;

            //                                         SVSI_SELECT | SVSI_DESELECTOTHERS | SVSI_ENSUREVISIBLE | SVSI_FOCUSED
            Invoke(document, "SelectItem", item, 0x0001 | 0x0004 | 0x0008 | 0x0010);
        }
        catch { /**/ }
    }

    private static object? Invoke(object target, string name, params object?[] args)
        => target.GetType().InvokeMember(name, BindingFlags.InvokeMethod | BindingFlags.GetProperty,
            null, target, args);

    private static object? GetProperty(object? target, string name)
        => target?.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null);

    private static void Release(object? comObject)
    {
        try
        {
            if (comObject != null && Marshal.IsComObject(comObject))
#pragma warning disable CA1416
                Marshal.ReleaseComObject(comObject);
#pragma warning restore CA1416
        }
        catch { /**/ }
    }

    #endregion

    #region LINUX

    public static bool TryShowItemsOverDBus(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        string uri;
        try { uri = new Uri(Path.GetFullPath(path)).AbsoluteUri; }
        catch { return false; }

        try
        {
            using var process = Process.Start(new ProcessStartInfo("gdbus")
            {
                ArgumentList =
                {
                    "call", "--session",
                    "--dest", "org.freedesktop.FileManager1",
                    "--object-path", "/org/freedesktop/FileManager1",
                    "--method", "org.freedesktop.FileManager1.ShowItems",
                    $"['{EscapeGVariant(uri)}']",
                    ""
                },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });

            if (process == null) return false;

            if (!process.WaitForExit(3000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /**/ }
                return false;
            }

            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string EscapeGVariant(string value)
        => value.Replace("\\", "\\\\").Replace("'", "\\'");

    #endregion
}
