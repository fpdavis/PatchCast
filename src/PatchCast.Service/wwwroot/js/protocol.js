// Wire-protocol parsing for the PatchCast browser client. Mirrors the exact byte
// layouts used by PatchCast.Protocol (AudioProtocol / PasswordProtocol). All
// integers are little-endian, matching the server.

export const CHANNEL_SYSTEM = 1;
export const CHANNEL_MICROPHONE = 2;

const AUDIO_MAGIC = 0x31544350; // "PCT1"
const AUTH_MAGIC = 0x31414350;  // "PCA1"
const HEADER_SIZE = 12;

// Builds the authentication request frame: magic u32, password length u16,
// then the UTF-8 password bytes (not pre-hashed; the server hashes and compares).
export function buildAuthFrame(password) {
    const passwordBytes = new TextEncoder().encode(password);
    const frame = new Uint8Array(6 + passwordBytes.length);
    const view = new DataView(frame.buffer);
    view.setUint32(0, AUTH_MAGIC, true);
    view.setUint16(4, passwordBytes.length, true);
    frame.set(passwordBytes, 6);
    return frame;
}

// Parses one audio packet (one WebSocket binary message). Returns
// { channel, format: { sampleRate, channels, bitsPerSample, isFloat }, data }
// where data is a Uint8Array view of the raw PCM bytes, or null if invalid.
export function parsePacket(arrayBuffer) {
    if (arrayBuffer.byteLength < HEADER_SIZE)
        return null;
    const view = new DataView(arrayBuffer);
    if (view.getUint32(0, true) !== AUDIO_MAGIC || view.getUint8(4) !== 1)
        return null;

    const channel = view.getUint8(5);
    const formatLength = view.getUint16(6, true);
    const dataLength = view.getInt32(8, true);
    const formatStart = HEADER_SIZE;
    const dataStart = formatStart + formatLength;
    if (dataLength < 0 || dataStart + dataLength > arrayBuffer.byteLength)
        return null;

    const format = parseWaveFormat(new DataView(arrayBuffer, formatStart, formatLength));
    if (format === null)
        return null;

    return { channel, format, data: new Uint8Array(arrayBuffer, dataStart, dataLength) };
}

// Parses the serialized NAudio WaveFormat. IMPORTANT: the blob begins with an
// int32 chunk length, so the WAVEFORMATEX fields start at byte offset 4.
function parseWaveFormat(view) {
    if (view.byteLength < 4 + 16)
        return null;

    const formatTag = view.getUint16(4, true);
    const channels = view.getUint16(6, true);
    const sampleRate = view.getUint32(8, true);
    // bytes 12-15 average-bytes-per-second, 16-17 block align (skipped)
    const bitsPerSample = view.getUint16(18, true);

    let isFloat = formatTag === 3; // WAVE_FORMAT_IEEE_FLOAT
    if (formatTag === 0xFFFE && view.byteLength >= 4 + 24 + 4) {
        // WAVE_FORMAT_EXTENSIBLE: SubFormat GUID Data1 at struct offset 24.
        // {00000001-...} = PCM, {00000003-...} = IEEE float.
        isFloat = view.getUint32(4 + 24, true) === 3;
    }

    return { sampleRate, channels, bitsPerSample, isFloat };
}
