using System.Net.WebSockets;
using System.Text;

// ReSharper disable NullableWarningSuppressionIsUsed
namespace SubathonManager.Tests.ServerUnitTests;

public sealed class MockWebSocket : WebSocket {
    private readonly Queue<(byte[] Data, WebSocketReceiveResult Result)> _receiveQueue = new();
    public List<ArraySegment<byte>> SentMessages { get; } = new();

    public override WebSocketState State => WebSocketState.Open;
    public override string? CloseStatusDescription => null;
    public override WebSocketCloseStatus? CloseStatus => null;
    public override string? SubProtocol => null;
    public bool Disposed { get; private set; }

    public void EnqueueReceive(string message) {
        byte[] bytes = Encoding.UTF8.GetBytes(message);
        _receiveQueue.Enqueue((
            bytes,
            new WebSocketReceiveResult(
                bytes.Length,
                WebSocketMessageType.Text,
                true
            )
        ));
    }

    public void EnqueueClose() {
        _receiveQueue.Enqueue((
            Array.Empty<byte>(),
            new WebSocketReceiveResult(0, WebSocketMessageType.Close, true)
        ));
    }

    public override Task CloseAsync(
        WebSocketCloseStatus closeStatus,
        string? statusDescription,
        CancellationToken cancellationToken) {
        return Task.CompletedTask;
    }

    public override Task CloseOutputAsync(
        WebSocketCloseStatus closeStatus,
        string? statusDescription,
        CancellationToken cancellationToken) {
        return Task.CompletedTask;
    }

    public override void Abort() {
    }

    public override Task SendAsync(
        ArraySegment<byte> buffer,
        WebSocketMessageType messageType,
        bool endOfMessage,
        CancellationToken cancellationToken) {
        SentMessages.Add(buffer);
        return Task.CompletedTask;
    }

    public override Task<WebSocketReceiveResult> ReceiveAsync(
        ArraySegment<byte> buffer,
        CancellationToken cancellationToken) {
        if (_receiveQueue.Count == 0)
            return Task.FromResult(
                new WebSocketReceiveResult(0, WebSocketMessageType.Close, true)
            );
        (byte[] data, WebSocketReceiveResult result) = _receiveQueue.Dequeue();
        Array.Copy(data, 0, buffer.Array!, buffer.Offset, data.Length);
        return Task.FromResult(result);
    }

    public override void Dispose() {
        Disposed = true;
    }
}