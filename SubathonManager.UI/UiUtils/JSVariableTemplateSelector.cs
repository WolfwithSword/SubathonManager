using Avalonia.Controls;
using Avalonia.Controls.Templates;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Models;

namespace SubathonManager.UI.UiUtils;

public class JsVariableTemplateSelector : IDataTemplate
{
    public IDataTemplate? DefaultTemplate { get; set; } // same as String

    public IDataTemplate? EventTypeListTemplate { get; set; }
    public IDataTemplate? EventSubTypeListTemplate { get; set; }
    public IDataTemplate? BooleanTemplate { get; set; }
    public IDataTemplate? EventTypeSelectTemplate { get; set; }
    public IDataTemplate? EventSubTypeSelectTemplate { get; set; }
    public IDataTemplate? StringSelectTemplate { get; set; }
    public IDataTemplate? FileVarTemplate { get; set; }
    public IDataTemplate? IntTemplate { get; set; }
    public IDataTemplate? PercentTemplate { get; set; }
    public IDataTemplate? FloatTemplate { get; set; }
    public IDataTemplate? FilteredEventTypeListTemplate { get; set; }

    public bool Match(object? data) => data is JsVariable;

    public Control? Build(object? param) => Pick(param)?.Build(param);

    private IDataTemplate? Pick(object? item)
    {
        if (item is not JsVariable jsVar) return DefaultTemplate;

        if (((WidgetVariableType?)jsVar.Type).IsFileVariable()) return FileVarTemplate;

        if (jsVar.Type.GetFilteredEventTypes() is { Count: > 0 } &&
            jsVar.Type != WidgetVariableType.EventTypeList)
            return FilteredEventTypeListTemplate;

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
