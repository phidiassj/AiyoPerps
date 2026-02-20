namespace AiyoPerps.Services;

public sealed class OrderBookAutoHidePolicy
{
    public double MinChartWidth { get; init; } = 640;
    public double MinOrderEntryWidth { get; init; } = 320;
    public double MinOrderBookWidth { get; init; } = 280;
    public double SplitterWidth { get; init; } = 4;
    public double Padding { get; init; } = 24;

    public bool ShouldShowOrderBook(double availableWidth)
    {
        var threshold = MinChartWidth + MinOrderEntryWidth + MinOrderBookWidth + (SplitterWidth * 2) + Padding;
        return availableWidth >= threshold;
    }
}
