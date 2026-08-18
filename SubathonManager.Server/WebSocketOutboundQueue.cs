using System.Net.WebSockets;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace SubathonManager.Server;

public enum OutboundCoalesceKey
{
    None = 0,
    SubathonTimer,
    SubathonTotals,
    SubscriptionTotals,
    GoalsList
}

internal sealed class WebSocketOutboundQueue
{
    private const int Capacity = 256;
    private static readonly long StallTimeoutMs = (long)TimeSpan.FromSeconds(30).TotalMilliseconds;

    private readonly record struct Slot(byte[]? Payload, OutboundCoalesceKey Key);

    private static readonly int KeyCount = Enum.GetValues<OutboundCoalesceKey>().Length;

    private readonly Channel<Slot> _queue = Channel.CreateBounded<Slot>(
        new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

    private readonly byte[]?[] _latest = new byte[]?[KeyCount];
    private readonly int[] _marked = new int[KeyCount];

    private readonly Func<ArraySegment<byte>, CancellationToken, Task> _send;
    private readonly Func<WebSocketState> _state;
    private readonly Action _onStalled;
    private readonly ILogger? _logger;
    private readonly Guid _clientId;

    private long _fullSinceTicks;
    private int _stalledFired;
    private int _started;
    private int _completed;
    private Task _pump = Task.CompletedTask;

    internal WebSocketOutboundQueue(
        Guid clientId,
        Func<ArraySegment<byte>, CancellationToken, Task> send,
        Func<WebSocketState> state,
        Action onStalled,
        ILogger? logger)
    {
        _clientId = clientId;
        _send = send;
        _state = state;
        _onStalled = onStalled;
        _logger = logger;
    }

    internal void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        _pump = Task.Run(PumpAsync);
    }

    internal bool TryEnqueue(byte[] payload, OutboundCoalesceKey key = OutboundCoalesceKey.None)
    {
        if (Volatile.Read(ref _completed) == 1) return false;

        if (key == OutboundCoalesceKey.None)
            return _queue.Writer.TryWrite(new Slot(payload, key)) ? NoteWritten() : NoteFull();

        int i = (int)key;
        Interlocked.Exchange(ref _latest[i], payload);

        if (Interlocked.Exchange(ref _marked[i], 1) == 1) return true;

        if (_queue.Writer.TryWrite(new Slot(null, key))) return NoteWritten();

        Interlocked.Exchange(ref _marked[i], 0);
        return NoteFull();
    }

    private bool NoteWritten()
    {
        Volatile.Write(ref _fullSinceTicks, 0);
        return true;
    }

    private bool NoteFull()
    {
        long now = Environment.TickCount64;
        long prev = Interlocked.CompareExchange(ref _fullSinceTicks, now, 0);
        long fullSince = prev == 0 ? now : prev;

        if (now - fullSince >= StallTimeoutMs && Interlocked.Exchange(ref _stalledFired, 1) == 0)
        {
            _logger?.LogWarning(
                "WebSocket client [{ClientId}] outbound queue has been full for over 30s. dropping connection",
                _clientId);
            _onStalled();
        }

        return false;
    }

    private async Task PumpAsync()
    {
        try
        {
            while (await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (_queue.Reader.TryRead(out Slot slot))
                {
                    byte[]? payload = slot.Payload;
                    if (payload is null)
                    {
                        int i = (int)slot.Key;
                        Interlocked.Exchange(ref _marked[i], 0);
                        payload = Interlocked.Exchange(ref _latest[i], null);
                        if (payload is null) continue;
                    }

                    if (_state() != WebSocketState.Open) return;
                    await _send(payload, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "WebSocket client [{ClientId}] outbound pump stopped", _clientId);
        }
    }

    internal void Complete()
    {
        if (Interlocked.Exchange(ref _completed, 1) == 1) return;
        _queue.Writer.TryComplete();
    }

    internal async Task CompleteAsync(TimeSpan drainTimeout)
    {
        Complete();
        try
        {
            await _pump.WaitAsync(drainTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger?.LogDebug("WebSocket client [{ClientId}] outbound queue did not drain time before timeout", _clientId);
        }
    }
}
