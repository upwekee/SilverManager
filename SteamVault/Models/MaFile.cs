using System.Text.Json;
using System.Text.Json.Serialization;

namespace SteamVault.Models;

/// <summary>
/// Flexible JSON converter for strings that converts JSON numbers, booleans, and strings safely to string?.
/// Prevents JsonException when steamid, device_id, or account_name are numbers in .maFile JSON.
/// </summary>
public sealed class FlexibleStringConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return reader.GetString();
            case JsonTokenType.Number:
                if (reader.TryGetInt64(out var lVal))
                    return lVal.ToString();
                if (reader.TryGetDouble(out var dVal))
                    return dVal.ToString(System.Globalization.CultureInfo.InvariantCulture);
                using (var doc = JsonDocument.ParseValue(ref reader))
                    return doc.RootElement.GetRawText();
            case JsonTokenType.True:
                return "true";
            case JsonTokenType.False:
                return "false";
            case JsonTokenType.Null:
            case JsonTokenType.None:
                return null;
            default:
                using (var doc = JsonDocument.ParseValue(ref reader))
                    return doc.RootElement.GetRawText();
        }
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value == null)
            writer.WriteNullValue();
        else
            writer.WriteStringValue(value);
    }
}

/// <summary>
/// Flexible JSON converter for ulong that converts JSON strings or numbers safely to ulong.
/// </summary>
public sealed class FlexibleUlongConverter : JsonConverter<ulong>
{
    public override ulong Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                return reader.GetUInt64();
            case JsonTokenType.String:
                var str = reader.GetString();
                return ulong.TryParse(str, out var val) ? val : 0;
            default:
                return 0;
        }
    }

    public override void Write(Utf8JsonWriter writer, ulong value, JsonSerializerOptions options)
    {
        writer.WriteNumberValue(value);
    }
}

/// <summary>
/// SDA maFile JSON shape (compatible with Jessecar96/SteamDesktopAuthenticator and all bot formats).
/// </summary>
public sealed class MaFile
{
    /// <summary>Local source path, populated by the importer and never serialized into the maFile JSON.</summary>
    [JsonIgnore]
    public string? Path { get; set; }

    [JsonPropertyName("shared_secret")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? SharedSecret { get; set; }

    [JsonPropertyName("identity_secret")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? IdentitySecret { get; set; }

    [JsonPropertyName("account_name")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? AccountName { get; set; }

    [JsonPropertyName("device_id")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? DeviceId { get; set; }

    [JsonPropertyName("Session")]
    public MaFileSession? Session { get; set; }

    [JsonPropertyName("steamid")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? SteamId { get; set; }

    public static MaFile? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        var raw = json.TrimStart('\uFEFF').Trim();
        var opts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters =
            {
                new FlexibleStringConverter(),
                new FlexibleUlongConverter()
            }
        };
        return JsonSerializer.Deserialize<MaFile>(raw, opts);
    }
}

public sealed class MaFileSession
{
    [JsonPropertyName("SteamID")]
    [JsonConverter(typeof(FlexibleUlongConverter))]
    public ulong SteamId { get; set; }

    [JsonPropertyName("SteamLogin")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? SteamLogin { get; set; }
}
