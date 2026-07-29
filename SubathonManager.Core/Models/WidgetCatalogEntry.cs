using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using SubathonManager.Core.Enums;

namespace SubathonManager.Core.Models;

[ExcludeFromCodeCoverage]
public class WidgetCatalogEntry
{
    [Key]
    public int Id { get; set; }

    public string PackPath { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public long FileModifiedTicks { get; set; }

    public WidgetCatalogSource Source { get; set; } = WidgetCatalogSource.Imported;

    public string PackId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Group { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Entry { get; set; } = string.Empty;
    public string DocsUrl { get; set; } = string.Empty;
    public string Tags { get; set; } = string.Empty;
    public string PreviewImage { get; set; } = string.Empty;
    public string PreviewCachePath { get; set; } = string.Empty;

    public DateTime LastSeenUtc { get; set; }

    public float ScaleX { get; set; } = 1;
    public float ScaleY { get; set; } = 1;
}
