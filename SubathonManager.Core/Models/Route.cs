namespace SubathonManager.Core.Models;
using System.ComponentModel.DataAnnotations;

public class Route
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public List<Widget> Widgets { get; set; } = new();

    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    
    public DateTime CreatedTimestamp { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedTimestamp { get; set; } = DateTime.UtcNow;

    public string GetRouteUrl(bool editMode = false)
    {
        string qString = editMode ? "?edit=true" : "";
        return $"http://localhost:{Config.Data["Server"]["Port"]}/route/{Id.ToString()}{qString}";
    }
    
    
    
}