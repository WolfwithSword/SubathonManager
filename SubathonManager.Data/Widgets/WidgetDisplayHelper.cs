using SubathonManager.Core.Enums;
using SubathonManager.Core.Models;

namespace SubathonManager.Data.Widgets;

public static class WidgetDisplayHelper
{
    public static WidgetDisplayKind GetDisplayKind(this Widget widget) => widget.Type switch
    {
        WidgetType.Image => WidgetDisplayKind.Image,
        WidgetType.Video => WidgetDisplayKind.Video,
        _ => WidgetPackPaths.TryResolve(widget.HtmlPath, 
            out _, out _, out _, out _)
            ? WidgetDisplayKind.Widget
            : WidgetDisplayKind.UnpackedWidget
    };

    public static string GetLabel(this WidgetDisplayKind kind) => kind switch
    {
        WidgetDisplayKind.Image => "Image",
        WidgetDisplayKind.Video => "Video",
        WidgetDisplayKind.UnpackedWidget => "Unpacked Widget",
        _ => "Widget"
    };
}
