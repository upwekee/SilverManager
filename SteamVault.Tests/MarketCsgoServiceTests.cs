using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using SteamVault.Services;
using Xunit;

namespace SteamVault.Tests;

public class MarketCsgoServiceTests
{
    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }

    [Fact]
    public async Task GetMoneyAsync_ParsesAvailableAndFrozenBalance()
    {
        var json = @"{
            ""money"": 1500.50,
            ""money_settlement"": 350.00,
            ""currency"": ""RUB"",
            ""success"": true
        }";

        var handler = new MockHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json)
        });

        var client = new HttpClient(handler);
        var svc = new MarketCsgoService(client);

        var info = await svc.GetMoneyAsync("test_api_key");

        Assert.True(info.Success);
        Assert.Equal(1500.50m, info.Available);
        Assert.Equal(350.00m, info.Settlement);
        Assert.Equal("RUB", info.Currency);
    }

    [Fact]
    public async Task MoneySendAsync_FormatsUrlAndSendsRequest()
    {
        string? requestedUrl = null;

        var handler = new MockHttpMessageHandler(req =>
        {
            requestedUrl = req.RequestUri?.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(@"{""success"": true}")
            };
        });

        var client = new HttpClient(handler);
        var svc = new MarketCsgoService(client);

        var result = await svc.MoneySendAsync("sender_key_123", "target_key_456", 150000, "secret_pay_pass");

        Assert.True(result.Success);
        Assert.NotNull(requestedUrl);
        Assert.Contains("https://market.csgo.com/api/v2/money-send/150000/target_key_456", requestedUrl);
        Assert.Contains("pay_pass=secret_pay_pass", requestedUrl);
        Assert.Contains("key=sender_key_123", requestedUrl);
    }

    [Fact]
    public async Task AddToSaleAsync_FormatsPriceAndAssetId()
    {
        string? requestedUrl = null;

        var handler = new MockHttpMessageHandler(req =>
        {
            requestedUrl = req.RequestUri?.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(@"{""success"": true, ""item_id"": ""99887766""}")
            };
        });

        var client = new HttpClient(handler);
        var svc = new MarketCsgoService(client);

        var result = await svc.AddToSaleAsync("api_key_abc", "asset_12345", 25000, "RUB");

        Assert.True(result.Success);
        Assert.Equal("99887766", result.ItemId);
        Assert.NotNull(requestedUrl);
        Assert.Contains("https://market.csgo.com/api/v2/add-to-sale", requestedUrl);
        Assert.Contains("id=asset_12345", requestedUrl);
        Assert.Contains("price=25000", requestedUrl);
    }
}
