using SteamVault.Models;
using Xunit;

namespace SteamVault.Tests;

public class ItemClassifierTests
{
    [Theory]
    [InlineData("Revolution Case", "Base Grade Container", "Cases")]
    [InlineData("Sticker | Titan | Katowice 2014", "Extraordinary Sticker", "Stickers")]
    [InlineData("★ Karambit | Doppler (Factory New)", "Covert Knife", "Knives")]
    [InlineData("AK-47 | Redline (Field-Tested)", "Classified Rifle", "Weapons")]
    public void Classify_known_types(string name, string type, string expected)
    {
        var item = new InventoryItem { MarketHashName = name, Type = type };
        Assert.Equal(expected, ItemClassifier.Classify(item));
    }

    [Fact]
    public void IsCase_true_for_cases_and_capsules()
    {
        var c = new InventoryItem { MarketHashName = "Kilowatt Case", Type = "Base Grade Container" };
        var cap = new InventoryItem { MarketHashName = "Sticker Capsule", Type = "High Grade Capsule" };
        Assert.True(ItemClassifier.IsCase(c));
        Assert.True(ItemClassifier.IsCase(cap));
        Assert.False(ItemClassifier.IsCase(new InventoryItem { MarketHashName = "AK-47 | Blue Laminate", Type = "Mil-Spec Rifle", Exterior = "Field-Tested" }));
    }
}
