using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using PatchCast.Protocol;
using System.Runtime.InteropServices;

namespace PatchCast.Client;

internal sealed class AudioStreamPlayer : IDisposable
{
    private WasapiOut? output;
    private BufferedWaveProvider? buffer;
    private MediaFoundationResampler? resampler;
    private MMDevice? outputDevice;
    private MeteringSampleProvider? meter;
    private VolumeSampleProvider? streamVolume;
    private byte[]? currentFormat;
    private float volume = 1f;
    private bool muted;

    // An envelope follower over the incoming peak. It is advanced in the audio
    // domain (one step per metering block, decaying by audio time, not UI time),
    // so a steady signal yields a steady value even though the output device
    // pulls audio in irregular bursts. The UI reads it without resetting it.
    private float envelope;
    private float envelopeDecayPerBlock = 1f;

    public void Add(AudioPacket packet)
    {
        if (currentFormat is null || !currentFormat.AsSpan().SequenceEqual(packet.WaveFormat))
            Reset(packet.WaveFormat);
        buffer!.AddSamples(packet.Data, 0, packet.Data.Length);
    }

    public void SetVolume(float value)
    {
        volume = Math.Clamp(value, 0f, 1f);
        ApplyVolume();
    }

    public void SetMuted(bool value)
    {
        muted = value;
        ApplyVolume();
    }

    // Returns the current incoming-level envelope (0..1), measured before the
    // volume slider and mute are applied. Reading does not reset it, so it stays
    // steady between the output device's bursty reads instead of dropping to zero.
    public float ReadIncomingPeak() => envelope;

    public void Stop()
    {
        output?.Stop();
        output?.Dispose();
        resampler?.Dispose();
        outputDevice?.Dispose();
        output = null;
        resampler = null;
        outputDevice = null;
        meter = null;
        streamVolume = null;
        buffer = null;
        currentFormat = null;
        envelope = 0f;
    }

    private void Reset(byte[] serializedFormat)
    {
        Stop();
        if (serializedFormat.Length < sizeof(int))
            throw new InvalidDataException("The server sent an incomplete audio format.");
        var formatChunkLength = BitConverter.ToInt32(serializedFormat, 0);
        if (formatChunkLength <= 0 || formatChunkLength != serializedFormat.Length - sizeof(int))
            throw new InvalidDataException("The server sent an invalid audio format chunk.");
        var formatPointer = Marshal.AllocHGlobal(formatChunkLength);
        WaveFormat wireFormat;
        try
        {
            Marshal.Copy(serializedFormat, sizeof(int), formatPointer, formatChunkLength);
            wireFormat = WaveFormat.MarshalFromPtr(formatPointer);
        }
        finally
        {
            Marshal.FreeHGlobal(formatPointer);
        }
        var playbackFormat = wireFormat is WaveFormatExtensible extensible
            ? extensible.ToStandardWaveFormat()
            : wireFormat;
        buffer = new BufferedWaveProvider(playbackFormat)
        {
            BufferDuration = TimeSpan.FromSeconds(2),
            DiscardOnBufferOverflow = true
        };
        using var deviceEnumerator = new MMDeviceEnumerator();
        outputDevice = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var outputFormat = outputDevice.AudioClient.MixFormat;

        // Meter the incoming audio before any gain or mute is applied so the UI
        // can show the true level the server is sending. Use short ~10 ms blocks
        // and decay the envelope per block toward a ~250 ms release time constant.
        var framesPerBlock = Math.Max(1, playbackFormat.SampleRate / 100);
        var blockSeconds = framesPerBlock / (double)playbackFormat.SampleRate;
        envelopeDecayPerBlock = (float)Math.Exp(-blockSeconds / 0.25);
        meter = new MeteringSampleProvider(buffer.ToSampleProvider(), framesPerBlock);
        meter.StreamVolume += OnStreamVolume;

        // Apply gain in software to this stream only. WasapiOut.Volume controls
        // the client's endpoint volume and must not be used for PatchCast sliders.
        streamVolume = new VolumeSampleProvider(meter);
        ApplyVolume();
        var volumeWaveProvider = new SampleToWaveProvider(streamVolume);

        // Convert the server's capture format to the exact mix format accepted by
        // the client's default output device. This handles different sample rates,
        // channel counts, and WAVE_FORMAT_EXTENSIBLE layouts between computers.
        resampler = new MediaFoundationResampler(volumeWaveProvider, outputFormat)
        {
            ResamplerQuality = 60
        };
        output = new WasapiOut(outputDevice, AudioClientShareMode.Shared, useEventSync: false, latency: 150);
        output.Init(resampler);
        ApplyVolume();
        output.Play();
        currentFormat = serializedFormat.ToArray();
    }

    private void OnStreamVolume(object? sender, StreamVolumeEventArgs e)
    {
        var peak = 0f;
        foreach (var channelPeak in e.MaxSampleValues)
            if (channelPeak > peak)
                peak = channelPeak;

        // Rise instantly to a louder block; otherwise decay smoothly. Because
        // each block represents a fixed slice of audio, a burst of blocks decays
        // by the right amount of audio time at once, keeping the value steady.
        var decayed = envelope * envelopeDecayPerBlock;
        envelope = peak > decayed ? peak : decayed;
    }

    private void ApplyVolume()
    {
        if (streamVolume is not null)
            streamVolume.Volume = muted ? 0f : volume;
    }

    public void Dispose() => Stop();
}
