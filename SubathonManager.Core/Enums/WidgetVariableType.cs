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
    SoundFile
}

[ExcludeFromCodeCoverage]
public static class WidgetVariableTypeHelper
{
    private static readonly WidgetVariableType[] FileVariables = new[]
    {
        WidgetVariableType.AnyFile,
        WidgetVariableType.ImageFile,
        WidgetVariableType.VideoFile,
        WidgetVariableType.SoundFile
    };

    public static bool IsFileVariable(this WidgetVariableType? varType) =>
        varType.HasValue && FileVariables.Contains(varType.Value);
}
    