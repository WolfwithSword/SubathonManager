using System.Text;
using System.Net.WebSockets;
using SubathonManager.Server;

namespace SubathonManager.Tests.ServerUnitTests;

public class MockHttpContext: IHttpContext
{
    public string Method { get; set; } = "GET";
    public string Path { get; set; } = "/";
    public string QueryString { get; set; } = "";
    public Stream Body { get; set; } = new MemoryStream();
    public Encoding Encoding { get; set; } = Encoding.UTF8;
    public bool IsWebSocket { get; set; }

    public int StatusCode { get; private set; }
    public string ResponseBody { get; private set; } = "";
    
    public MockWebSocket Socket { get; } = new();
    public int AcceptCalls { get; private set; }
    
    private readonly MemoryStream _responseStream = new();
    public Stream ResponseBodyStream => _responseStream;

    public Task ServeFile(string fullPath, string contentType)
    {
        return Task.CompletedTask;
    }
    
    public Task<WebSocket>? AcceptWebSocketAsync(string? subProtocol = null)
    {
        AcceptCalls++;
        return IsWebSocket
            ? Task.FromResult<System.Net.WebSockets.WebSocket>(Socket)
            : null;
    }
    
    public Task WriteResponse(int statusCode, string body, bool addCors = false, string? contentType = null)
    {
        StatusCode = statusCode;
        ResponseBody = body;
        ResponseBodyStream.WriteAsync(Encoding.UTF8.GetBytes(body));
        return Task.CompletedTask;
    }
    

    public string GetResponseText()
    {
        _responseStream.Position = 0;
        return new StreamReader(_responseStream).ReadToEnd();
    }
}