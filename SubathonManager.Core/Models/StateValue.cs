using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;

namespace SubathonManager.Core.Models;

[ExcludeFromCodeCoverage]
public class StateValue
{
    [Key] public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    public string TypeName { get; set; } = "String";
}

public static class StateKeys
{
    public const string WheelSpinsOwed = "WheelSpinsOwed";
}
