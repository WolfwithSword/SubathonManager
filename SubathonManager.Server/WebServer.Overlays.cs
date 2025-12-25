using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SubathonManager.Core.Models;

namespace SubathonManager.Server;

public partial class WebServer
{
  
    public void AddRoute(Route route)
    {
        foreach (var widget in route.Widgets)
        {
            string folder = Path.GetDirectoryName(widget.HtmlPath)!;
            if (!_servedFolders.Add(folder))
            {
                _logger?.LogInformation($"Registered static folder: {folder}");
            }
        }
    }

    internal async Task HandleWidgetRequest(IHttpContext ctx)
    {
        var path = ctx.Path;
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length >= 2)
        {
            string widgetId = parts[1];
            if (Guid.TryParse(widgetId, out var widgetGuid))
            {
                
                await using var db = await _factory.CreateDbContextAsync();
                var widget = await db.Widgets
                    .Include(ww => ww.CssVariables)
                    .Include(ww => ww.JsVariables)
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
                            cssOverrides.AppendLine(GetWebsocketInjectionScript());
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
                            
                            var jsOverrides = new StringBuilder();
                            jsOverrides.AppendLine("\n<script>\n");
                            foreach (var v in widget.JsVariables)
                                jsOverrides.Append(v.GetInjectLine());
                            jsOverrides.AppendLine("</script>\n");
                            if (html.Contains("<script>", StringComparison.OrdinalIgnoreCase))
                            {
                                int count = 0;
                                html = Regex.Replace(
                                    html,
                                    "<script>",
                                    m => count++ == 0 ? jsOverrides + "\n<script>" : m.Value,
                                    RegexOptions.IgnoreCase
                                );
                            }
                            else
                            {
                                html += jsOverrides;
                            }

                            await ctx.WriteResponse(200, html, true, "text/html");
                            return;
                        }
                        await ctx.ServeFile(filePath, GetContentType(filePath));
                        return;
                    }
                }
            }
        }
        await ctx.WriteResponse(404, "Widget not found");
    }

    internal async Task HandleRouteRequest(IHttpContext ctx)
    {
        var path = ctx.Path;
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
        {
            string routeId = parts[1].Split('?')[0];
            if (Guid.TryParse(routeId, out var routeGuid))
            {
                await using var db = await _factory.CreateDbContextAsync();
                var route = await db.Routes
                    .Include(r => r.Widgets)
                    .ThenInclude(w => w.CssVariables)
                    .FirstOrDefaultAsync(r => r.Id == routeGuid);
                if (route != null)
                {
                    var queryString = System.Web.HttpUtility.ParseQueryString(ctx.QueryString);
                    bool isEditor = queryString["edit"] != null && queryString["edit"]!.Equals("true");
            
                    string html = GenerateMergedPage(route, isEditor);
                    await ctx.WriteResponse(200, html, true, "text/html");
                    return;
                }
            }
        }
        await ctx.WriteResponse(404, "Route not found");
    }

    private string GenerateMergedPage(Route route, bool isEditor = false)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine(
            $"<html><head><title>overlay-{route.Id}</title><link rel=\"icon\" type=\"image/x-icon\" href=\"https://raw.githubusercontent.com/WolfwithSword/SubathonManager/refs/heads/main/assets/icon.ico\"><meta charset=\"UTF-8\"></head><body style='margin:0;'>");

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
                    user-select: none; 
                    -webkit-user-select: none;
                    -moz-user-select: none;
                    -ms-user-select: none;
                }}

                .widget-wrapper iframe {{
                    width: 100%;
                    height: 100%;
                    border: none;
                    background: transparent;
                    pointer-events: none;
                    transform-origin: top left;
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
                .widget-hidden {{
                    opacity: 0.4 !important;
                    outline-color: rgba(60, 10, 10, 0.4);
                }}
             
                .resize-handle {{
                    position: absolute;
                    width: 12px;
                    height: 12px;
                    background: rgba(180,180,255,0.7);
                    border: 1px solid rgba(100,100,255,0.9);
                    border-radius: 2px;
                    z-index: 9999;
                    cursor: pointer;
                }}

                .resize-handle.shift-active {{
                    background: orange !important;
                    border-color: darkorange !important;
                }}

                .handle-nw {{ top: -6px; left: -6px; cursor: nwse-resize; }}
                .handle-ne {{ top: -6px; right: -6px; cursor: nesw-resize; }}
                .handle-sw {{ bottom: -6px; left: -6px; cursor: nesw-resize; }}
                .handle-se {{ bottom: -6px; right: -6px; cursor: nwse-resize; }}

                .handle-n {{ top: -6px; left: 50%; transform: translateX(-50%); cursor: n-resize; }}
                .handle-s {{ bottom: -6px; left: 50%; transform: translateX(-50%); cursor: s-resize; }}
                .handle-e {{ right: -6px; top: 50%; transform: translateY(-50%); cursor: e-resize; }}
                .handle-w {{ left: -6px; top: 50%; transform: translateY(-50%); cursor: w-resize; }}
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
        sb.AppendLine($@"<div data-id=""{route.Id}"" id=""overlay"" class=""{overlayClass}"">");

        foreach (var w in route.Widgets)
        {
            if (!isEditor && !w.Visibility) continue;
            if (w.ScaleX == 0) w.ScaleX = 1;
            if (w.ScaleY == 0) w.ScaleY = 1;

            string cssClass = "widget-wrapper";
            if (isEditor) cssClass += " widget-edit";
            if (!w.Visibility) cssClass += " widget-hidden";

            string titleAttr = isEditor ? $" title=\"{WebUtility.HtmlEncode(w.Name)}\nZ:{w.Z}\"" : "";
            sb.AppendLine($@"<div data-id=""{w.Id.ToString()}"" class=""{cssClass}"" {titleAttr} 
                           style=""left:{w.X}px; top:{w.Y}px; z-index:{w.Z}; 
                                    width:{w.Width * w.ScaleX}px; height:{w.Height * w.ScaleY}px;"">");
            if (isEditor)
            {
                sb.AppendLine(@"
                 <div class='resize-handle handle-nw'></div>
                 <div class='resize-handle handle-ne'></div>
                 <div class='resize-handle handle-sw'></div>

                 <div class='resize-handle handle-se'></div>

                 <div class='resize-handle handle-n'></div>

                 <div class='resize-handle handle-s'></div>
                 <div class='resize-handle handle-e'></div>

                 <div class='resize-handle handle-w'></div>
             ");
            }

            sb.AppendLine($@"<iframe src=""/widget/{w.Id}/"" 
                            data-scalex=""{w.ScaleX}""
                            data-scaley=""{w.ScaleY}""
                            sandbox=""allow-scripts allow-same-origin"" 
                            frameborder=""0"" scrolling=""no"">
                         </iframe>");

            sb.AppendLine("</div>");
        }

        sb.AppendLine("</div>");

        sb.AppendLine(@$"
         <script>
            function resizeIframe(iframe) {{
                try {{
                    const sx = parseFloat(iframe.dataset.scalex) || 1;
                    const sy = parseFloat(iframe.dataset.scaley) || 1;

                    const wrapper = iframe.parentElement;
                    const originalWidth  = parseFloat(wrapper.dataset.origWidth)  || iframe.offsetWidth / sx;
                    const originalHeight = parseFloat(wrapper.dataset.origHeight) || iframe.offsetHeight / sy;

                    iframe.style.width  = originalWidth + 'px';
                    iframe.style.height = originalHeight + 'px';
                    iframe.style.transform = `scale(${{sx}}, ${{sy}})`;
                    iframe.style.transformOrigin = ""top left"";

                    wrapper.style.width  = (originalWidth  * sx) + 'px';
                    wrapper.style.height = (originalHeight * sy) + 'px';

                }} catch (e) {{
                    console.error('Cannot resize iframe:', e);
                }}
            }}
            document.querySelectorAll('iframe').forEach(iframe => {{
                iframe.onload = () => resizeIframe(iframe);
            }});
         </script>
     ");

        sb.AppendLine(@$"{GetWebsocketInjectionScript(route.Id.ToString())}");

        // moving/resizing/selecting in edit mode
        if (isEditor)
        {
            sb.AppendLine(@"
            <script>
            document.querySelectorAll('.widget-wrapper').forEach(wrapper => {
                let isDragging = false;
                let offsetX = 0, offsetY = 0;

                wrapper.addEventListener('mousedown', async e => {
                    isDragging = true;
                    offsetX = e.clientX - wrapper.offsetLeft;
                    offsetY = e.clientY - wrapper.offsetTop;

                    const maxZ = Math.max(...[...document.querySelectorAll('.widget-wrapper')]
                        .map(w => parseInt(w.style.zIndex) || 0));

                    const id = wrapper.dataset.id;
                    await fetch(`/api/select/${id}`, {
                        method: 'GET'
                    });
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
            
            // resize as separate listeners
            sb.AppendLine(@$"
            <script>
            const MIN_WIDTH = 24;  // px
            const MIN_HEIGHT = 24; // px
            const MIN_SCALE = 0.1;

            document.querySelectorAll('.widget-wrapper').forEach(wrapper => {{
                const iframe = wrapper.querySelector('iframe');

                let scaleX = parseFloat(iframe.dataset.scalex) || 1;
                let scaleY = parseFloat(iframe.dataset.scaley) || 1;

                const originalWidth  = wrapper.dataset.origWidth  || wrapper.offsetWidth  / scaleX;
                const originalHeight = wrapper.dataset.origHeight || wrapper.offsetHeight / scaleY;

                wrapper.dataset.origWidth  = originalWidth;
                wrapper.dataset.origHeight = originalHeight;

                iframe.style.width  = originalWidth + 'px';
                iframe.style.height = originalHeight + 'px';
                iframe.style.transform = `scale(${{scaleX}}, ${{scaleY}})`;
                iframe.style.transformOrigin = 'top left';
                wrapper.style.width  = (originalWidth * scaleX) + 'px';
                wrapper.style.height = (originalHeight * scaleY) + 'px';

                let isResizing = false;
                let activeHandle = null;
                let startX, startY;
                let baselineWidth, baselineHeight;
                let startLeft, startTop;
                let isShiftHeld = false;

                wrapper.querySelectorAll('.resize-handle').forEach(handle => {{

                    // aspect ratio mode on
                    document.addEventListener('keydown', (e) => {{
                        if (e.key === 'Shift') {{
                            isShiftHeld = true;
                            document.querySelectorAll('.handle-nw, .handle-ne, .handle-sw, .handle-se')
                                    .forEach(handle => handle.classList.add('shift-active'));
                        }}
                    }});

                    // aspect ratio mode off
                    document.addEventListener('keyup', (e) => {{
                        if (e.key === 'Shift') {{
                            isShiftHeld = false;
                            document.querySelectorAll('.handle-nw, .handle-ne, .handle-sw, .handle-se')
                                    .forEach(handle => handle.classList.remove('shift-active'));
                        }}
                    }});

                    handle.addEventListener('mousedown', e => {{
                        e.stopPropagation();
                        isResizing = true;
                        activeHandle = handle;
                        startX = e.clientX;
                        startY = e.clientY;
                        baselineWidth  = parseFloat(wrapper.dataset.origWidth);
                        baselineHeight = parseFloat(wrapper.dataset.origHeight);
                        startLeft = wrapper.offsetLeft;
                        startTop = wrapper.offsetTop;
                    }});
                }});

                document.addEventListener('mousemove', e => {{
                    if (!isResizing) return;

                    let dx = (e.clientX - startX) / scaleX;
                    let dy = (e.clientY - startY) / scaleY;

                    let newWidth  = baselineWidth;
                    let newHeight = baselineHeight;
                    let newLeft   = startLeft;
                    let newTop    = startTop;

                    if (activeHandle.classList.contains('handle-e') || 
                        activeHandle.classList.contains('handle-ne') || 
                        activeHandle.classList.contains('handle-se')) {{
                        newWidth = baselineWidth + dx;
                    }}
                    if (activeHandle.classList.contains('handle-w') ||
                        activeHandle.classList.contains('handle-nw') ||
                        activeHandle.classList.contains('handle-sw')) {{
                        newWidth = baselineWidth - dx;
                        newLeft = startLeft + dx * scaleX;
                    }}
                    if (activeHandle.classList.contains('handle-s') || 
                        activeHandle.classList.contains('handle-se') || 
                        activeHandle.classList.contains('handle-sw')) {{
                        newHeight = baselineHeight + dy;
                    }}
                    if (activeHandle.classList.contains('handle-n') || 
                        activeHandle.classList.contains('handle-nw') || 
                        activeHandle.classList.contains('handle-ne')) {{
                        newHeight = baselineHeight - dy;
                        newTop = startTop + dy * scaleY;
                    }}

                    if (e.shiftKey && (
                        activeHandle.classList.contains('handle-nw') ||
                        activeHandle.classList.contains('handle-ne') ||
                        activeHandle.classList.contains('handle-sw') ||
                        activeHandle.classList.contains('handle-se')
                    )) {{
                        const aspectRatio = baselineWidth / baselineHeight;

                        let candidateWidth = newWidth;
                        let candidateHeight = newHeight;

                        if (Math.abs(newWidth / baselineWidth) > Math.abs(newHeight / baselineHeight)) {{
                            candidateHeight = candidateWidth / aspectRatio;
                        }} else {{
                            candidateWidth = candidateHeight * aspectRatio;
                        }}

                        if (candidateWidth < MIN_WIDTH) {{
                            candidateWidth = MIN_WIDTH;
                            candidateHeight = candidateWidth / aspectRatio;
                        }}
                        if (candidateHeight < MIN_HEIGHT) {{
                            candidateHeight = MIN_HEIGHT;
                            candidateWidth = candidateHeight * aspectRatio;
                        }}

                        newWidth = candidateWidth;
                        newHeight = candidateHeight;

                        if (activeHandle.classList.contains('handle-nw')) {{
                            newLeft = startLeft + (baselineWidth - newWidth) * scaleX;
                            newTop  = startTop + (baselineHeight - newHeight) * scaleY;
                        }} else if (activeHandle.classList.contains('handle-ne')) {{
                            newTop = startTop + (baselineHeight - newHeight) * scaleY;
                        }} else if (activeHandle.classList.contains('handle-sw')) {{
                            newLeft = startLeft + (baselineWidth - newWidth) * scaleX;
                        }}
                    }}

                    if (newWidth < MIN_WIDTH) {{
                        if (activeHandle.classList.contains('handle-w') ||
                            activeHandle.classList.contains('handle-nw') ||
                            activeHandle.classList.contains('handle-sw')) {{
                            newLeft += (newWidth - MIN_WIDTH) * scaleX;
                        }}
                        newWidth = MIN_WIDTH;
                    }}

                    if (newHeight < MIN_HEIGHT) {{
                        if (activeHandle.classList.contains('handle-n') ||
                            activeHandle.classList.contains('handle-nw') ||
                            activeHandle.classList.contains('handle-ne')) {{
                            newTop += (newHeight - MIN_HEIGHT) * scaleY;
                        }}
                        newHeight = MIN_HEIGHT;
                    }}

                    const newScaleX = newWidth / baselineWidth;
                    const newScaleY = newHeight / baselineHeight;

                    iframe.style.transform = `scale(${{newScaleX * scaleX}}, ${{newScaleY * scaleY}})`;

                    const rect = iframe.getBoundingClientRect();
                    wrapper.style.width  = rect.width + 'px';
                    wrapper.style.height = rect.height + 'px';
                    wrapper.style.left   = newLeft + 'px';
                    wrapper.style.top    = newTop + 'px';
                }});

                document.addEventListener('mouseup', e => {{
                    if (!isResizing) return;
                    isResizing = false;

                    scaleX = parseFloat(iframe.style.transform.match(/scale\(([^,]+)/)[1]);
                    scaleY = parseFloat(iframe.style.transform.match(/scale\([^,]+,\s*([^)]+)/)[1]);

                    iframe.dataset.scalex = scaleX;
                    iframe.dataset.scaley = scaleY;

                    const id = wrapper.dataset.id;
                    fetch(`/api/update-size/${{id}}`, {{
                        method: 'POST',
                        headers: {{ 'Content-Type': 'application/json' }},
                        body: JSON.stringify({{ scaleX, scaleY, x: wrapper.offsetLeft, y: wrapper.offsetTop }})
                    }});
                }});
            }});
            </script>
            ");
        }

        sb.AppendLine("</body></html>");
        return sb.ToString();
    }
}