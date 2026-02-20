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

            widgets = Widgets.Select(w => w.ToJson("")).ToArray(), //todo

            meta = new
            {
                created = CreatedTimestamp,
                updated = UpdatedTimestamp
            }
        };

        return JsonSerializer.SerializeToElement(obj);
    }
    
    // todo remember that for all files when exporting i should collect
    // their references including FullPath ones and make them all relative somehow
    // standardize when it's a fullpath thing or a duplicate filename thing as like "ref1/thing.txt" etc
    
    // idea - hash the full path of the file regardless w/o name,
    // make that an internal folder name, then put it in there as file.
    // store variable path as that hash and filename. ez.
    
    
    
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

        foreach (var widgetJson in json.GetProperty("widgets").EnumerateArray())
        {
            var widget = Widget.FromJson(widgetJson, "", route.Id); // todo
            if (widget != null)
                route.Widgets.Add(widget);
        }
        
        return route;
    }
}