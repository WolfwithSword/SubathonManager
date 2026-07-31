using System.Text.RegularExpressions;

namespace SubathonManager.Core;

public static partial class ResourcePaths
{
    public const string UrlPrefix = "/resources/";
    public const string BundleFolder = "resources";

    public static string Root => Path.GetFullPath("./resources");
    public static readonly string[] DefaultFolders = ["images", "images/logos", "audio"]; // think of more later?

    public static void EnsureCreated()
    {
        try
        {
            Directory.CreateDirectory(Root);
            foreach (var folder in DefaultFolders)
                Directory.CreateDirectory(Path.Combine(Root, folder.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch { /**/ }
    }

    public static List<string> EnumerateRelative()
    {
        try
        {
            string root = Root;
            if (!Directory.Exists(root)) return [];

            return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
                .Where(rel => !rel.StartsWith('.') && !rel.Contains("/."))
                .OrderBy(rel => rel, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch { return []; }
    }

    public static IEnumerable<string> FindReferences(string? text)
    {
        if (string.IsNullOrEmpty(text)) yield break;

        foreach (Match m in ReferenceRegex().Matches(text))
        {
            string rel = m.Groups[1].Value.Split('?')[0].Split('#')[0].Trim('/');
            if (rel.Length == 0) continue;
            yield return Uri.UnescapeDataString(rel);
        }
    }

    public static string? RelativeFromUrl(string? value)
    {
        if (!IsResourceUrl(value)) return null;
        string rel = value!.Replace('\\', '/')[UrlPrefix.Length..].Split('?')[0].Split('#')[0].Trim('/');
        return rel.Length == 0 ? null : rel;
    }

    public static bool IsResourceUrl(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && value.Replace('\\', '/').StartsWith(UrlPrefix, StringComparison.OrdinalIgnoreCase);

    public static string? ToResourceUrl(string? localPath)
    {
        if (string.IsNullOrWhiteSpace(localPath)) return null;
        if (IsResourceUrl(localPath)) return localPath.Replace('\\', '/');

        try
        {
            string root = Root;
            string full = Path.GetFullPath(localPath);

            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return null;

            string relative = full[(root.Length + 1)..].Replace('\\', '/').Trim('/');
            return relative.Length == 0 ? null : UrlPrefix + relative;
        }
        catch { return null; }
    }

    public static string? ToLocalPath(string? resourceUrl)
    {
        if (!IsResourceUrl(resourceUrl)) return null;

        try
        {
            string relative = resourceUrl!.Replace('\\', '/')[UrlPrefix.Length..].Trim('/');
            string root = Root;
            string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));

            return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                ? full
                : null;
        }
        catch { return null; }
    }

    [GeneratedRegex(@"/resources/([^""'\s\\>\)]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ReferenceRegex();

    public static string? ResolveRequestPath(string? requestPath)
    {
        if (string.IsNullOrWhiteSpace(requestPath)) return null;

        string relative = requestPath.Split('?')[0];
        if (relative.StartsWith(UrlPrefix, StringComparison.OrdinalIgnoreCase))
            relative = relative[UrlPrefix.Length..];
        relative = relative.Trim('/');

        if (relative.Length == 0) return null;

        try
        {
            relative = Uri.UnescapeDataString(relative);
            if (relative.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return null;

            string root = Root;
            string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));

            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return null;

            return File.Exists(full) ? full : null;
        }
        catch { return null; }
    }
}
