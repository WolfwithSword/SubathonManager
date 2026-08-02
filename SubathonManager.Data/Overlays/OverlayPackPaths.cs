using System.Text.RegularExpressions;
using SubathonManager.Core;
using SubathonManager.Data.Widgets;
// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.Data.Overlays;

public static partial class OverlayPackPaths
{
    public const string OverlayExtension = ".smo";
    public const string UnpackFolderName = "unpack";
    public static string ImportsRoot => Path.GetFullPath(Path.Combine("./imports", "overlays"));

    public static string OverlayRoot(string author, string name)
    {
        string authorFolder = WidgetPackPaths.Slug(author);
        if (string.IsNullOrEmpty(authorFolder)) authorFolder = "unknown";

        string nameFolder = WidgetPackPaths.Slug(name);
        if (string.IsNullOrEmpty(nameFolder)) nameFolder = "overlay";

        return Path.Combine(ImportsRoot, authorFolder, nameFolder);
    }

    public static string ArchiveFile(string author, string name, string version)
    {
        string versionFile = SanitizeSegment(version);
        if (string.IsNullOrEmpty(versionFile)) versionFile = "1.0.0";
        return Path.Combine(OverlayRoot(author, name), versionFile + OverlayExtension);
    }

    public static string UnpackDir(string author, string name)
        => Path.Combine(OverlayRoot(author, name), UnpackFolderName);

    public static List<string> ImportedVersions(string author, string name)
    {
        string folder = OverlayRoot(author, name);
        if (!Directory.Exists(folder)) return [];

        try
        {
            return Directory.EnumerateFiles(folder, "*" + OverlayExtension)
                .Select(Path.GetFileNameWithoutExtension)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v!)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    public static string RouteName(string name, string version)
    {
        string trimmed = (name ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(trimmed)) trimmed = "Imported Overlay";

        string display = WidgetPackPaths.DisplayVersion(version);
        return string.IsNullOrWhiteSpace(display) ? trimmed : $"{trimmed} v{display}";
    }

    public static string BaseRouteName(string? routeName)
    {
        return string.IsNullOrWhiteSpace(routeName) ? string.Empty :
            VersionSuffixRegex().Replace(routeName.Trim(), string.Empty).Trim();
    }

    [GeneratedRegex(@"\s+v\d[\w.\-]*$", RegexOptions.IgnoreCase)]
    private static partial Regex VersionSuffixRegex();

    public static string BuildFileName(string author, string name, string version)
    {
        var parts = new[] { author, name, version }
            .Select(p => SanitizeSegment(p).Replace(' ', '-'))
            .Where(p => !string.IsNullOrWhiteSpace(p));

        string joined = string.Join('_', parts);
        return (string.IsNullOrWhiteSpace(joined) ? "overlay" : joined) + OverlayExtension;
    }

    private static string SanitizeSegment(string? value) => SafeFileName.Sanitize(value);
}
