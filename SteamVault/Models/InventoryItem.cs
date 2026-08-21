using CommunityToolkit.Mvvm.ComponentModel;

namespace SteamVault.Models;

public partial class InventoryItem : ObservableObject
{
    public string AccountId { get; set; } = "";
    public string AccountLogin { get; set; } = "";
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
    /// <summary>Days Steam reports for market restriction (often trade hold related).</summary>
    public int MarketTradableRestriction { get; set; }
    /// <summary>When item becomes tradable (UTC), if known from owner_descriptions.</summary>
    public DateTime? TradableAfter { get; set; }

    /// <summary>Paint wear 0..1 when Steam descriptions expose it.</summary>
    public double? FloatValue { get; set; }
    /// <summary>Sticker names parsed from inventory descriptions (if any).</summary>
    public List<string> Stickers { get; set; } = [];

    [ObservableProperty] private decimal _price;
    [ObservableProperty] private bool _isSelected;

    /// <summary>
    /// Source for the rarity accent: the Steam tag colour when present, otherwise the
    /// rarity name so the UI can still map a colour (cases/capsules often ship no colour).
    /// </summary>
    public string? RarityAccentSource =>
        !string.IsNullOrWhiteSpace(RarityColor) ? RarityColor : Rarity;

    public string RarityText => string.IsNullOrWhiteSpace(Rarity) ? "—" : Rarity;
    /// <summary>Wear quality short label (FN/MW/FT/WW/BS) or full exterior.</summary>
    public string QualityText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Exterior)) return "";
            var e = Exterior.ToLowerInvariant();
            if (e.Contains("factory new")) return "FN";
            if (e.Contains("minimal wear")) return "MW";
            if (e.Contains("field-tested") || e.Contains("field tested")) return "FT";
            if (e.Contains("well-worn") || e.Contains("well worn")) return "WW";
            if (e.Contains("battle-scarred") || e.Contains("battle scarred")) return "BS";
            return Exterior;
        }
    }
    public string FloatText => FloatValue is > 0 and < 1 ? $"Float {FloatValue:0.000000000}" : "";
    public bool HasFloat => FloatValue is > 0 and < 1;
    public bool HasStickers => Stickers.Count > 0;
    public string StickersText => Stickers.Count == 0 ? "" : string.Join(" · ", Stickers.Take(4));
    public string StickersShort => Stickers.Count == 0 ? "" : (Stickers.Count == 1 ? Stickers[0] : $"{Stickers.Count} stickers");

    public string PriceText => Price > 0 ? $"${Price:0.00}" : "—";
    public string Key => $"{AccountId}:{AssetId}";
    /// <summary>
    /// True when the item can never be traded or sold (e.g. Service Medals, Coins, Pins, Trophies).
    /// Real skins, knives, gloves, cases, and stickers with trade holds are NEVER permanently untradable.
    /// </summary>
    public bool IsPermanentlyUntradable
    {
        get
        {
            if (Tradable || Marketable) return false;
            if (TradableAfter.HasValue && TradableAfter.Value > DateTime.UtcNow) return false;
            if (!string.IsNullOrWhiteSpace(Exterior)) return false;
            return true;
        }
    }

    /// <summary>
    /// Item is on a temporary trade hold if it has a future TradableAfter date or is a marketable skin with a trade restriction.
    /// </summary>
    public bool IsOnTradeHold =>
        !IsPermanentlyUntradable && (
            (TradableAfter.HasValue && TradableAfter.Value > DateTime.UtcNow) ||
            (!Tradable && (Marketable || !string.IsNullOrWhiteSpace(Exterior)))
        );

    public string HoldText =>
        IsOnTradeHold
            ? (TradableAfter.HasValue ? $"hold → {TradableAfter:dd.MM}" : "hold")
            : "ok";

    /// <summary>
    /// Formatted hold countdown badge (e.g. "🔒 3d 14h", "🔒 5h 20m", "🔒 < 1m", "🔒 Hold").
    /// </summary>
    public string HoldBadgeText
    {
        get
        {
            if (!IsOnTradeHold) return "";
            if (!TradableAfter.HasValue)
            {
                return "🔒 Hold";
            }

            var rem = TradableAfter.Value - DateTime.UtcNow;
            if (rem.TotalSeconds <= 0) return "🔓 Ready";

            if (rem.TotalDays >= 1)
                return $"🔒 {(int)rem.TotalDays}d {rem.Hours}h";
            if (rem.TotalHours >= 1)
                return $"🔒 {(int)rem.TotalHours}h {rem.Minutes}m";
            if (rem.TotalMinutes >= 1)
                return $"🔒 {(int)rem.TotalMinutes}m";

            return "🔒 < 1m";
        }
    }

    /// <summary>Detailed tooltip for trade lock.</summary>
    public string HoldTooltipText
    {
        get
        {
            if (!IsOnTradeHold) return "Tradable";
            if (TradableAfter.HasValue)
                return $"Trade locked until {TradableAfter.Value.ToLocalTime():dd.MM.yyyy HH:mm}";
            return "Trade locked (7-day restriction)";
        }
    }

    partial void OnPriceChanged(decimal value) => OnPropertyChanged(nameof(PriceText));
}
