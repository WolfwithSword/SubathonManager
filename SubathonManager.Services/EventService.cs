using SubathonManager.Core.Models;
using SubathonManager.Data;
namespace SubathonManager.Services;

// We don't need one of these for Subathon updates, because those fire every second anyways
public class EventService: IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly PriorityQueue<SubathonEvent, DateTime> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly object _lock = new();
    private Task? _processingTask;

    public EventService()
    {
        Core.Events.SubathonEvents.SubathonEventCreated += AddSubathonEvent;
        _processingTask = Task.Run(LoopAsync);
        _processingTask.ContinueWith(t => 
                Console.WriteLine($"Event loop crashed: {t.Exception}"), 
            TaskContinuationOptions.OnlyOnFaulted);
    }

    private void AddSubathonEvent(SubathonEvent subathonEvent)
    {
        lock (_lock)
        {
            _queue.Enqueue(subathonEvent, subathonEvent.EventTimestamp);
        }

        _signal.Release();
    }
    
    public async Task LoopAsync()
    {
        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                await _signal.WaitAsync(_cts.Token);
                SubathonEvent? next = null;
                lock (_lock)
                {
                    if (_queue.TryDequeue(out var item, out _))
                    {
                        next = item;
                    }
                }

                if (next == null) continue;
                bool wasEffective = await AppDbContext.ProcessSubathonEvent(next);
                
                if (wasEffective)
                    Core.Events.SubathonEvents.RaiseSubathonEventProcessed(next);
                // also webserver should listen to the timer updated stuff, just like MainWindow does
            }
        }
        catch (OperationCanceledException ex)
        {
            Console.WriteLine(ex.Message);
            // TODO
        }
    }
    
    public async Task StopAsync()
    {
        _cts.Cancel();
        _signal.Release();
        if (_processingTask != null)
            await _processingTask;
    }
    
    public void Dispose()
    {
        _cts.Cancel();
        _signal.Dispose();
        _cts.Dispose();
    }
}