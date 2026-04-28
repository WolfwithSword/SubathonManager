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
    
    private readonly Dictionary<string, ScheduledAction> _scheduledActions = [];
    private readonly Lock _lock = new();
    
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
                    await TickAsync();
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

    // private void Tick()
    // {
    //     TimerEvents.RaiseTimerTickEvent(_tickInterval);
    // }
    
    private async Task TickAsync()
    {
        TimerEvents.RaiseTimerTickEvent(_tickInterval);

        ScheduledAction[] snapshot;
        lock (_lock) snapshot = [.. _scheduledActions.Values];

        foreach (var action in snapshot)
        {
            action.Accumulator += _tickInterval;
            if (action.Accumulator < action.Interval) continue;

            action.Accumulator = TimeSpan.Zero;
            action.IsRunning = true;
            try
            {
                await action.Callback(_cts.Token);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Scheduled action threw an exception.");
            }
            finally
            {
                action.IsRunning = false;
            }
        }
    }
    
    public IDisposable Register(string key, TimeSpan interval, Action callback)
        => Register(key, interval, _ => { callback(); return Task.CompletedTask; });
    
    public IDisposable Register(string key, TimeSpan interval, Func<CancellationToken, Task> callback)
    {
        _logger?.LogInformation($"Registering {key} for interval: {interval}");
        var action = new ScheduledAction(key, interval, callback);
        lock (_lock) _scheduledActions[key] = action; 
        return new ActionRegistration(() => Unregister(key, action));
    }

    public void Unregister(string key)
    {
        lock (_lock) _scheduledActions.Remove(key);
    }
    
    private void Unregister(string key, ScheduledAction expected)
    {
        lock (_lock)
        {
            if (_scheduledActions.TryGetValue(key, out var current) && ReferenceEquals(current, expected))
            {
                _scheduledActions.Remove(key);
                _logger?.LogInformation($"Removed {key} timed action");
            }
        }
    }
    
    public void Dispose()
    {
        if(!_cts.IsCancellationRequested)
            _cts.Cancel();
        _periodicTimer?.Dispose();
        _cts.Dispose();
    }
    
    private sealed class ScheduledAction(string key, TimeSpan interval, Func<CancellationToken, Task> callback)
    {
        public string Key { get; } = key;
        public TimeSpan Interval { get; } = interval;
        public Func<CancellationToken, Task> Callback { get; } = callback;
        public TimeSpan Accumulator { get; set; } = TimeSpan.Zero;
        public bool IsRunning { get; set; } = false; 
    }

    private sealed class ActionRegistration(Action onDispose) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                onDispose();
        }
    }
}