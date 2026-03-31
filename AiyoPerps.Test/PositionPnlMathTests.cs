using AiyoPerps.Core;
using Xunit;

namespace AiyoPerps.Test;

public sealed class PositionPnlMathTests
{
    [Fact]
    public void ComputeUnrealizedPnlPct_UsesNotionalWithoutLeverage()
    {
        var actual = PositionPnlMath.ComputeUnrealizedPnlPct(51.82812m, 0.725201m);

        Assert.Equal(1.3992423418020950788876771900m, actual);
    }

    [Fact]
    public void ComputeDirectionalPnlPct_UsesPriceChangeDirection()
    {
        var longPct = PositionPnlMath.ComputeDirectionalPnlPct(16.9m, 9.02781m, 9.09135m);
        var shortPct = PositionPnlMath.ComputeDirectionalPnlPct(-2m, 100m, 95m);

        Assert.Equal(0.7038251801932030027215902900m, longPct);
        Assert.Equal(5m, shortPct);
    }

    [Fact]
    public void ComputeUnrealizedPnlPctOrDirectional_PrefersPnlOverDirectionalFallback()
    {
        var actual = PositionPnlMath.ComputeUnrealizedPnlPctOrDirectional(
            153.64635m,
            1.076306m,
            16.9m,
            9.02781m,
            9.09135m);

        Assert.Equal(0.7005086681200041523928163600m, actual);
    }
}
