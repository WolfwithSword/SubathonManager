using System.Diagnostics;
using System.Text;

namespace SubathonManager.UI.Platform;

public sealed class PosixPlatformIntegration : PlatformIntegrationBase
{
    private const string SchemeMime = "x-scheme-handler/subathonmanager";
    private const string OverlayMime = "application/x-subathonmanager-overlay";
    private const string DesktopFileName = "subathonmanager.desktop";
    private const string MimePackageFileName = "subathonmanager-overlay.xml";
    private const string IconName = "subathonmanager";

    private static readonly int[] IconSizes = [48, 64, 128, 256];

    public override void RegisterFileAssociations()
    {
        if (!OperatingSystem.IsLinux())
            return;

        try
        {
            RegisterLinux();
        }
        catch {/**/}
    }

    private static void RegisterLinux()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
            return;

        string dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");

        string appsDir = Path.Combine(dataHome, "applications");
        string mimePackagesDir = Path.Combine(dataHome, "mime", "packages");
        Directory.CreateDirectory(appsDir);
        Directory.CreateDirectory(mimePackagesDir);

        string desktopPath = Path.Combine(appsDir, DesktopFileName);
        string mimePath = Path.Combine(mimePackagesDir, MimePackageFileName);

        bool iconInstalled = InstallHicolorIcons(Path.GetDirectoryName(exePath)!, dataHome);

        string iconLine;
        if (iconInstalled)
        {
            iconLine = $"Icon={IconName}\n";
        }
        else
        {
            string iconFile = Path.Combine(Path.GetDirectoryName(exePath)!, "Assets", "icon.png");
            iconLine = File.Exists(iconFile) ? $"Icon={iconFile}\n" : string.Empty;
        }

        string desktopContent =
            "[Desktop Entry]\n" +
            "Type=Application\n" +
            "Name=Subathon Manager\n" +
            $"Exec=\"{exePath}\" %U\n" +
            iconLine +
            "Terminal=false\n" +
            "NoDisplay=false\n" +
            "Categories=Utility;\n" +
            "StartupWMClass=SubathonManager\n" +
            $"MimeType={OverlayMime};{SchemeMime};\n";

        string mimeContent =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
            "<mime-info xmlns=\"http://www.freedesktop.org/standards/shared-mime-info\">\n" +
            $"  <mime-type type=\"{OverlayMime}\">\n" +
            "    <comment>Subathon Manager Overlay</comment>\n" +
            "    <glob pattern=\"*.smo\"/>\n" +
            "  </mime-type>\n" +
            "</mime-info>\n";

        bool desktopChanged = WriteIfChanged(desktopPath, desktopContent);
        bool mimeChanged = WriteIfChanged(mimePath, mimeContent);

        if (!desktopChanged && !mimeChanged)
            return;

        if (mimeChanged)
            Run("update-mime-database", Path.Combine(dataHome, "mime"));
        if (desktopChanged)
            Run("update-desktop-database", appsDir);

        Run("xdg-mime", "default", DesktopFileName, SchemeMime);
        Run("xdg-mime", "default", DesktopFileName, OverlayMime);
    }

    private static bool InstallHicolorIcons(string appDir, string dataHome)
    {
        string hicolor = Path.Combine(dataHome, "icons", "hicolor");
        bool anyPresent = false;
        bool anyChanged = false;

        foreach (int size in IconSizes)
        {
            string source = Path.Combine(appDir, "Assets", $"icon_{size}.png");
            if (!File.Exists(source))
                continue;

            string targetDir = Path.Combine(hicolor, $"{size}x{size}", "apps");
            string target = Path.Combine(targetDir, $"{IconName}.png");

            try
            {
                Directory.CreateDirectory(targetDir);
                if (!FilesEqual(source, target))
                {
                    File.Copy(source, target, true);
                    anyChanged = true;
                }
                anyPresent = true;
            }
            catch { /**/ }
        }

        if (anyChanged)
            Run("gtk-update-icon-cache", "-f", "-t", hicolor);

        return anyPresent;
    }

    private static bool FilesEqual(string a, string b)
    {
        try
        {
            var infoA = new FileInfo(a);
            var infoB = new FileInfo(b);
            if (!infoB.Exists || infoA.Length != infoB.Length)
                return false;
            return File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
        }
        catch
        {
            return false;
        }
    }

    private static bool WriteIfChanged(string path, string content)
    {
        if (File.Exists(path) && File.ReadAllText(path) == content)
            return false;
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return true;
    }

    private static void Run(string fileName, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var a in args)
                psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi);
            proc?.WaitForExit(4000);
        }
        catch { /**/ } // nothing took effect, oh well
    }
}
