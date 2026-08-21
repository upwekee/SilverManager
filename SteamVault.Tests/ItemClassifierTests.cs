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

    [Fact]
    public void HoldBadgeText_formats_correctly_when_on_hold()
    {
        var futureDate = DateTime.UtcNow.AddDays(3).AddHours(14);
        var item = new InventoryItem
        {
            Tradable = false,
            Marketable = true,
            TradableAfter = futureDate
        };

        Assert.True(item.IsOnTradeHold);
        Assert.False(item.IsPermanentlyUntradable);
        Assert.StartsWith("🔒 3d 1", item.HoldBadgeText);
    }

    [Fact]
    public void Trade_protected_cs2_skins_are_on_trade_hold()
    {
        var p250 = new InventoryItem
        {
            Name = "P250 | Copper Oxide",
            Exterior = "Battle-Scarred",
            Tradable = false,
            Marketable = false,
            TradableAfter = DateTime.UtcNow.AddDays(4)
        };

        Assert.False(p250.IsPermanentlyUntradable);
        Assert.True(p250.IsOnTradeHold);
        Assert.StartsWith("🔒 3d 2", p250.HoldBadgeText);
    }

    [Fact]
    public void Permanently_untradable_items_do_not_have_trade_hold()
    {
        var serviceMedal = new InventoryItem
        {
            Tradable = false,
            Marketable = false,
            TradableAfter = null
        };
        Assert.True(serviceMedal.IsPermanentlyUntradable);
        Assert.False(serviceMedal.IsOnTradeHold);
        Assert.Equal("", serviceMedal.HoldBadgeText);
    }

    [Fact]
    public void Parse_maFile_with_number_steamid()
    {
        var json = @"{
            ""shared_secret"": ""SBDpGuk+o1PR3meEh15ywa1nlEQ="",
            ""identity_secret"": ""xw699f7aXypcKO6OnJzRr5WWuq8="",
            ""account_name"": ""ric0na"",
            ""steamid"": 76561199851183833,
            ""Session"": {
                ""SteamID"": 76561199851183833,
                ""SteamLogin"": ""ric0na""
            }
        }";

        var ma = SteamVault.Models.MaFile.Parse(json);
        Assert.NotNull(ma);
        Assert.Equal("SBDpGuk+o1PR3meEh15ywa1nlEQ=", ma.SharedSecret);
        Assert.Equal("xw699f7aXypcKO6OnJzRr5WWuq8=", ma.IdentitySecret);
        Assert.Equal("ric0na", ma.AccountName);
        Assert.Equal("76561199851183833", ma.SteamId);
        Assert.Equal(76561199851183833UL, ma.Session?.SteamId);
    }

    [Fact]
    public void Parse_maFile_with_string_steamid()
    {
        var json = @"{
            ""shared_secret"": ""abc="",
            ""identity_secret"": ""def="",
            ""account_name"": ""testacc"",
            ""steamid"": ""76561199851183833"",
            ""Session"": {
                ""SteamID"": ""76561199851183833""
            }
        }";

        var ma = SteamVault.Models.MaFile.Parse(json);
        Assert.NotNull(ma);
        Assert.Equal("76561199851183833", ma.SteamId);
        Assert.Equal(76561199851183833UL, ma.Session?.SteamId);
    }
}
