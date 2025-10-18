using SubathonManager.Core.Events;

namespace SubathonManager.Services;

public class TimerService
{
    private TimeSpan _tickInterval = TimeSpan.FromSeconds(1);
    private readonly CancellationTokenSource _cts = new();
    
    public async Task StartAsync()
    {
        var periodicTimer = new PeriodicTimer(_tickInterval);
        try
        {
            while (await periodicTimer.WaitForNextTickAsync(_cts.Token))
            {
                Tick();
            }
        }
        catch (OperationCanceledException ex)
        {
            // TODO
        }
    }
    
    public void Stop() => _cts.Cancel();

    private void Tick()
    {
        TimerEvents.RaiseTimerTickEvent(_tickInterval);
    }
}