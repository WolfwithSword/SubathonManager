using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SubathonManager.Core.Models;

public class GoAffProStore
{
    // Placeholder for future dynamic goaffpro stores
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int RowId { get; set; }
    public int SiteId { get; init; }
    public string StoreName { get; set; } = "";
    public string EventName { get; set; } = "";

    public bool Enabled { get; set; } = true;
    public string InternalName => StoreName.Replace(" ", "");  
    public string InternalEventName => EventName.Replace(" ", "");  
}