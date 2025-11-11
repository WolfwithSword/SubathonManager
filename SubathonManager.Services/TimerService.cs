using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using SubathonManager.Core.Events;
using SubathonManager.Core;

namespace SubathonManager.Services;

public class TimerService
{
    private readonly TimeSpan _tickInterval = TimeSpan.FromSeconds(1);
    private readonly CancellationTokenSource _cts = new();
    private readonly ILogger? _logger = AppServices.Provider.GetRequiredService<ILogger<TimerService>>();
    
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
            _logger?.LogError(ex, ex.Message);
        }
    }
    
    public void Stop() => _cts.Cancel();

    private void Tick()
    {
        TimerEvents.RaiseTimerTickEvent(_tickInterval);
    }
}