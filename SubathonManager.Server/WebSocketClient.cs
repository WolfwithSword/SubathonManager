using System.Net.WebSockets;
using Microsoft.Extensions.Logging;
using SubathonManager.Core.Enums;
using SubathonManager.Server.Interfaces;

namespace SubathonManager.Server;

public class WebSocketClient : IWebSocketClient {
    private readonly WebSocketOutboundQueue _outbound;
    private readonly WebSocket _socket;

    public WebSocketClient(WebSocket socket, ILogger? logger = null) {
        _socket = socket;
        _outbound = new WebSocketOutboundQueue(ClientId,
            (buffer, ct) => _socket.SendAsync(buffer, WebSocketMessageType.Text, true, ct),
            () => _socket.State, Abort, logger);
    }

    public List<SubathonEventSource> IntegrationSources { get; } = new();

    public Guid ClientId { get; } = Guid.NewGuid();

    public List<WebsocketClientMessageType> ClientTypes { get; set; } = new() { WebsocketClientMessageType.Generic };

    public WebSocketState State => _socket.State;

    public Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage,
        CancellationToken cancellationToken) {
        return _socket.SendAsync(buffer, messageType, endOfMessage, cancellationToken);
    }

    public Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToke) {
        return _socket.ReceiveAsync(buffer, cancellationToke);
    }

    public Task CloseAsync(WebSocketCloseStatus closeStatus, string statusDescription,
        CancellationToken cancellationToken) {
        return _socket.CloseAsync(closeStatus, statusDescription, cancellationToken);
    }

    public void Abort() {
        _socket.Abort();
    }

    public void StartOutbound() {
        _outbound.Start();
    }

    public bool TryEnqueue(byte[] payload, OutboundCoalesceKey key = OutboundCoalesceKey.None) {
        return _outbound.TryEnqueue(payload, key);
    }

    public void CompleteOutbound() {
        _outbound.Complete();
    }

    public Task CompleteOutboundAsync(TimeSpan drainTimeout) {
        return _outbound.CompleteAsync(drainTimeout);
    }
}