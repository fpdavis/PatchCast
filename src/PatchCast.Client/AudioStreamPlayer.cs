using NAudio.Wave;
using PatchCast.Protocol;

namespace PatchCast.Client;

internal sealed class AudioStreamPlayer : IDisposable
{
    private WaveOutEvent? output;
    private BufferedWaveProvider? buffer;
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
        output = null;
        buffer = null;
        currentFormat = null;
    }

    private void Reset(byte[] serializedFormat)
    {
        Stop();
        using var stream = new MemoryStream(serializedFormat, writable: false);
        using var reader = new BinaryReader(stream);
        var waveFormat = WaveFormat.FromFormatChunk(reader, serializedFormat.Length);
        buffer = new BufferedWaveProvider(waveFormat)
        {
            BufferDuration = TimeSpan.FromSeconds(2),
            DiscardOnBufferOverflow = true
        };
        output = new WaveOutEvent { DesiredLatency = 150 };
        output.Init(buffer);
        ApplyVolume();
        output.Play();
        currentFormat = serializedFormat.ToArray();
    }

    private void ApplyVolume()
    {
        if (output is not null)
            output.Volume = muted ? 0f : volume;
    }

    public void Dispose() => Stop();
}
