using System.Text.Json.Serialization;

namespace SteamVault.Models;

/// <summary>
/// SDA maFile JSON shape (compatible with Jessecar96/SteamDesktopAuthenticator).
/// </summary>
public sealed class MaFile
{
    /// <summary>Local source path, populated by the importer and never serialized into the maFile JSON.</summary>
    [JsonIgnore]
    public string? Path { get; set; }

    [JsonPropertyName("shared_secret")]
    public string? SharedSecret { get; set; }

    [JsonPropertyName("identity_secret")]
    public string? IdentitySecret { get; set; }

    [JsonPropertyName("account_name")]
    public string? AccountName { get; set; }

    [JsonPropertyName("device_id")]
    public string? DeviceId { get; set; }

    [JsonPropertyName("Session")]
    public MaFileSession? Session { get; set; }

    [JsonPropertyName("steamid")]
    public string? SteamId { get; set; }
}

public sealed class MaFileSession
{
    [JsonPropertyName("SteamID")]
    public ulong SteamId { get; set; }

    [JsonPropertyName("SteamLogin")]
    public string? SteamLogin { get; set; }
}
