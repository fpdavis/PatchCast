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

    public static async Task HandleAsync(HttpContext context, PatchCastOptions options, ILogger logger)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var endpoint = $"ws:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
        using var webSocket = await context.WebSockets.AcceptWebSocketAsync();

        var session = new AudioSession(logger);
        await session.RunAsync(
            endpoint,
            cancellationToken => AuthenticateAsync(webSocket, options.Password, cancellationToken),
            async (packet, cancellationToken) =>
                await webSocket.SendAsync(AudioProtocol.Serialize(packet), WebSocketMessageType.Binary, endOfMessage: true, cancellationToken),
            context.RequestAborted);

        if (webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Session ended", CancellationToken.None);
            }
            catch
            {
                // The client may already be gone; nothing more to do.
            }
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
