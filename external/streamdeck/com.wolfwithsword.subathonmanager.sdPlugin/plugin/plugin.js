// SubathonManager - Stream Deck plugin

const DEFAULT_APP_PORT = 14040;
const APP_RECONNECT_MS = 5000;
const APP_PING_MS = 15000;
const ACK_TIMEOUT_MS = 3000;

let sdSocket = null;
let sdPluginUUID = null;

let globalSettings = { port: DEFAULT_APP_PORT };

let appSocket = null;
let appConnected = false;
let appCommands = []; // [{ command, description, requires_parameter, is_control }]
let appReconnectTimer = null;
let appPingTimer = null;

let lastPI = null;

const pendingAcks = new Map();

// register
function connectElgatoStreamDeckSocket(inPort, inPluginUUID, inRegisterEvent, inInfo) {
    sdPluginUUID = inPluginUUID;
    sdSocket = new WebSocket('ws://127.0.0.1:' + inPort);

    sdSocket.onopen = () => {
        sdSend({ event: inRegisterEvent, uuid: inPluginUUID });
        sdSend({ event: 'getGlobalSettings', context: sdPluginUUID });
        connectApp();
    };

    sdSocket.onmessage = (evt) => {
        let msg;
        try { msg = JSON.parse(evt.data); } catch { return; }

        switch (msg.event) {
            case 'didReceiveGlobalSettings': {
                const settings = (msg.payload && msg.payload.settings) || {};
                const newPort = parsePort(settings.port);
                const changed = newPort !== globalSettings.port;
                globalSettings = { port: newPort };
                if (changed) reconnectApp();
                break;
            }
            case 'keyDown':
                handleKeyDown(msg.context, (msg.payload && msg.payload.settings) || {});
                break;
            case 'propertyInspectorDidAppear':
                lastPI = { action: msg.action, context: msg.context };
                pushStateToPI();
                break;
            case 'sendToPlugin': {
                lastPI = { action: msg.action, context: msg.context };
                const payload = msg.payload || {};
                if (payload.event === 'getCommands') pushStateToPI();
                if (payload.event === 'setPort') {
                    const newPort = parsePort(payload.port);
                    const changed = newPort !== globalSettings.port;
                    globalSettings = { port: newPort };
                    sdSend({ event: 'setGlobalSettings', context: sdPluginUUID, payload: globalSettings });
                    if (changed) reconnectApp();
                    pushStateToPI();
                }
                break;
            }
        }
    };
}

function sdSend(obj) {
    if (sdSocket && sdSocket.readyState === WebSocket.OPEN)
        sdSocket.send(JSON.stringify(obj));
}

function parsePort(value) {
    const port = parseInt(value, 10);
    return Number.isFinite(port) && port > 0 && port <= 65535 ? port : DEFAULT_APP_PORT;
}


function connectApp() {
    // subathonmanager conn
    if (appSocket && (appSocket.readyState === WebSocket.OPEN || appSocket.readyState === WebSocket.CONNECTING))
        return;

    appSocket = new WebSocket('ws://127.0.0.1:' + globalSettings.port + '/ws');

    appSocket.onopen = () => {
        appConnected = true;
        appSend({ ws_type: 'hello', origin: 'StreamDeck Plugin' });
        appSend({ ws_type: 'IntegrationSource', source: 'StreamDeck' });
        appSend({ ws_type: 'Command', request: 'commands' });
        startAppPing();
        pushStateToPI();
    };

    appSocket.onmessage = (evt) => {
        let data;
        try { data = JSON.parse(evt.data); } catch { return; }

        if (data.type === 'command_list' && Array.isArray(data.commands)) {
            appCommands = data.commands;
            pushStateToPI();
        }
        else if (data.type === 'command_ack' && data.context) {
            const pending = pendingAcks.get(data.context);
            if (pending) {
                clearTimeout(pending);
                pendingAcks.delete(data.context);
                sdSend({ event: data.success ? 'showOk' : 'showAlert', context: data.context });
            }
        }
    };

    appSocket.onclose = () => {
        appConnected = false;
        stopAppPing();
        pushStateToPI();
        if (appReconnectTimer) clearTimeout(appReconnectTimer);
        appReconnectTimer = setTimeout(connectApp, APP_RECONNECT_MS);
    };

    appSocket.onerror = () => {
        try { appSocket.close(); } catch { /**/ }
    };
}

function reconnectApp() {
    if (appReconnectTimer) clearTimeout(appReconnectTimer);
    if (appSocket) {
        try { appSocket.close(); } catch { /**/ }
    }
    appReconnectTimer = setTimeout(connectApp, 100);
}

function appSend(obj) {
    if (appSocket && appSocket.readyState === WebSocket.OPEN)
        appSocket.send(JSON.stringify(obj));
}

function startAppPing() {
    stopAppPing();
    appPingTimer = setInterval(() => appSend({ ws_type: 'ping' }), APP_PING_MS);
}

function stopAppPing() {
    if (appPingTimer) clearInterval(appPingTimer);
    appPingTimer = null;
}

function handleKeyDown(context, settings) {
    const command = settings.command;
    if (!command || !appConnected) {
        sdSend({ event: 'showAlert', context: context });
        return;
    }

    const parameter = (settings.parameter || '').trim();

    appSend({
        ws_type: 'Command',
        type: 'Command',
        command: command,
        message: parameter,
        user: 'EXTERNAL',
        source: 'StreamDeck',
        context: context
    });

    if (pendingAcks.has(context)) clearTimeout(pendingAcks.get(context));
    pendingAcks.set(context, setTimeout(() => {
        pendingAcks.delete(context);
        sdSend({ event: 'showAlert', context: context });
    }, ACK_TIMEOUT_MS));
}

function pushStateToPI() {
    if (!lastPI) return;
    sdSend({
        event: 'sendToPropertyInspector',
        action: lastPI.action,
        context: lastPI.context,
        payload: {
            event: 'state',
            connected: appConnected,
            commands: appCommands,
            port: globalSettings.port
        }
    });
}
