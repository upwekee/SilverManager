using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SteamVault.Services;

/// <summary>
/// Steam Guard TOTP + mobile confirmation hashes.
/// Logic adapted from geel9/SteamAuth (open source).
/// </summary>
public static class SteamTotp
{
    private static readonly byte[] CodeTranslations =
    [
        50, 51, 52, 53, 54, 55, 56, 57, 66, 67, 68, 70, 71, 72, 74, 75, 77, 78, 80, 81, 82, 84, 86, 87, 88, 89
    ];

    private static long? _serverTimeOffset;
    private static DateTime _offsetFetchedAt = DateTime.MinValue;

    public static async Task AlignTimeAsync(CancellationToken ct = default)
    {
        if (_serverTimeOffset.HasValue && DateTime.UtcNow - _offsetFetchedAt < TimeSpan.FromHours(1))
            return;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            var json = await http.GetStringAsync("https://api.steampowered.com/ITwoFactorService/QueryTime/v1/", ct);
            // {"response":{"server_time":"..."}}
            var idx = json.IndexOf("server_time", StringComparison.Ordinal);
            if (idx < 0) return;
            var digStart = json.IndexOfAny(['0', '1', '2', '3', '4', '5', '6', '7', '8', '9'], idx);
            if (digStart < 0) return;
            var digEnd = digStart;
            while (digEnd < json.Length && char.IsDigit(json[digEnd])) digEnd++;
            if (long.TryParse(json.AsSpan(digStart, digEnd - digStart), out var serverTime))
            {
                var local = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                _serverTimeOffset = serverTime - local;
                _offsetFetchedAt = DateTime.UtcNow;
            }
        }
        catch
        {
            // offline: use local time
        }
    }

    public static long GetSteamTime()
    {
        var local = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return local + (_serverTimeOffset ?? 0);
    }

    public static string GenerateAuthCode(string sharedSecret)
    {
        return GenerateAuthCodeForTime(sharedSecret, GetSteamTime());
    }

    public static string GenerateAuthCodeForTime(string sharedSecret, long time)
    {
        if (string.IsNullOrWhiteSpace(sharedSecret))
            return "";

        var unescaped = Regex.Unescape(sharedSecret);
        var key = Convert.FromBase64String(unescaped);
        time /= 30L;

        var timeArray = new byte[8];
        for (var i = 8; i > 0; i--)
        {
            timeArray[i - 1] = (byte)time;
            time >>= 8;
        }

        using var hmac = new HMACSHA1(key);
        var hashed = hmac.ComputeHash(timeArray);
        var b = (byte)(hashed[19] & 0xF);
        var codePoint =
            ((hashed[b] & 0x7F) << 24) |
            ((hashed[b + 1] & 0xFF) << 16) |
            ((hashed[b + 2] & 0xFF) << 8) |
            (hashed[b + 3] & 0xFF);

        var code = new byte[5];
        for (var i = 0; i < 5; i++)
        {
            code[i] = CodeTranslations[codePoint % CodeTranslations.Length];
            codePoint /= CodeTranslations.Length;
        }

        return Encoding.UTF8.GetString(code);
    }

    public static string GenerateConfirmationHash(string identitySecret, long time, string tag)
    {
        var decode = Convert.FromBase64String(identitySecret);
        var n2 = 8 + Math.Min(tag?.Length ?? 0, 32);
        var array = new byte[n2];
        var t = time;
        for (var i = 8; i > 0; i--)
        {
            array[i - 1] = (byte)t;
            t >>= 8;
        }

        if (!string.IsNullOrEmpty(tag))
            Encoding.UTF8.GetBytes(tag, 0, Math.Min(tag.Length, 32), array, 8);

        using var hmac = new HMACSHA1(decode);
        var hashed = hmac.ComputeHash(array);
        return Uri.EscapeDataString(Convert.ToBase64String(hashed));
    }

    public static string EnsureDeviceId(string? deviceId, string steamId64)
    {
        if (!string.IsNullOrWhiteSpace(deviceId))
            return deviceId;
        // SDA-compatible random device id
        return $"android:{Guid.NewGuid()}";
    }
}
