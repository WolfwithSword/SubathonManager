using System.Net;
using System.Text;
using System.Net.WebSockets;
using System.Diagnostics.CodeAnalysis;
namespace SubathonManager.Server;


[ExcludeFromCodeCoverage]
public sealed class HttpListenerContextAdapter : IHttpContext
{
    private readonly HttpListenerContext _ctx;

    public HttpListenerContextAdapter(HttpListenerContext ctx)
    {
        _ctx = ctx;
    }

    public string Method => _ctx.Request.HttpMethod;
    public string Path => _ctx.Request.Url!.AbsolutePath;
    public Stream Body => _ctx.Request.InputStream;
    public string QueryString => _ctx.Request.Url!.Query.TrimStart('?');
    public Encoding Encoding => _ctx.Request.ContentEncoding;
    public bool IsWebSocket => _ctx.Request.IsWebSocketRequest;
    
    public Task<WebSocket>? AcceptWebSocketAsync(string? subProtocol = null)
    {
        if (!_ctx.Request.IsWebSocketRequest)
            return null;

        return AcceptInternalAsync(subProtocol);
    }

    private async Task<WebSocket> AcceptInternalAsync(string? subProtocol)
    {
        var wsContext = await _ctx.AcceptWebSocketAsync(subProtocol);
        return wsContext.WebSocket;
    }
    
    public async Task WriteResponse(int code, string body, bool addCors = false, string? contentType = null)
    {
        _ctx.Response.StatusCode = code;
        if (contentType != null)
            _ctx.Response.ContentType = contentType;

        if (addCors)
            AddCorsHeaders(_ctx.Response);
        var bytes = Encoding.UTF8.GetBytes(body);
        await _ctx.Response.OutputStream.WriteAsync(bytes);
        _ctx.Response.Close();
    }
    
    public async Task ServeFile(string fullPath, string contentType)
    {
        _ctx.Response.ContentType = contentType;
        AddCorsHeaders(_ctx.Response);  
        await using var fs = File.OpenRead(fullPath);
        _ctx.Response.ContentLength64 = fs.Length;
        await fs.CopyToAsync(_ctx.Response.OutputStream);
        _ctx.Response.Close();
    }
    
    private static void AddCorsHeaders(HttpListenerResponse response)
    {
        response.AddHeader("Access-Control-Allow-Origin", "*");
        response.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
        response.AddHeader("Access-Control-Allow-Headers", "Content-Type");
    }
}