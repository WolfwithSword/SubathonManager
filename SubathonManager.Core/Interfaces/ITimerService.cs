namespace SubathonManager.Core.Interfaces;

public interface ITimerService
{
    IDisposable Register(string key, TimeSpan interval, Func<CancellationToken, Task> callback);
    IDisposable Register(string key, TimeSpan interval, Action callback);
    void Unregister(string key);
}
