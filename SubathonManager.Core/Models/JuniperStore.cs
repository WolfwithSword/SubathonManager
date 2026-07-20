using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace SubathonManager.Core.Models;

[ExcludeFromCodeCoverage]
public class JuniperStore
{
    [Key]
    public Guid RowId { get; set; }
    public required string StoreName { get; init; }
    public bool Enabled { get; set; } = true;
    
    public List<JuniperProduct> Products { get; set; } = new();
    
    public DateTime LastFetched { get; set; } = DateTime.UtcNow;
    
}
[ExcludeFromCodeCoverage]
public class JuniperProduct
{
    [Key]
    public required BigInteger ProductId { get; init; }

    public string ProductName { get; set; } = "Product";
    
    [ForeignKey(nameof(Store))]
    public required Guid StoreId { get; init; }

    public required JuniperStore Store { get; init; }
    
    public DateTime LastFetched { get; set; } = DateTime.UtcNow;
    public bool Valid { get; set; } = true;
    
    // i assume we do not care about variants for purposes of subathons...
    
}