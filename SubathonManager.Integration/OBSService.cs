using Microsoft.Extensions.Logging;
using OBSWebsocketDotNet;
using OBSWebsocketDotNet.Communication;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Objects;

namespace SubathonManager.Integration;

public class OBSService : IAppService
{
    private readonly ILogger? _logger;
    private readonly IConfig _config;
    private readonly OBSWebsocket _obs = new();
    private readonly Utils.ServiceReconnectState _reconnectState =
        new(TimeSpan.FromSeconds(5), maxRetries: 1000, maxBackoff: TimeSpan.FromMinutes(5));

    public bool Connected => _obs.IsConnected;

    public OBSService(ILogger<OBSService>? logger, IConfig config)
    {
        _logger = logger;
        _config = config;

        _obs.Connected += OnConnected;
        _obs.Disconnected += OnDisconnected;
    }
    
    public List<string> GetScenes()
    {
        return _obs.GetSceneList().Scenes
            .Select(s => s.Name)
            .ToList();
    }

    public string GetCurrentScene()
    {
        return _obs.GetCurrentProgramScene();
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _ = Task.Run(TryConnect, cancellationToken);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        _reconnectState.Cts?.Cancel();
        if (_obs.IsConnected) _obs.Disconnect();
        return Task.CompletedTask;
    }

    public void TryConnect()
    {
        var host = _config.Get("OBS", "Host", "localhost")!;
        var port = _config.Get("OBS", "Port", "4455")!;
        var password = _config.GetFromEncoded("OBS", "Password", "")!;

        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(port)) return;

        try
        {
            var url = $"ws://{host}:{port}";
            _obs.ConnectAsync(url, password);
        }
        catch (Exception ex)
        {
            if (_reconnectState.Retries < 3)
                _logger?.LogWarning(ex, "[OBSService] Connection attempt failed");
            else
            {
                _logger?.LogDebug(ex, "[OBSService] Connection attempt failed");
            }
        }
    }

    private void OnConnected(object? sender, EventArgs e)
    {
        _logger?.LogInformation("[OBSService] Connected");
        _reconnectState.Reset();
        _reconnectState.Cts?.Cancel();

        IntegrationEvents.RaiseConnectionUpdate(new IntegrationConnection
        {
            Source = SubathonEventSource.OBS,
            Service = "OBS",
            Name = "WebSocket",
            Status = true
        });
    }

    private void OnDisconnected(object? sender, ObsDisconnectionInfo e)
    {

        IntegrationEvents.RaiseConnectionUpdate(new IntegrationConnection
        {
            Source = SubathonEventSource.OBS,
            Service = "OBS",
            Name = "WebSocket",
            Status = false
        });

        if (_reconnectState.Retries < 2)
        {
            _logger?.LogWarning("[OBSService] Disconnected: {Reason}", e.DisconnectReason ?? "Not Running?");
            _ = Task.Run(ReconnectWithBackoffAsync);
        }
    }

    private async Task ReconnectWithBackoffAsync()
    {
        if (!await _reconnectState.Lock.WaitAsync(0)) return;

        try
        {
            _reconnectState.Cts?.Cancel();
            _reconnectState.Cts = new CancellationTokenSource();
            var token = _reconnectState.Cts.Token;

            var host = _config.Get("OBS", "Host", "")!;
            var port = _config.Get("OBS", "Port", "")!;
            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(port)) return;

            while (!token.IsCancellationRequested && !_obs.IsConnected)
            {
                if (_reconnectState.Retries >= _reconnectState.MaxRetries)
                {
                    _logger?.LogError("[OBSService] Max reconnect retries reached");
                    return;
                }

                _reconnectState.Retries++;
                var delay = _reconnectState.Backoff;

                if (_reconnectState.Retries < 3 || _reconnectState.Retries % 10 == 0)
                {
                    _logger?.LogDebug("[OBSService] Reconnect attempt {N} in {Delay}s",
                        _reconnectState.Retries, delay.TotalSeconds);
                }

                try
                {
                    await Task.Delay(delay, token);
                    if (!_obs.IsConnected) TryConnect();
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "[OBSService] Reconnect error");
                }

                _reconnectState.Backoff = TimeSpan.FromMilliseconds(
                    Math.Min(
                        _reconnectState.Backoff.TotalMilliseconds * 2,
                        _reconnectState.MaxBackoff.TotalMilliseconds));
            }
        }
        finally
        {
            _reconnectState.Lock.Release();
        }
    }

    public bool SaveConfig(string host, string port, string password, bool forceSave = false)
    {
        bool hasUpdated = false;
        hasUpdated |= _config.Set("OBS", "Host", host);
        hasUpdated |= _config.Set("OBS", "Port", port);
        hasUpdated |= _config.SetEncoded("OBS", "Password", password);
        if (hasUpdated && forceSave)
            _config.Save();
        return hasUpdated;
    }

    public (string host, string port, string password) GetConfig()
    {
        return (
            _config.Get("OBS", "Host", "localhost")!,
            _config.Get("OBS", "Port", "4455")!,
            _config.GetFromEncoded("OBS", "Password", "")!
        );
    }

    public async Task AddBrowserSource(
        string sourceName, string url, int width, int height,
        string sceneName, bool fitToScreen = false)
    {
        if (!_obs.IsConnected)
            throw new InvalidOperationException("OBS is not connected");

        var existingInputs = _obs.GetInputList();
        string finalName = sourceName;
        int count = 1;
        while (existingInputs.Any(i => i.InputName == finalName))
            finalName = $"{sourceName} ({count++})";

        var settings = new Newtonsoft.Json.Linq.JObject
        {
            ["url"] = url,
            ["width"] = width,
            ["height"] = height,
            ["reroute_audio"] = false,
            ["restart_when_active"] = false,
            ["shutdown"] = false,
            ["is_local_file"] = false,
            ["css"] = "body { background-color: rgba(0, 0, 0, 0); margin: 0px auto; overflow: hidden; }"
        };

        _obs.CreateInput(sceneName, finalName, "browser_source", settings, true);
        await Task.Delay(500);
        if (fitToScreen)
        {
            try
            {
                var sceneItems = _obs.GetSceneItemList(sceneName);
                var item = sceneItems.FirstOrDefault(si => si.SourceName == finalName);
                if (item != null)
                {
                    var videoSettings = _obs.GetVideoSettings();
            
                    var transformRequest = new Newtonsoft.Json.Linq.JObject
                    {
                        ["sceneName"] = sceneName,
                        ["sceneItemId"] = item.ItemId,
                        ["sceneItemTransform"] = new Newtonsoft.Json.Linq.JObject
                        {
                            ["boundsType"] = "OBS_BOUNDS_SCALE_INNER",
                            ["boundsWidth"] = (double)videoSettings.BaseWidth,
                            ["boundsHeight"] = (double)videoSettings.BaseHeight,
                            ["boundsAlignment"] = 0,
                            ["positionX"] = 0.0,
                            ["positionY"] = 0.0
                        }
                    };
                    _obs.SendRequest("SetSceneItemTransform", transformRequest);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[OBS] SetSceneItemTransform failed");
            }
        }
    }
    
    
}