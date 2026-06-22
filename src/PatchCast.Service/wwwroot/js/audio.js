// Audio decoding + playback for the PatchCast browser client.
//
// Each incoming packet is converted from its PCM format to interleaved Float32,
// resampled to the AudioContext's sample rate, and queued into a per-stream
// AudioWorklet. Volume/mute are applied by a GainNode AFTER metering, so the
// level meter always reflects the incoming signal (matching the WinForms client).
//
// Note: typed-array views assume little-endian, which is true on all browser
// platforms and matches the server's little-endian wire format.

const RELEASE_SECONDS = 0.25; // envelope release time constant

export class StreamPlayer {
    constructor(audioContext, channels) {
        this.ctx = audioContext;
        this.channels = channels;
        this.volume = 1;
        this.muted = false;
        this.envelope = 0; // incoming level (pre-gain), audio-domain follower

        this.gain = audioContext.createGain();
        this.gain.connect(audioContext.destination);
        this.node = new AudioWorkletNode(audioContext, 'pcm-player', {
            numberOfInputs: 0,
            numberOfOutputs: 1,
            outputChannelCount: [channels],
            processorOptions: { channels, maxFrames: Math.floor(audioContext.sampleRate * 0.4) },
        });
        this.node.connect(this.gain);
        this.applyGain();
    }

    setVolume(value) { this.volume = value; this.applyGain(); }
    setMuted(value) { this.muted = value; this.applyGain(); }
    applyGain() { this.gain.gain.value = this.muted ? 0 : this.volume; }

    // Decodes a packet, advances the level envelope, and queues audio for playback.
    accept(packet) {
        const decoded = decodeToFloat(packet);
        if (decoded === null)
            return;

        const { samples, peak, seconds } = decoded;
        const decay = Math.exp(-seconds / RELEASE_SECONDS);
        this.envelope = Math.max(peak, this.envelope * decay);

        const output = packet.format.sampleRate === this.ctx.sampleRate
            ? samples
            : resampleInterleaved(samples, this.channels, packet.format.sampleRate, this.ctx.sampleRate);
        this.node.port.postMessage(output, [output.buffer]);
    }

    flush() { this.node.port.postMessage('flush'); }

    dispose() {
        try { this.node.disconnect(); } catch { /* already gone */ }
        try { this.gain.disconnect(); } catch { /* already gone */ }
    }
}

// Converts raw PCM bytes to interleaved Float32, returning the peak magnitude and
// duration as well. Supports 32-bit float and 16-bit PCM; returns null otherwise.
function decodeToFloat(packet) {
    const { format, data } = packet;
    let samples;

    if (format.isFloat && format.bitsPerSample === 32) {
        const aligned = data.slice(); // copy to a 4-byte-aligned buffer
        samples = new Float32Array(aligned.buffer, 0, Math.floor(aligned.byteLength / 4));
    } else if (!format.isFloat && format.bitsPerSample === 16) {
        const aligned = data.slice();
        const pcm = new Int16Array(aligned.buffer, 0, Math.floor(aligned.byteLength / 2));
        samples = new Float32Array(pcm.length);
        for (let i = 0; i < pcm.length; i++)
            samples[i] = pcm[i] / 32768;
    } else {
        return null;
    }

    let peak = 0;
    for (let i = 0; i < samples.length; i++) {
        const magnitude = Math.abs(samples[i]);
        if (magnitude > peak)
            peak = magnitude;
    }

    const seconds = format.channels > 0 ? (samples.length / format.channels) / format.sampleRate : 0;
    return { samples, peak, seconds };
}

// Linear-interpolation resampler for interleaved Float32. Adequate for playback;
// bypassed entirely when the source already matches the context rate.
function resampleInterleaved(source, channels, sourceRate, targetRate) {
    const sourceFrames = source.length / channels;
    const targetFrames = Math.max(1, Math.round(sourceFrames * targetRate / sourceRate));
    const output = new Float32Array(targetFrames * channels);
    const ratio = sourceFrames / targetFrames;

    for (let i = 0; i < targetFrames; i++) {
        const position = i * ratio;
        const index = Math.floor(position);
        const frac = position - index;
        const nextIndex = Math.min(index + 1, sourceFrames - 1);
        for (let c = 0; c < channels; c++) {
            const a = source[index * channels + c];
            const b = source[nextIndex * channels + c];
            output[i * channels + c] = a + (b - a) * frac;
        }
    }

    return output;
}
