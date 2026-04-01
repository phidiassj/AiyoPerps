using AiyoPerps.Services;
using AiyoPerps.Models;
using AiyoPerps.Core;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace AiyoPerps.Test;

public sealed class HyperliquidVenueAdapterTests
{
    [Theory]
    [InlineData("Order was never placed, already canceled, or filled. asset=140002")]
    [InlineData("Order was never placed, already cancelled, or filled. asset=140002")]
    [InlineData("already canceled")]
    public void IsIdempotentCancelRejection_RecognizesAlreadyClosedOrderMessages(string message)
    {
        var method = typeof(HyperliquidVenueAdapter).GetMethod("IsIdempotentCancelRejection", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var actual = method!.Invoke(null, [message]);

        Assert.IsType<bool>(actual);
        Assert.True((bool)actual!);
    }

    [Fact]
    public async Task ResolveAssetIndexAsync_ShouldKeepDefaultAndBuilderDexAssetsSeparate()
    {
        await using var venue = new HyperliquidVenueAdapter("mainnet", new AccountCredentials(), new AppLogger());
        SetPrivateField(venue, "_assetByCoin", new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase)
        {
            ["HYPE"] = 51,
            ["HYNA:HYPE"] = 140002
        });
        SetPrivateField(venue, "_dexByCoin", new Dictionary<string, string?>(System.StringComparer.OrdinalIgnoreCase)
        {
            ["HYPE"] = null,
            ["HYNA:HYPE"] = "hyna"
        });

        var method = typeof(HyperliquidVenueAdapter).GetMethod("ResolveAssetIndexAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var defaultTask = Assert.IsAssignableFrom<Task<int>>(method!.Invoke(venue, ["HYPE", CancellationToken.None]));
        var builderTask = Assert.IsAssignableFrom<Task<int>>(method.Invoke(venue, ["hyna:HYPE", CancellationToken.None]));

        Assert.Equal(51, await defaultTask);
        Assert.Equal(140002, await builderTask);
    }

    [Fact]
    public async Task ParsePositionsFromClearinghouseState_ShouldReadMultiDexSnapshot()
    {
        const string json = """
            {
              "marginSummary":{"accountValue":"376.093782"},
              "assetPositions":[
                {"type":"oneWay","position":{"coin":"SOL","szi":"1.8","leverage":{"type":"cross","value":10},"entryPx":"83.161","positionValue":"149.7528","unrealizedPnl":"0.063","marginUsed":"14.97528"}},
                {"type":"oneWay","position":{"coin":"HYPE","szi":"14.11","leverage":{"type":"cross","value":10},"entryPx":"37.7021","positionValue":"524.28527","unrealizedPnl":"-7.691703","marginUsed":"52.428527"}}
              ]
            }
            """;

        await using var venue = new HyperliquidVenueAdapter("mainnet", new AccountCredentials(), new AppLogger());
        using var doc = JsonDocument.Parse(json);
        var method = typeof(HyperliquidVenueAdapter).GetMethod("ParsePositionsFromClearinghouseState", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var result = Assert.IsAssignableFrom<List<VenuePosition>>(method!.Invoke(venue, [doc.RootElement, null]));

        Assert.Equal(2, result.Count);
        Assert.Equal("SOL", result[0].Symbol);
        Assert.InRange(result[0].MarkPrice, 83.19m, 83.20m);
        Assert.Equal("HYPE", result[1].Symbol);
    }

    [Fact]
    public async Task ParseAccountStateMessage_ShouldUpdateWsCaches()
    {
        const string midsMessage = """
            {
              "channel":"allMids",
              "data":{"dex":"xyz","mids":{"xyz:MU":"348.085"}}
            }
            """;
        const string ordersMessage = """
            {
              "channel":"openOrders",
              "data":{
                "dex":"xyz",
                "user":"0xabc",
                "orders":[{"coin":"MU","oid":"366","sz":"0.5","limitPx":"340","status":"open"}]
              }
            }
            """;
        const string positionsMessage = """
            {
              "channel":"allDexsClearinghouseState",
              "data":{
                "user":"0xabc",
                "clearinghouseStates":[
                  ["xyz",{"marginSummary":{"accountValue":"12.5"},"assetPositions":[{"type":"oneWay","position":{"coin":"MU","szi":"0.5","leverage":{"type":"cross","value":5},"entryPx":"300","positionValue":"174.0425","unrealizedPnl":"24.0425","marginUsed":"34.8085"}}]}]
                ]
              }
            }
            """;

        await using var venue = new HyperliquidVenueAdapter("mainnet", new AccountCredentials(), new AppLogger());
        var method = typeof(HyperliquidVenueAdapter).GetMethod("ParseAccountStateMessage", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        method!.Invoke(venue, [midsMessage]);
        method.Invoke(venue, [ordersMessage]);
        method.Invoke(venue, [positionsMessage]);

        var mids = Assert.IsAssignableFrom<IReadOnlyDictionary<string, decimal>>(GetPrivateField(venue, "_allMidsCache"));
        var orders = Assert.IsAssignableFrom<IReadOnlyList<VenueOpenOrder>>(GetPrivateField(venue, "_openOrdersCache"));
        var positions = Assert.IsAssignableFrom<IReadOnlyList<VenuePosition>>(GetPrivateField(venue, "_positionsCache"));
        var perpUsd = Assert.IsType<decimal>(GetPrivateField(venue, "_perpAccountUsdCache"));

        Assert.Equal(348.085m, mids["xyz:MU"]);
        Assert.Single(orders);
        Assert.Equal("xyz:MU", orders[0].Symbol);
        Assert.Single(positions);
        Assert.Equal("xyz:MU", positions[0].Symbol);
        Assert.Equal(12.5m, perpUsd);
    }

    private static void SetPrivateField(object target, string fieldName, object value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(target, value);
    }

    private static object GetPrivateField(object target, string fieldName)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return field!.GetValue(target)!;
    }
}
