namespace AiyoPerps.Core;

public static class PositionPnlMath
{
    public static decimal ComputeUnrealizedPnlPct(decimal notionalUsd, decimal unrealizedPnlUsd)
    {
        return notionalUsd > 0m && unrealizedPnlUsd != 0m
            ? (unrealizedPnlUsd / notionalUsd) * 100m
            : 0m;
    }

    public static decimal ComputeDirectionalPnlPct(decimal quantity, decimal entryPrice, decimal markPrice)
    {
        if (entryPrice <= 0m || markPrice <= 0m || quantity == 0m)
        {
            return 0m;
        }

        return quantity < 0m
            ? ((entryPrice - markPrice) / entryPrice) * 100m
            : ((markPrice - entryPrice) / entryPrice) * 100m;
    }

    public static decimal ComputeUnrealizedPnlPctOrDirectional(decimal notionalUsd, decimal unrealizedPnlUsd, decimal quantity, decimal entryPrice, decimal markPrice)
    {
        var pct = ComputeUnrealizedPnlPct(notionalUsd, unrealizedPnlUsd);
        return pct != 0m ? pct : ComputeDirectionalPnlPct(quantity, entryPrice, markPrice);
    }
}
