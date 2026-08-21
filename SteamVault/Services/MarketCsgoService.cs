using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace SteamVault.Services;

public record MarketMoneyInfo(decimal Available, decimal Settlement, string Currency, bool Success, string? Error);
public record MarketSaleResult(bool Success, string? ItemId, string? Error);
public record MarketTransferResult(bool Success, string? Error);
public record MarketPriceInfo(long LowestSellKopecks, long BestBuyOrderKopecks);

public sealed class MarketCsgoService
{
    private readonly HttpClient _http;

    public MarketCsgoService(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    /// <summary>
    /// Fetches current account balance (money) and 7-day frozen settlement (money_settlement).
    /// </summary>
    public async Task<MarketMoneyInfo> GetMoneyAsync(string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return new MarketMoneyInfo(0, 0, "RUB", false, "Missing Market API key");

        try
        {
            var url = $"https://market.csgo.com/api/v2/get-money?key={apiKey.Trim()}";
            using var resp = await _http.GetAsync(url, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                return new MarketMoneyInfo(0, 0, "RUB", false, $"HTTP {(int)resp.StatusCode}: {body}");

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            var success = root.TryGetProperty("success", out var s) && s.GetBoolean();
            if (!success)
            {
                var err = root.TryGetProperty("error", out var e) ? e.GetString() : "Unknown Market error";
                return new MarketMoneyInfo(0, 0, "RUB", false, err);
            }

            var money = root.TryGetProperty("money", out var m) ? m.GetDecimal() : 0m;
            var settlement = root.TryGetProperty("money_settlement", out var ms) ? ms.GetDecimal() : 0m;
            var currency = root.TryGetProperty("currency", out var c) ? c.GetString() ?? "RUB" : "RUB";

            return new MarketMoneyInfo(money, settlement, currency, true, null);
        }
        catch (Exception ex)
        {
            return new MarketMoneyInfo(0, 0, "RUB", false, ex.Message);
        }
    }

    /// <summary>
    /// Triggers inventory refresh on Market CSGO.
    /// </summary>
    public async Task<bool> UpdateInventoryAsync(string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return false;
        try
        {
            var url = $"https://market.csgo.com/api/v2/update-inventory?key={apiKey.Trim()}";
            using var resp = await _http.GetAsync(url, ct);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>
    /// Fetches current lowest sell price and best buy order price for a given MarketHashName.
    /// Returns values in kopecks (1/100 RUB).
    /// </summary>
    public async Task<MarketPriceInfo> GetItemBestPricesAsync(string apiKey, string marketHashName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(marketHashName)) return new MarketPriceInfo(0, 0);

        try
        {
            var url = $"https://market.csgo.com/api/v2/search-item-by-hash-name?key={apiKey.Trim()}&hash_name={Uri.EscapeDataString(marketHashName)}";
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return new MarketPriceInfo(0, 0);

            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (!root.TryGetProperty("success", out var s) || !s.GetBoolean()) return new MarketPriceInfo(0, 0);

            long lowestSellKopecks = 0;
            long bestBuyOrderKopecks = 0;

            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    if (item.TryGetProperty("price", out var p))
                    {
                        var kopecks = p.GetInt64();
                        if (lowestSellKopecks == 0 || kopecks < lowestSellKopecks)
                            lowestSellKopecks = kopecks;
                    }
                    if (item.TryGetProperty("buy_order", out var bo))
                    {
                        var boKopecks = bo.GetInt64();
                        if (boKopecks > bestBuyOrderKopecks)
                            bestBuyOrderKopecks = boKopecks;
                    }
                }
            }

            return new MarketPriceInfo(lowestSellKopecks, bestBuyOrderKopecks);
        }
        catch
        {
            return new MarketPriceInfo(0, 0);
        }
    }

    /// <summary>
    /// Puts an item up for sale on market.csgo.com.
    /// </summary>
    public async Task<MarketSaleResult> AddToSaleAsync(string apiKey, string assetId, long priceInKopecks, string currency = "RUB", CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return new MarketSaleResult(false, null, "Missing Market API key");

        if (string.IsNullOrWhiteSpace(assetId))
            return new MarketSaleResult(false, null, "Missing Steam Asset ID");

        if (priceInKopecks <= 0)
            return new MarketSaleResult(false, null, "Price must be greater than 0");

        try
        {
            var url = $"https://market.csgo.com/api/v2/add-to-sale?key={apiKey.Trim()}&id={assetId.Trim()}&price={priceInKopecks}&cur={currency}";
            using var resp = await _http.GetAsync(url, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                return new MarketSaleResult(false, null, $"HTTP {(int)resp.StatusCode}: {body}");

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            var success = root.TryGetProperty("success", out var s) && s.GetBoolean();
            if (!success)
            {
                var err = root.TryGetProperty("error", out var e) ? e.GetString() : "Failed to add item to sale";
                return new MarketSaleResult(false, null, err);
            }

            var itemId = root.TryGetProperty("item_id", out var id) ? id.ToString() : null;
            return new MarketSaleResult(true, itemId, null);
        }
        catch (Exception ex)
        {
            return new MarketSaleResult(false, null, ex.Message);
        }
    }

    /// <summary>
    /// Transfers money balance from sender account to recipient user API key via money-send endpoint.
    /// Endpoint: https://market.csgo.com/api/v2/money-send/[amount]/[user_api_key]?pay_pass=[pay_pass]&key=[your_secret_key]
    /// </summary>
    public async Task<MarketTransferResult> MoneySendAsync(string senderApiKey, string recipientUserApiKey, long amountInKopecks, string payPass, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(senderApiKey))
            return new MarketTransferResult(false, "Sender Market API key is required");

        if (string.IsNullOrWhiteSpace(recipientUserApiKey))
            return new MarketTransferResult(false, "Recipient Market API key is required");

        if (string.IsNullOrWhiteSpace(payPass))
            return new MarketTransferResult(false, "Payment password (pay_pass) is required");

        if (amountInKopecks <= 0)
            return new MarketTransferResult(false, "Transfer amount must be greater than 0");

        try
        {
            var url = $"https://market.csgo.com/api/v2/money-send/{amountInKopecks}/{recipientUserApiKey.Trim()}?pay_pass={Uri.EscapeDataString(payPass.Trim())}&key={senderApiKey.Trim()}";
            using var resp = await _http.GetAsync(url, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
                return new MarketTransferResult(false, $"HTTP {(int)resp.StatusCode}: {body}");

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            var success = root.TryGetProperty("success", out var s) && s.GetBoolean();
            if (!success)
            {
                var err = root.TryGetProperty("error", out var e) ? e.GetString() : "Money transfer failed";
                return new MarketTransferResult(false, err);
            }

            return new MarketTransferResult(true, null);
        }
        catch (Exception ex)
        {
            return new MarketTransferResult(false, ex.Message);
        }
    }
}
