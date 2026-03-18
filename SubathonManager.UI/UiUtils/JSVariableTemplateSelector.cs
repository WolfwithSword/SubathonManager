using System.Windows;
using System.Windows.Controls;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Models;

namespace SubathonManager.UI.UiUtils;

public class JsVariableTemplateSelector : DataTemplateSelector
{
    public DataTemplate? DefaultTemplate { get; set; } // same as String
    
    public DataTemplate? EventTypeListTemplate { get; set; }
    public DataTemplate? EventSubTypeListTemplate { get; set; }
    public DataTemplate? BooleanTemplate { get; set; }
    public DataTemplate? EventTypeSelectTemplate { get; set; }
    public DataTemplate? EventSubTypeSelectTemplate { get; set; }
    public DataTemplate? StringSelectTemplate { get; set; }
    public DataTemplate? FileVarTemplate { get; set; }
    public DataTemplate? IntTemplate { get; set; }
    public DataTemplate? PercentTemplate { get; set; }
    public DataTemplate? FloatTemplate { get; set; }
    
    public override DataTemplate? SelectTemplate(object? item, DependencyObject container)
    {
        if (item is not JsVariable jsVar) return DefaultTemplate;
        
        if (((WidgetVariableType?)jsVar.Type).IsFileVariable()) return FileVarTemplate;
        return jsVar.Type switch
        {
            WidgetVariableType.String => DefaultTemplate,
            WidgetVariableType.Int => IntTemplate,
            WidgetVariableType.Percent => PercentTemplate,
            WidgetVariableType.Float => FloatTemplate,
            WidgetVariableType.Boolean => BooleanTemplate,
            WidgetVariableType.EventSubTypeList => EventSubTypeListTemplate,
            WidgetVariableType.EventSubTypeSelect => EventSubTypeSelectTemplate,
            WidgetVariableType.StringSelect => StringSelectTemplate,
            WidgetVariableType.EventTypeSelect => EventTypeSelectTemplate,
            WidgetVariableType.EventTypeList => EventTypeListTemplate,
            _ => DefaultTemplate
        };
    }
}