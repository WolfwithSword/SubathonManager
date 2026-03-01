namespace SubathonManager.Core.Interfaces;

public interface IAppService
{
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
}