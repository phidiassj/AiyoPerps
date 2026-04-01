using AiyoPerps.Services;
using System.Reflection;
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
}
