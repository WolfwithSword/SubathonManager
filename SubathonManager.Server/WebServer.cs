using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubathonManager.Data;
using SubathonManager.Core;
using SubathonManager.Core.Events;

namespace SubathonManager.Server;

public partial class WebServer
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    public int Port { get; }
    private readonly HttpListener _listener;
    public bool Running { get; private set; }
    
    private readonly HashSet<string> _servedFolders = new();
    private readonly ILogger? _logger = AppServices.Provider.GetRequiredService<ILogger<WebServer>>();
    private readonly IConfig _config;
    private SubathonValueConfigHelper _valueHelper = new ();
    
    record RouteKey(string Method, string Pattern);
    private readonly List<(RouteKey key, Func<HttpListenerContext, Task> handler)> _routes
        = new();
    
    public WebServer(IDbContextFactory<AppDbContext> factory, IConfig config, int port = 14040)
    {
        _factory = factory;
        _config = config; // unused but handy to have for future
        using (var db = _factory.CreateDbContext())
        {
            var routes = db.Routes.ToList();
            if (routes.Count == 0)
            {
                _logger?.LogDebug("No routes found");
            }
            else {
                foreach (var route in routes)
                {
                    AddRoute(route);
                }
            }
        }

        _routes.Clear();
        SetupApiRoutes();
        SetupOverlayRoutes();
        SetupWebsocketListeners();
        Port = port;
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{Port}/");
    }

    private void SetupOverlayRoutes()
    {
        _routes.Add((new RouteKey("GET", "/ws"),HandleWebSocketRequestAsync));
        _routes.Add((new RouteKey("GET", "/widget/"),HandleWidgetRequest ));
        _routes.Add((new RouteKey("GET", "/route/"),HandleRouteRequest ));
    }

    public async Task StartAsync()
    {
        Running = true;
        _listener.Start();
        _logger?.LogInformation($"WebServer running at http://localhost:{Port}/");
        WebServerEvents.RaiseWebServerStatusChange(Running);
        while (Running)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleRequestAsync(context));
            }
            catch (HttpListenerException ex)
            {
                _logger?.LogError(ex, $"WebServer Error: {ex.Message}");
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, $"WebServer Error: {ex.Message}");
            }
        }
    }

    public void Stop()
    {
        Running = false;
        try
        {
            _listener.Stop();
            StopWebsocketServer();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "WebServer Error on Stopping");
        }
        finally
        {
            WebServerEvents.RaiseWebServerStatusChange(Running);
        }
    }
    
    private async Task HandleRequestAsync(HttpListenerContext ctx)
    {
        string path = ctx.Request.Url?.AbsolutePath ?? "/";
        var method = ctx.Request.HttpMethod;

        if (path != "/ws")
            _logger?.LogDebug($"Request:[{method}] {path}");
        else
            _logger?.LogTrace($"Request: [{method}] {path}");
        
        
        bool handled = false;
        foreach (var (key, handler) in _routes)
        {
            if (method == key.Method && path.StartsWith(key.Pattern, StringComparison.OrdinalIgnoreCase))
            {
                await handler(ctx);
                handled = true;
                break;
            }
        }

        if (handled) return;
        
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

        int code = 404;
        string msg = "404 Not Found";
        if (path.StartsWith("/api"))
        {
            code = 400;
            msg = "Invalid API Request";
        }

        await MakeApiResponse(ctx, code, msg);
    }
    
    private static void AddCorsHeaders(HttpListenerResponse response)
    {
        response.AddHeader("Access-Control-Allow-Origin", "*");
        response.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
        response.AddHeader("Access-Control-Allow-Headers", "Content-Type");
    }

    internal static string GetContentType(string filePath)
    {
        string ext = Path.GetExtension(filePath).ToLower();
        string contentType = ext switch
        {
            // web standard
            ".html" or ".htm" => "text/html",
            ".css" => "text/css",
            ".js" => "application/javascript",
            ".json" => "application/json",
            
            // img
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".avif" => "image/avif",
            ".bmp"  => "image/bmp",
            ".svg"  => "image/svg+xml",
            ".ico"  => "image/x-icon",
            
            // video
            ".mp4" => "video/mp4",
            ".m4v" => "video/x-m4v",
            ".webm" => "video/webm",
            ".ogv" => "video/ogg",
            
            // audio
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".ogg" or ".oga" => "audio/ogg",
            ".opus" => "audio/opus",
            ".m4a" => "audio/mp4",
            
            // local fonts
            ".woff" => "font/woff",
            ".woff2" => "font/woff2",
            ".ttf" => "font/ttf",
            ".otf" => "font/otf",
            
            // idk but other files 
            ".txt" => "text/plain",
            ".csv" => "text/csv",
            ".xml" => "application/xml",
            
            _ => "application/octet-stream"
        };
        return contentType;
    }

    private async Task ServeFile(HttpListenerContext ctx, string fullPath)
    {
        ctx.Response.ContentType = GetContentType(fullPath);
        AddCorsHeaders(ctx.Response);  
        await using var fs = File.OpenRead(fullPath);
        ctx.Response.ContentLength64 = fs.Length;
        await fs.CopyToAsync(ctx.Response.OutputStream);
        ctx.Response.Close();
    }

}