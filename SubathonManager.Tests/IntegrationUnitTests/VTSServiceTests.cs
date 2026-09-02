using System.Buffers.Binary;
using IniParser.Model;
using Microsoft.Extensions.Logging;
using Moq;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Objects;
using SubathonManager.Core.Security;
using SubathonManager.Integration;
using SubathonManager.Tests.Utility;

namespace SubathonManager.Tests.IntegrationUnitTests;

[Collection("GlobalState")]
public class VTSServiceTests {
    private static VTSService MakeService(
        bool enabled = false,
        string? host = null,
        string? port = null,
        string? token = null,
        ITimerService? timerService = null) {
        var logger = new Mock<ILogger<VTSService>>();
        var storage = new InMemorySecureStorage(token != null
            ? new Dictionary<string, string> { [StorageKeys.VTubeStudioAuthToken] = token }
            : null);

        var config = new FakeConfig();
        config.SetBool(VTSService.ConfigSection, "Enabled", enabled);
        if (host != null) config.Set(VTSService.ConfigSection, "Host", host);
        if (port != null) config.Set(VTSService.ConfigSection, "Port", port);

        return new VTSService(logger.Object, config, storage, timerService);
    }

    private static async Task<List<IntegrationConnection>> CaptureConnectionsAsync(Func<Task> trigger) {
        var captured = new List<IntegrationConnection>();

        void Handler(IntegrationConnection connection) {
            if (connection.Source == SubathonEventSource.VTubeStudio) captured.Add(connection);
        }

        IntegrationEvents.ConnectionUpdated += Handler;
        try {
            await trigger();
        }
        finally {
            IntegrationEvents.ConnectionUpdated -= Handler;
        }

        return captured;
    }

    [Fact]
    public void NewService_IsDisconnectedWithEmptyCaches() {
        VTSService service = MakeService();

        Assert.False(service.Connected);
        Assert.Null(service.CurrentModelId);
        Assert.Null(service.CurrentModelName);
        Assert.Empty(service.CachedHotkeys);
        Assert.Empty(service.CachedExpressions);
        Assert.Empty(service.CachedParameters);
        Assert.Empty(service.HeldParameters);
    }

    [Fact]
    public void Enabled_ReflectsConfig() {
        Assert.False(MakeService().Enabled);
        Assert.True(MakeService(true).Enabled);
    }

    [Fact]
    public void GetConfig_FallsBackToVTubeStudioDefaults() {
        (string host, string port, bool enabled) = MakeService().GetConfig();

        Assert.Equal("localhost", host);
        Assert.Equal("8001", port);
        Assert.False(enabled);
    }

    [Fact]
    public void GetConfig_ReturnsStoredValues() {
        (string host, string port, bool enabled) = MakeService(true, "192.168.0.5", "9001").GetConfig();

        Assert.Equal("192.168.0.5", host);
        Assert.Equal("9001", port);
        Assert.True(enabled);
    }

    [Fact]
    public void SaveConfig_PersistsAllThreeValues() {
        VTSService service = MakeService();

        Assert.True(service.SaveConfig("10.0.0.2", "8002", true));

        (string host, string port, bool enabled) = service.GetConfig();
        Assert.Equal("10.0.0.2", host);
        Assert.Equal("8002", port);
        Assert.True(enabled);
    }

    [Fact]
    public void SaveConfig_ReportsNoChange_WhenValuesAreIdentical() {
        VTSService service = MakeService(true, "localhost", "8001");
        Assert.False(service.SaveConfig("localhost", "8001", true));
    }

    [Fact]
    public void SaveConfig_OnlyWritesToDisk_WhenForced() {
        var config = new FakeConfig();
        var service = new VTSService(null, config, new InMemorySecureStorage());

        service.SaveConfig("host-a", "1", true);
        Assert.Equal(0, config.SaveCount);

        service.SaveConfig("host-b", "2", true, true);
        Assert.Equal(1, config.SaveCount);
    }

    [Fact]
    public void ClearAuthToken_RemovesTheStoredToken() {
        var storage = new InMemorySecureStorage(new Dictionary<string, string> {
            [StorageKeys.VTubeStudioAuthToken] = "stored-token"
        });
        var service = new VTSService(null, new FakeConfig(), storage);

        Assert.True(storage.Exists(StorageKeys.VTubeStudioAuthToken));
        service.ClearAuthToken();
        Assert.False(storage.Exists(StorageKeys.VTubeStudioAuthToken));
    }

    [Fact]
    public void ClearAuthToken_IsSafeWhenNoTokenStored() {
        VTSService service = MakeService();
        service.ClearAuthToken();
        Assert.False(service.Connected);
    }

    [Fact]
    public async Task StartAsync_WhenDisabled_DoesNotConnect() {
        VTSService service = MakeService();

        List<IntegrationConnection> connections = await CaptureConnectionsAsync(() => service.StartAsync());

        Assert.False(service.Connected);
        IntegrationConnection status = Assert.Single(connections);
        Assert.False(status.Status);
        Assert.False(status.Configured);
        Assert.Equal("VTubeStudio", status.Service);
    }

    [Fact]
    public async Task StatusBroadcast_ReportsConfigured_WhenIntegrationIsEnabled() {
        VTSService service = MakeService();
        service.SaveConfig("localhost", "8001", true);

        List<IntegrationConnection> connections = await CaptureConnectionsAsync(() => service.StopAsync());

        IntegrationConnection status = Assert.Single(connections);
        Assert.True(status.Configured);
        Assert.False(status.Status);
    }

    [Fact]
    public async Task StopAsync_OnNeverStartedService_BroadcastsDisconnected() {
        VTSService service = MakeService();

        List<IntegrationConnection> connections = await CaptureConnectionsAsync(() => service.StopAsync());

        Assert.False(service.Connected);
        Assert.Contains(connections, c => !c.Status);
    }

    [Fact]
    public void Dispose_DoesNotThrow() {
        VTSService service = MakeService();
        service.Dispose();
        Assert.False(service.Connected);
    }

    [Fact]
    public async Task RefreshAsync_WhenDisconnected_ReturnsFalse() {
        Assert.False(await MakeService().RefreshAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Listings_WhenDisconnected_AreEmpty() {
        VTSService service = MakeService();

        Assert.Empty(await service.GetHotkeysAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await service.GetExpressionsAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await service.GetParametersAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await service.GetLive2DParametersAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Reads_WhenDisconnected_ReturnNull() {
        VTSService service = MakeService();

        Assert.Null(await service.GetParameterValueAsync("FaceAngleX", TestContext.Current.CancellationToken));
        Assert.Null(await service.GetExpressionStateAsync("a.exp3.json", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Writes_WhenDisconnected_ReturnFalse() {
        VTSService service = MakeService();

        Assert.False(await service.TriggerHotkeyAsync("abc", ct: TestContext.Current.CancellationToken));
        Assert.False(await service.SetExpressionStateAsync("a.exp3.json", true, TestContext.Current.CancellationToken));
        Assert.False(await service.SetParameterValueAsync("FaceAngleX", 1, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ApplyExpressionAction_DoNothing_SucceedsWithoutTouchingVTubeStudio() {
        VTSService service = MakeService();
        Assert.True(await service.ApplyExpressionActionAsync("a.exp3.json", VtsToggleAction.DoNothing, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(VtsToggleAction.On)]
    [InlineData(VtsToggleAction.Off)]
    [InlineData(VtsToggleAction.Toggle)]
    public async Task ApplyExpressionAction_RealActions_FailWhenDisconnected(VtsToggleAction action) {
        Assert.False(await MakeService().ApplyExpressionActionAsync("a.exp3.json", action, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExecuteWheelAction_WhenDisconnected_ReturnsFalse() {
        VTSService service = MakeService();
        var action = new VTSWheelAction {
            Kind = VtsTargetKind.Expression,
            Target = "cat_ears.exp3.json",
            ToggleAction = VtsToggleAction.On
        };

        Assert.False(await service.ExecuteWheelActionAsync(action, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExecuteWheelAction_WithInvalidAction_ReturnsFalse() {
        VTSService service = MakeService();
        var action = new VTSWheelAction { Kind = VtsTargetKind.Expression, Target = "" };

        Assert.False(await service.ExecuteWheelActionAsync(action, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExecuteWheelAction_WhenDisconnected_SchedulesNoRevertTimer() {
        var timer = new RecordingTimerService();
        VTSService service = MakeService(timerService: timer);

        var action = new VTSWheelAction {
            Kind = VtsTargetKind.Expression,
            Target = "cat_ears.exp3.json",
            ToggleAction = VtsToggleAction.On,
            Duration = TimeSpan.FromSeconds(30),
            AfterToggle = VtsToggleAction.Off
        };

        Assert.False(await service.ExecuteWheelActionAsync(action, TestContext.Current.CancellationToken));
        Assert.Empty(timer.Registered);
    }

    [Fact]
    public void ReleaseParameter_OnUnheldParameter_ReturnsFalse() {
        VTSService service = MakeService();

        Assert.False(service.IsParameterHeld("FaceAngleX"));
        Assert.False(service.ReleaseParameter("FaceAngleX"));
    }

    [Fact]
    public void ReleaseAllParameters_IsSafeWhenNothingHeld() {
        VTSService service = MakeService();
        service.ReleaseAllParameters();
        Assert.Empty(service.HeldParameters);
    }

    [Fact]
    public async Task FailedParameterSet_DoesNotLeaveTheParameterHeld() {
        VTSService service = MakeService();

        Assert.False(await service.SetParameterValueAsync("FaceAngleX", 1, ct: TestContext.Current.CancellationToken));
        Assert.False(service.IsParameterHeld("FaceAngleX"));
        Assert.Empty(service.HeldParameters);
    }

    [Fact]
    public void PluginIcon_IsABare128SquarePngInBase64() {
        string? icon = VTSService.PluginIconBase64;

        Assert.False(string.IsNullOrWhiteSpace(icon));
        Assert.DoesNotContain("data:", icon);

        byte[] png = Convert.FromBase64String(icon!);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png.Take(8));

        Assert.Equal(128u, BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4)));
        Assert.Equal(128u, BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4)));
    }
    
    private sealed class FakeConfig : IConfig {
        private readonly Dictionary<(string, string), string> _values = new();
        public int SaveCount { get; private set; }

        public string GetDatabasePath() {
            return "";
        }

        public void Save() {
            SaveCount++;
        }

        public void LoadOrCreateDefault() { }

        public bool MigrateConfig() {
            return false;
        }

        public KeyDataCollection? GetSection(string sectionName) {
            return null;
        }

        public string? Get(string section, string key, string? defaultValue = "") {
            return _values.TryGetValue((section, key), out string? value) ? value : defaultValue;
        }

        public bool GetBool(string section, string key, bool defaultValue = false) {
            return _values.TryGetValue((section, key), out string? value) && bool.TryParse(value, out bool parsed)
                ? parsed
                : defaultValue;
        }

        public string? GetFromEncoded(string section, string key, string? defaultValue = "") {
            return Get(section, key, defaultValue);
        }

        public bool Set(string section, string key, string value) {
            bool changed = !_values.TryGetValue((section, key), out string? existing) || existing != value;
            _values[(section, key)] = value;
            return changed;
        }

        public bool SetBool(string section, string key, bool? value) {
            return Set(section, key, (value ?? false).ToString());
        }

        public bool SetEncoded(string section, string key, string value) {
            return Set(section, key, value);
        }

        public OrderTypeModes GetOrderTypeMode(string section, string orderEnumName, OrderTypeModes modeDefault) {
            return modeDefault;
        }

        public bool SetOrderTypeMode(string section, string orderEnumName, OrderTypeModes mode) {
            return false;
        }

        public string GetInstallId() {
            return "test-install";
        }
    }

    private sealed class RecordingTimerService : ITimerService {
        public List<(string Key, TimeSpan Interval)> Registered { get; } = [];
        public List<string> Unregistered { get; } = [];

        public IDisposable Register(string key, TimeSpan interval, Func<CancellationToken, Task> callback) {
            Registered.Add((key, interval));
            return new Noop();
        }

        public IDisposable Register(string key, TimeSpan interval, Action callback) {
            Registered.Add((key, interval));
            return new Noop();
        }

        public void Unregister(string key) {
            Unregistered.Add(key);
        }

        private sealed class Noop : IDisposable {
            public void Dispose() { }
        }
    }
}
