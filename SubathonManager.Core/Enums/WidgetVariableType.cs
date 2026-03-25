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
    FolderPath,
    OrderEventTypeList,
    TokenEventTypeList,
    SubEventTypeList,
    FollowEventTypeList,
    DonationEventTypeList
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

    public static List<SubathonEventType> GetFilteredEventTypes(this WidgetVariableType varType) => varType switch
    {
        WidgetVariableType.OrderEventTypeList => SubathonEventSubTypeHelper.OrderEventTypes,
        WidgetVariableType.SubEventTypeList => SubathonEventSubTypeHelper.SubEventTypes,
        WidgetVariableType.TokenEventTypeList =>  SubathonEventSubTypeHelper.TokenEventTypes,
        WidgetVariableType.FollowEventTypeList =>  SubathonEventSubTypeHelper.FollowEventTypes,
        WidgetVariableType.DonationEventTypeList =>  SubathonEventSubTypeHelper.DonationEventTypes,
        WidgetVariableType.EventTypeList => Enum.GetValues<SubathonEventType>().ToList(),
        _ => []
    };

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
        
        WidgetVariableType.OrderEventTypeList => typeof(SubathonEventType), 
        WidgetVariableType.TokenEventTypeList => typeof(SubathonEventType), 
        WidgetVariableType.SubEventTypeList => typeof(SubathonEventType), 
        WidgetVariableType.FollowEventTypeList => typeof(SubathonEventType), 
        WidgetVariableType.DonationEventTypeList => typeof(SubathonEventType), 
        
        _ => typeof(string)
    };
}

public enum WidgetCssVariableType
{
    Default, // same as string
    String, 
    Color,
    Alignment,
    Size,
    Float,
    Int,
    Opacity
}

[ExcludeFromCodeCoverage]
public static class WidgetCssVariableTypeHelper
{
    public static List<string> GetOptions(this WidgetCssVariableType varType) => varType switch
    {
        WidgetCssVariableType.Alignment => ["left", "center", "right"], 
        WidgetCssVariableType.Size => ["px", "%", "pt", "rem", "em", "vh", "vw", "vmin", "vmax", "cm", "mm", "in", "ch", "ex"],
        _ => []
    };
}