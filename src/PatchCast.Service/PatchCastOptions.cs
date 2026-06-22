namespace PatchCast.Service;

public sealed class PatchCastOptions
{
    public const string SectionName = "PatchCast";
    public int Port { get; set; } = 4747;

    // HTTPS / secure-WebSocket port for the browser client. Must differ from Port.
    public int WebPort { get; set; } = 4748;
    public string Password { get; set; } = string.Empty;
}
