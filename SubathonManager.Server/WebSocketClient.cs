using System.Net.WebSockets;
using SubathonManager.Core.Enums;

namespace SubathonManager.Server;

public class WebSocketClient : IWebSocketClient
{
    private readonly WebSocket _socket;
    private Guid _clientId = Guid.NewGuid();
    
    public WebSocketClient(WebSocket socket)
    {
        _socket = socket;
    }

    public List<SubathonEventSource> IntegrationSources { get; } = new();

    public Guid ClientId => _clientId;
    
    public List<WebsocketClientMessageType> ClientTypes { get; set; } = new(){ WebsocketClientMessageType.Generic };

    public WebSocketState State => _socket.State;

    public Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage,
        CancellationToken cancellationToken) => _socket.SendAsync(buffer, messageType, endOfMessage, cancellationToken);

    public Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToke) 
        => _socket.ReceiveAsync(buffer, cancellationToke);

    public Task CloseAsync(WebSocketCloseStatus closeStatus, string statusDescription, CancellationToken cancellationToken) 
        => _socket.CloseAsync(closeStatus, statusDescription, cancellationToken);
}