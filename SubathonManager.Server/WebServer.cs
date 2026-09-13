using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Data;
using SubathonManager.Server.Interfaces;

namespace SubathonManager.Server;

public partial class WebServer : IAppService {
    private readonly IConfig _config;

    // ReSharper disable once InconsistentNaming
    internal readonly IDbContextFactory<AppDbContext> _factory;
    private readonly ILogger? _logger;

    private readonly List<(RouteKey key, Func<IHttpContext, Task> handler)> _routes
        = new();

    private readonly HashSet<string> _servedFolders = new();
    private WebApplication? _app;
    private readonly SubathonValueConfigHelper _valueHelper;

    public WebServer(ILogger<WebServer>? logger, IConfig config, IDbContextFactory<AppDbContext> factory) {
        _factory = factory;
        _config = config; // unused but handy to have for future
        _logger = logger ?? AppServices.Provider.GetRequiredService<ILogger<WebServer>>();
        _valueHelper = new SubathonValueConfigHelper(factory, null);
        using (AppDbContext db = _factory.CreateDbContext()) {
            List<Route> routes = db.Routes.ToList();
            if (routes.Count == 0)
                _logger?.LogDebug("No routes found");
            else
                foreach (Route route in routes)
                    AddRoute(route);
        }

        Port = int.Parse(_config.Get("Server", "Port", "14040")!);
    }

    public int Port { get; set; }
    public bool Running { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken = default) {
        Port = int.Parse(_config.Get("Server", "Port", "14040")!);
        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
            try {
                Initialize();
                await _app!.StartAsync(cancellationToken);
                Running = true;
                _logger?.LogInformation($"WebServer running at http://localhost:{Port}/");
                WebServerEvents.RaiseWebServerStatusChange(Running);
                return;
            }
            catch (Exception ex) {
                Running = false;

                await StopServerAsync();
                _logger?.LogWarning(ex, "WebServer failed to start on port {Port} (attempt {Attempt}/{Max})",
                    Port, attempt, maxAttempts);
                if (attempt < maxAttempts)
                    await Task.Delay(250, cancellationToken);
            }

        _logger?.LogError("WebServer could not start on port {Port} after {Max} attempts", Port, maxAttempts);
        WebServerEvents.RaiseWebServerStatusChange(Running);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default) {
        Running = false;
        await StopServerAsync();
        WebServerEvents.RaiseWebServerStatusChange(Running);
    }

    internal Task InvokeHandleRequest(IHttpContext ctx) {
        return HandleRequestAsync(ctx);
    }

    internal object InvokeBuildDataSummary(List<SubathonEvent> e) {
        return BuildDataSummary(e);
    }

    internal void Initialize() {
        StopServerAsync().GetAwaiter().GetResult();

        _routes.Clear();
        SetupApiRoutes();
        SetupWebhookRoutes();
        SetupOverlayRoutes();
        SetupWebsocketListeners();

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();

        builder.Services.AddSingleton<IHostLifetime, NoopHostLifetime>();
        builder.WebHost.UseUrls($"http://127.0.0.1:{Port}");

        WebApplication app = builder.Build();
        app.UseWebSockets();
        app.Run(ctx => HandleRequestAsync(new AspNetContextAdapter(ctx)));
        _app = app;
    }

    private void SetupOverlayRoutes() {
        _routes.Add((new RouteKey("GET", "/ws"), HandleWebSocketRequestAsync));
        _routes.Add((new RouteKey("GET", "/widget/"), HandleWidgetRequest));
        _routes.Add((new RouteKey("GET", "/route/"), HandleRouteRequest));
        _routes.Add((new RouteKey("GET", ResourcePaths.UrlPrefix), HandleResourceRequest));
    }

    private async Task StopServerAsync() {
        StopWebsocketServer();
        if (_app == null) return;
        WebApplication? app = _app;
        _app = null;

        try {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await app.StopAsync(cts.Token);
        }
        catch {
            /**/
        }

        try {
            await app.DisposeAsync();
        }
        catch {
            /**/
        }
    }

    public void Stop() {
        Running = false;
        try {
            StopServerAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex) {
            _logger?.LogWarning(ex, "WebServer Error on Stopping");
        }
        finally {
            WebServerEvents.RaiseWebServerStatusChange(Running);
        }
    }

    internal Func<IHttpContext, Task>? MatchRoute(string method, string path) {
        return _routes
            .Where(r => r.key.Method == method && path.StartsWith(r.key.Pattern))
            .Select(r => r.handler)
            .FirstOrDefault();
    }

    private async Task HandleRequestAsync(IHttpContext ctx) {
        string path = ctx.Path ?? "/";
        string method = ctx.Method;

        if (path != "/ws")
            _logger?.LogDebug($"Request:[{method}] {path}");
        else
            _logger?.LogTrace($"Request: [{method}] {path}");


        var handled = false;

        if (path.Contains("/externalPath/")) {
            path = path.Split("/externalPath/").Last();
            if (File.Exists(path)) {
                await ctx.ServeFile(path, GetContentType(path));
                return;
            }

            await ctx.WriteResponse(400, "File not found");
            return;
        }

        Func<IHttpContext, Task>? routeHandler = MatchRoute(method, path);
        if (routeHandler != null) {
            handled = true;
            await routeHandler(ctx);
        }

        if (handled) return;

        // Check if request is for a local file in a *known* widget folder
        // so we can load properly resources the html desires
        foreach (string folder in _servedFolders) {
            string fixedFolder = folder.Replace("\\", "/");
            if (path.Contains(fixedFolder) && File.Exists(path.TrimStart('/'))) {
                await ctx.ServeFile(path.TrimStart('/'), GetContentType(path));
                return;
            }
        }

        var code = 404;
        var msg = "404 Not Found";
        if (path.StartsWith("/api")) {
            code = 400;
            msg = "Invalid API Request";
        }

        await ctx.WriteResponse(code, msg);
    }

    internal static string GetContentType(string filePath) {
        string ext = Path.GetExtension(filePath).ToLower();
        string contentType = ext switch {
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
            ".bmp" => "image/bmp",
            ".svg" => "image/svg+xml",
            ".ico" => "image/x-icon",

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

    private record RouteKey(string Method, string Pattern);
}