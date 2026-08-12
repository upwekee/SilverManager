using SteamVault.Models;
using SteamVault.Services;
using Xunit;

namespace SteamVault.Tests;

public class PortfolioAnalyticsTests
{
    [Fact]
    public void BuildQualityRows_groups_wear()
    {
        var items = new[]
        {
            new InventoryItem { MarketHashName = "A", Exterior = "Factory New", Price = 1, Amount = 1 },
            new InventoryItem { MarketHashName = "B", Exterior = "Factory New", Price = 2, Amount = 1 },
            new InventoryItem { MarketHashName = "C", Exterior = "Field-Tested", Price = 3, Amount = 1 },
            new InventoryItem { MarketHashName = "Revolution Case", Type = "Base Grade Container", Price = 1 },
        };

        var rows = PortfolioAnalytics.BuildQualityRows(items);
        Assert.Contains(rows, r => r.Name.Contains("Factory") && r.Count == 2);
        Assert.Contains(rows, r => r.Name.Contains("Field") && r.Count == 1);
        Assert.DoesNotContain(rows, r => r.Name.Contains("Revolution"));
    }

    [Fact]
    public void FloatSummary_empty_when_no_floats()
    {
        var items = new[] { new InventoryItem { MarketHashName = "x", Price = 1 } };
        var s = PortfolioAnalytics.FloatSummary(items);
        Assert.Equal(0, s.withFloat);
        Assert.Null(s.avg);
    }

    [Fact]
    public void FloatSummary_averages()
    {
        var items = new[]
        {
            new InventoryItem { MarketHashName = "a", FloatValue = 0.1 },
            new InventoryItem { MarketHashName = "b", FloatValue = 0.3 },
        };
        var s = PortfolioAnalytics.FloatSummary(items);
        Assert.Equal(2, s.withFloat);
        Assert.Equal(0.2, s.avg!.Value, 5);
    }
}
