using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PatchCast.Client;

internal sealed class ClientSettings
{
    public string? LastHost { get; set; }
    public List<HostProfile> Hosts { get; set; } = [];

    public HostProfile? FindHost(string host) => Hosts.FirstOrDefault(
        profile => string.Equals(profile.Host, host.Trim(), StringComparison.OrdinalIgnoreCase));

    public HostProfile GetOrCreateHost(string host)
    {
        host = host.Trim();
        var profile = FindHost(host);
        if (profile is not null)
        {
            profile.Host = host;
            return profile;
        }

        profile = new HostProfile { Host = host };
        Hosts.Add(profile);
        return profile;
    }

    public bool RemoveHost(string host)
    {
        var profile = FindHost(host);
        return profile is not null && Hosts.Remove(profile);
    }
}

internal sealed class HostProfile
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 4747;
    public string? ProtectedPassword { get; set; }
    public int SystemVolume { get; set; } = 100;
    public bool MuteSystemAudio { get; set; }
    public int MicrophoneVolume { get; set; } = 100;
    public bool MuteMicrophone { get; set; }
    public string? CertificatePin { get; set; }
}

internal static class ClientSettingsStore
{
    private static readonly byte[] PasswordEntropy = Encoding.UTF8.GetBytes("PatchCast.Client.Password.v1");
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PatchCast");
    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "client-settings.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static ClientSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var settings = JsonSerializer.Deserialize<ClientSettings>(File.ReadAllText(SettingsPath)) ?? new ClientSettings();
                NormalizeAndMigrate(settings);
                return settings;
            }
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
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporaryPath, SettingsPath, overwrite: true);
    }

    public static string? LoadPassword(HostProfile profile)
    {
        if (string.IsNullOrEmpty(profile.ProtectedPassword))
            return null;

        try
        {
            var encrypted = Convert.FromBase64String(profile.ProtectedPassword);
            var clearBytes = ProtectedData.Unprotect(encrypted, PasswordEntropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(clearBytes);
        }
        catch
        {
            return null;
        }
    }

    public static void SetPassword(HostProfile profile, string password, bool savePassword)
    {
        profile.ProtectedPassword = savePassword
            ? Convert.ToBase64String(ProtectedData.Protect(
                Encoding.UTF8.GetBytes(password),
                PasswordEntropy,
                DataProtectionScope.CurrentUser))
            : null;
    }

    private static void NormalizeAndMigrate(ClientSettings settings)
    {
        settings.Hosts ??= [];
        settings.Hosts = settings.Hosts
            .Where(profile => !string.IsNullOrWhiteSpace(profile.Host))
            .GroupBy(profile => profile.Host.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        foreach (var profile in settings.Hosts)
        {
            profile.Host = profile.Host.Trim();
            profile.Port = Math.Clamp(profile.Port, 1, 65535);
            profile.SystemVolume = Math.Clamp(profile.SystemVolume, 0, 100);
            profile.MicrophoneVolume = Math.Clamp(profile.MicrophoneVolume, 0, 100);
        }

        settings.LastHost = settings.FindHost(settings.LastHost ?? string.Empty)?.Host
            ?? settings.Hosts.FirstOrDefault()?.Host;
    }
}
