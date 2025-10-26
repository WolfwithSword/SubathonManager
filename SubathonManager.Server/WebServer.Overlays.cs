using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SubathonManager.Core.Models;
using SubathonManager.Data;

namespace SubathonManager.Server;

public partial class WebServer
{
  
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

    private async Task<bool> HandleWidgetRequest(HttpListenerContext ctx, string path)
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
                            html += $"{GetWebsocketInjectionScript()}";
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
                            return true;
                        }
                        else
                        {
                            await ServeFile(ctx, filePath);
                            return true;
                        }
                    }
                }
            }
        }
        return false;
    }

    private async Task<bool> HandleRouteRequest(HttpListenerContext ctx, string path)
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
                    return true; 
                }
            }
        }

        return false;
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
            
            /*if (isEditor) // todo check if performant doing this each time, we only sync variables if it's viewed in editor?
            // i also sync when opening one for editing. Honestly this here may not even be necessary anymore
            {
                WidgetEntityHelper widgetHelper = new WidgetEntityHelper();
                widgetHelper.SyncCssVariables(w);
            }*/
            
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