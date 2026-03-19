using SubathonManager.Core.Interfaces;

namespace SubathonManager.Core.Models;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

[ExcludeFromCodeCoverage]
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

    public string GetRouteUrl(IConfig config, bool editMode = false)
    {
        string qString = editMode ? "?edit=true" : "";
        return $"http://localhost:{config.Get("Server", "Port", "14040")}/route/{Id}{qString}";
    }
    
    /*
    public JsonElement ToJson()
    {
        var obj = new
        {
            name = Name,

            resolution = new
            {
                width = Width,
                height = Height
            },

            widgets = Widgets.Select(w => w.ToJson("")).ToArray(),

            meta = new
            {
                created = CreatedTimestamp,
                updated = UpdatedTimestamp
            }
        };

        return JsonSerializer.SerializeToElement(obj);
    }
    
    public static Route? FromJson(JsonElement json)
    {
        var name = json.GetProperty("name").GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name)) return null;
        
        var route = new Route
        {
            Name = name
        };

        var res = json.GetProperty("resolution");
        route.Width = res.GetProperty("width").GetInt32();
        route.Height = res.GetProperty("height").GetInt32();

        foreach (var widget in json.GetProperty("widgets").EnumerateArray().Select(widgetJson => Widget.FromJson(widgetJson, "", route.Id)).OfType<Widget>())
        {
            route.Widgets.Add(widget);
        }
        
        return route;
    }
    */
}