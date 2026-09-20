using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Net.WebSockets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Models;
using SubathonManager.Core.Objects;
using SubathonManager.Core.Security;
using SubathonManager.Core.Security.Interfaces;
using SubathonManager.Data;
using VTubeStudio.Client;
using VTubeStudio.Client.Errors;
using VTubeStudio.Client.Events;
using VTubeStudio.Client.Messages;

namespace SubathonManager.Integration;

[ExcludeFromCodeCoverage]
public sealed record VtsHotkey(string Id, string Name, string Type, string File, string Description) {
    public bool IsExpressionToggle => string.Equals(Type, "ToggleExpression", StringComparison.OrdinalIgnoreCase);
}

[ExcludeFromCodeCoverage]
public sealed record VtsExpression(string File, string Name, bool Active);

[ExcludeFromCodeCoverage]
public sealed record VtsParameter(
    string Name,
    string AddedBy, // may be way to determine?
    double Value,
    double Min,
    double Max,
    double DefaultValue,
    bool IsCustom);

public class VTSService(
    ILogger<VTSService>? logger,
    IConfig config,
    ISecureStorage secureStorage,
    ITimerService? timerService = null,
    IDbContextFactory<AppDbContext>? dbFactory = null)
    : IAppService, IDisposable {
    public const string ConfigSection = "VTubeStudio";
    private const string PluginName = "Subathon Manager";
    private const string PluginDeveloper = "WolfwithSword";
    private const string ServiceName = "VTubeStudio";

    /////////////////
    private const string ParamCurrentPoints = "SMCurrentPoints";
    private const string ParamCurrentMoney = "SMCurrentMoney";
    private const string ParamCurrentGoalValue = "SMCurrentGoalValue";
    private const string ParamUntilNextGoal = "SMUntilNextGoal";
    private const string ParamGoalProgress = "SMGoalProgress";
    private const string ParamMultiplierActive = "SMMultiplierActive";
    private const string ParamWheelSpinActive = "SMWheelSpinActive";
    private const string ParamPromptActive = "SMPromptActive";
    private const string ParamTimerPaused = "SMTimerPaused";
    private const string ParamTimerLocked = "SMTimerLocked";

    private const string PluginIconResource = "SubathonManager.Integration.assets.icon_128.png";
    private const double ParamMaxValue = 1000000;

    private static readonly Lazy<string?> LazyPluginIcon = new(LoadPluginIcon, LazyThreadSafetyMode.PublicationOnly);

    private readonly ConcurrentDictionary<string, ParameterValue> _customParameters = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, ParameterValue> _heldParameters = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, double> _lastPublished = new(StringComparer.Ordinal);
    private readonly ILogger? _logger = logger;

    private readonly Utils.ServiceReconnectState _reconnectState =
        new(TimeSpan.FromSeconds(3), 1000, TimeSpan.FromSeconds(30), true);

    private readonly List<IDisposable> _subscriptions = [];

    private VTubeStudioClient? _client;
    private CancellationTokenSource? _holdCts;
    private CancellationTokenSource? _publishCts;
    private int _spinGeneration;
    private volatile bool _stopRequested;

    [ExcludeFromCodeCoverage]
    private List<ParameterCreationRequest> CustomParameterRequests => [
        new() {
            ParameterName = ParamCurrentPoints,
            Explanation = "Subathon Manager: current points",
            Min = 0,
            Max = ParamMaxValue,
            DefaultValue = 0
        },
        new() {
            ParameterName = ParamCurrentMoney,
            Explanation = "Subathon Manager: current money total",
            Min = 0,
            Max = ParamMaxValue,
            DefaultValue = 0
        },
        new() {
            ParameterName = ParamCurrentGoalValue,
            Explanation = "Subathon Manager: value of the goal in progress",
            Min = 0,
            Max = ParamMaxValue,
            DefaultValue = 0
        },
        new() {
            ParameterName = ParamUntilNextGoal,
            Explanation = "Subathon Manager: amount still needed for the next goal",
            Min = 0,
            Max = ParamMaxValue,
            DefaultValue = 0
        },
        new() {
            ParameterName = ParamGoalProgress,
            Explanation = "Subathon Manager: percent of the way to the next goal (0-100)",
            Min = 0,
            Max = 100,
            DefaultValue = 0
        },
        Flag(ParamMultiplierActive, "a multiplier is active"),
        Flag(ParamWheelSpinActive, "a wheel spin is in progress"),
        Flag(ParamPromptActive, "a prompt is running"),
        Flag(ParamTimerPaused, "the timer is paused"),
        Flag(ParamTimerLocked, "the timer is locked")
    ];

    public bool Connected { get; private set; }

    public string? CurrentModelId { get; private set; }
    public string? CurrentModelName { get; private set; }

    public IReadOnlyList<VtsHotkey> CachedHotkeys { get; private set; } = [];
    public IReadOnlyList<VtsExpression> CachedExpressions { get; private set; } = [];
    public IReadOnlyList<VtsParameter> CachedParameters { get; private set; } = [];

    public bool Enabled => config.GetBool(ConfigSection, "Enabled");
    private string Host => (config.Get(ConfigSection, "Host", "localhost") ?? "localhost").Trim();
    private string Port => (config.Get(ConfigSection, "Port", "8001") ?? "8001").Trim();

    private int PublishIntervalMs =>
        int.TryParse(config.Get(ConfigSection, "PublishIntervalMs", "500"), out int ms) && ms is >= 100 and <= 900
            ? ms
            : 500;

    private int InjectIntervalMs =>
        int.TryParse(config.Get(ConfigSection, "InjectIntervalMs", "100"), out int ms) && ms >= 20 ? ms : 100;

    public IReadOnlyCollection<string> HeldParameters => _heldParameters.Keys.ToList();

    internal static string? PluginIconBase64 => LazyPluginIcon.Value;

    public Task StartAsync(CancellationToken ct = default) {
        _stopRequested = false;
        BroadcastStatus(false);

        if (!Enabled) {
            _logger?.LogInformation("[VTSService] Disabled. Integration inactive");
            return Task.CompletedTask;
        }

        _ = Task.Run(() => ConnectAsync(ct), ct);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct = default) {
        _stopRequested = true;
        _reconnectState.Cts?.Cancel();
        await TeardownClientAsync();
        BroadcastStatus(false);
    }

    public void Dispose() {
        _stopRequested = true;
        UnhookParameters();
        StopHoldLoop();
        StopPublishLoop();
        _reconnectState.Dispose();
        GC.SuppressFinalize(this);
    }

    [ExcludeFromCodeCoverage]
    private static ParameterCreationRequest Flag(string name, string what) {
        return new ParameterCreationRequest {
            ParameterName = name,
            Explanation = $"Subathon Manager: 1 if {what}, otherwise 0",
            Min = 0,
            Max = 1,
            DefaultValue = 0
        };
    }

    private static string? LoadPluginIcon() {
        try {
            using Stream? stream = typeof(VTSService).Assembly.GetManifestResourceStream(PluginIconResource);
            if (stream == null) return null;

            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.Length > 0 ? Convert.ToBase64String(buffer.ToArray()) : null;
        }
        catch {
            return null;
        }
    }

    public event Action? ModelDataChanged;

    [ExcludeFromCodeCoverage]
    public async Task RestartAsync(CancellationToken ct = default) {
        await StopAsync(ct);
        await StartAsync(ct);
    }

    public bool SaveConfig(string host, string port, bool enabled, bool forceSave = false) {
        var hasUpdated = false;
        hasUpdated |= config.Set(ConfigSection, "Host", host);
        hasUpdated |= config.Set(ConfigSection, "Port", port);
        hasUpdated |= config.SetBool(ConfigSection, "Enabled", enabled);
        if (hasUpdated && forceSave)
            config.Save();
        return hasUpdated;
    }

    public (string host, string port, bool enabled) GetConfig() {
        return (Host, Port, Enabled);
    }

    public void ClearAuthToken() {
        secureStorage.Delete(StorageKeys.VTubeStudioAuthToken);
    }

    [ExcludeFromCodeCoverage]
    private async Task ConnectAsync(CancellationToken ct) {
        if (_stopRequested || !Enabled) return;

        await TeardownClientAsync();

        VTubeStudioClient? client = null;
        try {
            var options = new VTubeStudioClientOptions {
                Endpoint = new Uri($"ws://{Host}:{Port}"),
                PluginName = PluginName,
                PluginDeveloper = PluginDeveloper,
                PluginIcon = PluginIconBase64
            };

            client = new VTubeStudioClient(options);
            client.Disconnected += OnDisconnected;
            if (_logger != null && _logger.IsEnabled(LogLevel.Debug))
                client.EventReceived += OnRawEventReceived;
            await client.ConnectAsync(ct);

            string? existingToken = secureStorage.GetOrDefault(StorageKeys.VTubeStudioAuthToken, string.Empty);
            if (string.IsNullOrWhiteSpace(existingToken)) {
                existingToken = null;
                _logger?.LogInformation(
                    "[VTSService] No stored auth token. Approve the plugin in the VTubeStudio window");
            }

            string token = await client.RequestAndAuthenticateAsync(existingToken, ct);
            if (!string.IsNullOrWhiteSpace(token) && !string.Equals(token, existingToken, StringComparison.Ordinal))
                secureStorage.Set(StorageKeys.VTubeStudioAuthToken, token);

            _client = client;
            client = null;
            Connected = true;
            _reconnectState.Reset();
            _reconnectState.Cts?.Cancel();

            _logger?.LogInformation("[VTSService] Connected and authenticated");
            BroadcastStatus(true);

            await SubscribeToModelEventsAsync(CancellationToken.None);
            StartHoldLoop();
            StartPublishLoop();
            await RefreshAsync(CancellationToken.None);

            await SetupParameters(CancellationToken.None);
            HookParameters();
            await RefreshPublishedValuesAsync(CancellationToken.None);
        }
        catch (OperationCanceledException) {
            await DiscardAsync(client);
        }
        catch (VTubeStudioApiException ex) {
            await DiscardAsync(client);

            if (ex.ErrorId is VTubeStudioErrorId.TokenRequestDenied
                or VTubeStudioErrorId.AuthenticationTokenMissing) {
                ClearAuthToken();
                _logger?.LogWarning("[VTSService] Authentication rejected ({ErrorId}). Stored token cleared",
                    ex.ErrorId);
            }
            else if (_reconnectState.Retries < 2) {
                _logger?.LogWarning("[VTSService] API error during connect: {ErrorId} - {Message}",
                    ex.ErrorId, ex.ApiMessage);
            }

            _ = Task.Run(ReconnectWithBackoffAsync, CancellationToken.None);
        }
        catch (Exception ex) {
            await DiscardAsync(client);
            LogConnectFailure(ex);
            _ = Task.Run(ReconnectWithBackoffAsync, CancellationToken.None);
        }
    }

    [ExcludeFromCodeCoverage]
    private async Task SetupParameters(CancellationToken ct = default) {
        if (_client == null) return;

        foreach (ParameterCreationRequest customParameter in CustomParameterRequests)
            try {
                ParameterCreationResponse res = await _client.CreateParameterAsync(customParameter, ct);
                if (string.IsNullOrWhiteSpace(res.ParameterName)) {
                    _logger?.LogWarning("[VTSService] VTube Studio refused to create {Parameter}",
                        customParameter.ParameterName);
                    continue;
                }

                SeedCustomParameter(res.ParameterName);
                _logger?.LogDebug("[VTSService] Custom parameter ready: {Parameter}", res.ParameterName);
            }
            catch (Exception ex) {
                _logger?.LogWarning("[VTSService] Could not create custom parameter {Parameter}: {Reason}",
                    customParameter.ParameterName, ParseException(ex));
            }

        _logger?.LogInformation("[VTSService] {Count} custom parameter(s) available", _customParameters.Count);
    }

    [ExcludeFromCodeCoverage]
    private void HookParameters() {
        SubathonEvents.SubathonDataUpdate += OnSubathonDataUpdate;
        SubathonEvents.SubathonGoalListUpdated += OnGoalListUpdated;
        SubathonEvents.SubathonGoalCompleted += OnGoalCompleted;
        SubathonEvents.PromptRunStarted += OnPromptRunChanged;
        SubathonEvents.PromptRunUpdate += OnPromptRunChanged;
        WheelEvents.WheelSpinStarted += OnWheelSpinStarted;
        WheelEvents.WheelSpinResult += OnWheelSpinResult;
    }

    private void UnhookParameters() {
        SubathonEvents.SubathonDataUpdate -= OnSubathonDataUpdate;
        SubathonEvents.SubathonGoalListUpdated -= OnGoalListUpdated;
        SubathonEvents.SubathonGoalCompleted -= OnGoalCompleted;
        SubathonEvents.PromptRunStarted -= OnPromptRunChanged;
        SubathonEvents.PromptRunUpdate -= OnPromptRunChanged;
        WheelEvents.WheelSpinStarted -= OnWheelSpinStarted;
        WheelEvents.WheelSpinResult -= OnWheelSpinResult;
    }

    [ExcludeFromCodeCoverage]
    private void OnRawEventReceived(object? sender, VTubeStudioEventArgs e) {
        _logger?.LogDebug("[VTSService] Event received from VTube Studio: {EventName}", e.EventName);
    }

    [ExcludeFromCodeCoverage]
    private async Task DiscardAsync(VTubeStudioClient? client) {
        if (client == null) return;
        UnhookParameters();
        client.Disconnected -= OnDisconnected;
        if (_logger != null && _logger.IsEnabled(LogLevel.Debug))
            client.EventReceived -= OnRawEventReceived;
        await SafeDisposeAsync(client);
    }

    [ExcludeFromCodeCoverage]
    private void LogConnectFailure(Exception ex) {
        int retries = _reconnectState.Retries;

        if (IsExpectedConnectFailure(ex)) {
            if (retries < 2)
                _logger?.LogWarning(
                    "[VTSService] Cannot reach VTube Studio at {Host}:{Port} ({Reason}). Is it running with the " +
                    "Plugin API started? Retrying in the background", Host, Port, ParseException(ex));
            else if (retries % 10 == 0)
                _logger?.LogTrace("[VTSService] Still cannot reach VTube Studio at {Host}:{Port} (attempt {N})",
                    Host, Port, retries);
            return;
        }

        if (retries < 2)
            _logger?.LogWarning(ex, "[VTSService] Connection attempt failed");
        else if (retries % 10 == 0)
            _logger?.LogDebug("[VTSService] Connection attempt failed (attempt {N}): {Reason}",
                retries, ParseException(ex));
    }

    [ExcludeFromCodeCoverage]
    private static bool IsExpectedConnectFailure(Exception ex) {
        for (Exception? e = ex; e != null; e = e.InnerException)
            if (e is WebSocketException or SocketException or IOException or HttpRequestException
                or TimeoutException or OperationCanceledException)
                return true;

        return false;
    }

    [ExcludeFromCodeCoverage]
    private static string ParseException(Exception ex) {
        Exception root = ex;
        while (root.InnerException != null) root = root.InnerException;
        return root is SocketException socket ? socket.SocketErrorCode.ToString() : root.Message;
    }

    [ExcludeFromCodeCoverage]
    private void OnDisconnected(object? sender, EventArgs e) {
        if (!Connected) return;
        Connected = false;
        StopHoldLoop();
        StopPublishLoop();
        BroadcastStatus(false);

        if (_stopRequested) return;
        if (_reconnectState.Retries < 2)
            _logger?.LogWarning("[VTSService] Disconnected from VTube Studio. Retrying in the background");
        _ = Task.Run(ReconnectWithBackoffAsync, CancellationToken.None);
    }

    [ExcludeFromCodeCoverage]
    private async Task ReconnectWithBackoffAsync() {
        if (!await _reconnectState.Lock.WaitAsync(0)) return;

        try {
            _reconnectState.Cts?.Cancel();
            _reconnectState.Cts = new CancellationTokenSource();
            CancellationToken token = _reconnectState.Cts.Token;

            while (!token.IsCancellationRequested && !Connected && !_stopRequested && Enabled) {
                if (!_reconnectState.InfiniteRetries && _reconnectState.Retries >= _reconnectState.MaxRetries) {
                    _logger?.LogError("[VTSService] Max reconnect retries reached");
                    return;
                }

                _reconnectState.Retries++;
                TimeSpan delay = _reconnectState.Backoff;

                if (_reconnectState.Retries < 3 || _reconnectState.Retries % 60 == 0)
                    _logger?.LogDebug("[VTSService] Reconnect attempt {N} in {Delay}s",
                        _reconnectState.Retries, delay.TotalSeconds);

                try {
                    await Task.Delay(delay, token);
                    if (!Connected && !_stopRequested)
                        await ConnectAsync(token);
                }
                catch (OperationCanceledException) {
                    return;
                }
                catch (Exception ex) {
                    if (_reconnectState.Retries < 2)
                        _logger?.LogWarning(ex, "[VTSService] Reconnect error");
                    else
                        _logger?.LogDebug("[VTSService] Reconnect error: {Reason}", ParseException(ex));
                }

                _reconnectState.Backoff = TimeSpan.FromMilliseconds(
                    Math.Min(
                        _reconnectState.Backoff.TotalMilliseconds * 2,
                        _reconnectState.MaxBackoff.TotalMilliseconds));
            }
        }
        finally {
            _reconnectState.Lock.Release();
        }
    }

    private async Task TeardownClientAsync() {
        StopHoldLoop();
        StopPublishLoop();

        foreach (IDisposable sub in _subscriptions)
            try {
                sub.Dispose();
            }
            catch {
                /**/
            }

        _subscriptions.Clear();
        _heldParameters.Clear();

        VTubeStudioClient? client = Interlocked.Exchange(ref _client, null);
        Connected = false;
        CurrentModelId = null;
        CurrentModelName = null;
        CachedHotkeys = [];
        CachedExpressions = [];
        CachedParameters = [];

        if (client == null) return;
        UnhookParameters();
        client.Disconnected -= OnDisconnected;
        if (_logger != null && _logger.IsEnabled(LogLevel.Debug))
            client.EventReceived -= OnRawEventReceived;
        await SafeDisposeAsync(client);
    }

    [ExcludeFromCodeCoverage]
    private async Task SafeDisposeAsync(VTubeStudioClient client) {
        try {
            await client.DisposeAsync();
        }
        catch (Exception ex) {
            _logger?.LogDebug("[VTSService] Error disposing client: {Reason}", ParseException(ex));
        }
    }

    [ExcludeFromCodeCoverage]
    private async Task SubscribeToModelEventsAsync(CancellationToken ct) {
        VTubeStudioClient? client = _client;
        if (client == null) return;

        try {
            _subscriptions.Add(client.Events.On<ModelLoadedEventPayload>(OnModelLoaded));
            _subscriptions.Add(client.Events.On<ModelConfigChangedEventPayload>(OnModelConfigChanged));

            await client.SubscribeAsync<ModelLoadedEventPayload>(ct: ct);

            EventSubscriptionResponse confirmed =
                await client.SubscribeAsync<ModelConfigChangedEventPayload>(ct: ct);

            _logger?.LogInformation("[VTSService] Subscribed to model events. VTubeStudio confirms {Count}: {Events}",
                confirmed.SubscribedEventCount, string.Join(", ", confirmed.SubscribedEvents));
        }
        catch (Exception ex) when (IsExpectedConnectFailure(ex)) {
            _logger?.LogDebug("[VTSService] Could not subscribe to model events: {Reason}", ParseException(ex));
        }
        catch (Exception ex) {
            _logger?.LogWarning(ex, "[VTSService] Failed to subscribe to model events");
        }
    }

    [ExcludeFromCodeCoverage]
    private void OnModelLoaded(ModelLoadedEventPayload payload) {
        _logger?.LogInformation("[VTSService] Model loaded event: {Model} (loaded: {Loaded})",
            payload.ModelName ?? "none", payload.ModelLoaded);

        _ = Task.Run(() => ApplyModelChangeAsync(
            payload.ModelLoaded ? payload.ModelId : null,
            payload.ModelLoaded ? payload.ModelName : null,
            CancellationToken.None));
    }

    [ExcludeFromCodeCoverage]
    private void OnModelConfigChanged(ModelConfigChangedEventPayload payload) {
        _logger?.LogInformation("[VTSService] Model config changed event: {Model}", payload.ModelName ?? "none");
        _ = Task.Run(() => RefreshAsync());
    }

    private void BroadcastStatus(bool status) {
        IntegrationEvents.RaiseConnectionUpdate(new IntegrationConnection {
            Source = SubathonEventSource.VTubeStudio,
            Service = ServiceName,
            Name = "WebSocket",
            Status = status,
            Configured = Enabled
        });
    }

    [ExcludeFromCodeCoverage]
    public async Task<bool> RefreshAsync(CancellationToken ct = default) {
        VTubeStudioClient? client = _client;
        if (client == null || !Connected) return false;

        try {
            CurrentModelResponse model = await client.GetCurrentModelAsync(ct);
            CurrentModelId = model.ModelLoaded ? model.ModelId : null;
            CurrentModelName = model.ModelLoaded ? model.ModelName : null;

            CachedHotkeys = await GetHotkeysAsync(ct);
            CachedExpressions = await GetExpressionsAsync(ct);
            CachedParameters = await GetParametersAsync(ct);

            _logger?.LogDebug(
                "[VTSService] Refreshed {Model}: {Hotkeys} hotkeys, {Expressions} expressions, {Parameters} parameters",
                CurrentModelName ?? "no model", CachedHotkeys.Count, CachedExpressions.Count, CachedParameters.Count);

            ModelDataChanged?.Invoke();
            return true;
        }
        catch (Exception ex) when (IsExpectedConnectFailure(ex)) {
            _logger?.LogDebug("[VTSService] Refresh skipped: {Reason}", ParseException(ex));
            return false;
        }
        catch (Exception ex) {
            _logger?.LogWarning(ex, "[VTSService] Refresh failed");
            return false;
        }
    }

    [ExcludeFromCodeCoverage]
    public async Task<IReadOnlyList<VtsHotkey>> GetHotkeysAsync(CancellationToken ct = default) {
        VTubeStudioClient? client = _client;
        if (client == null || !Connected) return [];

        try {
            HotkeysInCurrentModelResponse response =
                await client.GetHotkeysAsync(new HotkeysInCurrentModelRequest(), ct);
            if (!response.ModelLoaded) return [];

            return response.AvailableHotkeys
                .Select(h =>
                    new VtsHotkey(h.HotkeyId, h.Name, h.Type, h.File ?? string.Empty, h.Description ?? string.Empty))
                .ToList();
        }
        catch (Exception ex) {
            _logger?.LogWarning(ex, "[VTSService] Failed to list hotkeys");
            return [];
        }
    }

    [ExcludeFromCodeCoverage]
    public async Task<IReadOnlyList<VtsExpression>> GetExpressionsAsync(CancellationToken ct = default) {
        VTubeStudioClient? client = _client;
        if (client == null || !Connected) return [];

        try {
            ExpressionStateResponse response =
                await client.GetExpressionStateAsync(new ExpressionStateRequest { Details = false }, ct);
            if (!response.ModelLoaded) return [];

            return response.Expressions
                .Select(e => new VtsExpression(e.File, e.Name, e.Active))
                .ToList();
        }
        catch (Exception ex) {
            _logger?.LogWarning(ex, "[VTSService] Failed to list expressions");
            return [];
        }
    }

    [ExcludeFromCodeCoverage]
    public async Task<IReadOnlyList<VtsParameter>> GetParametersAsync(CancellationToken ct = default) {
        VTubeStudioClient? client = _client;
        if (client == null || !Connected) return [];

        try {
            InputParameterListResponse response = await client.GetInputParametersAsync(ct);
            if (!response.ModelLoaded) return [];

            List<VtsParameter> parameters = response.DefaultParameters
                .Select(p => Map(p, false))
                .ToList();

            parameters.AddRange(response.CustomParameters.Select(p => Map(p, true)));
            return parameters;
        }
        catch (Exception ex) {
            _logger?.LogWarning(ex, "[VTSService] Failed to list input parameters");
            return [];
        }

        static VtsParameter Map(ParameterInfo p, bool isCustom) {
            return new VtsParameter(p.Name, p.AddedBy ?? "", p.Value, p.Min, p.Max, p.DefaultValue, isCustom);
        }
    }

    [ExcludeFromCodeCoverage]
    public async Task<IReadOnlyList<VtsParameter>> GetLive2DParametersAsync(CancellationToken ct = default) {
        // unused, but future scope?
        VTubeStudioClient? client = _client;
        if (client == null || !Connected) return [];

        try {
            Live2DParameterListResponse response = await client.GetLive2DParametersAsync(ct);
            if (!response.ModelLoaded) return [];

            return response.Parameters
                .Select(p => new VtsParameter(p.Name, p.AddedBy ?? "", p.Value, p.Min, p.Max, p.DefaultValue, false))
                .ToList();
        }
        catch (Exception ex) {
            _logger?.LogWarning(ex, "[VTSService] Failed to list Live2D parameters");
            return [];
        }
    }

    [ExcludeFromCodeCoverage]
    public async Task<double?> GetParameterValueAsync(string parameterName, CancellationToken ct = default) {
        VTubeStudioClient? client = _client;
        if (client == null || !Connected || string.IsNullOrWhiteSpace(parameterName)) return null;

        try {
            ParameterInfo info = await client.GetParameterValueAsync(
                new ParameterValueRequest { Name = parameterName }, ct);
            return info.Value;
        }
        catch (Exception ex) {
            _logger?.LogWarning(ex, "[VTSService] Failed to read parameter {Parameter}", parameterName);
            return null;
        }
    }

    [ExcludeFromCodeCoverage]
    public async Task<bool?> GetExpressionStateAsync(string expressionFile, CancellationToken ct = default) {
        VTubeStudioClient? client = _client;
        if (client == null || !Connected || string.IsNullOrWhiteSpace(expressionFile)) return null;

        try {
            ExpressionStateResponse response = await client.GetExpressionStateAsync(
                new ExpressionStateRequest { Details = false, ExpressionFile = expressionFile }, ct);

            ExpressionInfo? match = response.Expressions
                .FirstOrDefault(e => string.Equals(e.File, expressionFile, StringComparison.OrdinalIgnoreCase));
            return match?.Active;
        }
        catch (Exception ex) {
            _logger?.LogWarning(ex, "[VTSService] Failed to read expression state {Expression}", expressionFile);
            return null;
        }
    }

    [ExcludeFromCodeCoverage]
    public async Task<bool> TriggerHotkeyAsync(string hotkeyId, string? itemInstanceId = null,
        CancellationToken ct = default) {
        VTubeStudioClient? client = _client;
        if (client == null || !Connected || string.IsNullOrWhiteSpace(hotkeyId)) return false;

        try {
            await client.TriggerHotkeyAsync(new HotkeyTriggerRequest {
                HotkeyId = hotkeyId,
                ItemInstanceId = itemInstanceId
            }, ct);
            return true;
        }
        catch (Exception ex) {
            _logger?.LogWarning(ex, "[VTSService] Failed to trigger hotkey {Hotkey}", hotkeyId);
            return false;
        }
    }

    [ExcludeFromCodeCoverage]
    public async Task<bool> SetExpressionStateAsync(string expressionFile, bool active,
        CancellationToken ct = default) {
        VTubeStudioClient? client = _client;
        if (client == null || !Connected || string.IsNullOrWhiteSpace(expressionFile)) return false;

        try {
            await client.SetExpressionAsync(new ExpressionActivationRequest {
                ExpressionFile = expressionFile,
                Active = active
            }, ct);
            return true;
        }
        catch (Exception ex) {
            _logger?.LogWarning(ex, "[VTSService] Failed to set expression {Expression} to {Active}",
                expressionFile, active);
            return false;
        }
    }

    [ExcludeFromCodeCoverage]
    public async Task<bool> ApplyExpressionActionAsync(string expressionFile, VtsToggleAction action,
        CancellationToken ct = default) {
        if (action == VtsToggleAction.DoNothing) return true;
        if (!Connected || string.IsNullOrWhiteSpace(expressionFile)) return false;

        bool active;
        if (action == VtsToggleAction.Toggle) {
            bool? current = await GetExpressionStateAsync(expressionFile, ct);
            if (current == null) return false;
            active = !current.Value;
        }
        else {
            active = action == VtsToggleAction.On;
        }

        return await SetExpressionStateAsync(expressionFile, active, ct);
    }

    [ExcludeFromCodeCoverage]
    public async Task<bool> SetParameterValueAsync(string parameterName, double value, double? weight = null,
        bool hold = true, CancellationToken ct = default) {
        VTubeStudioClient? client = _client;
        if (client == null || !Connected || string.IsNullOrWhiteSpace(parameterName)) return false;

        var injection = new ParameterValue { Id = parameterName, Value = value, Weight = weight };
        if (hold) _heldParameters[parameterName] = injection;

        try {
            await InjectAsync(client, [injection], ct);
            return true;
        }
        catch (Exception ex) {
            if (hold) _heldParameters.TryRemove(parameterName, out _);
            _logger?.LogWarning(ex, "[VTSService] Failed to set parameter {Parameter}", parameterName);
            return false;
        }
    }

    public bool ReleaseParameter(string parameterName) {
        return _heldParameters.TryRemove(parameterName, out _);
    }

    public void ReleaseAllParameters() {
        _heldParameters.Clear();
    }

    public bool IsParameterHeld(string parameterName) {
        return _heldParameters.ContainsKey(parameterName);
    }

    [ExcludeFromCodeCoverage]
    private static Task InjectAsync(VTubeStudioClient? client, ICollection<ParameterValue> values,
        CancellationToken ct = default) {
        if (client is not { IsConnected: true }) return Task.CompletedTask;

        IReadOnlyList<ParameterValue> list = values.ToList();
        return client.InjectParameterDataAsync(new InjectParameterDataRequest {
            Mode = "set",
            FaceFound = false,
            ParameterValues = list
        }, ct);
    }

    [ExcludeFromCodeCoverage]
    private void StartPublishLoop() {
        StopPublishLoop();
        var cts = new CancellationTokenSource();
        _publishCts = cts;
        _ = Task.Run(() => RunPublishLoopAsync(cts.Token), cts.Token);
    }

    private void StopPublishLoop() {
        CancellationTokenSource? cts = Interlocked.Exchange(ref _publishCts, null);

        try {
            cts?.Cancel();
        }
        catch {
            /**/
        }
    }

    [ExcludeFromCodeCoverage]
    private async Task RunPublishLoopAsync(CancellationToken ct) {
        var reportedFailure = false;

        while (!ct.IsCancellationRequested) {
            try {
                await Task.Delay(PublishIntervalMs, ct);
            }
            catch (OperationCanceledException) {
                return;
            }

            if (!Connected || _client == null || _customParameters.IsEmpty) continue;

            try {
                await InjectCustomParametersAsync(ct);
                reportedFailure = false;
            }
            catch (OperationCanceledException) {
                return;
            }
            catch (Exception ex) {
                if (reportedFailure) continue;
                reportedFailure = true;
                _logger?.LogDebug("[VTSService] Custom parameter keepalive failed: {Reason}", ParseException(ex));
            }
        }
    }

    [ExcludeFromCodeCoverage]
    private void StartHoldLoop() {
        StopHoldLoop();
        var cts = new CancellationTokenSource();
        _holdCts = cts;
        _ = Task.Run(() => RunHoldLoopAsync(cts.Token), cts.Token);
    }

    private void StopHoldLoop() {
        CancellationTokenSource? cts = Interlocked.Exchange(ref _holdCts, null);

        try {
            cts?.Cancel();
        }
        catch {
            /**/
        }
    }

    [ExcludeFromCodeCoverage]
    private async Task RunHoldLoopAsync(CancellationToken ct) {
        var reportedFailure = false;

        while (!ct.IsCancellationRequested) {
            try {
                await Task.Delay(InjectIntervalMs, ct);
            }
            catch (OperationCanceledException) {
                return;
            }

            VTubeStudioClient? client = _client;
            if (client == null || !Connected || _heldParameters.IsEmpty) continue;

            try {
                await InjectAsync(client, _heldParameters.Values.ToList(), ct);
                reportedFailure = false;
            }
            catch (OperationCanceledException) {
                return;
            }
            catch (Exception ex) {
                if (reportedFailure) continue;
                reportedFailure = true;
                _logger?.LogDebug("[VTSService] Parameter hold injection failed: {Reason}", ParseException(ex));
            }
        }
    }

    [ExcludeFromCodeCoverage]
    private async Task ApplyModelChangeAsync(string? modelId, string? modelName, CancellationToken ct) {
        if (string.Equals(modelId, CurrentModelId, StringComparison.Ordinal)) return;

        _logger?.LogInformation("[VTSService] Model changed: {Previous} -> {Current}",
            CurrentModelName ?? "none", modelName ?? "none");

        CurrentModelId = modelId;
        CurrentModelName = modelName;
        _heldParameters.Clear();
        await RefreshAsync(ct);
    }

    public async Task<bool> ExecuteWheelActionAsync(VTSWheelAction action, CancellationToken ct = default) {
        if (!Connected) {
            _logger?.LogInformation(
                "[VTSService] Wheel action skipped: not connected to VTube Studio. Leaving the spin pending");
            return false;
        }

        if (!action.IsValid(out string invalid)) {
            _logger?.LogWarning("[VTSService] Wheel action is not valid: {Error}", invalid);
            return false;
        }

        switch (action.Kind) {
            case VtsTargetKind.Expression:
                return await RunExpressionWheelActionAsync(action, ct);
            case VtsTargetKind.Parameter:
                return await RunParameterWheelActionAsync(action, ct);
            case VtsTargetKind.Hotkey:
                return await RunHotkeyWheelActionAsync(action, ct);
            default:
                return false;
        }
    }

    [ExcludeFromCodeCoverage]
    private async Task<bool> RunExpressionWheelActionAsync(VTSWheelAction action, CancellationToken ct) {
        if (!await TargetExistsAsync(action, ct)) return false;
        if (!await ApplyExpressionActionAsync(action.Target, action.ToggleAction, ct)) return false;
        ScheduleRevert(action, null);
        return true;
    }

    [ExcludeFromCodeCoverage]
    private async Task<bool> RunParameterWheelActionAsync(VTSWheelAction action, CancellationToken ct) {
        double? original = await GetParameterValueAsync(action.Target, ct);
        if (original == null) {
            _logger?.LogWarning(
                "[VTSService] Wheel action target parameter \"{Parameter}\" not found on the current model",
                action.Target);
            return false;
        }

        if (!await SetParameterValueAsync(action.Target, action.Value, ct: ct)) return false;
        ScheduleRevert(action, original);
        return true;
    }

    [ExcludeFromCodeCoverage]
    private async Task<bool> RunHotkeyWheelActionAsync(VTSWheelAction action, CancellationToken ct) {
        if (!await TargetExistsAsync(action, ct)) return false;
        if (!await TriggerHotkeyAsync(action.Target, ct: ct)) return false;
        ScheduleRevert(action, null);
        return true;
    }

    [ExcludeFromCodeCoverage]
    private async Task<bool> TargetExistsAsync(VTSWheelAction action, CancellationToken ct) {
        if (Matches()) return true;

        await RefreshAsync(ct);
        if (Matches()) return true;

        _logger?.LogWarning(
            "[VTSService] Wheel action target {Kind} \"{Target}\" is not on the current model ({Model})",
            action.Kind, action.Target, CurrentModelName ?? "none");
        return false;

        bool Matches() {
            return action.Kind switch {
                VtsTargetKind.Expression => CachedExpressions.Any(e =>
                    string.Equals(e.File, action.Target, StringComparison.OrdinalIgnoreCase)),
                VtsTargetKind.Hotkey => CachedHotkeys.Any(h =>
                    string.Equals(h.Id, action.Target, StringComparison.OrdinalIgnoreCase)),
                VtsTargetKind.Parameter => CachedParameters.Any(p =>
                    string.Equals(p.Name, action.Target, StringComparison.Ordinal)),
                _ => false
            };
        }
    }

    [ExcludeFromCodeCoverage]
    private void ScheduleRevert(VTSWheelAction action, double? originalValue) {
        if (!action.HasRevert) return;

        if (timerService == null) {
            _logger?.LogWarning("[VTSService] No timer service available; skipping the wheel action revert.");
            return;
        }

        string key = action.TimerKey;
        timerService.Register(key, action.Duration, async revertCt => {
            timerService.Unregister(key);
            await RevertWheelActionAsync(action, originalValue, revertCt);
        });

        _logger?.LogInformation("[VTSService] Wheel action revert for {Target} scheduled in {Duration}",
            action.Target, action.Duration);
    }

    [ExcludeFromCodeCoverage]
    private async Task RevertWheelActionAsync(VTSWheelAction action, double? originalValue, CancellationToken ct) {
        if (!Connected) {
            _logger?.LogInformation("[VTSService] Skipping wheel action revert for {Target}: not connected.",
                action.Target);
            return;
        }

        switch (action.Kind) {
            case VtsTargetKind.Expression:
                await ApplyExpressionActionAsync(action.Target, action.AfterToggle, ct);
                break;

            case VtsTargetKind.Parameter:
                switch (action.AfterParameter) {
                    case VtsParameterAfterAction.DoNothing:
                        ReleaseParameter(action.Target);
                        break;
                    case VtsParameterAfterAction.ResetToOriginal:
                        ReleaseParameter(action.Target);
                        if (originalValue.HasValue)
                            await SetParameterValueAsync(action.Target, originalValue.Value, hold: false, ct: ct);
                        break;
                    case VtsParameterAfterAction.SetNewValue:
                        ReleaseParameter(action.Target);
                        await SetParameterValueAsync(action.Target, action.AfterValue, hold: false, ct: ct);
                        break;
                }

                break;

            case VtsTargetKind.Hotkey:
                if (action.AfterHotkey == VtsHotkeyAfterAction.TriggerAgain)
                    await TriggerHotkeyAsync(action.Target, ct: ct);
                break;
        }
    }

    [ExcludeFromCodeCoverage]
    private void OnSubathonDataUpdate(SubathonData data, DateTime timestamp) {
        _ = Task.Run(() => RefreshPublishedValuesAsync(CancellationToken.None));
    }

    [ExcludeFromCodeCoverage]
    private void OnGoalListUpdated(List<SubathonGoal> goals, long currentValue, GoalsType type) {
        UpdateGoalValues(goals, currentValue);
        _ = Task.Run(() => PublishCustomParametersAsync(CancellationToken.None));
    }

    [ExcludeFromCodeCoverage]
    private void OnGoalCompleted(SubathonGoal goal, long currentValue) {
        _ = Task.Run(() => RefreshPublishedValuesAsync(CancellationToken.None));
    }

    [ExcludeFromCodeCoverage]
    private async Task RefreshPublishedValuesAsync(CancellationToken ct) {
        if (!Connected) return;
        if (dbFactory == null) {
            _logger?.LogDebug("[VTSService] No database factory; cannot read subathon values to publish");
            return;
        }

        try {
            await using AppDbContext db = await dbFactory.CreateDbContextAsync(ct);

            SubathonData? subathon = await db.SubathonDatas
                .Include(x => x.Multiplier)
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.IsActive, ct);
            if (subathon == null) return;

            SetCustomParameter(ParamCurrentPoints, Math.Max(0, subathon.Points));
            SetCustomParameter(ParamCurrentMoney, Math.Max(0, subathon.GetRoundedMoneySumWithCents()));
            SetCustomFlag(ParamMultiplierActive, subathon.Multiplier?.IsRunning() ?? false);
            SetCustomFlag(ParamTimerPaused, subathon.IsPaused);
            SetCustomFlag(ParamTimerLocked, subathon.IsLocked);

            bool promptRunning = await db.SubathonPromptRuns
                .AsNoTracking()
                .AnyAsync(r => r.Status == SubathonPromptRunStatus.Active, ct);
            SetCustomFlag(ParamPromptActive, promptRunning);

            SubathonGoalSet? goalSet = await db.SubathonGoalSets
                .AsNoTracking()
                .Include(g => g.Goals)
                .FirstOrDefaultAsync(g => g.IsActive, ct);

            long currentValue = goalSet?.Type == GoalsType.Money ? subathon.GetRoundedMoneySum() : subathon.Points;
            UpdateGoalValues(goalSet?.Goals, currentValue);
        }
        catch (Exception ex) {
            _logger?.LogDebug("[VTSService] Could not refresh published values: {Reason}", ParseException(ex));
        }

        await PublishCustomParametersAsync(ct);
    }

    [ExcludeFromCodeCoverage]
    private async Task PublishCustomParametersAsync(CancellationToken ct) {
        if (!Connected || _client == null || _customParameters.IsEmpty) return;
        if (!HasCustomParameterChanged(_customParameters.Values.ToList())) return;

        try {
            await InjectCustomParametersAsync(ct);
        }
        catch (Exception ex) {
            _logger?.LogDebug("[VTSService] Could not publish custom parameters: {Reason}", ParseException(ex));
        }
    }

    [ExcludeFromCodeCoverage]
    private async Task InjectCustomParametersAsync(CancellationToken ct) {
        List<ParameterValue> snapshot = _customParameters.Values.ToList();
        if (snapshot.Count == 0) return;

        await InjectAsync(_client, snapshot, ct);
        foreach (ParameterValue value in snapshot) _lastPublished[value.Id] = value.Value;
    }

    [ExcludeFromCodeCoverage]
    private bool HasCustomParameterChanged(List<ParameterValue> snapshot) {
        foreach (ParameterValue value in snapshot) {
            if (!_lastPublished.TryGetValue(value.Id, out double previous)) return true;
            if (Math.Abs(previous - value.Value) > 0.0001) return true;
        }

        return false;
    }

    [ExcludeFromCodeCoverage]
    private void UpdateGoalValues(IEnumerable<SubathonGoal>? goals, long currentValue) {
        if (!Connected || _client is not { IsConnected: true }) return;

        List<SubathonGoal> ordered = (goals ?? []).OrderBy(g => g.Points).ToList();

        if (ordered.Count == 0) {
            SetCustomParameter(ParamCurrentGoalValue, 0);
            SetCustomParameter(ParamUntilNextGoal, 0);
            SetCustomParameter(ParamGoalProgress, 0);
            return;
        }

        SubathonGoal? next = ordered.FirstOrDefault(g => g.Points > currentValue);
        if (next == null) {
            SetCustomParameter(ParamGoalProgress, 100);
            return;
        }

        SetCustomParameter(ParamCurrentGoalValue, Math.Max(0, next.Points));
        SetCustomParameter(ParamUntilNextGoal, Math.Max(0, next.Points - currentValue));

        long previous = ordered.Where(g => g.Points <= currentValue)
            .Select(g => g.Points)
            .DefaultIfEmpty(0)
            .Max();

        long span = next.Points - previous;
        double progress = span <= 0 ? 100 : (currentValue - previous) / (double)span * 100;
        SetCustomParameter(ParamGoalProgress, Math.Clamp(progress, 0, 100));
    }

    [ExcludeFromCodeCoverage]
    private void SetCustomFlag(string parameterName, bool active) {
        SetCustomParameter(parameterName, active ? 1 : 0);
    }

    [ExcludeFromCodeCoverage]
    private void OnPromptRunChanged(SubathonPromptRun run, SubathonPrompt? prompt) {
        SetCustomFlag(ParamPromptActive, run.IsActive);
        _ = Task.Run(() => PublishCustomParametersAsync(CancellationToken.None));
    }

    [ExcludeFromCodeCoverage]
    private void OnWheelSpinStarted(WheelSet wheel, int delaySeconds) {
        int generation = Interlocked.Increment(ref _spinGeneration);
        SetCustomFlag(ParamWheelSpinActive, true);
        _ = Task.Run(() => PublishCustomParametersAsync(CancellationToken.None));

        _ = Task.Run(async () => {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, delaySeconds) + 5));
            if (Volatile.Read(ref _spinGeneration) != generation) return;
            ClearWheelSpinFlag();
        });
    }

    [ExcludeFromCodeCoverage]
    private void OnWheelSpinResult(WheelSet wheel, WheelItem? item, WheelSpinHistory history, int spinsOwed) {
        Interlocked.Increment(ref _spinGeneration);
        ClearWheelSpinFlag();
    }

    [ExcludeFromCodeCoverage]
    private void ClearWheelSpinFlag() {
        SetCustomFlag(ParamWheelSpinActive, false);
        _ = Task.Run(() => PublishCustomParametersAsync(CancellationToken.None));
    }

    [ExcludeFromCodeCoverage]
    private void SetCustomParameter(string parameterName, double value) {
        if (!_customParameters.ContainsKey(parameterName)) return;

        _customParameters[parameterName] = new ParameterValue { Id = parameterName, Value = value };
    }

    [ExcludeFromCodeCoverage]
    private void SeedCustomParameter(string parameterName) {
        _customParameters.TryAdd(parameterName, new ParameterValue { Id = parameterName, Value = 0 });
    }
}