using System.Net;
using System.Text;

using SubathonManager.Data;

namespace SubathonManager.Server;

public partial class WebServer
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
            StopWebsocketServer();
        }
        catch
        {
            //
        }
    }
    
    private async Task HandleRequestAsync(HttpListenerContext ctx)
    {
        string path = ctx.Request.Url?.AbsolutePath ?? "/";
        if (path != "/ws")
            Console.WriteLine($"Request: {path}");

        if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            if (await HandleApiReqeustAsync(ctx, path)) return;
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

}