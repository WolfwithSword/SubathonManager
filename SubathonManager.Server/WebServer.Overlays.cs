using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SubathonManager.Core.Enums;

namespace SubathonManager.Server;

public partial class WebServer
{
  
    public void AddRoute(Core.Models.Route route)
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

                    if (widget.Type.IsAsset() && string.IsNullOrWhiteSpace(relativePath))
                    {
                        string fileName = Path.GetFileName(widget.HtmlPath);
                        string html = widget.Type == WidgetType.Video
                            ? $"<html><body style='margin:0;padding:0;overflow:hidden;background:transparent;'><video src='./{fileName}' style='height:720px;width:auto;object-fit:fill;' autoplay loop muted playsinline></video></body></html>"
                            : $"<html><body style='margin:0;padding:0;overflow:hidden;background:transparent;'><img src='./{fileName}' style='height:400px;width:auto;object-fit:fill;'></body></html>";
                        await ctx.WriteResponse(200, html, true, "text/html");
                        return;
                    }

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
                            var fontLoader = """
                                             <script>
                                             let addedGoogleFont = false;
                                             let addedCdnFont = false;
                                             function loadGoogleFont(fontName) {
                                               if (!addedGoogleFont) {
                                                     const precon1 = document.createElement('link');
                                                     precon1.href = "https://fonts.googleapis.com";
                                                     precon1.rel = "preconnect";
                                                     
                                                     const precon2 = document.createElement('link');
                                                     precon2.rel = "preconnect";
                                                     precon2.crossorigin = true;
                                                     precon2.href="https://fonts.gstatic.com";
                                                     addedGoogleFont = true;
                                                     document.head.appendChild(precon1);
                                                     document.head.appendChild(precon2);
                                               }
                                               const link = document.createElement('link');
                                               link.href = `https://fonts.googleapis.com/css2?family=${fontName.replace(/ /g, '+')}&display=swap`;
                                               link.rel = 'stylesheet';
                                               document.head.appendChild(link);
                                             }
                                             
                                             function loadCdnFont(fontName) {
                                               const link = document.createElement('link');
                                               link.href = `https://fonts.cdnfonts.com/css/${fontName.replace(/ /g, '-').toLowerCase()}`;
                                               link.rel = 'stylesheet';
                                               document.head.appendChild(link);
                                             }

                                             let addedBunnyFont = false;
                                             function loadBunnyFont(fontName) {
                                               if (!addedBunnyFont) {
                                                     const precon = document.createElement('link');
                                                     precon.href = "https://fonts.bunny.net";
                                                     precon.rel = "preconnect";
                                                     addedBunnyFont = true;
                                                     document.head.appendChild(precon);
                                               }
                                               const link = document.createElement('link');
                                               link.href = `https://fonts.bunny.net/css?family=${fontName.replace(/ /g, '+')}&display=swap`;
                                               link.rel = 'stylesheet';
                                               document.head.appendChild(link);
                                             }
                                             </script>
                                             """;
                            if (html.Contains("<script>", StringComparison.OrdinalIgnoreCase))
                            {
                                int count = 0;
                                html = Regex.Replace(
                                    html,
                                    "<script>",
                                    m => count++ == 0 ? fontLoader + "\n"+jsOverrides + "\n<script>" : m.Value,
                                    RegexOptions.IgnoreCase
                                );
                            }
                            else
                            {
                                html += fontLoader + "\n";                         
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
        await ctx.WriteResponse(404, "Route/Overlay not found");
    }

    private string GenerateMergedPage(Core.Models.Route route, bool isEditor = false)
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
                    transform-origin: top left;
                    --handle-size: 12px;
                    --handle-half: 6px;
                    --chrome-outline: 2px;
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
                    transform-origin: top left;
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

                #chrome-layer {{
                    position: absolute;
                    left: 0;
                    top: 0;
                    width: 100%;
                    height: 100%;
                    pointer-events: none;
                }}

                .widget-chrome {{
                    position: absolute;
                    pointer-events: auto;
                    user-select: none;
                    -webkit-user-select: none;
                    -moz-user-select: none;
                    -ms-user-select: none;
                    outline: var(--chrome-outline) solid;
                    cursor: move;
                    outline-color: rgba(50, 50, 50, 0.4);
                    border-radius: 8px;
                    box-shadow: 0 0 10px rgba(55,55,55,0.3);
                    transition: outline-color 0.2s, box-shadow 0.2s;
                }}
                .widget-chrome:hover {{
                    outline-color: rgba(100, 180, 255, 0.9);
                    animation: pulse 1.5s infinite;
                }}
                .chrome-hidden {{
                    opacity: 0.4 !important;
                    outline-color: rgba(60, 10, 10, 0.4);
                }}

                .widget-hidden {{
                    opacity: 0.4 !important;
                }}

                .resize-handle {{
                    position: absolute;
                    width: var(--handle-size);
                    height: var(--handle-size);
                    background: rgba(180,180,255,0.7);
                    border: calc(var(--chrome-outline) / 2) solid rgba(100,100,255,0.9);
                    border-radius: 2px;
                    z-index: 9999;
                    cursor: pointer;
                    box-sizing: border-box;
                }}

                .resize-handle.shift-active {{
                    background: orange !important;
                    border-color: darkorange !important;
                }}

                .resize-handle.ctrl-dimension {{
                    background: #50e890 !important;
                    border-color: #20b860 !important;
                }}

                .handle-nw {{ top: calc(0px - var(--handle-half)); left: calc(0px - var(--handle-half)); cursor: nwse-resize; }}
                .handle-ne {{ top: calc(0px - var(--handle-half)); right: calc(0px - var(--handle-half)); cursor: nesw-resize; }}
                .handle-sw {{ bottom: calc(0px - var(--handle-half)); left: calc(0px - var(--handle-half)); cursor: nesw-resize; }}
                .handle-se {{ bottom: calc(0px - var(--handle-half)); right: calc(0px - var(--handle-half)); cursor: nwse-resize; }}

                .handle-n {{ top: calc(0px - var(--handle-half)); left: 50%; transform: translateX(-50%); cursor: n-resize; }}
                .handle-s {{ bottom: calc(0px - var(--handle-half)); left: 50%; transform: translateX(-50%); cursor: s-resize; }}
                .handle-e {{ right: calc(0px - var(--handle-half)); top: 50%; transform: translateY(-50%); cursor: e-resize; }}
                .handle-w {{ left: calc(0px - var(--handle-half)); top: 50%; transform: translateY(-50%); cursor: w-resize; }}
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
            if (!w.Visibility) cssClass += " widget-hidden";

            string sx = w.ScaleX.ToString(CultureInfo.InvariantCulture);
            string sy = w.ScaleY.ToString(CultureInfo.InvariantCulture);
            string px = w.X.ToString(CultureInfo.InvariantCulture);
            string py = w.Y.ToString(CultureInfo.InvariantCulture);

            sb.AppendLine($@"<div data-id=""{w.Id.ToString()}"" class=""{cssClass}""
                           data-scalex=""{sx}""
                           data-scaley=""{sy}""
                           data-orig-width=""{w.Width}""
                           data-orig-height=""{w.Height}""
                           style=""left:{px}px; top:{py}px; z-index:{w.Z};
                                    width:{w.Width}px; height:{w.Height}px;
                                    transform:scale({sx}, {sy});"">");

            sb.AppendLine($@"<iframe src=""/widget/{w.Id}/""
                            data-widget-id=""{w.Id}""
                            sandbox=""allow-scripts allow-same-origin""
                            frameborder=""0"" scrolling=""no"">
                         </iframe>");

            sb.AppendLine("</div>");
        }

        if (isEditor)
        {
            sb.AppendLine(@"<div id=""chrome-layer"">");
            foreach (var w in route.Widgets)
            {
                string chromeClass = "widget-chrome";
                if (!w.Visibility) chromeClass += " chrome-hidden";

                sb.AppendLine($@"<div data-id=""{w.Id}"" class=""{chromeClass}""
                               title=""{WebUtility.HtmlEncode(w.Name)}&#10;Z:{w.Z}""
                               style=""z-index:{w.Z};"">
                     <div class='resize-handle handle-nw'></div>
                     <div class='resize-handle handle-ne'></div>
                     <div class='resize-handle handle-sw'></div>
                     <div class='resize-handle handle-se'></div>
                     <div class='resize-handle handle-n'></div>
                     <div class='resize-handle handle-s'></div>
                     <div class='resize-handle handle-e'></div>
                     <div class='resize-handle handle-w'></div>
                 </div>");
            }
            sb.AppendLine("</div>");
        }

        sb.AppendLine("</div>");

        sb.AppendLine(@$"
         <script>
            window.applyWidgetLayout = function (wrapper, width, height, sx, sy) {{
                if (!wrapper) return;
                width  = parseFloat(width)  || parseFloat(wrapper.dataset.origWidth)  || 400;
                height = parseFloat(height) || parseFloat(wrapper.dataset.origHeight) || 400;
                sx = parseFloat(sx) || 1;
                sy = parseFloat(sy) || 1;

                wrapper.dataset.origWidth  = width;
                wrapper.dataset.origHeight = height;
                wrapper.dataset.scalex = sx;
                wrapper.dataset.scaley = sy;

                wrapper.style.width  = width  + 'px';
                wrapper.style.height = height + 'px';
                wrapper.style.transformOrigin = 'top left';
                wrapper.style.transform = 'scale(' + sx + ', ' + sy + ')';

                syncChrome(wrapper);
            }};


            window.syncChrome = function (wrapper) {{
                const chrome = document.querySelector(
                    '.widget-chrome[data-id=""' + wrapper.dataset.id + '""]');
                if (!chrome) return;
                chrome.style.left   = wrapper.style.left || '0px';
                chrome.style.top    = wrapper.style.top  || '0px';
                chrome.style.width  = (parseFloat(wrapper.dataset.origWidth)
                                     * parseFloat(wrapper.dataset.scalex)) + 'px';
                chrome.style.height = (parseFloat(wrapper.dataset.origHeight)
                                     * parseFloat(wrapper.dataset.scaley)) + 'px';
            }};

            window.setWidgetPosition = function (wrapper, x, y) {{
                wrapper.style.left = x + 'px';
                wrapper.style.top  = y + 'px';
                syncChrome(wrapper);
            }};

            document.querySelectorAll('.widget-wrapper').forEach(wrapper => {{
                applyWidgetLayout(wrapper,
                    wrapper.dataset.origWidth, wrapper.dataset.origHeight,
                    wrapper.dataset.scalex,    wrapper.dataset.scaley);
            }});

            window.snapTo = function (val, grid) {{ return Math.round(val / grid) * grid; }};

            window.__previewZoom = 1;
            window.__setPreviewZoom = function (z) {{
                z = parseFloat(z) || 1;
                window.__previewZoom = z;
                const overlay = document.getElementById('overlay');
                if (!overlay) return;
                overlay.style.transformOrigin = 'top left';
                if (z === 1) {{
                    overlay.style.transform = '';
                    overlay.style.margin = '';
                }} else {{
                    overlay.style.margin = '0';
                    overlay.style.transform = 'scale(' + z + ')';
                }}

                overlay.style.setProperty('--handle-size',    (12 / z) + 'px');
                overlay.style.setProperty('--handle-half',    (6  / z) + 'px');
                overlay.style.setProperty('--chrome-outline', (2  / z) + 'px');
            }};
         </script>
     ");

        sb.AppendLine(@$"{GetWebsocketInjectionScript(route.Id.ToString())}");

        // moving/resizing/selecting in edit mode
        if (isEditor)
        {
            sb.AppendLine(@"
            <script>
            document.querySelectorAll('.widget-chrome').forEach(chrome => {
                const wrapper = document.querySelector(
                    '.widget-wrapper[data-id=""' + chrome.dataset.id + '""]');
                if (!wrapper) return;

                let isDragging = false;
                let offsetX = 0, offsetY = 0;

                chrome.addEventListener('mousedown', async e => {
                    isDragging = true;
                    const z = window.__previewZoom || 1;
                    offsetX = e.clientX / z - wrapper.offsetLeft;
                    offsetY = e.clientY / z - wrapper.offsetTop;

                    const id = wrapper.dataset.id;
                    await fetch(`/api/select/${id}`, {
                        method: 'GET'
                    });
                    e.preventDefault();
                });

                document.addEventListener('mousemove', e => {
                    if (!isDragging) return;
                    const z = window.__previewZoom || 1;
                    const rawX = e.clientX / z - offsetX;
                    const rawY = e.clientY / z - offsetY;
                    setWidgetPosition(wrapper,
                        snapEnabled ? snapTo(rawX, SNAP_SIZE) : rawX,
                        snapEnabled ? snapTo(rawY, SNAP_SIZE) : rawY);
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

            let snapEnabled = false;
            const SNAP_SIZE = 20;

            document.querySelectorAll('.widget-chrome').forEach(chrome => {{
                const wrapper = document.querySelector(
                    '.widget-wrapper[data-id=""' + chrome.dataset.id + '""]');
                if (!wrapper) return;

                let scaleX = parseFloat(wrapper.dataset.scalex) || 1;
                let scaleY = parseFloat(wrapper.dataset.scaley) || 1;

                applyWidgetLayout(wrapper,
                    wrapper.dataset.origWidth, wrapper.dataset.origHeight, scaleX, scaleY);

                let pendingScaleX = scaleX;
                let pendingScaleY = scaleY;

                let isResizing = false;
                let activeHandle = null;
                let startX, startY;
                let baselineWidth, baselineHeight;
                let startLeft, startTop;
                let isShiftHeld = false;
                let isCtrlHeld  = false;
                let isAltHeld   = false;

                const EDGE_HANDLES   = ['handle-n', 'handle-s', 'handle-e', 'handle-w'];
                const CORNER_HANDLES = ['handle-nw', 'handle-ne', 'handle-sw', 'handle-se'];

                function isEdgeHandle(handle) {{
                    return EDGE_HANDLES.some(c => handle.classList.contains(c));
                }}
                function isCornerHandle(handle) {{
                    return CORNER_HANDLES.some(c => handle.classList.contains(c));
                }}

                function updateHandleIndicators() {{
                    chrome.querySelectorAll('.resize-handle').forEach(h => {{
                        h.classList.remove('shift-active', 'ctrl-dimension');
                        if (isCtrlHeld && !isShiftHeld) {{
                            h.classList.add('ctrl-dimension');
                        }} else if (isShiftHeld && !isCtrlHeld) {{
                            if (isCornerHandle(h)) h.classList.add('shift-active');
                        }}
                    }});
                }}

                document.addEventListener('keydown', (e) => {{
                    if (e.key === 'Shift') {{ isShiftHeld = true; updateHandleIndicators(); }}
                    if (e.key === 'Control') {{ isCtrlHeld = true; updateHandleIndicators(); }}
                    if (e.key === 'Alt') {{ isAltHeld = true; snapEnabled = true; }}
                }});
                document.addEventListener('keyup', (e) => {{
                    if (e.key === 'Shift') {{ isShiftHeld = false; updateHandleIndicators(); }}
                    if (e.key === 'Control') {{ isCtrlHeld = false; updateHandleIndicators(); }}
                    if (e.key === 'Alt') {{ isAltHeld = false; snapEnabled = false; }}
                }});

                chrome.querySelectorAll('.resize-handle').forEach(handle => {{
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
                        pendingScaleX = scaleX;
                        pendingScaleY = scaleY;
                    }});
                }});

                document.addEventListener('mousemove', e => {{
                    if (!isResizing) return;

                    const z = window.__previewZoom || 1;
                    const dx = (e.clientX - startX) / z / scaleX;
                    const dy = (e.clientY - startY) / z / scaleY;

                    if (e.ctrlKey && !e.shiftKey) {{
                        let newWidth  = baselineWidth;
                        let newHeight = baselineHeight;
                        let newLeft   = startLeft;
                        let newTop    = startTop;

                        if (activeHandle.classList.contains('handle-e') ||
                            activeHandle.classList.contains('handle-ne') ||
                            activeHandle.classList.contains('handle-se')) {{
                            newWidth = Math.max(MIN_WIDTH, baselineWidth + dx);
                        }}

                        if (activeHandle.classList.contains('handle-w') ||
                            activeHandle.classList.contains('handle-nw') ||
                            activeHandle.classList.contains('handle-sw')) {{
                            const clamped = Math.max(MIN_WIDTH, baselineWidth - dx);
                            newLeft = startLeft + (baselineWidth - clamped) * scaleX;
                            newWidth = clamped;
                        }}

                        if (activeHandle.classList.contains('handle-s') ||
                            activeHandle.classList.contains('handle-se') ||
                            activeHandle.classList.contains('handle-sw')) {{
                            newHeight = Math.max(MIN_HEIGHT, baselineHeight + dy);
                        }}

                        if (activeHandle.classList.contains('handle-n') ||
                            activeHandle.classList.contains('handle-nw') ||
                            activeHandle.classList.contains('handle-ne')) {{
                            const clamped = Math.max(MIN_HEIGHT, baselineHeight - dy);
                            newTop = startTop + (baselineHeight - clamped) * scaleY;
                            newHeight = clamped;
                        }}

                        if (snapEnabled) {{
                            newWidth  = snapTo(newWidth,  SNAP_SIZE);
                            newHeight = snapTo(newHeight, SNAP_SIZE);
                            newLeft   = snapTo(newLeft,   SNAP_SIZE);
                            newTop    = snapTo(newTop,    SNAP_SIZE);
                        }}

                        applyWidgetLayout(wrapper, newWidth, newHeight, scaleX, scaleY);
                        setWidgetPosition(wrapper, newLeft, newTop);
                        return;
                    }}

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

                    if (e.shiftKey && !e.ctrlKey && isCornerHandle(activeHandle)) {{
                        const aspectRatio = baselineWidth / baselineHeight;
                        let candidateWidth = newWidth;
                        let candidateHeight = newHeight;

                        if (Math.abs(newWidth / baselineWidth) > Math.abs(newHeight / baselineHeight)) {{
                            candidateHeight = candidateWidth / aspectRatio;
                        }} else {{
                            candidateWidth = candidateHeight * aspectRatio;
                        }}

                        if (candidateWidth < MIN_WIDTH) {{ candidateWidth = MIN_WIDTH;  candidateHeight = candidateWidth / aspectRatio;}}
                        if (candidateHeight < MIN_HEIGHT) {{ candidateHeight = MIN_HEIGHT; candidateWidth = candidateHeight * aspectRatio;}}

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

                    if (snapEnabled) {{
                        newWidth  = snapTo(newWidth,  SNAP_SIZE);
                        newHeight = snapTo(newHeight, SNAP_SIZE);
                        newLeft   = snapTo(newLeft,   SNAP_SIZE);
                        newTop    = snapTo(newTop,    SNAP_SIZE);
                    }}

                    pendingScaleX = (newWidth  / baselineWidth)  * scaleX;
                    pendingScaleY = (newHeight / baselineHeight) * scaleY;

                    applyWidgetLayout(wrapper, baselineWidth, baselineHeight,
                                      pendingScaleX, pendingScaleY);
                    setWidgetPosition(wrapper, newLeft, newTop);
                }});

                document.addEventListener('mouseup', e => {{
                    if (!isResizing) return;
                    isResizing = false;

                    const id = wrapper.dataset.id;

                    if (e.ctrlKey && !e.shiftKey) {{
                        const newWidth  = Math.round(parseFloat(wrapper.dataset.origWidth));
                        const newHeight = Math.round(parseFloat(wrapper.dataset.origHeight));
                        const x = wrapper.offsetLeft;
                        const y = wrapper.offsetTop;

                        applyWidgetLayout(wrapper, newWidth, newHeight, scaleX, scaleY);

                        fetch(`/api/update-dimensions/${{id}}`, {{
                            method: 'POST',
                            headers: {{ 'Content-Type': 'application/json' }},
                            body: JSON.stringify({{ width: newWidth, height: newHeight, x, y }})
                        }});
                        return;
                    }}

                    scaleX = pendingScaleX;
                    scaleY = pendingScaleY;

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