using System.Diagnostics.CodeAnalysis;
namespace SubathonManager.Core.Enums;

public enum WidgetVariableType
{
    Int,
    String,
    Float,
    StringList,
    EventTypeList,
    Boolean,
    StringSelect,
    Percent,
    EventTypeSelect,
    AnyFile,
    ImageFile,
    VideoFile,
    SoundFile,
    EventSubTypeList,
    EventSubTypeSelect,
    FolderPath
}

[ExcludeFromCodeCoverage]
public static class WidgetVariableTypeHelper
{
    private static readonly WidgetVariableType[] FileVariables = new[]
    {
        WidgetVariableType.AnyFile,
        WidgetVariableType.ImageFile,
        WidgetVariableType.VideoFile,
        WidgetVariableType.SoundFile,
        WidgetVariableType.FolderPath
    };

    public static bool IsFileVariable(this WidgetVariableType? varType) =>
        varType.HasValue && FileVariables.Contains(varType.Value);

    public static bool IsEnumVariable(this WidgetVariableType? varType) =>
        varType.HasValue && GetClsSingleType(varType.Value).IsEnum;

    public static Type GetClsSingleType(this WidgetVariableType varType) => varType switch
    {
        WidgetVariableType.Int => typeof(int),
        WidgetVariableType.String => typeof(string),
        WidgetVariableType.Float => typeof(float),
        WidgetVariableType.Boolean => typeof(bool),
        WidgetVariableType.Percent => typeof(int), // short
        
        WidgetVariableType.StringList => typeof(string),
        WidgetVariableType.EventTypeList => typeof(SubathonEventType),
        WidgetVariableType.EventSubTypeList => typeof(SubathonEventSubType),
        WidgetVariableType.StringSelect => typeof(string),
        WidgetVariableType.EventTypeSelect => typeof(SubathonEventType),
        WidgetVariableType.EventSubTypeSelect => typeof(SubathonEventSubType),
        
        WidgetVariableType.AnyFile => typeof(string),
        WidgetVariableType.ImageFile => typeof(string),
        WidgetVariableType.VideoFile => typeof(string),
        WidgetVariableType.SoundFile => typeof(string),
        WidgetVariableType.FolderPath => typeof(string),
        
        _ => throw new ArgumentOutOfRangeException(nameof(varType), varType, null)
    };
}
    