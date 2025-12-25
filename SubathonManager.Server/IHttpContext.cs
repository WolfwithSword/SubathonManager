using System.Text;
using System.Net.WebSockets;
namespace SubathonManager.Server;

public interface IHttpContext
{
    string Method { get; }
    string Path { get; }
    
    string QueryString { get; }
    
    Stream Body { get; }
    Encoding Encoding { get; }
    bool IsWebSocket { get; }
    
    Task<WebSocket>? AcceptWebSocketAsync(string? subProtocol = null);

    Task WriteResponse(int statusCode, string body, bool addCors = false, string? contentType = null); 
    
    Task ServeFile(string fullPath, string contentType);
}