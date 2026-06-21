namespace PatchCast.Service;

public sealed class PatchCastOptions
{
    public const string SectionName = "PatchCast";
    public int Port { get; set; } = 4747;
    public string Password { get; set; } = string.Empty;
}
