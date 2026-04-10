using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Interfaces;

namespace SubathonManager.Services;

[ExcludeFromCodeCoverage]
public class TelemetryService(ILogger<TelemetryService>? logger, IConfig config): IDisposable, IAppService
{
    private readonly HttpClient _http = new();
    private static readonly TimeSpan PingInterval = TimeSpan.FromHours(1);
    private DateTime LastPinged { get; set; } = DateTime.MinValue;
    private readonly string _telemetryUrl = "https://telemetry.subathonmanager.app";
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private static string? TelemetryKey => 
        "BUILD_TELEMETRY_KEY".StartsWith("BUILD") ? Environment.GetEnvironmentVariable("SM_TELEMETRY_SECRET") : "BUILD_TELEMETRY_KEY";

    private async Task TryPingAsync()
    {
        if (!config.GetBool("Telemetry", "Enabled", false)) return;
        
        if (DateTime.UtcNow - LastPinged < PingInterval) return;

        try
        {
            LastPinged = DateTime.UtcNow;
            await Task.Delay(5000); // offset a bit
            var connections = Utils.GetAllConnections().Where(x => x.Service != "Unknown")
                .Select(c => new
                {
                    source = c.Source.ToString(),
                    service = c.Service,
                    connected = c.Status
                });
            
            var payload = new
            {
                install_id = config.GetInstallId(),
                version = AppServices.AppVersion,
                connections
            };
            
            var request = new HttpRequestMessage(HttpMethod.Post, _telemetryUrl);
            request.Headers.Add("X-Telemetry-Key", TelemetryKey);
            request.Content = JsonContent.Create(payload);

            var response = await _http.SendAsync(request);
            //LastPinged = DateTime.UtcNow;
            logger?.LogDebug("Telemetry sent for install id {InstallId}. Response: {ResponseCode}", config.GetInstallId(), response.StatusCode);
        }
        catch { /**/ }
    }
    
    
    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await TryPingAsync();
            try
            {
                await Task.Delay(PingInterval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        LastPinged = DateTime.MinValue;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loopTask = RunLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        _cts?.Cancel();
        return _loopTask ?? Task.CompletedTask;
    }
}