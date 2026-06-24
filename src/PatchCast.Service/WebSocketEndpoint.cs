using System.Net.WebSockets;
using PatchCast.Protocol;

namespace PatchCast.Service;

// Handles the browser-facing "/ws" endpoint. TLS is already terminated by Kestrel,
// so this mirrors the TCP path from the password handshake onward: it authenticates,
// then streams each AudioPacket as a single binary WebSocket message (preserving
// message boundaries for the browser) using the shared AudioSession.
internal static class WebSocketEndpoint
{
    private const int MaxAuthMessageBytes = 6 + 512;

    public static async Task HandleAsync(HttpContext context, PatchCastOptions options, PasswordCooldown cooldown, ILogger logger)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var endpoint = $"ws:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
        using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
        using var clientGone = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var watchTask = Task.CompletedTask;

        var session = new AudioSession(logger, cooldown);
        await session.RunAsync(
            endpoint,
            async cancellationToken =>
            {
                var accepted = await AuthenticateAsync(webSocket, options.Password, cancellationToken);
                // Once authenticated the only inbound traffic is the client's Close
                // frame; watch for it so the stream stops as soon as the client
                // disconnects instead of running until the browser times out.
                if (accepted)
                    watchTask = WatchForCloseAsync(webSocket, clientGone);
                return accepted;
            },
            async (packet, cancellationToken) =>
                await webSocket.SendAsync(AudioProtocol.Serialize(packet), WebSocketMessageType.Binary, endOfMessage: true, cancellationToken),
            clientGone.Token);

        clientGone.Cancel();
        try { await watchTask; } catch { /* watch loop ended with the connection */ }

        if (webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await webSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Session ended", closeTimeout.Token);
            }
            catch
            {
                // The client may already be gone; nothing more to do.
            }
        }
    }

    // Reads (and discards) inbound frames until the client closes or the connection
    // drops, then cancels so the audio pump stops promptly.
    private static async Task WatchForCloseAsync(WebSocket webSocket, CancellationTokenSource clientGone)
    {
        var buffer = new ArraySegment<byte>(new byte[256]);
        try
        {
            while (!clientGone.IsCancellationRequested && webSocket.State == WebSocketState.Open)
            {
                var result = await webSocket.ReceiveAsync(buffer, clientGone.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
            }
        }
        catch
        {
            // A receive failure means the client is gone.
        }
        finally
        {
            if (!clientGone.IsCancellationRequested)
                clientGone.Cancel();
        }
    }

    private static async Task<bool> AuthenticateAsync(WebSocket webSocket, string expectedPassword, CancellationToken cancellationToken)
    {
        var request = await ReceiveMessageAsync(webSocket, MaxAuthMessageBytes, cancellationToken);
        var accepted = request is not null
            && PasswordProtocol.TryParseRequest(request, out var passwordBytes)
            && PasswordProtocol.Verify(passwordBytes, expectedPassword);

        await webSocket.SendAsync(new[] { accepted ? (byte)1 : (byte)0 }, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken);
        return accepted;
    }

    private static async Task<byte[]?> ReceiveMessageAsync(WebSocket webSocket, int maxBytes, CancellationToken cancellationToken)
    {
        var buffer = new ArraySegment<byte>(new byte[4096]);
        using var message = new MemoryStream();
        while (true)
        {
            var result = await webSocket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;
            message.Write(buffer.Array!, 0, result.Count);
            if (message.Length > maxBytes)
                return null;
            if (result.EndOfMessage)
                return message.ToArray();
        }
    }
}
