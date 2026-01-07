using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubathonManager.Data;
using SubathonManager.Core.Models;
using SubathonManager.Core;
using SubathonManager.Core.Events;

namespace SubathonManager.Server;

public partial class WebServer
{
    internal readonly IDbContextFactory<AppDbContext> _factory;
    public int Port { get; }
    private readonly HttpListener _listener;
    public bool Running { get; private set; }
    
    private readonly HashSet<string> _servedFolders = new();
    private readonly ILogger? _logger;
    private readonly IConfig _config;
    private SubathonValueConfigHelper _valueHelper;
    
    record RouteKey(string Method, string Pattern);
    private readonly List<(RouteKey key, Func<IHttpContext, Task> handler)> _routes
        = new();
    
    internal Task InvokeHandleRequest(IHttpContext ctx)
        => HandleRequestAsync(ctx);

    internal object InvokeBuildDataSummary(List<SubathonEvent> e)
        => BuildDataSummary(e);
    
    public WebServer(IDbContextFactory<AppDbContext> factory, IConfig config, ILogger? logger,
        int port = 14040)
    {
        _factory = factory;
        _config = config; // unused but handy to have for future
        _logger = logger ?? AppServices.Provider.GetRequiredService<ILogger<WebServer>>();
        _valueHelper = new(factory, null);
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
                _ = Task.Run(() => HandleRequestAsync(new HttpListenerContextAdapter(context)));
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
    
    internal Func<IHttpContext, Task>? MatchRoute(string method, string path)
    {
        return _routes
            .Where(r => r.key.Method == method && path.StartsWith(r.key.Pattern))
            .Select(r => r.handler)
            .FirstOrDefault();
    }
    
    private async Task HandleRequestAsync(IHttpContext ctx)
    {
        string path = ctx.Path ?? "/";
        var method = ctx.Method;

        if (path != "/ws")
            _logger?.LogDebug($"Request:[{method}] {path}");
        else
            _logger?.LogTrace($"Request: [{method}] {path}");
        
        
        bool handled = false;

        if (path.Contains("/externalPath/"))
        {
            path = path.Split("/externalPath/").Last();
            if (File.Exists(path))
            {
                await ctx.ServeFile(path, GetContentType(path));
                return;
            }
            await ctx.WriteResponse(400, "File not found");
            return;
        }
        
        var routeHandler = MatchRoute(method, path);
        if (routeHandler != null)
        {
            handled = true;
            await routeHandler(ctx);
        }

        if (handled) return;
        
        // Check if request is for a local file in a *known* widget folder
        // so we can load properly resources the html desires
        foreach (var folder in _servedFolders)
        {
            var fixedFolder = folder.Replace("\\", "/");
            if (path.Contains(fixedFolder) && File.Exists(path.TrimStart('/')))
            {
                await ctx.ServeFile(path.TrimStart('/'), GetContentType(path));
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

        await ctx.WriteResponse(code, msg);
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

}