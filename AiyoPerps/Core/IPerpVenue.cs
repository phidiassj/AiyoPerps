using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AiyoPerps.Core;

public interface IPerpVenue : IAsyncDisposable
{
    string VenueId { get; }
    Task ConnectMarketDataAsync(IEnumerable<string> subscriptions, CancellationToken cancellationToken = default);
    Task DisconnectMarketDataAsync(CancellationToken cancellationToken = default);
    Task<(bool IsSuccess, string Message)> ConfigureLeverageAsync(string symbol, decimal leverage, CancellationToken cancellationToken = default);
    Task<OrderAck> PlaceOrderAsync(string symbol, string side, decimal qty, decimal? price, CancellationToken cancellationToken = default);
    Task<OrderAck> PlaceCloseOrderAsync(string symbol, string side, decimal positionQty, decimal? price, CancellationToken cancellationToken = default);
    Task<OrderAck> CancelOrderAsync(string symbol, string orderId, CancellationToken cancellationToken = default);
    Task<(bool IsSuccess, string Message)> ValidateConnectionAsync(CancellationToken cancellationToken = default);
    IAsyncEnumerable<MarketEvent> MarketEvents(CancellationToken cancellationToken = default);
}
