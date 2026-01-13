using SubathonManager.Core.Enums;
using System.Net.WebSockets;

namespace SubathonManager.Server;

public interface IWebSocketClient
{
    List<WebsocketClientMessageType> ClientTypes { get; set; }
    
    WebSocketState State { get; }
    
    Guid ClientId { get; }

    List<SubathonEventSource> IntegrationSources { get; }
    
    Task SendAsync(ArraySegment<byte> buffer, 
        WebSocketMessageType messageType, 
        bool endOfMessage, 
        CancellationToken cancellationToken);

    Task<WebSocketReceiveResult> ReceiveAsync(
        ArraySegment<byte> buffer, 
        CancellationToken cancellationToke);

    Task CloseAsync(
        WebSocketCloseStatus closeStatus,
        string statusDescription,
        CancellationToken cancellationToken);
}