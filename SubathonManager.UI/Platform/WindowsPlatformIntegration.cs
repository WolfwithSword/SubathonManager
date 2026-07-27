using System.Runtime.Versioning;
using Microsoft.Win32;

namespace SubathonManager.UI.Platform;

[SupportedOSPlatform("windows")]
public sealed class WindowsPlatformIntegration : PlatformIntegrationBase
{
    public override void RegisterFileAssociations()
    {
        var exePath = Environment.ProcessPath!;

        EnsureRegistryValue(@"HKEY_CURRENT_USER\Software\Classes\.smo", "", "SubathonManager.Overlay");
        EnsureRegistryValue(@"HKEY_CURRENT_USER\Software\Classes\SubathonManager.Overlay", "", "Subathon Manager Overlay");
        EnsureRegistryValue(@"HKEY_CURRENT_USER\Software\Classes\SubathonManager.Overlay\DefaultIcon", "", $"{exePath},0");
        EnsureRegistryValue(@"HKEY_CURRENT_USER\Software\Classes\SubathonManager.Overlay\shell\open\command", "", $"\"{exePath}\" \"%1\"");

        EnsureRegistryValue(@"HKEY_CURRENT_USER\Software\Classes\subathonmanager", "", "URL:Subathon Manager Protocol");
        EnsureRegistryValue(@"HKEY_CURRENT_USER\Software\Classes\subathonmanager", "URL Protocol", "");
        EnsureRegistryValue(@"HKEY_CURRENT_USER\Software\Classes\subathonmanager\shell\open\command", "", $"\"{exePath}\" \"%1\"");

        EnsureRegistryValue(@"HKEY_CURRENT_USER\Software\Classes\SubathonManager.Overlay\shell\import", "", "Import into Subathon Manager");
        EnsureRegistryValue(@"HKEY_CURRENT_USER\Software\Classes\SubathonManager.Overlay\shell\import", "Icon", $"{exePath},0");

        EnsureRegistryValue(@"HKEY_CURRENT_USER\Software\Classes\Applications\SubathonManager.exe", "FriendlyAppName", "Subathon Manager");
        EnsureRegistryValue(@"HKEY_CURRENT_USER\Software\Classes\.smo\OpenWithProgids", "SubathonManager.Overlay", "");
    }

    private static void EnsureRegistryValue(string keyPath, string name, string expectedValue)
    {
        var currentValue = Registry.GetValue(keyPath, name, null) as string;
        if (currentValue == expectedValue)
            return;
        Registry.SetValue(keyPath, name, expectedValue);
    }
}
