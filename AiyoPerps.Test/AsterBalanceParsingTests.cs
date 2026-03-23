using AiyoPerps.Core;
using AiyoPerps.Services;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace AiyoPerps.Test;

public sealed class AsterBalanceParsingTests
{
    [Fact]
    public void ParseBalancesFromAccount_FiltersOutZeroQuantityRows()
    {
        using var document = JsonDocument.Parse("""
        {
          "assets": [
            {
              "asset": "USDT",
              "walletBalance": "100.5",
              "availableBalance": "100.5",
              "marginBalance": "100.5",
              "crossWalletBalance": "100.5"
            },
            {
              "asset": "BTC",
              "walletBalance": "0",
              "availableBalance": "0",
              "marginBalance": "25",
              "crossWalletBalance": "25"
            }
          ]
        }
        """);

        var parseBalances = typeof(AsterVenueAdapter).GetMethod(
            "ParseBalancesFromAccount",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(parseBalances);

        var balances = (parseBalances!.Invoke(null, [document.RootElement]) as System.Collections.IEnumerable)!
            .Cast<VenueBalance>()
            .ToList();

        var balance = Assert.Single(balances);
        Assert.Equal("USDT", balance.Asset);
        Assert.Equal(100.5m, balance.Quantity);
    }
}
