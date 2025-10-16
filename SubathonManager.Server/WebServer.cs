using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SubathonManager.Core.Models;

using SubathonManager.Data;

namespace SubathonManager.Server;

public class WebServer
{
    public int Port { get; }
    private readonly HttpListener _listener;
    private bool _running;
    
    private readonly HashSet<string> _servedFolders = new();
    
    public WebServer(int port = 14040)
    {
        
        using (var db = new AppDbContext())
        {
            var routes = db.Routes.ToList();
            if (routes.Count == 0)
            {
                Console.WriteLine("No routes found.");
                // Route route = new();
                // route.Name = "test";
                // //
                // Widget widget = new Widget("test", "C:\\Users\\WolfwithSword\\Documents\\subman\\testwidgets\\widget.html");
                // widget.Route = route;
                // //
                // db.Routes.Add(route);
                // db.SaveChanges();
                // db.Widgets.Add(widget);
                // db.SaveChanges();
                // widget.ScanCssVariables();
                // db.CssVariables.AddRange(widget.CssVariables);
                // db.SaveChanges();
                // AddRoute(route);
            }
            else {
                foreach (var route in routes)
                {
                    AddRoute(route);
                }
                
            }
        }
        
        Port = port;
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{Port}/");
    }

    public void AddRoute(Route route)
    {
        foreach (var widget in route.Widgets)
        {
            string folder = Path.GetDirectoryName(widget.HtmlPath)!;
            if (!_servedFolders.Contains(folder))
            {
                _servedFolders.Add(folder);
                Console.WriteLine($"Registered static folder: {folder}");
            }
        }
    }

    public async Task StartAsync()
    {
        _running = true;
        _listener.Start();
        Console.WriteLine($"WebServer running at http://localhost:{Port}/");

        while (_running)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleRequestAsync(context));
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Server Error] {ex.Message}");
            }
        }
    }

    public void Stop()
    {
        _running = false;
        try
        {
            _listener.Stop();
        }
        catch
        {
            
        }
    }
    
    private async Task HandleRequestAsync(HttpListenerContext ctx)
    {
        string path = ctx.Request.Url?.AbsolutePath ?? "/";
        Console.WriteLine($"Request: {path}");

        if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            if (path.StartsWith("/api/update-position/", StringComparison.OrdinalIgnoreCase))
            {
                var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

                if (parts.Length >= 3)
                {
                    string widgetId = parts[2];
                    
                    string body;
                    using (var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding))
                        body = await reader.ReadToEndAsync();
                    var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body);
                    
                    if (data == null)
                    {
                        ctx.Response.StatusCode = 400;
                        await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("Invalid update data"));
                        ctx.Response.Close();
                        return;
                    }
                    
                    var widgetHelper = new WidgetEntityHelper();
                    bool success = await widgetHelper.UpdateWidgetPosition(widgetId, data);
                    if (success)
                    {
                        ctx.Response.StatusCode = 200;
                        await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("OK"));
                        ctx.Response.Close();
                        return;
                    }
                    ctx.Response.StatusCode = 404;
                    await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("Widget Not Found"));
                    ctx.Response.Close();
                    return;
                }
                ctx.Response.StatusCode = 400;
                await ctx.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("Invalid Widget ID"));
                ctx.Response.Close();
                return;
            }
        }
        else if (path.StartsWith("/route/", StringComparison.OrdinalIgnoreCase))
        {
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                string routeId = parts[1];
                if (Guid.TryParse(routeId, out var routeGuid))
                {
                    using var db = new AppDbContext();
                    var route = await db.Routes
                        .Include(r => r.Widgets)
                        .ThenInclude(w => w.CssVariables)
                        .FirstOrDefaultAsync(r => r.Id == routeGuid);
                    if (route != null)
                    {
                        
                        var query = ctx.Request.Url!.Query.TrimStart('?');
                        var queryString = System.Web.HttpUtility.ParseQueryString(query);
                        bool isEditor = queryString["edit"] != null && queryString["edit"].Equals("true");
            
                        string html = GenerateMergedPage(route, isEditor);
                        ctx.Response.ContentType = "text/html";
                        AddCorsHeaders(ctx.Response);
                        byte[] bytes = Encoding.UTF8.GetBytes(html);
                        await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                        ctx.Response.Close();
                        return; 
                    }
                }
            }
        }
        else if (path.StartsWith("/widget/", StringComparison.OrdinalIgnoreCase))
        {

            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length >= 2)
            {
                string widgetId = parts[1];
                if (Guid.TryParse(widgetId, out var widgetGuid))
                {
                    
                    using var db = new AppDbContext();
                    var widget = await db.Widgets
                        .Include(ww => ww.CssVariables)
                        .FirstOrDefaultAsync(w => w.Id == widgetGuid);

                    if (widget != null)
                    {
                        string relativePath = string.Join('/', parts.Skip(2));
                        string folder = Path.GetDirectoryName(widget.HtmlPath)!;
                        string filePath = string.IsNullOrWhiteSpace(relativePath)
                            ? widget.HtmlPath
                            : Path.Combine(folder, relativePath.Replace('/', Path.DirectorySeparatorChar));

                        if (File.Exists(filePath))
                        {
                            if (Path.GetExtension(filePath).Equals(".html", StringComparison.OrdinalIgnoreCase))
                            {
                                string html = await File.ReadAllTextAsync(filePath);

                                var cssOverrides = new StringBuilder();
                                cssOverrides.AppendLine("<style type=\"text/css\">\n:root, html {");
                                foreach (var v in widget.CssVariables)
                                {
                                    cssOverrides.AppendLine($"  --{v.Name}: {v.Value} !important;");
                                }
                                cssOverrides.AppendLine("}\n</style>");
                                if (html.Contains("</head>", StringComparison.OrdinalIgnoreCase))
                                {
                                    html = html.Replace("</head>", cssOverrides + "\n</head>", StringComparison.OrdinalIgnoreCase);
                                }
                                else
                                {
                                    html += cssOverrides;
                                }
                                
                                ctx.Response.ContentType = "text/html";
                                AddCorsHeaders(ctx.Response);
                                byte[] bytes = Encoding.UTF8.GetBytes(html);
                                await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                                ctx.Response.Close();
                                return;
                            }
                            else
                            {
                                await ServeFile(ctx, filePath);
                                return;
                            }
                        }
                    }
                }
            }
        }

        // Check if request is for a local file in a *known* widget folder
        // so we can load properly resources the html desires
        foreach (var folder in _servedFolders)
        {
            var fixedFolder = folder.Replace("\\", "/");
            if (path.Contains(fixedFolder) && File.Exists(path.TrimStart('/')))
            {
                await ServeFile(ctx, path.TrimStart('/'));
                return;
            }

        }

        // Not found
        ctx.Response.StatusCode = 404;
        byte[] notFound = Encoding.UTF8.GetBytes("404 Not Found");
        await ctx.Response.OutputStream.WriteAsync(notFound, 0, notFound.Length);
        ctx.Response.Close();
    }
    
    private static void AddCorsHeaders(HttpListenerResponse response)
    {
        response.AddHeader("Access-Control-Allow-Origin", "*");
        response.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
        response.AddHeader("Access-Control-Allow-Headers", "Content-Type");
    }

    private async Task ServeFile(HttpListenerContext ctx, string fullPath)
    {
        string ext = Path.GetExtension(fullPath).ToLower();
        string contentType = ext switch
        {
            // TODO: More contentTypes, such as webm or webp or gif or mp4 or mp3 or wav etc?
            // need to test more too
            ".html" => "text/html",
            ".htm" => "text/html",
            ".css" => "text/css",
            ".js" => "application/javascript",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            _ => "application/octet-stream"
        };
        ctx.Response.ContentType = contentType;
        AddCorsHeaders(ctx.Response);  
        byte[] bytes = await File.ReadAllBytesAsync(fullPath);
        await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
        ctx.Response.Close();
    }

    private string GenerateMergedPage(Route route, bool isEditor = false)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("<html><head><meta charset=\"UTF-8\"></head><body style='margin:0;'>");
        
        sb.AppendLine($@"
            <style>
                html, body {{
                    margin: 0;
                    padding: 0;
                    width: 100%;
                    height: 100%;
                    overflow: hidden;
                    background: transparent;
                }}
                
                #overlay {{
                    position: relative;
                    width: {route.Width}px;
                    height: {route.Height}px;
                    margin: 0 auto;
                    background: transparent;
                }}

                .overlay-edit {{
                    box-shadow: 0 0 0 1px black;
                }}
                
                .widget-wrapper {{
                    position: absolute;
                    pointer-events: auto; 
                    background: rgba(0,0,0,0); 
                }}

                .widget-wrapper iframe {{
                    width: 100%;
                    height: 100%;
                    border: none;
                    background: transparent;
                    pointer-events: none;
                }}

                @keyframes pulse {{
                  0% {{ box-shadow: 0 0 10px rgba(255,255,255,0.3); }}
                  50% {{ box-shadow: 0 0 16px rgba(100,180,255,0.6); }}
                  100% {{ box-shadow: 0 0 10px rgba(255,255,255,0.3); }}
                }}
                .widget-edit {{
                    outline: 2px solid;
                    cursor: move;
                    outline-color: rgba(50, 50, 50, 0.4);
                    border-radius: 8px;
                    box-shadow: 0 0 10px rgba(55,55,55,0.3);
                    transition: outline-color 0.2s, box-shadow 0.2s;
                  }}
                .widget-edit:hover {{
                    outline-color: rgba(100, 180, 255, 0.9);
                    animation: pulse 1.5s infinite;
                 }}
            </style>
        ");
        
        if (isEditor)
        {
            sb.AppendLine(@"
                <style>
                    body {
                        background-color: rgba(50, 50, 50, 0.3);
                        background-image:
                            radial-gradient(rgba(10, 10, 140, 0.16) 1px, transparent 2px);
                        background-size: 20px 20px;
                        background-position: -10px -10px;
                    }
                </style>
            ");
        }
        
        string overlayClass = isEditor ? "overlay-edit" : ""; 
        sb.AppendLine($@"<div data-id=""{route.Id.ToString()}"" id=""overlay"" class=""{overlayClass}"">");

        
        foreach (var w in route.Widgets)
        {
            
            if (isEditor) // todo check if performant doing this each time, we only sync variables if it's viewed in editor?
            // i also sync when opening one for editing. Honestly this here may not even be necessary anymore
            {
                WidgetEntityHelper widgetHelper = new WidgetEntityHelper();
                widgetHelper.SyncCssVariables(w);
            }
            
            string cssClass = "widget-wrapper";
            if (isEditor) cssClass += " widget-edit";
            
            string titleAttr = isEditor ? $" title=\"{WebUtility.HtmlEncode(w.Name)}\nZ:{w.Z}\"" : "";
            sb.AppendLine($@"<div data-id=""{w.Id.ToString()}"" class=""{cssClass}"" {titleAttr} 
                              style=""left:{w.X}px; top:{w.Y}px; z-index:{w.Z}; 
                                       width:{w.Width}px; height:{w.Height}px;"">");

            sb.AppendLine($@"<iframe src=""/widget/{w.Id.ToString()}/"" 
                               sandbox=""allow-scripts allow-same-origin"" 
                               frameborder=""0"" scrolling=""no"">
                            </iframe>");

            sb.AppendLine("</div>");
        }

        sb.AppendLine("</div>");
        
        sb.AppendLine(@"
            <script>
            function resizeIframe(iframe) {
                try {
                    const doc = iframe.contentDocument || iframe.contentWindow.document;
                    iframe.style.height = doc.body.scrollHeight + 'px';
                    iframe.style.width  = doc.body.scrollWidth + 'px';
                } catch (e) {
                    console.error('Cannot resize iframe:', e);
                }
            }
            document.querySelectorAll('iframe').forEach(iframe => {
                iframe.onload = () => resizeIframe(iframe);
            });
            </script>
        ");

        // moving in edit mode
        if (isEditor)
        {
            sb.AppendLine(@"
            <script>
            document.querySelectorAll('.widget-wrapper').forEach(wrapper => {
                let isDragging = false;
                let offsetX = 0, offsetY = 0;

                wrapper.addEventListener('mousedown', e => {
                    isDragging = true;
                    offsetX = e.clientX - wrapper.offsetLeft;
                    offsetY = e.clientY - wrapper.offsetTop;

                    const maxZ = Math.max(...[...document.querySelectorAll('.widget-wrapper')]
                        .map(w => parseInt(w.style.zIndex) || 0));
                    //wrapper.style.zIndex = maxZ + 1;
                    e.preventDefault();
                });

                document.addEventListener('mousemove', e => {
                    if (!isDragging) return;
                    wrapper.style.left = (e.clientX - offsetX) + 'px';
                    wrapper.style.top = (e.clientY - offsetY) + 'px';
                });

                document.addEventListener('mouseup', e => {
                    if (!isDragging) return;
                    isDragging = false;

                    const id = wrapper.dataset.id;
                    const x = wrapper.offsetLeft;
                    const y = wrapper.offsetTop;
                    const z = parseInt(wrapper.style.zIndex) || 0;

                    fetch(`/api/update-position/${id}`, {
                        method: 'POST',
                        headers: {'Content-Type':'application/json'},
                        body: JSON.stringify({x, y, z})
                    });
                });
            });
            </script>
            ");
        }

        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

}