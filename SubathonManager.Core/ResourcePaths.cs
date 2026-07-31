namespace SubathonManager.Core;

public static class ResourcePaths
{
    public const string UrlPrefix = "/resources/";

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
