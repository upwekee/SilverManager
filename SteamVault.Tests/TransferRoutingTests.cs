using SteamVault.Models;
using SteamVault.Services;
using Xunit;

namespace SteamVault.Tests;

public class TransferRoutingTests
{
    [Fact]
    public void Global_warehouse_wins()
    {
        var src = new SteamAccount { Login = "farm1", GroupName = "A", Id = "1" };
        var wh = new SteamAccount { Login = "wh", Id = "w", OwnTradeUrl = "https://steamcommunity.com/tradeoffer/new/?partner=1&token=aaa" };
        var group = new AccountGroup { Name = "A", TradeUrl = "https://steamcommunity.com/tradeoffer/new/?partner=9&token=zzz" };
        var route = TransferRouting.ResolveRoute(src, wh, [group], [src, wh], "https://fallback", null, false, true);
        Assert.Equal(wh.OwnTradeUrl, route);
    }

    [Fact]
    public void Group_warehouse_used_when_route_by_group()
    {
        var src = new SteamAccount { Login = "farm1", GroupName = "FarmA", Id = "1" };
        var gwh = new SteamAccount { Login = "wh1", Id = "w1", OwnTradeUrl = "https://steamcommunity.com/tradeoffer/new/?partner=2&token=bbb" };
        var group = new AccountGroup { Name = "FarmA", WarehouseAccountId = "w1", TradeUrl = "https://steamcommunity.com/tradeoffer/new/?partner=9&token=zzz" };
        var route = TransferRouting.ResolveRoute(src, null, [group], [src, gwh], "https://fallback", null, false, true);
        Assert.Equal(gwh.OwnTradeUrl, route);
    }

    [Fact]
    public void Group_trade_url_fallback_when_no_warehouse_link()
    {
        var src = new SteamAccount { Login = "farm1", GroupName = "FarmA", Id = "1" };
        var group = new AccountGroup { Name = "FarmA", TradeUrl = "https://steamcommunity.com/tradeoffer/new/?partner=9&token=zzz" };
        var route = TransferRouting.ResolveRoute(src, null, [group], [src], "https://fallback", null, false, true);
        Assert.Equal(group.TradeUrl, route);
    }

    [Fact]
    public void Pasted_url_when_group_routing_off()
    {
        var src = new SteamAccount { Login = "farm1", GroupName = "FarmA", Id = "1" };
        var group = new AccountGroup { Name = "FarmA", TradeUrl = "https://steamcommunity.com/tradeoffer/new/?partner=9&token=zzz" };
        var route = TransferRouting.ResolveRoute(src, null, [group], [src], "https://fallback/url", null, false, false);
        Assert.Equal("https://fallback/url", route);
    }
}
