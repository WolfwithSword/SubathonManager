using System.Windows;
using System.Windows.Controls;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Models;

namespace SubathonManager.UI.UiUtils;

public class CssVariableTemplateSelector : DataTemplateSelector
{
    public DataTemplate? DefaultTemplate { get; set; }
    public DataTemplate? ColorTemplate { get; set; }
    public DataTemplate? SizeTemplate { get; set; }
    public DataTemplate? OptionsTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object? item, DependencyObject container)
    {
        if (item is CssVariable cssVar)
        {
            if (cssVar.Type == WidgetCssVariableType.Color) return ColorTemplate;
            if (cssVar.Type == WidgetCssVariableType.Size) return SizeTemplate;
            if (cssVar.Type.GetOptions().Count > 0) return OptionsTemplate;
        }
        return DefaultTemplate;
    }
}