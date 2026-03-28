using System.ComponentModel;
using System.Reflection;

namespace SubathonManager.Core.Enums;

public static class EnumExtensions
{
    public static string GetDescription(this Enum value)
    {
        var field = value.GetType().GetField(value.ToString());
        if (field == null) return value.ToString();

        var attr = field.GetCustomAttribute<DescriptionAttribute>();
        return attr?.Description ?? value.ToString();
    }
}