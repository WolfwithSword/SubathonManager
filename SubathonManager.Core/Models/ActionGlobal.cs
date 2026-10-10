using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using SubathonManager.Core.Enums;

namespace SubathonManager.Core.Models;

[ExcludeFromCodeCoverage]
public class ActionGlobal {
    public ActionStoreKind Kind { get; set; }
    [MaxLength(64)] public string Name { get; set; } = "";

    public ActionValueType ValueType { get; set; } = ActionValueType.Text;

    public string? Value { get; set; }

    // secrets only
    [MaxLength(128)] public string? StorageKey { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}
public record GlobalTypeConflict(string Name, ActionValueType Existing, ActionValueType Wanted);
