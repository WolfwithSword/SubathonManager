using System.Diagnostics.CodeAnalysis;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace SubathonManager.Server;

[ExcludeFromCodeCoverage]
public sealed class AspNetContextAdapter(HttpContext ctx) : IHttpContext
{
    public string Method => ctx.Request.Method;
    public string Path => ctx.Request.Path.Value ?? "/";
    public string QueryString => ctx.Request.QueryString.Value?.TrimStart('?') ?? string.Empty;
    public Stream Body => ctx.Request.Body;
    public Encoding Encoding => Encoding.UTF8;
    public bool IsWebSocket => ctx.WebSockets.IsWebSocketRequest;

    public IReadOnlyDictionary<string, string> Headers
    {
        get
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in ctx.Request.Headers)
                dict[key] = value.ToString();
            return dict;
        }
    }

    public Task<WebSocket>? AcceptWebSocketAsync(string? subProtocol = null)
        => ctx.WebSockets.IsWebSocketRequest
            ? ctx.WebSockets.AcceptWebSocketAsync(subProtocol)
            : null;

    public async Task WriteResponse(int code, string body, bool addCors = false, string? contentType = null)
    {
        ctx.Response.StatusCode = code;
        if (contentType != null)
            ctx.Response.ContentType = contentType;
        if (addCors)
            AddCorsHeaders(ctx.Response);
        await ctx.Response.WriteAsync(body, Encoding.UTF8);
    }

    public async Task ServeFile(string fullPath, string contentType)
    {
        ctx.Response.ContentType = contentType;
        AddCorsHeaders(ctx.Response);
        await ctx.Response.SendFileAsync(fullPath);
    }

    private static void AddCorsHeaders(HttpResponse response)
    {
        response.Headers["Access-Control-Allow-Origin"] = "*";
        response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
        response.Headers["Access-Control-Allow-Headers"] = "Content-Type";
    }
}

internal sealed class NoopHostLifetime : IHostLifetime
{
    public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
