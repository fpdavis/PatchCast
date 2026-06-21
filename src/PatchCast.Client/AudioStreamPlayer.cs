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
    private VolumeSampleProvider? streamVolume;
    private byte[]? currentFormat;
    private float volume = 1f;
    private bool muted;

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

    public void Stop()
    {
        output?.Stop();
        output?.Dispose();
        resampler?.Dispose();
        outputDevice?.Dispose();
        output = null;
        resampler = null;
        outputDevice = null;
        streamVolume = null;
        buffer = null;
        currentFormat = null;
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

        // Apply gain in software to this stream only. WasapiOut.Volume controls
        // the client's endpoint volume and must not be used for PatchCast sliders.
        streamVolume = new VolumeSampleProvider(buffer.ToSampleProvider());
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

    private void ApplyVolume()
    {
        if (streamVolume is not null)
            streamVolume.Volume = muted ? 0f : volume;
    }

    public void Dispose() => Stop();
}
