using SteamVault.Services;
using Xunit;

namespace SteamVault.Tests;

public class ProxyHelperTests
{
    [Theory]
    [InlineData("1.2.3.4:8080", true)]
    [InlineData("user:pass@1.2.3.4:8080", true)]
    [InlineData("http://1.2.3.4:8080", true)]
    [InlineData("", false)]
    [InlineData(":::", false)]
    public void IsValid_formats(string proxy, bool expected)
    {
        Assert.Equal(expected, ProxyHelper.IsValid(proxy));
    }

    [Fact]
    public void ParseLines_splits_and_trims()
    {
        var lines = ProxyHelper.ParseLines("a:1\n\nuser:pass@b:2\r\nc:3");
        Assert.Equal(3, lines.Count);
    }

    [Fact]
    public void Distribute_round_robin_covers_all()
    {
        var accounts = Enumerable.Range(0, 5).Select(i => new Models.SteamAccount { Login = $"a{i}" }).ToList();
        var pool = new List<string> { "1.1.1.1:80", "2.2.2.2:80" };
        ProxyHelper.Distribute(accounts, pool);
        Assert.All(accounts, a => Assert.True(a.HasProxy));
        Assert.Equal(2, accounts.Select(a => a.Proxy).Distinct().Count());
    }
}
