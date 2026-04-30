using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Interfaces;

namespace SubathonManager.Services;

[ExcludeFromCodeCoverage]
public class TelemetryService(ILogger<TelemetryService>? logger, IConfig config, TimerService? timerService = null): IDisposable, IAppService
{
    private readonly HttpClient _http = new();
    private readonly string _telemetryUrl = "https://telemetry.subathonmanager.app";
    private static string? TelemetryKey => 
        "BUILD_TELEMETRY_KEY".StartsWith("BUILD") ? Environment.GetEnvironmentVariable("SM_TELEMETRY_SECRET") : "BUILD_TELEMETRY_KEY";

    private async Task TryPingAsync(CancellationToken ct = default)
    {
        if (!config.GetBool("Telemetry", "Enabled", false)) return;
        
        try
        {
            await Task.Delay(5000, ct); // offset a bit
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

            var response = await _http.SendAsync(request, ct);
            logger?.LogDebug("Telemetry sent for install id {InstallId}. Response: {ResponseCode}", config.GetInstallId(), response.StatusCode);
        }
        catch { /**/ }
    }

    public void Dispose()
    {
        timerService?.Unregister("telemetry-ping");
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        timerService?.Unregister("telemetry-ping");
        timerService?.Register("telemetry-ping", TimeSpan.FromHours(1), TryPingAsync);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        timerService?.Unregister("telemetry-ping");
        return Task.CompletedTask;
    }
}