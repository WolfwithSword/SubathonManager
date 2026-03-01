using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using SubathonManager.Core.Events;
using SubathonManager.Core;
using System.Diagnostics.CodeAnalysis;
using SubathonManager.Core.Interfaces;

namespace SubathonManager.Services;


[ExcludeFromCodeCoverage]
public class TimerService : IDisposable, IAppService
{
    private readonly TimeSpan _tickInterval = TimeSpan.FromSeconds(1);
    private readonly CancellationTokenSource _cts = new();
    private readonly ILogger? _logger = AppServices.Provider.GetRequiredService<ILogger<TimerService>>();
    
    private PeriodicTimer? _periodicTimer;
    
    public async Task StartAsync(CancellationToken ct = default)
    {
        _periodicTimer ??= new PeriodicTimer(_tickInterval);

        try
        {
            if (!_cts.IsCancellationRequested)
            {
                while (await _periodicTimer.WaitForNextTickAsync(_cts.Token))
                {
                    Tick();
                    if (_cts.IsCancellationRequested) break;
                }
            }
        }
        catch (OperationCanceledException ex)
        {
            if (_cts.IsCancellationRequested) return;
            _logger?.LogError(ex, ex.Message);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, ex.Message);
        }
    }

    public void Stop()
    {
        if (_cts.IsCancellationRequested) return;
        _cts.Cancel();
        try
        {
            _periodicTimer?.Dispose();
        }
        catch { /**/ }
        _periodicTimer = null;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        Stop();
        return Task.CompletedTask;
    }

    private void Tick()
    {
        TimerEvents.RaiseTimerTickEvent(_tickInterval);
    }
    
    public void Dispose()
    {
        if(!_cts.IsCancellationRequested)
            _cts.Cancel();
        _periodicTimer?.Dispose();
        _cts.Dispose();
    }
}