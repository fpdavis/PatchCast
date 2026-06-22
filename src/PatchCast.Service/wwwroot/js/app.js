import { parsePacket, buildAuthFrame, CHANNEL_SYSTEM, CHANNEL_MICROPHONE } from './protocol.js';
import { StreamPlayer } from './audio.js';
import * as settings from './settings.js';

const RETRY_DELAYS = [0, 1, 2, 4, 8, 16, 32];
const STABLE_RESET_MS = 32000;
const METER_SMOOTHING = 0.2;
const MAX_LOG_ENTRIES = 2000;

const el = (id) => document.getElementById(id);
const ui = {
    host: el('hostInput'), hostList: el('hostList'), removeHost: el('removeHostBtn'),
    port: el('portInput'), password: el('passwordInput'), savePassword: el('savePasswordCheck'),
    systemMute: el('systemMuteCheck'), systemVolume: el('systemVolumeSlider'), systemMeter: el('systemMeterFill'),
    micMute: el('micMuteCheck'), micVolume: el('micVolumeSlider'), micMeter: el('micMeterFill'),
    status: el('statusLabel'), quality: el('qualityLabel'),
    connect: el('connectBtn'), showLog: el('showLogBtn'), log: el('logPanel'), logContent: el('logContent'),
};

const streams = {
    [CHANNEL_SYSTEM]: { player: null, display: 0, slider: ui.systemVolume, mute: ui.systemMute, meter: ui.systemMeter },
    [CHANNEL_MICROPHONE]: { player: null, display: 0, slider: ui.micVolume, mute: ui.micMute, meter: ui.micMeter },
};

let audioContext = null;
let workletReady = null;
let socket = null;
let authenticated = false;
let userDisconnect = false;
let retryIndex = 0;
let stableTimer = null;
let retryTimer = null;
let qualityTimer = null;
let meterRaf = null;
let bytesThisSecond = 0;
let packetsThisSecond = 0;

// ---- Activity log -------------------------------------------------------------

function log(message) {
    const now = new Date();
    const pad = (n, w = 2) => String(n).padStart(w, '0');
    const stamp = `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())} ` +
        `${pad(now.getHours())}:${pad(now.getMinutes())}:${pad(now.getSeconds())}.${pad(now.getMilliseconds(), 3)}`;
    const line = document.createElement('div');
    line.textContent = `${stamp}  ${message}`;
    ui.logContent.appendChild(line);
    while (ui.logContent.childElementCount > MAX_LOG_ENTRIES)
        ui.logContent.removeChild(ui.logContent.firstChild);
    ui.logContent.scrollTop = ui.logContent.scrollHeight;
}

function setStatus(text) { ui.status.textContent = text; }

// ---- Audio setup --------------------------------------------------------------

async function ensureAudio() {
    if (audioContext === null) {
        audioContext = new AudioContext();
        workletReady = audioContext.audioWorklet.addModule('js/pcm-player-processor.js');
    }
    await workletReady;
    if (audioContext.state === 'suspended')
        await audioContext.resume();
}

function playerFor(packet) {
    const stream = streams[packet.channel];
    if (stream === undefined)
        return null;
    if (stream.player === null) {
        stream.player = new StreamPlayer(audioContext, packet.format.channels);
        stream.player.setVolume(Number(stream.slider.value) / 100);
        stream.player.setMuted(stream.mute.checked);
        log(`${channelName(packet.channel)} stream: ${packet.format.sampleRate} Hz, ` +
            `${packet.format.channels} ch, ${packet.format.bitsPerSample}-bit ${packet.format.isFloat ? 'float' : 'PCM'}.`);
    }
    return stream.player;
}

function channelName(channel) {
    return channel === CHANNEL_SYSTEM ? 'System audio' : 'Microphone';
}

function disposeStreams() {
    for (const stream of Object.values(streams)) {
        if (stream.player !== null) {
            stream.player.dispose();
            stream.player = null;
        }
    }
}

// ---- Meters -------------------------------------------------------------------

function startMeters() {
    if (meterRaf !== null)
        return;
    const frame = () => {
        for (const stream of Object.values(streams)) {
            const target = stream.player !== null ? stream.player.envelope : 0;
            stream.display += (target - stream.display) * METER_SMOOTHING;
            const level = Math.max(0, Math.min(1, stream.display));
            stream.meter.style.width = `${(level * 100).toFixed(1)}%`;
            stream.meter.style.backgroundColor = level >= 0.9 ? '#dc3c3c' : level >= 0.7 ? '#e6be3c' : '#46c85a';
        }
        meterRaf = requestAnimationFrame(frame);
    };
    meterRaf = requestAnimationFrame(frame);
}

function stopMeters() {
    if (meterRaf !== null) {
        cancelAnimationFrame(meterRaf);
        meterRaf = null;
    }
}

// ---- Connection ---------------------------------------------------------------

function currentProfile() {
    return {
        host: ui.host.value.trim(),
        port: Number(ui.port.value),
        password: ui.password.value,
        savePassword: ui.savePassword.checked,
        systemVolume: Number(ui.systemVolume.value),
        muteSystemAudio: ui.systemMute.checked,
        microphoneVolume: Number(ui.micVolume.value),
        muteMicrophone: ui.micMute.checked,
    };
}

function persistProfile() {
    const profile = currentProfile();
    if (profile.host === '')
        return;
    settings.saveProfile(profile);
    refreshHostList(profile.host);
}

async function connect() {
    const profile = currentProfile();
    if (profile.host === '') {
        setStatus('Enter a server host or IP.');
        return;
    }

    try {
        await ensureAudio();
    } catch (error) {
        log(`Audio could not be started: ${error.message}`);
        setStatus('Audio unavailable');
        return;
    }

    persistProfile();
    userDisconnect = false;
    retryIndex = 0;
    setConnectionInputsEnabled(false);
    ui.connect.textContent = 'Disconnect';
    openSocket();
}

function openSocket() {
    const host = ui.host.value.trim();
    const port = Number(ui.port.value);
    const url = `wss://${host}:${port}/ws`;
    authenticated = false;
    setStatus('Connecting');
    log(`Connecting to ${url}.`);

    socket = new WebSocket(url);
    socket.binaryType = 'arraybuffer';
    socket.onopen = () => {
        setStatus('Authenticating');
        socket.send(buildAuthFrame(ui.password.value));
    };
    socket.onmessage = (event) => handleMessage(event.data);
    socket.onerror = () => log('WebSocket error (the server may be unreachable or the certificate untrusted).');
    socket.onclose = () => handleClose();
}

function handleMessage(payload) {
    if (!(payload instanceof ArrayBuffer))
        return;

    if (!authenticated) {
        const accepted = payload.byteLength >= 1 && new Uint8Array(payload)[0] === 1;
        if (!accepted) {
            log('Password rejected by the server.');
            setStatus('Password rejected');
            userDisconnect = true; // do not auto-retry a bad password
            socket.close();
            return;
        }
        authenticated = true;
        setStatus('Connected');
        log('Server password accepted. Receiving audio.');
        startStableTimer();
        startQualityTimer();
        startMeters();
        return;
    }

    bytesThisSecond += payload.byteLength;
    packetsThisSecond += 1;
    const packet = parsePacket(payload);
    if (packet === null)
        return;
    const player = playerFor(packet);
    if (player !== null)
        player.accept(packet);
}

function handleClose() {
    clearTimers();
    disposeStreams();
    authenticated = false;
    socket = null;

    if (userDisconnect) {
        setStatus('Disconnected');
        ui.quality.textContent = 'Not Connected';
        ui.connect.textContent = 'Connect';
        setConnectionInputsEnabled(true);
        stopMeters();
        return;
    }

    const delay = RETRY_DELAYS[retryIndex];
    retryIndex = Math.min(retryIndex + 1, RETRY_DELAYS.length - 1);
    log(`Connection lost. Next attempt in ${delay} second(s).`);
    scheduleReconnect(delay);
}

function scheduleReconnect(delaySeconds) {
    let remaining = delaySeconds;
    const tick = () => {
        if (userDisconnect)
            return;
        if (remaining <= 0) {
            openSocket();
            return;
        }
        setStatus(`Reconnecting (${remaining}s)`);
        remaining -= 1;
        retryTimer = setTimeout(tick, 1000);
    };
    tick();
}

function disconnect() {
    userDisconnect = true;
    persistProfile();
    log('Disconnect requested.');
    clearTimers();
    if (socket !== null)
        socket.close();
    else
        handleClose();
}

function startStableTimer() {
    stableTimer = setTimeout(() => { retryIndex = 0; log('Connection stable; retry delay reset.'); }, STABLE_RESET_MS);
}

function startQualityTimer() {
    bytesThisSecond = 0;
    packetsThisSecond = 0;
    qualityTimer = setInterval(() => {
        const mbits = (bytesThisSecond * 8) / 1_000_000;
        ui.quality.textContent = `${mbits.toFixed(2)} Mbit/s, ${packetsThisSecond} Packets/s; WSS/TLS via browser`;
        bytesThisSecond = 0;
        packetsThisSecond = 0;
    }, 1000);
}

function clearTimers() {
    for (const timer of [stableTimer, retryTimer]) {
        if (timer !== null) clearTimeout(timer);
    }
    if (qualityTimer !== null) clearInterval(qualityTimer);
    stableTimer = retryTimer = qualityTimer = null;
}

// ---- UI wiring ----------------------------------------------------------------

function setConnectionInputsEnabled(enabled) {
    ui.host.disabled = !enabled;
    ui.removeHost.disabled = !enabled;
    ui.port.disabled = !enabled;
    ui.password.disabled = !enabled;
    ui.savePassword.disabled = !enabled;
}

function refreshHostList(selected) {
    ui.hostList.innerHTML = '';
    for (const host of settings.listHosts()) {
        const option = document.createElement('option');
        option.value = host;
        ui.hostList.appendChild(option);
    }
    if (selected !== undefined)
        ui.host.value = selected;
}

function applyProfileToUi(profile) {
    ui.host.value = profile.host ?? '';
    if (profile.port) ui.port.value = profile.port;
    ui.password.value = profile.password ?? '';
    ui.savePassword.checked = Boolean(profile.savePassword);
    ui.systemVolume.value = profile.systemVolume ?? 100;
    ui.systemMute.checked = Boolean(profile.muteSystemAudio);
    ui.micVolume.value = profile.microphoneVolume ?? 100;
    ui.micMute.checked = Boolean(profile.muteMicrophone);
}

function bindStreamControls(channel) {
    const stream = streams[channel];
    stream.slider.addEventListener('input', () => {
        if (stream.player !== null) stream.player.setVolume(Number(stream.slider.value) / 100);
    });
    stream.mute.addEventListener('change', () => {
        if (stream.player !== null) stream.player.setMuted(stream.mute.checked);
    });
}

function init() {
    refreshHostList();
    const last = settings.getLastHost();
    const profile = last ? settings.loadProfile(last) : null;
    if (profile !== null)
        applyProfileToUi(profile);
    else
        ui.host.value = location.hostname || 'localhost';
    if (!ui.port.value)
        ui.port.value = location.port || 4748;

    bindStreamControls(CHANNEL_SYSTEM);
    bindStreamControls(CHANNEL_MICROPHONE);

    ui.connect.addEventListener('click', () => {
        if (socket !== null || (!userDisconnect && retryTimer !== null)) disconnect();
        else connect();
    });
    ui.showLog.addEventListener('click', () => {
        const visible = ui.log.classList.toggle('visible');
        ui.showLog.textContent = visible ? 'Hide Log' : 'Show Log';
    });
    ui.host.addEventListener('change', () => {
        const stored = settings.loadProfile(ui.host.value.trim());
        if (stored !== null) applyProfileToUi(stored);
        ui.removeHost.disabled = stored === null;
    });
    ui.removeHost.addEventListener('click', () => {
        const host = ui.host.value.trim();
        if (settings.findHost(host) === null) return;
        settings.removeHost(host);
        log(`Removed saved host ${host}.`);
        refreshHostList();
    });

    log('PatchCast browser client ready.');
}

init();
