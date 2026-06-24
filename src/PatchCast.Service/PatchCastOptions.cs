namespace PatchCast.Service;

public sealed class PatchCastOptions
{
    public const string SectionName = "PatchCast";
    public int Port { get; set; } = 4747;

    // HTTPS / secure-WebSocket port for the browser client. Must differ from Port.
    public int WebPort { get; set; } = 4748;
    public string Password { get; set; } = string.Empty;

    // Maximum brute-force cooldown, in seconds. After each failed password attempt
    // the required wait doubles (0, 1, 2, 4, ...) up to this cap and stays there
    // until a successful login or a service restart. Set to 0 to disable.
    public int MaxPasswordCooldownSeconds { get; set; } = 256;
}
