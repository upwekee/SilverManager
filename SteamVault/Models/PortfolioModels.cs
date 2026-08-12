namespace SteamVault.Models;

/// <summary>Aggregated inventory row for Stats (cases / top skins / categories).</summary>
public sealed class PortfolioItemRow
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "Other";
    public int Count { get; set; }
    public decimal TotalValue { get; set; }
    public decimal UnitPrice { get; set; }
    public string? ImageUrl { get; set; }
    /// <summary>Steam rarity tag colour, or the rarity name when no colour was sent.</summary>
    public string? RarityAccentSource { get; set; }
    public int AccountCount { get; set; }
    public double BarRatio { get; set; } // 0..1 width for UI bar
    public double Percent { get; set; }
    public string CountText => Count == 1 ? "1 pc" : $"{Count} pcs";
    public string ValueText => TotalValue > 0 ? $"${TotalValue:0.00}" : "—";
    public string UnitText => UnitPrice > 0 ? $"${UnitPrice:0.00}" : "—";
    public string MetaText => AccountCount > 1 ? $"{AccountCount} acc" : (AccountCount == 1 ? "1 acc" : "");
    public string PercentText => $"{Percent:0}%";
    public string Color { get; set; } = "#EDEDF0";
}

public sealed class PortfolioCategoryRow
{
    public string Name { get; set; } = "";
    public int Count { get; set; }
    public decimal Value { get; set; }
    public double BarRatio { get; set; }
    public double Percent { get; set; }
    public string Color { get; set; } = "#EDEDF0";
    public string ValueText => $"${Value:0.00}";
    public string CountText => $"{Count}";
    public string PercentText => $"{Percent:0}%";
}

/// <summary>Wear / exterior bucket for stats (FN, MW, …).</summary>
public sealed class QualityStatRow
{
    public string Name { get; set; } = "";
    public int Count { get; set; }
    public decimal Value { get; set; }
    public double BarRatio { get; set; }
    public double Percent { get; set; }
    public string ValueText => Value > 0 ? $"${Value:0.00}" : "—";
    public string CountText => Count == 1 ? "1" : $"{Count}";
    public string PercentText => $"{Percent:0}%";
}

public static class ItemClassifier
{
    public static string Classify(InventoryItem it)
    {
        var name = it.MarketHashName ?? "";
        var type = it.Type ?? "";
        var n = name.ToLowerInvariant();
        var t = type.ToLowerInvariant();

        if (name.Contains('★') || t.Contains("knife") || n.Contains("knife") || n.StartsWith("★"))
            return "Knives";
        if (t.Contains("glove") || n.Contains("gloves") || n.Contains("hand wraps"))
            return "Gloves";
        if (t.Contains("container") || n.EndsWith(" case") || n.Contains(" case ") ||
            (n.EndsWith("case") && !n.Contains("hard")))
            return "Cases";
        if (n.Contains("capsule") || t.Contains("capsule"))
            return "Capsules";
        if (t.Contains("sticker") || n.StartsWith("sticker |") || n.Contains("sticker |"))
            return "Stickers";
        if (t.Contains("agent") || n.Contains(" | agent") || n.StartsWith("agent "))
            return "Agents";
        if (t.Contains("music") || n.StartsWith("music kit"))
            return "Music";
        if (t.Contains("graffiti") || n.StartsWith("sealed graffiti") || n.Contains("graffiti |"))
            return "Graffiti";
        if (t.Contains("key") || n.EndsWith(" key") || n.Contains(" case key"))
            return "Keys";
        if (t.Contains("pin") || n.Contains(" collectible"))
            return "Pins";
        if (!string.IsNullOrEmpty(it.Exterior) || t.Contains("pistol") || t.Contains("rifle") ||
            t.Contains("smg") || t.Contains("shotgun") || t.Contains("machinegun") ||
            t.Contains("sniper") || t.Contains("weapon"))
            return "Weapons";
        return "Other";
    }

    public static bool IsCase(InventoryItem it)
    {
        var kind = Classify(it);
        return kind is "Cases" or "Capsules";
    }
}
