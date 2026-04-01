using AiyoPerps.Services;
using AiyoPerps.Models;
using System.Collections.Generic;
using System.Reflection;
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

    private static void SetPrivateField(object target, string fieldName, object value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(target, value);
    }
}
