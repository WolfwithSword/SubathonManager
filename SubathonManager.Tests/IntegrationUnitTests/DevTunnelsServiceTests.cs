using System.Reflection;
using DevTunnels.Client;
using DevTunnels.Client.Authentication;
using DevTunnels.Client.Hosting;
using DevTunnels.Client.Ports;
using DevTunnels.Client.Tunnels;
using Microsoft.Extensions.Logging;
using Moq;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Objects;
using SubathonManager.Integration;
using SubathonManager.Tests.Utility;

namespace SubathonManager.Tests.IntegrationUnitTests;

[Collection("SharedEventBusTests")]
public class DevTunnelsServiceTests
{
    public DevTunnelsServiceTests()
    {
        typeof(IntegrationEvents)
            .GetField("ConnectionUpdated", BindingFlags.Static | BindingFlags.NonPublic)
            ?.SetValue(null, null);
    }

    private static (DevTunnelsService service, Mock<IDevTunnelsClient> mockClient) MakeService(
        Dictionary<(string, string), string>? configValues = null)
    {
        var logger = new Mock<ILogger<DevTunnelsService>>();
        var mockClient = new Mock<IDevTunnelsClient>();
        IConfig config = MockConfig.MakeMockConfig(configValues ?? new Dictionary<(string, string), string>
        {
            { ("Server", "Port"), "14040" },
        });
        return (new DevTunnelsService(logger.Object, config, mockClient.Object), mockClient);
    }

    private static Mock<IDevTunnelHostSession> MakeSession(string publicUrl = "https://test.devtunnels.ms")
    {
        var mockSession = new Mock<IDevTunnelHostSession>();
        _ = mockSession.Setup(s => s.PublicUrl).Returns(new Uri(publicUrl));
        _ = mockSession.Setup(s => s.WaitForReadyAsync(It.IsAny<CancellationToken>())).Returns(ValueTask.CompletedTask);
        _ = mockSession.Setup(s => s.WaitForExitAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(async ct => await Task.Delay(Timeout.Infinite, ct));
        _ = mockSession.Setup(s => s.StopAsync(It.IsAny<CancellationToken>())).Returns(ValueTask.CompletedTask);
        _ = mockSession.As<IAsyncDisposable>().Setup(s => s.DisposeAsync()).Returns(ValueTask.CompletedTask);
        return mockSession;
    }

    private static void SetupStartTunnel(Mock<IDevTunnelsClient> mockClient, IDevTunnelHostSession session)
    {
        mockClient.Setup(c => c.CreateOrUpdateTunnelAsync(It.IsAny<string>(), It.IsAny<DevTunnelOptions>(), It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<DevTunnelStatus>(new DevTunnelStatus()));
        mockClient.Setup(c => c.CreateOrReplacePortAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<DevTunnelPortOptions>(), It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<DevTunnelPortStatus>(new DevTunnelPortStatus()));
        _ = mockClient.Setup(c => c.StartHostSessionAsync(It.IsAny<DevTunnelHostStartOptions>(), It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<IDevTunnelHostSession>(session));
    }

    [Fact]
    public async Task StartAsync_CliNotInstalled_BroadcastsCliOffline()
    {
        (DevTunnelsService? service, Mock<IDevTunnelsClient>? mockClient) = MakeService();
        _ = mockClient.Setup(c => c.ProbeCliAsync(It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<DevTunnelCliProbeResult>(
                new DevTunnelCliProbeResult(false, null, null, "not found", false, "CLI not found")));

        bool? lastTunnelStatus = null;
        void Handler(IntegrationConnection conn)
        {
            if (conn.Source == SubathonEventSource.DevTunnels && conn.Service == "Tunnel")
            {
                lastTunnelStatus = conn.Status;
            }
        }

        IntegrationEvents.ConnectionUpdated += Handler;
        try
        {
            await service.StartAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            IntegrationEvents.ConnectionUpdated -= Handler;
        }

        Assert.False(service.IsCliInstalled);
        Assert.False(service.IsLoggedIn);
        Assert.False(service.IsTunnelRunning);
        Assert.False(lastTunnelStatus);
        mockClient.Verify(c => c.GetLoginStatusAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StartAsync_CliInstalledButNotLoggedIn_DoesNotStartTunnel()
    {
        (DevTunnelsService? service, Mock<IDevTunnelsClient>? mockClient) = MakeService();
        _ = mockClient.Setup(c => c.ProbeCliAsync(It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<DevTunnelCliProbeResult>(
                new DevTunnelCliProbeResult(true, "devtunnel", new Version(1, 0), "v1.0", true, null)));
        _ = mockClient.Setup(c => c.GetLoginStatusAsync(It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<DevTunnelLoginStatus>(new DevTunnelLoginStatus { Status = "Logged out" }));

        await service.StartAsync(TestContext.Current.CancellationToken);

        Assert.True(service.IsCliInstalled);
        Assert.False(service.IsLoggedIn);
        Assert.False(service.IsTunnelRunning);
        mockClient.Verify(c => c.StartHostSessionAsync(It.IsAny<DevTunnelHostStartOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StartAsync_CliInstalledAndLoggedIn_DoesNotAutoStartTunnel()
    {
        (DevTunnelsService? service, Mock<IDevTunnelsClient>? mockClient) = MakeService();
        _ = mockClient.Setup(c => c.ProbeCliAsync(It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<DevTunnelCliProbeResult>(
                new DevTunnelCliProbeResult(true, "devtunnel", new Version(1, 0), "v1.0", true, null)));
        _ = mockClient.Setup(c => c.GetLoginStatusAsync(It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<DevTunnelLoginStatus>(new DevTunnelLoginStatus { Status = "Logged in", Username = "user@example.com" }));

        await service.StartAsync(TestContext.Current.CancellationToken);

        Assert.True(service.IsCliInstalled);
        Assert.True(service.IsLoggedIn);
        Assert.False(service.IsTunnelRunning);
        mockClient.Verify(c => c.StartHostSessionAsync(It.IsAny<DevTunnelHostStartOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StartTunnelAsync_StartsSessionAndBroadcastsPublicUrl()
    {
        (DevTunnelsService? service, Mock<IDevTunnelsClient>? mockClient) = MakeService(new Dictionary<(string, string), string>
        {
            { ("Server", "Port"), "14040" },
            { ("DevTunnels", "TunnelId"), "my-tunnel" },
        });

        Mock<IDevTunnelHostSession> mockSession = MakeSession("https://my-tunnel.devtunnels.ms");
        SetupStartTunnel(mockClient, mockSession.Object);

        string? broadcastedUrl = null;
        void Handler(IntegrationConnection conn)
        {
            if (conn.Source == SubathonEventSource.DevTunnels && conn.Service == "Tunnel" && conn.Status)
            {
                broadcastedUrl = conn.Name;
            }
        }

        IntegrationEvents.ConnectionUpdated += Handler;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await service.StartTunnelAsync(cts.Token);
        }
        finally
        {
            IntegrationEvents.ConnectionUpdated -= Handler;
        }

        Assert.True(service.IsTunnelRunning);
        Assert.Equal("https://my-tunnel.devtunnels.ms", service.PublicBaseUrl);
        Assert.Equal("https://my-tunnel.devtunnels.ms", broadcastedUrl);

        await service.StopTunnelAsync();
    }

    [Fact]
    public async Task StartTunnelAsync_WhenAlreadyRunning_IsIdempotent()
    {
        (DevTunnelsService? service, Mock<IDevTunnelsClient>? mockClient) = MakeService();
        Mock<IDevTunnelHostSession> mockSession = MakeSession();
        SetupStartTunnel(mockClient, mockSession.Object);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await service.StartTunnelAsync(cts.Token);
        await service.StartTunnelAsync(cts.Token);

        mockClient.Verify(c => c.StartHostSessionAsync(It.IsAny<DevTunnelHostStartOptions>(), It.IsAny<CancellationToken>()), Times.Once);

        await service.StopTunnelAsync();
    }

    [Fact]
    public async Task StartTunnelAsync_InvalidStoredTunnelId_ResetsToGeneratedId()
    {
        (DevTunnelsService? service, Mock<IDevTunnelsClient>? mockClient) = MakeService(new Dictionary<(string, string), string>
        {
            { ("Server", "Port"), "14040" },
            { ("DevTunnels", "TunnelId"), "bad.cluster.qualified.id" },
        });

        Mock<IDevTunnelHostSession> mockSession = MakeSession();
        SetupStartTunnel(mockClient, mockSession.Object);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await service.StartTunnelAsync(cts.Token);

        mockClient.Verify(c => c.CreateOrUpdateTunnelAsync(
            It.Is<string>(id => id.StartsWith("subathon-")),
            It.IsAny<DevTunnelOptions>(), It.IsAny<CancellationToken>()), Times.Once);

        await service.StopTunnelAsync();
    }

    [Fact]
    public async Task StopTunnelAsync_WithRunningSession_StopsSessionAndClearsState()
    {
        (DevTunnelsService? service, Mock<IDevTunnelsClient>? mockClient) = MakeService();
        Mock<IDevTunnelHostSession> mockSession = MakeSession();
        SetupStartTunnel(mockClient, mockSession.Object);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await service.StartTunnelAsync(cts.Token);
        Assert.True(service.IsTunnelRunning);

        await service.StopTunnelAsync();

        Assert.False(service.IsTunnelRunning);
        Assert.Null(service.PublicBaseUrl);
        mockSession.Verify(s => s.StopAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StopAsync_ResetsStateAndBroadcastsDisabled()
    {
        (DevTunnelsService? service, Mock<IDevTunnelsClient> _) = MakeService();

        bool? lastTunnelStatus = null;
        void Handler(IntegrationConnection conn)
        {
            if (conn.Source == SubathonEventSource.DevTunnels && conn.Service == "Tunnel")
            {
                lastTunnelStatus = conn.Status;
            }
        }

        IntegrationEvents.ConnectionUpdated += Handler;
        try
        {
            await service.StopAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            IntegrationEvents.ConnectionUpdated -= Handler;
        }

        Assert.False(service.IsCliInstalled);
        Assert.False(service.IsLoggedIn);
        Assert.False(service.IsTunnelRunning);
        Assert.False(lastTunnelStatus);
    }
}
