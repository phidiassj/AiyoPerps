namespace AiyoPerps.Services;

public sealed class ViewportService(OrderBookAutoHidePolicy policy)
{
    private readonly OrderBookAutoHidePolicy _policy = policy;

    public bool IsOrderBookVisible(double width)
    {
        return _policy.ShouldShowOrderBook(width);
    }
}
