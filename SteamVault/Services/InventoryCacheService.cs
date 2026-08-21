using System.Text.Json;
using SteamVault.Models;

namespace SteamVault.Services;

/// <summary>Disk cache of CS2 inventories so Drain/Load can skip full re-fetch.</summary>
public sealed class InventoryCacheService
{
    private readonly string _dir;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    public InventoryCacheService()
    {
        _dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SteamVault", "inv-cache");
        Directory.CreateDirectory(_dir);
    }

    public void Save(string accountId, IReadOnlyList<InventoryItem> items, string? login = null)
    {
        try
        {
            var dto = new CacheFile
            {
                SavedAt = DateTime.UtcNow,
                Items = items.Select(ToDto).ToList()
            };
            var json = JsonSerializer.Serialize(dto, JsonOpts);
            File.WriteAllText(PathFor(accountId), json);
            if (!string.IsNullOrWhiteSpace(login))
            {
                File.WriteAllText(PathFor(login.Trim().ToLowerInvariant()), json);
            }
        }
        catch { /* */ }
    }

    public List<InventoryItem>? TryLoad(string accountId, string login, TimeSpan maxAge)
    {
        try
        {
            var path = PathFor(accountId);
            if (!File.Exists(path) && !string.IsNullOrWhiteSpace(login))
            {
                path = PathFor(login.Trim().ToLowerInvariant());
            }
            if (!File.Exists(path)) return null;

            var dto = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(path));
            if (dto == null) return null;
            if (DateTime.UtcNow - dto.SavedAt > maxAge) return null;
            return dto.Items.Select(d => FromDto(d, accountId, login)).ToList();
        }
        catch { return null; }
    }

    public DateTime? GetSavedAt(string accountId)
    {
        try
        {
            var path = PathFor(accountId);
            if (!File.Exists(path)) return null;
            var dto = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(path));
            return dto?.SavedAt;
        }
        catch { return null; }
    }

    private string PathFor(string accountId) =>
        Path.Combine(_dir, accountId + ".json");

    private static ItemDto ToDto(InventoryItem i) => new()
    {
        AssetId = i.AssetId,
        ClassId = i.ClassId,
        InstanceId = i.InstanceId,
        MarketHashName = i.MarketHashName,
        Name = i.Name,
        Type = i.Type,
        Rarity = i.Rarity,
        RarityColor = i.RarityColor,
        Exterior = i.Exterior,
        ImageUrl = i.ImageUrl,
        Tradable = i.Tradable,
        Marketable = i.Marketable,
        Amount = i.Amount,
        Price = i.Price,
        TradableAfterUnix = i.TradableAfter?.ToUniversalTime() is { } t
            ? new DateTimeOffset(t).ToUnixTimeSeconds()
            : null,
        MarketTradableRestriction = i.MarketTradableRestriction,
        FloatValue = i.FloatValue,
        Stickers = i.Stickers is { Count: > 0 } ? i.Stickers.ToList() : null
    };

    private static InventoryItem FromDto(ItemDto d, string accountId, string login) => new()
    {
        AccountId = accountId,
        AccountLogin = login,
        AssetId = d.AssetId,
        ClassId = d.ClassId,
        InstanceId = d.InstanceId,
        MarketHashName = d.MarketHashName,
        Name = d.Name,
        Type = d.Type,
        Rarity = d.Rarity,
        RarityColor = d.RarityColor,
        Exterior = d.Exterior,
        ImageUrl = d.ImageUrl,
        Tradable = d.Tradable,
        Marketable = d.Marketable,
        Amount = d.Amount,
        Price = d.Price,
        MarketTradableRestriction = d.MarketTradableRestriction,
        TradableAfter = d.TradableAfterUnix is > 0
            ? DateTimeOffset.FromUnixTimeSeconds(d.TradableAfterUnix.Value).UtcDateTime
            : null,
        FloatValue = d.FloatValue,
        Stickers = d.Stickers ?? []
    };

    private sealed class CacheFile
    {
        public DateTime SavedAt { get; set; }
        public List<ItemDto> Items { get; set; } = [];
    }

    private sealed class ItemDto
    {
        public string AssetId { get; set; } = "";
        public string ClassId { get; set; } = "";
        public string InstanceId { get; set; } = "0";
        public string MarketHashName { get; set; } = "";
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public string Rarity { get; set; } = "";
        public string? RarityColor { get; set; }
        public string Exterior { get; set; } = "";
        public string? ImageUrl { get; set; }
        public bool Tradable { get; set; }
        public bool Marketable { get; set; }
        public int Amount { get; set; } = 1;
        public decimal Price { get; set; }
        public long? TradableAfterUnix { get; set; }
        public int MarketTradableRestriction { get; set; }
        public double? FloatValue { get; set; }
        public List<string>? Stickers { get; set; }
    }
}
