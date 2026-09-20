using System.Net.WebSockets;
using SubathonManager.Core.Enums;

namespace SubathonManager.Server.Interfaces;

public interface IWebSocketClient {
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

    void Abort();
    void StartOutbound();
    bool TryEnqueue(byte[] payload, OutboundCoalesceKey key = OutboundCoalesceKey.None);
    void CompleteOutbound();
    Task CompleteOutboundAsync(TimeSpan drainTimeout);
}