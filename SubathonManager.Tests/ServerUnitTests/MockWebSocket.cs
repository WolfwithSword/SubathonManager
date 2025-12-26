using System.Net.WebSockets;
namespace SubathonManager.Tests.ServerUnitTests;

public sealed class MockWebSocket : WebSocket
{
    public List<ArraySegment<byte>> SentMessages { get; } = new();

    public override WebSocketState State => WebSocketState.Open;
    public override string? CloseStatusDescription => null;
    public override WebSocketCloseStatus? CloseStatus => null;
    public override string? SubProtocol => null;
    public bool Disposed { get; private set; }
    
    private readonly Queue<WebSocketReceiveResult> _receiveQueue = new();
    
    public void EnqueueReceive(WebSocketReceiveResult result)
    {
        _receiveQueue.Enqueue(result);
    }
    
    public override Task CloseAsync(
        WebSocketCloseStatus closeStatus,
        string? statusDescription,
        CancellationToken cancellationToken)
        => Task.CompletedTask;

    public override Task CloseOutputAsync(
        WebSocketCloseStatus closeStatus,
        string? statusDescription,
        CancellationToken cancellationToken)
        => Task.CompletedTask;

    public override void Abort()
    {
    }

    public override Task SendAsync(
        ArraySegment<byte> buffer,
        WebSocketMessageType messageType,
        bool endOfMessage,
        CancellationToken cancellationToken)
    {
        SentMessages.Add(buffer);
        return Task.CompletedTask;
    }

    public override Task<WebSocketReceiveResult> ReceiveAsync(
        ArraySegment<byte> buffer,
        CancellationToken cancellationToken)
    {
        if (_receiveQueue.Count > 0)
        {
            var result = _receiveQueue.Dequeue();
            return Task.FromResult(result);
        }
        
        return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
    }

    public override void Dispose()
    {
        Disposed = true;
    }

}