using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using OBSWebsocketDotNet;
using OBSWebsocketDotNet.Communication;
using SubathonManager.Core;
using SubathonManager.Core.Enums;
using SubathonManager.Core.Events;
using SubathonManager.Core.Interfaces;
using SubathonManager.Core.Objects;
using SubathonManager.Core.Security;
using SubathonManager.Core.Security.Interfaces;
// ReSharper disable NullableWarningSuppressionIsUsed

namespace SubathonManager.Integration;

public sealed record ObsBrowserSourceCard(
    string SceneName,
    string ScenePath,
    int SceneItemId,
    string SourceName,
    string Url,
    int Width,
    int Height,
    bool Visible,
    bool? SrgbOff,
    Guid? RouteId);

[ExcludeFromCodeCoverage]
public class OBSService : IAppService
{
    private readonly ILogger? _logger;
    private readonly IConfig _config;
    private readonly ISecureStorage _secureStorage;
    private readonly OBSWebsocket _obs = new();
    private readonly Utils.ServiceReconnectState _reconnectState =
        new(TimeSpan.FromSeconds(3), maxRetries: 1000, maxBackoff: TimeSpan.FromSeconds(10), infiniteRetries: true);

    public bool Connected => _obs.IsConnected;

    private const string HelperHotkeyName = "subathonmanager_apply_tweaks";
    private const string ManagedMarkerKey = "subathon_managed";
    private const string ScriptFileName = "subathonmanager.lua";
    private const string ScriptResourceName = "SubathonManager.Integration.obs.subathonmanager.lua";

    public bool HelperScriptActive { get; private set; }
    public event Action<bool>? HelperScriptStatusChanged;
    
    public event Action? BrowserSourcesChanged;

    public static string ScriptPath => Path.GetFullPath(Path.Combine("obs", ScriptFileName));

    public OBSService(ILogger<OBSService>? logger, IConfig config, ISecureStorage secureStorage)
    {
        _logger = logger;
        _config = config;
        _secureStorage = secureStorage;

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
        var password = _secureStorage.GetOrDefault(StorageKeys.OBSWebSocketPassword, string.Empty);

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

        _ = Task.Run(CheckHelperScriptAsync);
    }

    private void OnDisconnected(object? sender, ObsDisconnectionInfo e)
    {
        if (HelperScriptActive)
        {
            HelperScriptActive = false;
            HelperScriptStatusChanged?.Invoke(false);
        }

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
    
    [ExcludeFromCodeCoverage]
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
                if (!_reconnectState.InfiniteRetries && _reconnectState.Retries >= _reconnectState.MaxRetries)
                {
                    _logger?.LogError("[OBSService] Max reconnect retries reached");
                    return;
                }

                _reconnectState.Retries++;
                var delay = _reconnectState.Backoff;

                if (!_reconnectState.InfiniteRetries && (_reconnectState.Retries < 3 || _reconnectState.Retries % 10 == 0))
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
        hasUpdated |= _secureStorage.Set(StorageKeys.OBSWebSocketPassword, password);
        if (hasUpdated && forceSave)
            _config.Save();
        return hasUpdated;
    }

    public (string host, string port, string password) GetConfig()
    {
        return (
            _config.Get("OBS", "Host", "localhost")!,
            _config.Get("OBS", "Port", "4455")!,
            _secureStorage.GetOrDefault(StorageKeys.OBSWebSocketPassword, string.Empty)!
        );
    }
    
    private async Task CheckHelperScriptAsync()
    {
        EnsureScriptFileOnDisk();
        await Task.Delay(250);
        if (!_obs.IsConnected) return;

        bool active = false;
        try
        {
            var response = _obs.SendRequest("GetHotkeyList");
            var hotkeys = response?["hotkeys"] as JArray;
            active = hotkeys != null &&
                     hotkeys.Any(h => string.Equals(h?.ToString()?.Trim(), HelperHotkeyName, StringComparison.Ordinal));

            if (active)
            {
                _logger?.LogInformation("[OBSService] Helper script detected");
            }
            else
            {
                _logger?.LogWarning(
                    "[OBSService] Helper script not loaded in OBS ({Count} hotkeys reported). Add it once via OBS Tools -> Scripts: {Path}",
                    hotkeys?.Count ?? -1, ScriptPath);
                _logger?.LogDebug("[OBSService] Hotkeys reported by OBS: {Hotkeys}",
                    hotkeys != null ? string.Join(", ", hotkeys.Select(h => h?.ToString())) : "<none>");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[OBSService] Helper script check failed");
        }

        HelperScriptActive = active;
        HelperScriptStatusChanged?.Invoke(active);
    }

    public void RecheckHelperScript()
    {
        if (!_obs.IsConnected) return;
        _ = Task.Run(CheckHelperScriptAsync);
    }

    private void EnsureScriptFileOnDisk()
    {
        try
        {
            using var stream = typeof(OBSService).Assembly.GetManifestResourceStream(ScriptResourceName);
            if (stream == null)
            {
                _logger?.LogWarning("[OBSService] Embedded helper script resource not found");
                return;
            }
            using var reader = new StreamReader(stream);
            var content = reader.ReadToEnd();

            var path = ScriptPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path) || File.ReadAllText(path) != content)
                File.WriteAllText(path, content);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[OBSService] Failed to write helper script to disk");
        }
    }
    
    public async Task TryApplyHelperTweaksAsync()
    {
        if (!_obs.IsConnected) return;
        if (!HelperScriptActive)
        {
            await CheckHelperScriptAsync();
        }
        if (!HelperScriptActive)
        {
            _logger?.LogDebug("[OBSService] Skipping helper tweaks - script not loaded in OBS");
            return;
        }

        try
        {
            var request = new JObject
            {
                ["hotkeyName"] = HelperHotkeyName
            };
            _obs.SendRequest("TriggerHotkeyByName", request);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[OBSService] TriggerHotkeyByName for helper script failed");
        }
    }
    
    public async Task<List<ObsBrowserSourceCard>> GetOverlayBrowserSourcesAsync(string serverPort)
    {
        var cards = new List<ObsBrowserSourceCard>();
        if (!_obs.IsConnected) return cards;

        if (HelperScriptActive)
        {
            await TryApplyHelperTweaksAsync();
            await Task.Delay(300);
        }

        bool adoptedAny = CollectBrowserSourceCards(serverPort, cards);

        if (adoptedAny && HelperScriptActive)
        {
            await TryApplyHelperTweaksAsync();
            await Task.Delay(300);
            cards.Clear();
            CollectBrowserSourceCards(serverPort, cards);
        }

        return cards;
    }

    private bool CollectBrowserSourceCards(string serverPort, List<ObsBrowserSourceCard> cards)
    {
        bool adoptedAny = false;
        var settingsCache = new Dictionary<string, JObject?>();
        var seenItems = new HashSet<string>();

        foreach (var sceneName in GetScenes())
        {
            adoptedAny |= CollectFromScene(sceneName, sceneName, isGroup: false, serverPort,
                cards, settingsCache, seenItems);
        }
        return adoptedAny;
    }

    private bool CollectFromScene(string sceneName, string scenePath, bool isGroup, string serverPort,
        List<ObsBrowserSourceCard> cards, Dictionary<string, JObject?> settingsCache,
        HashSet<string> seenItems)
    {
        bool adoptedAny = false;
        JArray? items;
        try
        {
            var response = _obs.SendRequest(isGroup ? "GetGroupSceneItemList" : "GetSceneItemList",
                new JObject { ["sceneName"] = sceneName });
            items = response?["sceneItems"] as JArray;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "[OBSService] Failed to list scene items for '{Scene}'", sceneName);
            return false;
        }
        if (items == null) return false;

        foreach (var item in items)
        {
            if (item?["isGroup"]?.Value<bool?>() == true)
            {
                var groupName = item["sourceName"]?.ToString();
                if (!string.IsNullOrEmpty(groupName))
                    adoptedAny |= CollectFromScene(groupName, $"{scenePath} / {groupName}", isGroup: true,
                        serverPort, cards, settingsCache, seenItems);
                continue;
            }

            var inputKind = item?["inputKind"]?.ToString() ?? "";
            if (!inputKind.StartsWith("browser_source")) continue;

            var sourceName = item?["sourceName"]?.ToString();
            if (string.IsNullOrEmpty(sourceName)) continue;
            int itemId = item?["sceneItemId"]?.Value<int>() ?? -1;
            bool visible = item?["sceneItemEnabled"]?.Value<bool>() ?? true;

            if (!settingsCache.TryGetValue(sourceName, out var settings))
            {
                try
                {
                    var response = _obs.SendRequest("GetInputSettings",
                        new JObject { ["inputName"] = sourceName });
                    settings = response?["inputSettings"] as JObject;
                }
                catch { settings = null; }
                settingsCache[sourceName] = settings;
            }
            if (settings == null) continue;

            var url = settings["url"]?.ToString() ?? "";
            bool managed = settings[ManagedMarkerKey]?.Value<bool?>() ?? false;
            var routeId = TryParseRouteId(url, serverPort, out bool urlMatchesServer);

            if (!managed && !urlMatchesServer) continue;

            if (!managed && urlMatchesServer)
            {
                try
                {
                    _obs.SendRequest("SetInputSettings", new Newtonsoft.Json.Linq.JObject
                    {
                        ["inputName"] = sourceName,
                        ["inputSettings"] = new JObject { [ManagedMarkerKey] = true },
                        ["overlay"] = true
                    });
                    adoptedAny = true;
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "[OBSService] Failed to adopt source '{Source}'", sourceName);
                }
            }

            bool? srgbOff = null;
            if (settings["subathon_blend_states"] is JObject states)
            {
                var state = states[$"{sceneName}|{itemId}"]?.ToString();
                if (state == "srgb_off") srgbOff = true;
                else if (state == "default") srgbOff = false;
            }

            int width = settings["width"]?.Value<int?>() ?? 800;
            int height = settings["height"]?.Value<int?>() ?? 600;

            if (!seenItems.Add($"{sceneName}|{itemId}")) continue;

            cards.Add(new ObsBrowserSourceCard(sceneName, scenePath, itemId, sourceName, url,
                width, height, visible, srgbOff, routeId));
        }

        return adoptedAny;
    }

    private static Guid? TryParseRouteId(string url, string serverPort, out bool urlMatchesServer)
    {
        urlMatchesServer = false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (uri.Port.ToString() != serverPort) return null;

        var path = uri.AbsolutePath;
        const string prefix = "/route/";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;

        urlMatchesServer = true;
        var idPart = path[prefix.Length..].TrimEnd('/');
        return Guid.TryParse(idPart, out var id) ? id : null;
    }

    public void SetSceneItemVisible(string sceneName, int sceneItemId, bool visible)
    {
        if (!_obs.IsConnected) return;
        try
        {
            _obs.SendRequest("SetSceneItemEnabled", new Newtonsoft.Json.Linq.JObject
            {
                ["sceneName"] = sceneName,
                ["sceneItemId"] = sceneItemId,
                ["sceneItemEnabled"] = visible
            });
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[OBSService] SetSceneItemEnabled failed for '{Scene}'/{Id}", sceneName, sceneItemId);
        }
    }

    public void RefreshBrowserSource(string sourceName)
    {
        if (!_obs.IsConnected) return;
        try
        {
            _obs.SendRequest("PressInputPropertiesButton", new JObject
            {
                ["inputName"] = sourceName,
                ["propertyName"] = "refreshnocache"
            });
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[OBSService] RefreshBrowserSource failed for '{Source}'", sourceName);
        }
    }

    public void RemoveBrowserSource(string sourceName)
    {
        if (!_obs.IsConnected) return;
        try
        {
            _obs.SendRequest("RemoveInput", new JObject { ["inputName"] = sourceName });
            BrowserSourcesChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[OBSService] RemoveInput failed for '{Source}'", sourceName);
        }
    }

    public void SetBrowserSourceSize(string sourceName, int width, int height)
    {
        if (!_obs.IsConnected) return;
        try
        {
            _obs.SendRequest("SetInputSettings", new JObject
            {
                ["inputName"] = sourceName,
                ["inputSettings"] = new JObject
                {
                    ["width"] = width,
                    ["height"] = height
                },
                ["overlay"] = true
            });
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[OBSService] SetBrowserSourceSize failed for '{Source}'", sourceName);
        }
    }

    public async Task RequestBlendMethodAsync(string sourceName, string sceneName, int sceneItemId, bool srgbOff)
    {
        if (!_obs.IsConnected) return;
        try
        {
            _obs.SendRequest("SetInputSettings", new JObject
            {
                ["inputName"] = sourceName,
                ["inputSettings"] = new JObject
                {
                    [ManagedMarkerKey] = true,
                    ["subathon_blend_request"] = new JObject
                    {
                        ["method"] = srgbOff ? "srgb_off" : "default",
                        ["scene"] = sceneName,
                        ["item"] = sceneItemId
                    }
                },
                ["overlay"] = true
            });
            await TryApplyHelperTweaksAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[OBSService] RequestBlendMethod failed for '{Source}'", sourceName);
        }
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

        var settings = new JObject
        {
            ["url"] = url,
            ["width"] = width,
            ["height"] = height,
            ["reroute_audio"] = false,
            ["restart_when_active"] = false,
            ["shutdown"] = false,
            ["is_local_file"] = false,
            ["css"] = "body { background-color: rgba(0, 0, 0, 0); margin: 0px auto; overflow: hidden; }",
            [ManagedMarkerKey] = true,
            ["subathon_blend_request"] = new JObject { ["method"] = "srgb_off" }
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
            
                    var transformRequest = new JObject
                    {
                        ["sceneName"] = sceneName,
                        ["sceneItemId"] = item.ItemId,
                        ["sceneItemTransform"] = new JObject
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

        await TryApplyHelperTweaksAsync();
        BrowserSourcesChanged?.Invoke();
    }
}