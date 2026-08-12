using SteamVault.Models;

namespace SteamVault.Services;

/// <summary>
/// Live portfolio breakdown from loaded inventory items.
/// Portfolio value always reflects current Items (after withdrawals removed).
/// </summary>
public static class PortfolioAnalytics
{
    private static readonly string[] CategoryOrder =
    [
        "Cases", "Capsules", "Weapons", "Knives", "Gloves",
        "Stickers", "Agents", "Keys", "Music", "Graffiti", "Pins", "Other"
    ];

    // Monochrome categorical ramp. With no hue to separate slices, separation is pure
    // luminance — every entry is a visible step apart, brightest first. Order encodes
    // relevance to a case farmer: cases and keys read loudest, junk fades into the page.
    // Red is deliberately absent here: in this UI red only ever means "something failed".
    private static readonly Dictionary<string, string> CategoryColors = new()
    {
        ["Cases"] = "#FFFFFF",
        ["Keys"] = "#DEDEE3",
        ["Weapons"] = "#C2C2C9",
        ["Knives"] = "#A8A8B0",
        ["Gloves"] = "#92929A",
        ["Capsules"] = "#7E7E87",
        ["Stickers"] = "#6C6C75",
        ["Agents"] = "#5C5C64",
        ["Music"] = "#4E4E55",
        ["Graffiti"] = "#42424A",
        ["Pins"] = "#3A3A42",
        ["Other"] = "#33333A",
    };

    public static List<PortfolioItemRow> BuildCaseRows(IEnumerable<InventoryItem> items, int top = 40)
    {
        var cases = items.Where(ItemClassifier.IsCase).ToList();
        return Aggregate(cases, top, byCount: true);
    }

    public static List<PortfolioItemRow> BuildTopSkins(IEnumerable<InventoryItem> items, int top = 30)
    {
        // expensive single items (not cases)
        var skins = items
            .Where(i => !ItemClassifier.IsCase(i) && i.Price > 0)
            .OrderByDescending(i => i.Price)
            .Take(top)
            .Select(i => new PortfolioItemRow
            {
                Name = i.MarketHashName,
                Kind = ItemClassifier.Classify(i),
                Count = 1,
                TotalValue = i.Price,
                UnitPrice = i.Price,
                ImageUrl = i.ImageUrl,
                RarityAccentSource = i.RarityAccentSource,
                AccountCount = 1,
                BarRatio = 0
            })
            .ToList();

        var max = skins.FirstOrDefault()?.UnitPrice ?? 1m;
        if (max <= 0) max = 1;
        foreach (var r in skins)
            r.BarRatio = (double)(r.UnitPrice / max);
        return skins;
    }

    public static List<PortfolioItemRow> BuildTopAggregated(IEnumerable<InventoryItem> items, int top = 25)
    {
        // group by market name (all types) by total value
        return Aggregate(items, top, byCount: false);
    }

    public static List<PortfolioCategoryRow> BuildCategories(IEnumerable<InventoryItem> items)
    {
        var list = items.ToList();
        var groups = list
            .GroupBy(ItemClassifier.Classify)
            .Select(g => new PortfolioCategoryRow
            {
                Name = g.Key,
                Count = g.Sum(x => Math.Max(1, x.Amount)),
                Value = g.Sum(x => x.Price * Math.Max(1, x.Amount)),
                Color = CategoryColors.GetValueOrDefault(g.Key, "#6C6C75")
            })
            .ToList();

        // ensure order + empty categories skipped
        var ordered = CategoryOrder
            .Select(name => groups.FirstOrDefault(g => g.Name == name))
            .Where(g => g != null && g.Count > 0)
            .Cast<PortfolioCategoryRow>()
            .Concat(groups.Where(g => !CategoryOrder.Contains(g.Name) && g.Count > 0))
            .ToList();

        var max = ordered.Count == 0 ? 1m : ordered.Max(c => c.Value);
        if (max <= 0) max = 1;
        var totalCount = ordered.Sum(c => c.Count);
        if (totalCount <= 0) totalCount = 1;
        foreach (var c in ordered)
        {
            c.BarRatio = (double)(c.Value / max);
            c.Percent = 100.0 * c.Count / totalCount;
        }
        return ordered;
    }

    /// <summary>Top N distinct items by quantity — for Home donut “how many of which skins”.</summary>
    public static List<PortfolioItemRow> BuildCountBreakdown(IEnumerable<InventoryItem> items, int top = 8)
    {
        var rows = Aggregate(items, top, byCount: true);
        var total = Math.Max(1, items.Sum(i => Math.Max(1, i.Amount)));
        // Slices arrive sorted by size, so a descending ramp makes rank readable at a
        // glance: the fattest slice is the brightest, the tail fades toward the page.
        var colors = new[]
        {
            "#FFFFFF", "#DEDEE3", "#C2C2C9", "#A8A8B0", "#92929A",
            "#7E7E87", "#6C6C75", "#5C5C64", "#4E4E55", "#42424A"
        };
        for (var i = 0; i < rows.Count; i++)
        {
            rows[i].Percent = 100.0 * rows[i].Count / total;
            rows[i].Color = colors[i % colors.Length];
        }
        return rows;
    }

    public static (decimal live, int items, int cases, int accountsWithInv) LiveTotals(IEnumerable<InventoryItem> items, IEnumerable<SteamAccount> accounts)
    {
        var list = items.ToList();
        var live = list.Sum(i => i.Price * Math.Max(1, i.Amount));
        var itemCount = list.Sum(i => Math.Max(1, i.Amount));
        var cases = list.Where(ItemClassifier.IsCase).Sum(i => Math.Max(1, i.Amount));
        var acc = accounts.Count(a => a.InventoryCount > 0 || a.InventoryValue > 0);
        return (live, itemCount, cases, acc);
    }

    /// <summary>CS2 rarity buckets (Consumer, Mil-Spec, Restricted, …) by item count.</summary>
    public static List<QualityStatRow> BuildRarityRows(IEnumerable<InventoryItem> items)
    {
        // Canonical rarity order (bright → rare). Unknown tags fall into Other.
        var order = new[]
        {
            "Consumer Grade", "Industrial Grade", "Mil-Spec Grade", "Restricted",
            "Classified", "Covert", "Extraordinary", "Contraband", "Base Grade",
            "High Grade", "Remarkable", "Exotic", "Extraordinary", "Other"
        };
        // de-dupe Extraordinary once
        order = order.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        var buckets = new Dictionary<string, (int count, decimal value)>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in order) buckets[key] = (0, 0m);

        foreach (var it in items)
        {
            var key = NormalizeRarity(it.Rarity);
            if (!buckets.ContainsKey(key)) key = "Other";
            var amt = Math.Max(1, it.Amount);
            var b = buckets[key];
            buckets[key] = (b.count + amt, b.value + it.Price * amt);
        }

        var total = Math.Max(1, buckets.Values.Sum(v => v.count));
        var max = Math.Max(1, buckets.Values.Max(v => v.count));
        return order
            .Where(k => buckets.TryGetValue(k, out var v) && v.count > 0)
            .Select(k =>
            {
                var v = buckets[k];
                return new QualityStatRow
                {
                    Name = k,
                    Count = v.count,
                    Value = v.value,
                    Percent = 100.0 * v.count / total,
                    BarRatio = (double)v.count / max
                };
            })
            .ToList();
    }

    private static string NormalizeRarity(string? rarity)
    {
        if (string.IsNullOrWhiteSpace(rarity)) return "Other";
        var r = rarity.Trim();
        // Steam sometimes sends "Mil-Spec Grade" / "Milspec" variants
        if (r.Contains("Consumer", StringComparison.OrdinalIgnoreCase)) return "Consumer Grade";
        if (r.Contains("Industrial", StringComparison.OrdinalIgnoreCase)) return "Industrial Grade";
        if (r.Contains("Mil", StringComparison.OrdinalIgnoreCase) && r.Contains("Spec", StringComparison.OrdinalIgnoreCase))
            return "Mil-Spec Grade";
        if (r.Contains("Restricted", StringComparison.OrdinalIgnoreCase)) return "Restricted";
        if (r.Contains("Classified", StringComparison.OrdinalIgnoreCase)) return "Classified";
        if (r.Contains("Covert", StringComparison.OrdinalIgnoreCase)) return "Covert";
        if (r.Contains("Contraband", StringComparison.OrdinalIgnoreCase)) return "Contraband";
        if (r.Contains("Extraordinary", StringComparison.OrdinalIgnoreCase)) return "Extraordinary";
        if (r.Contains("Base Grade", StringComparison.OrdinalIgnoreCase)) return "Base Grade";
        if (r.Contains("High Grade", StringComparison.OrdinalIgnoreCase)) return "High Grade";
        if (r.Contains("Remarkable", StringComparison.OrdinalIgnoreCase)) return "Remarkable";
        if (r.Contains("Exotic", StringComparison.OrdinalIgnoreCase)) return "Exotic";
        return r.Length > 28 ? r[..28] : r;
    }

    /// <summary>FN / MW / FT / WW / BS (+ Other) — kept for tests / optional use.</summary>
    public static List<QualityStatRow> BuildQualityRows(IEnumerable<InventoryItem> items)
    {
        var order = new[] { "FN", "MW", "FT", "WW", "BS", "Other" };
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["FN"] = "Factory New",
            ["MW"] = "Minimal Wear",
            ["FT"] = "Field-Tested",
            ["WW"] = "Well-Worn",
            ["BS"] = "Battle-Scarred",
            ["Other"] = "No wear / other"
        };

        var buckets = order.ToDictionary(k => k, _ => (count: 0, value: 0m));
        foreach (var it in items)
        {
            if (ItemClassifier.IsCase(it)) continue;
            var key = string.IsNullOrWhiteSpace(it.QualityText) ? "Other" : it.QualityText;
            if (!buckets.ContainsKey(key)) key = "Other";
            var amt = Math.Max(1, it.Amount);
            var b = buckets[key];
            buckets[key] = (b.count + amt, b.value + it.Price * amt);
        }

        var total = Math.Max(1, buckets.Values.Sum(v => v.count));
        var max = Math.Max(1, buckets.Values.Max(v => v.count));
        return order
            .Where(k => buckets[k].count > 0)
            .Select(k => new QualityStatRow
            {
                Name = labels.GetValueOrDefault(k, k),
                Count = buckets[k].count,
                Value = buckets[k].value,
                Percent = 100.0 * buckets[k].count / total,
                BarRatio = (double)buckets[k].count / max
            })
            .ToList();
    }

    public static (int withFloat, double? avg, double? min, double? max) FloatSummary(IEnumerable<InventoryItem> items)
    {
        var floats = items
            .Where(i => i.FloatValue is > 0 and < 1)
            .Select(i => i.FloatValue!.Value)
            .ToList();
        if (floats.Count == 0) return (0, null, null, null);
        return (floats.Count, floats.Average(), floats.Min(), floats.Max());
    }

    public static List<ChartBar> CategoryToBars(IEnumerable<PortfolioCategoryRow> cats)
    {
        var list = cats.ToList();
        var max = list.Count == 0 ? 1.0 : Math.Max(1, list.Max(c => (double)c.Value));
        return list.Select(c => new ChartBar
        {
            Label = c.Name.Length > 6 ? c.Name[..5] : c.Name,
            Value = (double)c.Value,
            Tip = $"{c.Name}: {c.Count} · ${c.Value:0.00}",
            Normalized = (double)c.Value / max
        }).ToList();
    }

    private static List<PortfolioItemRow> Aggregate(IEnumerable<InventoryItem> items, int top, bool byCount)
    {
        var rows = items
            .GroupBy(i => i.MarketHashName)
            .Select(g =>
            {
                var first = g.First();
                var count = g.Sum(x => Math.Max(1, x.Amount));
                var total = g.Sum(x => x.Price * Math.Max(1, x.Amount));
                return new PortfolioItemRow
                {
                    Name = g.Key,
                    Kind = ItemClassifier.Classify(first),
                    Count = count,
                    TotalValue = total,
                    UnitPrice = count > 0 ? total / count : first.Price,
                    ImageUrl = g.Select(x => x.ImageUrl).FirstOrDefault(u => !string.IsNullOrEmpty(u)),
                    RarityAccentSource = g.Select(x => x.RarityAccentSource).FirstOrDefault(r => !string.IsNullOrWhiteSpace(r)),
                    AccountCount = g.Select(x => x.AccountId).Distinct().Count()
                };
            })
            .ToList();

        rows = byCount
            ? rows.OrderByDescending(r => r.Count).ThenByDescending(r => r.TotalValue).Take(top).ToList()
            : rows.OrderByDescending(r => r.TotalValue).ThenByDescending(r => r.Count).Take(top).ToList();

        var max = byCount
            ? (rows.FirstOrDefault()?.Count ?? 1)
            : (double)(rows.FirstOrDefault()?.TotalValue ?? 1m);
        if (max <= 0) max = 1;
        foreach (var r in rows)
            r.BarRatio = byCount ? r.Count / max : (double)r.TotalValue / max;
        return rows;
    }
}
