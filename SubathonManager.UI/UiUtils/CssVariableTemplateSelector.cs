using Avalonia.Controls;
using Avalonia.Controls.Templates;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Models;

namespace SubathonManager.UI.UiUtils;

public class CssVariableTemplateSelector : IDataTemplate
{
    public IDataTemplate? DefaultTemplate { get; set; }
    public IDataTemplate? ColorTemplate { get; set; }
    public IDataTemplate? SizeTemplate { get; set; }
    public IDataTemplate? OptionsTemplate { get; set; }
    public IDataTemplate? FloatTemplate { get; set; }
    public IDataTemplate? IntTemplate { get; set; }
    public IDataTemplate? OpacityTemplate { get; set; }

    public bool Match(object? data) => data is CssVariable;

    public Control? Build(object? param) => Pick(param)?.Build(param);

    private IDataTemplate? Pick(object? item)
    {
        if (item is CssVariable cssVar)
        {
            if (cssVar.Type == WidgetCssVariableType.Color) return ColorTemplate;
            if (cssVar.Type == WidgetCssVariableType.Size) return SizeTemplate;
            if (cssVar.Type == WidgetCssVariableType.Int) return IntTemplate;
            if (cssVar.Type == WidgetCssVariableType.Float) return FloatTemplate;
            if (cssVar.Type == WidgetCssVariableType.Opacity) return OpacityTemplate;
            if (cssVar.Type.GetOptions().Count > 0) return OptionsTemplate;
        }
        return DefaultTemplate;
    }
}
