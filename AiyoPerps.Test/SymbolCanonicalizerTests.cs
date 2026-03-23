using AiyoPerps.Services;
using Xunit;

namespace AiyoPerps.Test;

public sealed class SymbolCanonicalizerTests
{
    [Theory]
    [InlineData("XBTUSD", "BTC-USD")]
    [InlineData("BTCUSDT", "BTC-USDT")]
    [InlineData("BTC_USDT_PERP", "BTC-USDT")]
    [InlineData("BTC-USD", "BTC-USD")]
    public void Format_GenericKnownVenuePatterns_ReturnsUnifiedDisplay(string rawSymbol, string expected)
    {
        var actual = SymbolCanonicalizer.Format(rawSymbol);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Format_HyperliquidVenueDefaultQuote_ReturnsUsdcDisplay()
    {
        var actual = SymbolCanonicalizer.Format("Hyperliquid", "BTC");

        Assert.Equal("BTC-USDC", actual);
    }

    [Fact]
    public void Describe_BitMexMetadata_NormalizesAssetAliasAndCanonicalKey()
    {
        var descriptor = SymbolCanonicalizer.Describe(
            "BitMEX",
            "XBTUSD",
            baseAsset: "XBT",
            quoteAsset: "USD",
            settleAsset: "XBT",
            contractType: "PERPETUAL");

        Assert.Equal("BTC-USD", descriptor.DisplaySymbol);
        Assert.Equal("PERP:BTC:USD:BTC", descriptor.CanonicalKey);
        Assert.Equal("BTC", descriptor.BaseAsset);
        Assert.Equal("USD", descriptor.QuoteAsset);
        Assert.Equal("BTC", descriptor.SettleAsset);
    }
}
