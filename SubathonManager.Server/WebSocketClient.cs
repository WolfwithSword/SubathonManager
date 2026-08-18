using System.Net.WebSockets;
using Microsoft.Extensions.Logging;
using SubathonManager.Core.Enums;

namespace SubathonManager.Server;

public class WebSocketClient : IWebSocketClient
{
    private readonly WebSocket _socket;
    private Guid _clientId = Guid.NewGuid();
    private readonly WebSocketOutboundQueue _outbound;

    public WebSocketClient(WebSocket socket, ILogger? logger = null)
    {
        _socket = socket;
        _outbound = new WebSocketOutboundQueue(_clientId,
            (buffer, ct) => _socket.SendAsync(buffer, WebSocketMessageType.Text, true, ct),
            () => _socket.State, Abort, logger);
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

    public void Abort() => _socket.Abort();

    public void StartOutbound() => _outbound.Start();

    public bool TryEnqueue(byte[] payload, OutboundCoalesceKey key = OutboundCoalesceKey.None)
        => _outbound.TryEnqueue(payload, key);

    public void CompleteOutbound() => _outbound.Complete();

    public Task CompleteOutboundAsync(TimeSpan drainTimeout) => _outbound.CompleteAsync(drainTimeout);
}
