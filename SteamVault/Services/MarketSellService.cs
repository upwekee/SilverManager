using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using SteamVault.Models;

namespace SteamVault.Services;

/// <summary>
/// Steam Community Market sell (junk auto-list).
/// Price is buyer-pays in USD cents; Steam fee ~15% applied by Valve.
/// </summary>
public static class MarketSellService
{
    /// <summary>
    /// List items under maxUsd as market listings.
    /// Returns (listed, failed, messages).
    /// </summary>
    public static async Task<(int listed, int failed, List<string> log)> SellJunkAsync(
        SteamSession session,
        IReadOnlyList<InventoryItem> items,
        decimal maxPriceUsd,
        int maxListings,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (!session.IsOnline)
            throw new InvalidOperationException("Account is offline");

        var junk = items
            .Where(i => i.Tradable && i.Marketable && i.Price > 0 && i.Price <= maxPriceUsd)
            .Take(Math.Max(1, maxListings))
            .ToList();

        // also include zero-price marketable as "junk" if max >= 0.03
        if (junk.Count == 0)
        {
            junk = items
                .Where(i => i.Tradable && i.Marketable && (i.Price <= maxPriceUsd || i.Price <= 0))
                .Take(Math.Max(1, maxListings))
                .ToList();
        }

        var listed = 0;
        var failed = 0;
        var log = new List<string>();
        using var http = session.CreateHttpClient();

        foreach (var item in junk)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Market · {item.MarketHashName}");

            // Steam expects price in cents the buyer pays
            var buyerUsd = item.Price > 0 ? item.Price : Math.Max(0.03m, maxPriceUsd);
            // minimum steam listing ~ $0.03
            if (buyerUsd < 0.03m) buyerUsd = 0.03m;
            var priceCents = (int)Math.Round(buyerUsd * 100m);
            if (priceCents < 3) priceCents = 3;

            try
            {
                var form = new Dictionary<string, string>
                {
                    ["sessionid"] = session.SessionId ?? "",
                    ["appid"] = "730",
                    ["contextid"] = "2",
                    ["assetid"] = item.AssetId,
                    ["amount"] = "1",
                    ["price"] = priceCents.ToString(CultureInfo.InvariantCulture)
                };
                using var content = new FormUrlEncodedContent(form);
                using var req = new HttpRequestMessage(HttpMethod.Post, "https://steamcommunity.com/market/sellitem/");
                req.Content = content;
                req.Headers.Referrer = new Uri("https://steamcommunity.com/market/");
                req.Headers.TryAddWithoutValidation("Origin", "https://steamcommunity.com");

                using var resp = await http.SendAsync(req, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);

                if (body.Contains("\"success\":true", StringComparison.OrdinalIgnoreCase) ||
                    body.Contains("\"success\": true", StringComparison.OrdinalIgnoreCase))
                {
                    listed++;
                    log.Add($"OK {item.MarketHashName} @ ${buyerUsd:0.00}");
                }
                else
                {
                    failed++;
                    var err = ExtractError(body);
                    log.Add($"FAIL {item.MarketHashName}: {err}");
                    // stop on hard rate limit
                    if (err.Contains("21", StringComparison.Ordinal) ||
                        err.Contains("rate", StringComparison.OrdinalIgnoreCase) ||
                        err.Contains("too many", StringComparison.OrdinalIgnoreCase))
                        break;
                }
            }
            catch (Exception ex)
            {
                failed++;
                log.Add($"FAIL {item.MarketHashName}: {ex.Message}");
            }

            await Task.Delay(1500, ct);
        }

        return (listed, failed, log);
    }

    private static string ExtractError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("message", out var m))
                return m.GetString() ?? body[..Math.Min(80, body.Length)];
        }
        catch { /* */ }
        return body.Length > 100 ? body[..100] : body;
    }
}
