using System.Buffers.Binary;

namespace PatchCast.Protocol;

public enum AudioChannel : byte
{
    SystemAudio = 1,
    Microphone = 2
}

public sealed record AudioPacket(AudioChannel Channel, byte[] WaveFormat, byte[] Data);

public static class AudioProtocol
{
    private const uint Magic = 0x31544350; // "PCT1" in little-endian order
    private const byte Version = 1;
    private const int HeaderSize = 12;
    private const int MaximumFormatSize = 256;
    private const int MaximumPayloadSize = 1024 * 1024;

    public static async ValueTask WriteAsync(Stream stream, AudioPacket packet, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(Serialize(packet), cancellationToken);
    }

    // Produces the complete on-the-wire bytes for a packet (header + format + data)
    // as a single buffer. Used for stream writes and for message-oriented transports
    // such as WebSockets, where one packet is sent as one binary message.
    public static byte[] Serialize(AudioPacket packet)
    {
        if (packet.WaveFormat.Length is < 1 or > MaximumFormatSize)
            throw new InvalidDataException("Audio format is invalid.");
        if (packet.Data.Length > MaximumPayloadSize)
            throw new InvalidDataException("Audio packet is too large.");

        var buffer = new byte[HeaderSize + packet.WaveFormat.Length + packet.Data.Length];
        var span = buffer.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span[..4], Magic);
        span[4] = Version;
        span[5] = (byte)packet.Channel;
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(6, 2), (ushort)packet.WaveFormat.Length);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(8, 4), packet.Data.Length);
        packet.WaveFormat.CopyTo(span.Slice(HeaderSize, packet.WaveFormat.Length));
        packet.Data.CopyTo(span[(HeaderSize + packet.WaveFormat.Length)..]);
        return buffer;
    }

    public static async ValueTask<AudioPacket> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[HeaderSize];
        await stream.ReadExactlyAsync(header, cancellationToken);

        if (BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0, 4)) != Magic || header[4] != Version)
            throw new InvalidDataException("The server sent an unsupported PatchCast stream.");

        var channel = (AudioChannel)header[5];
        if (channel is not AudioChannel.SystemAudio and not AudioChannel.Microphone)
            throw new InvalidDataException("The server sent an unknown audio channel.");

        var formatLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6, 2));
        if (formatLength is < 1 or > MaximumFormatSize)
            throw new InvalidDataException("The server sent an invalid audio format.");

        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8, 4));
        if (payloadLength is < 0 or > MaximumPayloadSize)
            throw new InvalidDataException("The server sent an invalid audio packet size.");

        var waveFormat = new byte[formatLength];
        await stream.ReadExactlyAsync(waveFormat, cancellationToken);
        var data = new byte[payloadLength];
        await stream.ReadExactlyAsync(data, cancellationToken);
        return new AudioPacket(channel, waveFormat, data);
    }
}
