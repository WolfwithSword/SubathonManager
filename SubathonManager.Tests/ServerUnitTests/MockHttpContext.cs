using System.Net.WebSockets;
using System.Text;
using SubathonManager.Server.Interfaces;

namespace SubathonManager.Tests.ServerUnitTests;

public class MockHttpContext : IHttpContext {
    private readonly MemoryStream _responseStream = new();

    public int StatusCode { get; private set; }
    public string ResponseBody { get; private set; } = "";

    public MockWebSocket Socket { get; } = new();
    public int AcceptCalls { get; private set; }
    public Stream ResponseBodyStream => _responseStream;

    public byte[]? ServedBytes { get; private set; }
    public string Method { get; set; } = "GET";
    public string Path { get; set; } = "/";
    public string QueryString { get; set; } = "";

    public IReadOnlyDictionary<string, string> Headers { get; set; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public Stream Body { get; set; } = new MemoryStream();
    public Encoding Encoding { get; set; } = Encoding.UTF8;
    public bool IsWebSocket { get; set; }

    public Task ServeFile(string fullPath, string contentType) {
        return Task.CompletedTask;
    }

    public Task ServeBytes(byte[] data, string contentType) {
        ServedBytes = data;
        StatusCode = 200;
        ResponseBodyStream.Write(data);
        return Task.CompletedTask;
    }

    public Task<WebSocket>? AcceptWebSocketAsync(string? subProtocol = null) {
        AcceptCalls++;
        return IsWebSocket
            ? Task.FromResult<WebSocket>(Socket)
            : null;
    }

    public Task WriteResponse(int statusCode, string body, bool addCors = false, string? contentType = null) {
        StatusCode = statusCode;
        ResponseBody = body;
        ResponseBodyStream.WriteAsync(Encoding.UTF8.GetBytes(body));
        return Task.CompletedTask;
    }


    public string GetResponseText() {
        _responseStream.Position = 0;
        return new StreamReader(_responseStream).ReadToEnd();
    }
}