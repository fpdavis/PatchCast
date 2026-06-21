using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PatchCast.Client;

internal sealed class ClientSettings
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 4747;
    public string? ProtectedPassword { get; set; }
    public Dictionary<string, string> CertificatePins { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal static class ClientSettingsStore
{
    private static readonly byte[] PasswordEntropy = Encoding.UTF8.GetBytes("PatchCast.Client.Password.v1");
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PatchCast");
    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "client-settings.json");

    public static ClientSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<ClientSettings>(File.ReadAllText(SettingsPath)) ?? new ClientSettings();
        }
        catch
        {
            // Invalid or inaccessible settings should not prevent the client from starting.
        }
        return new ClientSettings();
    }

    public static void Save(ClientSettings settings)
    {
        Directory.CreateDirectory(SettingsDirectory);
        var temporaryPath = SettingsPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryPath, SettingsPath, overwrite: true);
    }

    public static string? LoadPassword(ClientSettings settings)
    {
        if (string.IsNullOrEmpty(settings.ProtectedPassword))
            return null;

        try
        {
            var encrypted = Convert.FromBase64String(settings.ProtectedPassword);
            var clearBytes = ProtectedData.Unprotect(encrypted, PasswordEntropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(clearBytes);
        }
        catch
        {
            return null;
        }
    }

    public static void SetPassword(ClientSettings settings, string password, bool savePassword)
    {
        settings.ProtectedPassword = savePassword
            ? Convert.ToBase64String(ProtectedData.Protect(
                Encoding.UTF8.GetBytes(password),
                PasswordEntropy,
                DataProtectionScope.CurrentUser))
            : null;
    }
}
