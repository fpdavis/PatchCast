using System.Net.Sockets;
using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using PatchCast.Protocol;

namespace PatchCast.Service;

// Authenticates a client, then (only once authenticated) captures system audio
// (loopback) and the microphone and pumps audio packets to a transport-supplied
// sink until the client leaves. Shared by the TCP listener (Worker) and the
// WebSocket endpoint so the authentication, capture, and pump logic lives in
// exactly one place.
internal sealed class AudioSession(ILogger logger, PasswordCooldown cooldown)
{
    public async Task RunAsync(
        string endpoint,
        Func<CancellationToken, Task<bool>> authenticateAsync,
        Func<AudioPacket, CancellationToken, ValueTask> sendAsync,
        CancellationToken cancellationToken)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            // Throttle every attempt by the current brute-force cooldown before the
            // password is evaluated; the wait escalates with each prior failure.
            await cooldown.WaitAsync(linkedCancellation.Token);
            if (!await authenticateAsync(linkedCancellation.Token))
            {
                cooldown.RecordFailure();
                logger.LogWarning(
                    "Client {Endpoint} supplied an invalid password. Next attempt is throttled by {Seconds}s.",
                    endpoint, (int)cooldown.CurrentDelay.TotalSeconds);
                return;
            }

            cooldown.RecordSuccess();

            // Audio devices are opened only after a client authenticates, so an idle
            // server, an unauthenticated connection, or a port probe does no audio work.
            await CaptureAndStreamAsync(sendAsync, linkedCancellation);
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsExpectedClientDisconnect(exception))
        {
            logger.LogInformation("Client {Endpoint} closed the connection.", endpoint);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Client {Endpoint} disconnected or audio capture failed.", endpoint);
        }
        finally
        {
            logger.LogInformation("Client {Endpoint} disconnected.", endpoint);
        }
    }

    private async Task CaptureAndStreamAsync(
        Func<AudioPacket, CancellationToken, ValueTask> sendAsync,
        CancellationTokenSource linkedCancellation)
    {
        var packets = Channel.CreateBounded<AudioPacket>(new BoundedChannelOptions(128)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        using var loopback = new WasapiLoopbackCapture();
        using var microphone = new WasapiCapture();

        var stoppingCaptures = 0;
        var loopbackStarted = false;
        var microphoneStarted = false;
        var loopbackFormat = SerializeWaveFormat(loopback.WaveFormat);
        var microphoneFormat = SerializeWaveFormat(microphone.WaveFormat);

        void Queue(AudioChannel channel, byte[] format, WaveInEventArgs args)
        {
            var data = args.Buffer.AsSpan(0, args.BytesRecorded).ToArray();
            packets.Writer.TryWrite(new AudioPacket(channel, format, data));
        }

        void LoopbackDataAvailable(object? _, WaveInEventArgs args) =>
            Queue(AudioChannel.SystemAudio, loopbackFormat, args);

        void MicrophoneDataAvailable(object? _, WaveInEventArgs args) =>
            Queue(AudioChannel.Microphone, microphoneFormat, args);

        void CaptureStopped(string source, Exception? exception)
        {
            if (Volatile.Read(ref stoppingCaptures) != 0)
                return;
            if (exception is not null)
                logger.LogError(exception, "Capture of {Source} stopped unexpectedly.", source);
            try
            {
                linkedCancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // A late device callback can race with client teardown.
            }
        }

        void LoopbackStopped(object? _, StoppedEventArgs args) =>
            CaptureStopped("system audio", args.Exception);

        void MicrophoneStopped(object? _, StoppedEventArgs args) =>
            CaptureStopped("microphone", args.Exception);

        loopback.DataAvailable += LoopbackDataAvailable;
        microphone.DataAvailable += MicrophoneDataAvailable;
        loopback.RecordingStopped += LoopbackStopped;
        microphone.RecordingStopped += MicrophoneStopped;

        try
        {
            loopback.StartRecording();
            loopbackStarted = true;
            microphone.StartRecording();
            microphoneStarted = true;

            await foreach (var packet in packets.Reader.ReadAllAsync(linkedCancellation.Token))
                await sendAsync(packet, linkedCancellation.Token);
        }
        finally
        {
            Interlocked.Exchange(ref stoppingCaptures, 1);
            loopback.DataAvailable -= LoopbackDataAvailable;
            microphone.DataAvailable -= MicrophoneDataAvailable;
            loopback.RecordingStopped -= LoopbackStopped;
            microphone.RecordingStopped -= MicrophoneStopped;
            if (loopbackStarted)
                loopback.StopRecording();
            if (microphoneStarted)
                microphone.StopRecording();
            packets.Writer.TryComplete();
        }
    }

    public static bool IsExpectedClientDisconnect(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is IOException or System.Net.WebSockets.WebSocketException)
                return true;

            if (current is SocketException socketException && socketException.SocketErrorCode is
                SocketError.ConnectionAborted or
                SocketError.ConnectionReset or
                SocketError.OperationAborted or
                SocketError.Shutdown or
                SocketError.NotConnected)
            {
                return true;
            }
        }

        return false;
    }

    private static byte[] SerializeWaveFormat(WaveFormat format)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        format.Serialize(writer);
        return stream.ToArray();
    }
}
