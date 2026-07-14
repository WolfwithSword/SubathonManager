using System.ComponentModel.DataAnnotations;
using SubathonManager.Core.Enums;

namespace SubathonManager.Core.Models;

public class MakeShipTracking
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Url { get; set; } = "";
    public string Name { get; set; } = "";
    public string ShopifyProductId { get; set; } = "";
    public MakeShipProductType ProductType { get; set; } = MakeShipProductType.Unknown;

    public int Sales { get; set; }
    public int Orders { get; set; }
}
