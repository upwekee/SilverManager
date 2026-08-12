using System.Text.Json;

namespace SteamVault.Services;

/// <summary>
/// Free bulk prices: market.csgo.com, fallback prices.csgotrader.app
/// </summary>
public sealed class PriceService
{
    private readonly Dictionary<string, decimal> _prices = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastRefresh = DateTime.MinValue;
    private string? _source;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public int Count => _prices.Count;
    public string? Source => _source;
    public DateTime LastRefresh => _lastRefresh;

    public decimal GetPrice(string? marketHashName)
    {
        if (string.IsNullOrEmpty(marketHashName)) return 0;
        return _prices.TryGetValue(marketHashName, out var p) ? p : 0;
    }

    public async Task<int> RefreshAsync(bool force = false, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (!force && _prices.Count > 0 && DateTime.UtcNow - _lastRefresh < TimeSpan.FromMinutes(30))
                return _prices.Count;

            try
            {
                await LoadMarketCsgoAsync(ct);
            }
            catch
            {
                await LoadCsgoTraderAsync(ct);
            }

            _lastRefresh = DateTime.UtcNow;
            return _prices.Count;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task LoadMarketCsgoAsync(CancellationToken ct)
    {
        using var http = CreateHttp();
        var json = await http.GetStringAsync("https://market.csgo.com/api/v2/prices/USD.json", ct);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("items", out var items))
            throw new InvalidOperationException("market.csgo.com: no items");

        _prices.Clear();
        if (items.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in items.EnumerateObject())
            {
                var price = ExtractPrice(prop.Value);
                if (price > 0) _prices[prop.Name] = price;
            }
        }
        else if (items.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in items.EnumerateArray())
            {
                var name = el.TryGetProperty("market_hash_name", out var n) ? n.GetString()
                    : el.TryGetProperty("name", out var n2) ? n2.GetString() : null;
                var price = ExtractPrice(el);
                if (!string.IsNullOrEmpty(name) && price > 0) _prices[name] = price;
            }
        }

        if (_prices.Count == 0) throw new InvalidOperationException("market.csgo.com: 0 prices");
        _source = "market.csgo.com (USD)";
    }

    private async Task LoadCsgoTraderAsync(CancellationToken ct)
    {
        using var http = CreateHttp();
        http.Timeout = TimeSpan.FromSeconds(60);
        var json = await http.GetStringAsync("https://prices.csgotrader.app/latest/prices_v6.json", ct);
        using var doc = JsonDocument.Parse(json);
        _prices.Clear();
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            decimal price = 0;
            if (prop.Value.TryGetProperty("steam", out var steam))
            {
                price = GetDecimal(steam, "last_24h")
                        ?? GetDecimal(steam, "last_7d")
                        ?? GetDecimal(steam, "last_30d")
                        ?? 0;
            }
            if (price <= 0 && prop.Value.TryGetProperty("buff163", out var buff))
            {
                if (buff.TryGetProperty("starting_at", out var sa) && sa.TryGetProperty("price", out var p))
                    price = p.ValueKind == JsonValueKind.Number ? p.GetDecimal() : decimal.TryParse(p.GetString(), out var d) ? d : 0;
            }
            if (price > 0) _prices[prop.Name] = Math.Round(price, 2);
        }
        if (_prices.Count == 0) throw new InvalidOperationException("csgotrader: 0 prices");
        _source = "prices.csgotrader.app";
    }

    private static decimal ExtractPrice(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Number) return Math.Round(el.GetDecimal(), 2);
        if (el.ValueKind == JsonValueKind.String && decimal.TryParse(el.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var s))
            return Math.Round(s, 2);
        if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "price", "avg", "avg_price" })
            {
                if (!el.TryGetProperty(key, out var p)) continue;
                if (p.ValueKind == JsonValueKind.Number) return Math.Round(p.GetDecimal(), 2);
                if (p.ValueKind == JsonValueKind.String && decimal.TryParse(p.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d))
                    return Math.Round(d, 2);
            }
        }
        return 0;
    }

    private static decimal? GetDecimal(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p)) return null;
        if (p.ValueKind == JsonValueKind.Number) return Math.Round(p.GetDecimal(), 2);
        if (p.ValueKind == JsonValueKind.String && decimal.TryParse(p.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d))
            return Math.Round(d, 2);
        return null;
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "SteamVault/1.0");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
        return http;
    }
}
