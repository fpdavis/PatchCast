using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using PatchCast.Protocol;

namespace PatchCast.Service;

public sealed class Worker(
    ILogger<Worker> logger,
    IOptions<PatchCastOptions> options,
    ServerCertificateProvider certificateProvider) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var port = options.Value.Port;
        if (port is < 1 or > 65535)
            throw new InvalidOperationException("PatchCast:Port must be between 1 and 65535.");
        if (string.IsNullOrWhiteSpace(options.Value.Password))
            throw new InvalidOperationException("PatchCast:Password must be configured and cannot be blank.");

        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        logger.LogInformation("PatchCast is listening on TCP port {Port}.", port);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stoppingToken);
                _ = HandleClientAsync(client, stoppingToken);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken serviceToken)
    {
        var endpoint = client.Client.RemoteEndPoint;
        logger.LogInformation("Client {Endpoint} connected.", endpoint);

        var packets = Channel.CreateBounded<AudioPacket>(new BoundedChannelOptions(128)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        using (client)
        using (var secureStream = new SslStream(client.GetStream(), leaveInnerStreamOpen: false))
        using (var loopback = new WasapiLoopbackCapture())
        using (var microphone = new WasapiCapture())
        using (var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(serviceToken))
        {
            var loopbackFormat = SerializeWaveFormat(loopback.WaveFormat);
            var microphoneFormat = SerializeWaveFormat(microphone.WaveFormat);

            void Queue(AudioChannel channel, byte[] format, WaveInEventArgs args)
            {
                var data = args.Buffer.AsSpan(0, args.BytesRecorded).ToArray();
                packets.Writer.TryWrite(new AudioPacket(channel, format, data));
            }

            loopback.DataAvailable += (_, args) => Queue(AudioChannel.SystemAudio, loopbackFormat, args);
            microphone.DataAvailable += (_, args) => Queue(AudioChannel.Microphone, microphoneFormat, args);
            loopback.RecordingStopped += (_, args) => HandleCaptureStopped("system audio", args.Exception, linkedCancellation);
            microphone.RecordingStopped += (_, args) => HandleCaptureStopped("microphone", args.Exception, linkedCancellation);

            try
            {
                await secureStream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificateProvider.GetCertificate(),
                    ClientCertificateRequired = false,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck
                }, linkedCancellation.Token);

                if (!await PasswordProtocol.AuthenticateServerAsync(
                        secureStream,
                        options.Value.Password,
                        linkedCancellation.Token))
                {
                    logger.LogWarning("Client {Endpoint} supplied an invalid password.", endpoint);
                    await Task.Delay(TimeSpan.FromSeconds(1), linkedCancellation.Token);
                    return;
                }

                loopback.StartRecording();
                microphone.StartRecording();

                await foreach (var packet in packets.Reader.ReadAllAsync(linkedCancellation.Token))
                    await AudioProtocol.WriteAsync(secureStream, packet, linkedCancellation.Token);
            }
            catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Client {Endpoint} disconnected or audio capture failed.", endpoint);
            }
            finally
            {
                loopback.StopRecording();
                microphone.StopRecording();
                packets.Writer.TryComplete();
                logger.LogInformation("Client {Endpoint} disconnected.", endpoint);
            }
        }
    }

    private void HandleCaptureStopped(string source, Exception? exception, CancellationTokenSource cancellation)
    {
        if (exception is not null)
            logger.LogError(exception, "Capture of {Source} stopped unexpectedly.", source);
        cancellation.Cancel();
    }

    private static byte[] SerializeWaveFormat(WaveFormat format)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        format.Serialize(writer);
        return stream.ToArray();
    }
}
