using AiyoPerps.Services;
using System.Reflection;
using Xunit;

namespace AiyoPerps.Test;

public sealed class SymbolCatalogSyncServiceTests
{
    [Theory]
    [InlineData("xyz:MU")]
    [InlineData("W")]
    public void IsValidHyperliquidSymbol_AcceptsBuilderAndSingleCharacterSymbols(string symbol)
    {
        var method = typeof(SymbolCatalogSyncService).GetMethod(
            "IsValidHyperliquidSymbol",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        var valid = (bool)method!.Invoke(null, [symbol])!;

        Assert.True(valid);
    }

    [Theory]
    [InlineData("xyz:MU:EXTRA")]
    [InlineData("xyz:MU-USD")]
    public void IsValidHyperliquidSymbol_RejectsMalformedBuilderSymbols(string symbol)
    {
        var method = typeof(SymbolCatalogSyncService).GetMethod(
            "IsValidHyperliquidSymbol",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        var valid = (bool)method!.Invoke(null, [symbol])!;

        Assert.False(valid);
    }
}
