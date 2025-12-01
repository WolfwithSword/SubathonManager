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
    
    public WebServer(IDbContextFactory<AppDbContext> factory ,int port = 14040)
    {
        _factory = factory;
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
        SetupWebsocketListeners();
        Port = port;
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{Port}/");
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
        if (path != "/ws")
            _logger?.LogDebug($"Request: {path}");

        if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            if (await HandleApiRequestAsync(ctx, path)) return;
        }
        else if (path.StartsWith("/route/", StringComparison.OrdinalIgnoreCase))
        {
            if (await HandleRouteRequest(ctx, path)) return;
        }
        else if (path.StartsWith("/widget/", StringComparison.OrdinalIgnoreCase))
        {
            if (await HandleWidgetRequest(ctx, path)) return;
        }
        else if (path.StartsWith("/ws", StringComparison.OrdinalIgnoreCase))
        {
            if (await HandleWebSocketRequestAsync(ctx, path)) return;
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
        ctx.Response.ContentType = contentType;
        AddCorsHeaders(ctx.Response);  
        byte[] bytes = await File.ReadAllBytesAsync(fullPath);
        await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
        ctx.Response.Close();
    }

}