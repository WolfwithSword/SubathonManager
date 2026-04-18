using System.Diagnostics.CodeAnalysis;
using DevTunnels.Client;
using DevTunnels.Client.Authentication;
using DevTunnels.Client.Hosting;
using DevTunnels.Client.Ports;
using DevTunnels.Client.Tunnels;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Objects;

namespace SubathonManager.Integration;

public class DevTunnelsService(
    ILogger<DevTunnelsService>? logger,
    IConfig config,
    IDevTunnelsClient client) : IDisposable, IAppService
{
    private readonly string _configSection = "DevTunnels";
    private bool _disposed = false;

    private IDevTunnelHostSession? _session;
    private CancellationTokenSource? _cts;

    // Serialises concurrent StartTunnelAsync calls so at most one session-start attempt
    // runs at a time. Without this, two webhook integrations firing StartTunnelAsync
    // concurrently could both pass the _session != null guard and launch two CLI processes.
    private readonly SemaphoreSlim _startLock = new(1, 1);

    // Publicly readable so the UI can show the live URL without subscribing to events.
    public string? PublicBaseUrl { get; private set; }
    public bool IsCliInstalled { get; private set; }
    public bool IsLoggedIn { get; private set; }
    public bool IsTunnelRunning { get; private set; }

    public async Task StartAsync(CancellationToken ct = default)
    {
        // 1. Probe CLI
        var probe = await client.ProbeCliAsync(ct);
        IsCliInstalled = probe.IsInstalled;

        BroadcastCliStatus(probe);

        if (!probe.IsInstalled)
        {
            logger?.LogInformation("[DevTunnels] CLI not installed, skipping auto-start");
            BroadcastLoginStatus(null);
            BroadcastTunnelStatus(false, null);
            return;
        }

        // 2. Check login
        var login = await client.GetLoginStatusAsync(ct);
        IsLoggedIn = login.IsLoggedIn;

        BroadcastLoginStatus(login);

        if (!login.IsLoggedIn)
        {
            logger?.LogInformation("[DevTunnels] Not logged in; tunnel will start on demand when a webhook integration is enabled");
            BroadcastTunnelStatus(false, null);
            return;
        }

        // Tunnel is intentionally not started here. Webhook integrations (e.g. KoFiService)
        // call StartTunnelAsync on demand when they determine they have a configured token.
        // This avoids opening a public tunnel when no integration actually requires it.
        logger?.LogInformation("[DevTunnels] CLI ready and logged in; tunnel will start on demand");
        BroadcastTunnelStatus(false, null);
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        await StopTunnelAsync();

        IsCliInstalled = false;
        IsLoggedIn = false;

        BroadcastCliStatus(null);
        BroadcastLoginStatus(null);
        BroadcastTunnelStatus(false, null);
    }

    // Interactive actions called from the UI

    public async Task<DevTunnelCliProbeResult> RefreshCliStatusAsync(CancellationToken ct = default)
    {
        var probe = await client.ProbeCliAsync(ct);
        IsCliInstalled = probe.IsInstalled;
        BroadcastCliStatus(probe);
        return probe;
    }

    public async Task<DevTunnelLoginStatus> LoginAsync(LoginProvider provider, CancellationToken ct = default)
    {
        var status = await client.LoginAsync(provider, ct);
        IsLoggedIn = status.IsLoggedIn;
        BroadcastLoginStatus(status);
        return status;
    }

    public async Task<DevTunnelLoginStatus> LogoutAsync(CancellationToken ct = default)
    {
        await StopTunnelAsync();
        await client.LogoutAsync(ct);
        IsLoggedIn = false;
        BroadcastLoginStatus(null);
        BroadcastTunnelStatus(false, null);
        return new DevTunnelLoginStatus { Status = "Logged out" };
    }

    public async Task StartTunnelAsync(CancellationToken ct = default)
    {
        // Fast path: no lock needed if a session is already running.
        if (_session != null) return;

        await _startLock.WaitAsync(ct);
        try
        {
            // Re-check under the lock in case another caller just finished starting.
            if (_session != null) return;

            BroadcastTunnelStatus(false, null, starting: true);

            var serverPort = int.TryParse(config.Get("Server", "Port", "14040"), out var p) ? p : 14040;
            var tunnelId = config.Get(_configSection, "TunnelId", string.Empty);

            // The Azure DevTunnels CLI internally assigns a random tunnel ID that may not match
            // the short-name format required by StartHostSessionAsync validation. We always use
            // the label (name) we chose as the persistent ID; the CLI accepts it as a host arg.
            // Also clear any cluster-qualified IDs persisted by older code (e.g. "name.abc.usw2").
            if (!string.IsNullOrWhiteSpace(tunnelId) && !DevTunnelValidation.IsValidTunnelId(tunnelId))
            {
                logger?.LogInformation("[DevTunnels] Stored tunnel ID is not in short-name format; resetting");
                tunnelId = string.Empty;
                config.Set(_configSection, "TunnelId", string.Empty);
                config.Save();
            }

            if (string.IsNullOrWhiteSpace(tunnelId))
            {
                tunnelId = $"subathon-{serverPort}";
                config.Set(_configSection, "TunnelId", tunnelId);
                config.Save();
            }

            // Always ensure the tunnel and port exist before hosting (idempotent operations).
            // The port must be pre-registered on the tunnel; passing PortNumber to StartHostSessionAsync
            // is for ephemeral tunnels only and causes a service-side error on named persistent tunnels.
            logger?.LogInformation("[DevTunnels] Ensuring tunnel '{Id}' and port {Port} exist", tunnelId, serverPort);
            await client.CreateOrUpdateTunnelAsync(
                tunnelId,
                new DevTunnelOptions { AllowAnonymous = true, Description = "SubathonManager webhook tunnel" },
                ct);
            await client.CreateOrReplacePortAsync(
                tunnelId,
                serverPort,
                new DevTunnelPortOptions { Protocol = "http", AllowAnonymous = true },
                ct);

            logger?.LogInformation("[DevTunnels] Starting host session for tunnel '{Id}'", tunnelId);

            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _session = await client.StartHostSessionAsync(
                new DevTunnelHostStartOptions { TunnelId = tunnelId, ReadyTimeout = TimeSpan.FromSeconds(30) },
                _cts.Token);

            await _session.WaitForReadyAsync(_cts.Token);

            PublicBaseUrl = _session.PublicUrl?.ToString()?.TrimEnd('/');
            IsTunnelRunning = true;

            logger?.LogInformation("[DevTunnels] Tunnel running at {Url}", PublicBaseUrl);
            BroadcastTunnelStatus(true, PublicBaseUrl);

            // Monitor for unexpected exit in the background
            _ = MonitorSessionAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "[DevTunnels] Failed to start tunnel");
            await StopTunnelAsync();
            BroadcastTunnelStatus(false, null);
        }
        finally
        {
            _startLock.Release();
        }
    }

    public async Task StopTunnelAsync()
    {
        if (_session == null) return;

        BroadcastTunnelStatus(false, null, stopping: true);

        PublicBaseUrl = null;
        IsTunnelRunning = false;

        try { await _session.StopAsync(); } catch { /**/ }
        await _session.DisposeAsync();
        _session = null;

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        BroadcastTunnelStatus(false, null);
    }

    // Helpers

    private async Task MonitorSessionAsync(CancellationToken ct)
    {
        try
        {
            if (_session == null) return;
            await _session.WaitForExitAsync(ct);

            if (!ct.IsCancellationRequested && IsTunnelRunning)
            {
                logger?.LogWarning("[DevTunnels] Host session exited unexpectedly: {Reason}", _session.FailureReason);
                await StopTunnelAsync();
                BroadcastTunnelStatus(false, null);
            }
        }
        catch (OperationCanceledException) { /**/ }
    }

    private void BroadcastCliStatus(DevTunnelCliProbeResult? probe)
    {
        IntegrationEvents.RaiseConnectionUpdate(new IntegrationConnection
        {
            Source = SubathonEventSource.DevTunnels,
            Service = "Cli",
            Name = probe?.IsInstalled == true ? probe.Version?.ToString() ?? "" : "",
            Status = probe?.IsInstalled ?? false
        });
    }

    private void BroadcastLoginStatus(DevTunnelLoginStatus? login)
    {
        IntegrationEvents.RaiseConnectionUpdate(new IntegrationConnection
        {
            Source = SubathonEventSource.DevTunnels,
            Service = "Login",
            Name = login?.Username ?? "",
            Status = login?.IsLoggedIn ?? false
        });
    }

    private void BroadcastTunnelStatus(bool running, string? url, bool starting = false, bool stopping = false)
    {
        IsTunnelRunning = running;
        IntegrationEvents.RaiseConnectionUpdate(new IntegrationConnection
        {
            Source = SubathonEventSource.DevTunnels,
            Service = "Tunnel",
            Name = starting ? "(starting…)" : stopping ? "(stopping…)" : (url ?? ""),
            Status = running
        });
    }

    [ExcludeFromCodeCoverage]
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    [ExcludeFromCodeCoverage]
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _startLock.Dispose();
        _disposed = true;
    }
}
