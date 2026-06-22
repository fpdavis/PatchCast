// AudioWorklet processor: a simple interleaved-Float32 ring buffer. The main
// thread posts decoded chunks (already at this context's sample rate); process()
// drains them into the output, emitting silence on underrun. Fed via postMessage
// (transferable buffers) rather than SharedArrayBuffer to avoid COOP/COEP headers.
class PcmPlayerProcessor extends AudioWorkletProcessor {
    constructor(options) {
        super();
        this.channels = options.processorOptions?.channels ?? 2;
        this.maxFrames = options.processorOptions?.maxFrames ?? Math.floor(sampleRate * 0.4);
        this.queue = [];
        this.head = null;
        this.headIndex = 0;
        this.queuedFrames = 0;

        this.port.onmessage = (event) => {
            if (event.data === 'flush') {
                this.queue = [];
                this.head = null;
                this.headIndex = 0;
                this.queuedFrames = 0;
                return;
            }
            const chunk = event.data; // interleaved Float32Array
            this.queue.push(chunk);
            this.queuedFrames += chunk.length / this.channels;
            // Cap latency: drop the oldest chunks if we get too far ahead.
            while (this.queuedFrames > this.maxFrames && this.queue.length > 1) {
                this.queuedFrames -= this.queue.shift().length / this.channels;
            }
        };
    }

    process(_inputs, outputs) {
        const output = outputs[0];
        const channelCount = output.length;
        const frameCount = output[0].length;

        for (let i = 0; i < frameCount; i++) {
            if (this.head === null || this.headIndex >= this.head.length) {
                this.head = this.queue.shift() ?? null;
                this.headIndex = 0;
            }
            if (this.head === null) {
                for (let c = 0; c < channelCount; c++)
                    output[c][i] = 0;
                continue;
            }
            for (let c = 0; c < channelCount; c++)
                output[c][i] = this.head[this.headIndex + c] ?? 0;
            this.headIndex += this.channels;
            this.queuedFrames = Math.max(0, this.queuedFrames - 1);
        }

        return true;
    }
}

registerProcessor('pcm-player', PcmPlayerProcessor);
