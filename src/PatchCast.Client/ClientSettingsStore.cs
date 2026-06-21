using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PatchCast.Client;

internal sealed class ClientSettings
{
    public string? LastHost { get; set; }
    public List<HostProfile> Hosts { get; set; } = [];

    // Version-one properties are read only to migrate existing installations.
    [JsonPropertyName("Host")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyHost { get; set; }

    [JsonPropertyName("Port")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LegacyPort { get; set; }

    [JsonPropertyName("ProtectedPassword")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyProtectedPassword { get; set; }

    [JsonPropertyName("CertificatePins")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? LegacyCertificatePins { get; set; }

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
        settings.LegacyHost = null;
        settings.LegacyPort = null;
        settings.LegacyProtectedPassword = null;
        settings.LegacyCertificatePins = null;
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

        if (!string.IsNullOrWhiteSpace(settings.LegacyHost))
        {
            var profile = settings.GetOrCreateHost(settings.LegacyHost);
            profile.Port = Math.Clamp(settings.LegacyPort ?? 4747, 1, 65535);
            profile.ProtectedPassword ??= settings.LegacyProtectedPassword;
            settings.LastHost ??= profile.Host;
        }

        if (settings.LegacyCertificatePins is not null)
        {
            foreach (var (key, pin) in settings.LegacyCertificatePins)
            {
                if (!TrySplitPinKey(key, out var host, out var port))
                    continue;
                var profile = settings.GetOrCreateHost(host);
                profile.Port = port;
                profile.CertificatePin ??= pin;
            }
        }

        settings.LastHost = settings.FindHost(settings.LastHost ?? string.Empty)?.Host
            ?? settings.Hosts.FirstOrDefault()?.Host;
    }

    private static bool TrySplitPinKey(string key, out string host, out int port)
    {
        host = string.Empty;
        port = 4747;
        var separator = key.LastIndexOf(':');
        if (separator <= 0 || !int.TryParse(key[(separator + 1)..], out port) || port is < 1 or > 65535)
            return false;
        host = key[..separator];
        return !string.IsNullOrWhiteSpace(host);
    }
}
