using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SteamVault.Services;

public sealed class ProxyCheckResult
{
    public string Proxy { get; init; } = "";
    public string ProxyShort { get; init; } = "";
    public bool Ok { get; init; }
    public int Ms { get; init; }
    public string? ExitIp { get; init; }
    public string Message { get; init; } = "";
    public string StatusText => Ok
        ? $"OK · {Ms}ms · {ExitIp ?? "?"}"
        : $"FAIL · {Message}";
}

public sealed class ProxyUsageRow
{
    public string Proxy { get; init; } = "";
    public string ProxyShort { get; init; } = "";
    public int Count { get; init; }
    public string AccountsPreview { get; init; } = "";
    public string CountText => Count == 1 ? "1 acc (personal)" : $"{Count} acc (shared)";
}

/// <summary>
/// Parse proxy strings, bulk lists, and lightweight connectivity checks.
/// Formats: host:port | user:pass@host:port | host:port:user:pass | user:pass:host:port |
/// http://… | socks5://… 
/// </summary>
public static class ProxyHelper
{
    public static IWebProxy? TryCreate(string? proxy)
    {
        var norm = Normalize(proxy);
        if (norm == null) return null;

        try
        {
            string scheme = "http://";
            string rest = norm;
            if (norm.Contains("://"))
            {
                var idx = norm.IndexOf("://", StringComparison.Ordinal);
                scheme = norm[..(idx + 3)];
                rest = norm[(idx + 3)..];
            }

            if (rest.Contains('@'))
            {
                var at = rest.LastIndexOf('@');
                var cred = rest[..at];
                var hostPart = rest[(at + 1)..];
                var colon = cred.IndexOf(':');
                var user = colon >= 0 ? cred[..colon] : cred;
                var pass = colon >= 0 ? cred[(colon + 1)..] : "";

                return new WebProxy($"{scheme}{hostPart}")
                {
                    Credentials = new NetworkCredential(
                        Uri.UnescapeDataString(user),
                        Uri.UnescapeDataString(pass)),
                    BypassProxyOnLocal = false
                };
            }

            var uri = new Uri($"{scheme}{rest}");
            var web = new WebProxy(uri) { BypassProxyOnLocal = false };
            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                var parts = uri.UserInfo.Split(':', 2);
                web.Credentials = new NetworkCredential(
                    Uri.UnescapeDataString(parts[0]),
                    parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "");
            }
            return web;
        }
        catch
        {
            return null;
        }
    }

    public static bool IsValid(string? proxy) =>
        !string.IsNullOrWhiteSpace(proxy) && TryCreate(proxy) != null;

    public static string Mask(string? proxy)
    {
        if (string.IsNullOrWhiteSpace(proxy)) return "(none)";
        var p = proxy!;
        var at = p.LastIndexOf('@');
        if (at > 0) return "…@" + p[(at + 1)..];
        return p.Length > 28 ? p[..26] + "…" : p;
    }

    /// <summary>HTTP GET via proxy to ipify + optional steamcommunity ping.</summary>
    public static async Task<ProxyCheckResult> CheckAsync(string? proxy, int timeoutMs = 9000, CancellationToken ct = default)
    {
        var norm = Normalize(proxy);
        if (norm == null)
            return new ProxyCheckResult { Ok = false, Message = "empty", ProxyShort = "(none)" };

        var webProxy = TryCreate(norm);
        if (webProxy == null)
            return new ProxyCheckResult { Proxy = norm, ProxyShort = Mask(norm), Ok = false, Message = "bad format" };

        var sw = Stopwatch.StartNew();
        try
        {
            using var handler = new HttpClientHandler
            {
                Proxy = webProxy,
                UseProxy = true,
                AutomaticDecompression = DecompressionMethods.All,
                // many residential proxies fail TLS if strict
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            };
            using var http = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromMilliseconds(timeoutMs)
            };
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) SteamVault/1.0");

            // 1) exit IP
            string? exitIp = null;
            using (var resp = await http.GetAsync("https://api.ipify.org?format=json", ct))
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode)
                    return new ProxyCheckResult
                    {
                        Proxy = norm, ProxyShort = Mask(norm), Ok = false,
                        Ms = (int)sw.ElapsedMilliseconds,
                        Message = $"HTTP {(int)resp.StatusCode}"
                    };
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("ip", out var ipEl))
                        exitIp = ipEl.GetString();
                }
                catch { exitIp = body.Trim().Trim('"'); }
            }

            // 2) light steam reachability (don't fail whole check if only this fails)
            var steamOk = false;
            try
            {
                using var steamResp = await http.GetAsync("https://steamcommunity.com/", ct);
                steamOk = (int)steamResp.StatusCode is >= 200 and < 500;
            }
            catch { /* optional */ }

            sw.Stop();
            return new ProxyCheckResult
            {
                Proxy = norm,
                ProxyShort = Mask(norm),
                Ok = true,
                Ms = (int)sw.ElapsedMilliseconds,
                ExitIp = exitIp,
                Message = steamOk ? "ipify+steam OK" : "ipify OK (steam slow/blocked)"
            };
        }
        catch (TaskCanceledException)
        {
            return new ProxyCheckResult
            {
                Proxy = norm, ProxyShort = Mask(norm), Ok = false,
                Ms = (int)sw.ElapsedMilliseconds, Message = "timeout"
            };
        }
        catch (Exception ex)
        {
            var msg = ex.InnerException?.Message ?? ex.Message;
            if (msg.Length > 80) msg = msg[..80] + "…";
            return new ProxyCheckResult
            {
                Proxy = norm, ProxyShort = Mask(norm), Ok = false,
                Ms = (int)sw.ElapsedMilliseconds, Message = msg
            };
        }
    }

    public static async Task<List<ProxyCheckResult>> CheckManyAsync(
        IEnumerable<string> proxies, int maxParallel = 5, int timeoutMs = 9000,
        IProgress<(int done, int total, ProxyCheckResult last)>? progress = null,
        CancellationToken ct = default)
    {
        var list = proxies.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var results = new List<ProxyCheckResult>();
        var done = 0;
        using var gate = new SemaphoreSlim(Math.Max(1, maxParallel));
        var tasks = list.Select(async p =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var r = await CheckAsync(p, timeoutMs, ct);
                lock (results)
                {
                    results.Add(r);
                    done++;
                    progress?.Report((done, list.Count, r));
                }
                return r;
            }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks);
        return results.OrderByDescending(r => r.Ok).ThenBy(r => r.Ms).ToList();
    }

    public static List<ProxyUsageRow> BuildUsage(IEnumerable<SteamVault.Models.SteamAccount> accounts)
    {
        return accounts
            .Where(a => a.HasProxy)
            .GroupBy(a => a.Proxy!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ProxyUsageRow
            {
                Proxy = g.Key,
                ProxyShort = Mask(g.Key),
                Count = g.Count(),
                AccountsPreview = string.Join(", ", g.Select(x => x.Login).Take(6))
                    + (g.Count() > 6 ? $" +{g.Count() - 6}" : "")
            })
            .OrderByDescending(r => r.Count)
            .ThenBy(r => r.ProxyShort)
            .ToList();
    }

    /// <summary>Normalize one line into host:port or user:pass@host:port or scheme://…</summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim().Trim(',', ';');
        if (s.StartsWith('#') || s.StartsWith("//")) return null;

        // strip quotes
        if ((s.StartsWith('"') && s.EndsWith('"')) || (s.StartsWith('\'') && s.EndsWith('\'')))
            s = s[1..^1].Trim();

        string scheme = "";
        if (s.Contains("://", StringComparison.Ordinal))
        {
            var idx = s.IndexOf("://", StringComparison.Ordinal);
            scheme = s[..(idx + 3)];
            s = s[(idx + 3)..];
        }

        // user:pass@host:port
        if (s.Contains('@'))
            return scheme + s;

        var parts = s.Split(':');
        // host:port
        if (parts.Length == 2 && int.TryParse(parts[1], out _))
            return scheme + s;

        // host:port:user:pass  (very common export format)
        if (parts.Length >= 4 && int.TryParse(parts[1], out _))
        {
            var host = parts[0];
            var port = parts[1];
            var user = parts[2];
            var pass = string.Join(':', parts.Skip(3));
            return $"{scheme}{user}:{pass}@{host}:{port}";
        }

        // user:pass:host:port
        if (parts.Length >= 4 && int.TryParse(parts[^1], out _))
        {
            var port = parts[^1];
            var host = parts[^2];
            // if host looks like IP or domain
            if (host.Contains('.') || Regex.IsMatch(host, @"^\d"))
            {
                var user = parts[0];
                var pass = string.Join(':', parts.Skip(1).Take(parts.Length - 3));
                return $"{scheme}{user}:{pass}@{host}:{port}";
            }
        }

        return scheme + s; // last chance — TryCreate may still accept
    }

    public static List<string> ParseLines(string? text)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return list;
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var n = Normalize(line);
            if (n == null) continue;
            if (!IsValid(n)) continue;
            // de-dupe while preserving order
            if (!list.Contains(n, StringComparer.OrdinalIgnoreCase))
                list.Add(n);
        }
        return list;
    }

    public static List<string> ParseFile(string path) =>
        File.Exists(path) ? ParseLines(File.ReadAllText(path)) : [];

    /// <summary>
    /// Assign proxies to accounts. Round-robin if fewer proxies than accounts.
    /// </summary>
    public static int Distribute(IReadOnlyList<SteamVault.Models.SteamAccount> accounts, IReadOnlyList<string> proxies)
    {
        if (accounts.Count == 0 || proxies.Count == 0) return 0;
        for (var i = 0; i < accounts.Count; i++)
            accounts[i].Proxy = proxies[i % proxies.Count];
        return accounts.Count;
    }
}
