namespace SubathonManager.Core.Enums;

public enum WidgetType
{
    Html = 0,
    Image = 1,
    Video = 2
}

public static class WidgetTypeHelper
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".avif", ".bmp", ".svg" };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".m4v", ".webm", ".ogm", ".mkv", ".mov" };

    public static WidgetType DetectFromPath(string path)
    {
        var ext = Path.GetExtension(path);
        
        if (ImageExtensions.Contains(ext)) return WidgetType.Image;
        if (VideoExtensions.Contains(ext)) return WidgetType.Video;
        
        return WidgetType.Html;
    }

    public static bool IsAsset(this WidgetType type) => type != WidgetType.Html;
}
