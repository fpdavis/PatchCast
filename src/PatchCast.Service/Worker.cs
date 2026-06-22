using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;
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
                _ = HandleClientSafelyAsync(client, stoppingToken);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task HandleClientSafelyAsync(TcpClient client, CancellationToken serviceToken)
    {
        try
        {
            await HandleClientAsync(client, serviceToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unhandled failure while processing client {Endpoint}.", client.Client.RemoteEndPoint);
            client.Dispose();
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken serviceToken)
    {
        var endpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        logger.LogInformation("Client {Endpoint} connected.", endpoint);

        using (client)
        using (var secureStream = new SslStream(client.GetStream(), leaveInnerStreamOpen: false))
        {
            var session = new AudioSession(logger);
            await session.RunAsync(
                endpoint,
                async cancellationToken =>
                {
                    await secureStream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = certificateProvider.GetCertificate(),
                        ClientCertificateRequired = false,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                    }, cancellationToken);

                    return await PasswordProtocol.AuthenticateServerAsync(secureStream, options.Value.Password, cancellationToken);
                },
                (packet, cancellationToken) => AudioProtocol.WriteAsync(secureStream, packet, cancellationToken),
                serviceToken);
        }
    }
}
