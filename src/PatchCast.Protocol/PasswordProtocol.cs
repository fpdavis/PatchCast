using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace PatchCast.Protocol;

public static class PasswordProtocol
{
    private const uint Magic = 0x31414350; // "PCA1" in little-endian order
    private const int MaximumPasswordBytes = 512;

    public static async ValueTask<bool> AuthenticateServerAsync(
        Stream stream,
        string expectedPassword,
        CancellationToken cancellationToken)
    {
        var header = new byte[6];
        await stream.ReadExactlyAsync(header, cancellationToken);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0, 4)) != Magic)
            throw new InvalidDataException("Invalid PatchCast authentication request.");

        var length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4, 2));
        if (length > MaximumPasswordBytes)
            throw new InvalidDataException("Password is too long.");

        var suppliedBytes = new byte[length];
        await stream.ReadExactlyAsync(suppliedBytes, cancellationToken);
        var expectedBytes = Encoding.UTF8.GetBytes(expectedPassword);
        var suppliedHash = SHA256.HashData(suppliedBytes);
        var expectedHash = SHA256.HashData(expectedBytes);
        var authenticated = CryptographicOperations.FixedTimeEquals(suppliedHash, expectedHash);

        await stream.WriteAsync(new[] { authenticated ? (byte)1 : (byte)0 }, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        return authenticated;
    }

    public static async ValueTask<bool> AuthenticateClientAsync(
        Stream stream,
        string password,
        CancellationToken cancellationToken)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        if (passwordBytes.Length > MaximumPasswordBytes)
            throw new ArgumentException("Password must be 512 UTF-8 bytes or fewer.", nameof(password));

        var request = new byte[6 + passwordBytes.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(0, 4), Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(4, 2), (ushort)passwordBytes.Length);
        passwordBytes.CopyTo(request.AsSpan(6));
        await stream.WriteAsync(request, cancellationToken);
        await stream.FlushAsync(cancellationToken);

        var response = new byte[1];
        await stream.ReadExactlyAsync(response, cancellationToken);
        return response[0] == 1;
    }
}
