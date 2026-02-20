using AiyoPerps.Core;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace AiyoPerps.Services;

public class FakePerpVenue(string venueId) : IPerpVenue
{
    private readonly Random _random = new();
    private bool _connected;

    public string VenueId { get; } = venueId;

    public Task ConnectMarketDataAsync(IEnumerable<string> subscriptions, CancellationToken cancellationToken = default)
    {
        _connected = true;
        return Task.CompletedTask;
    }

    public Task DisconnectMarketDataAsync(CancellationToken cancellationToken = default)
    {
        _connected = false;
        return Task.CompletedTask;
    }

    public Task<(bool IsSuccess, string Message)> ConfigureLeverageAsync(string symbol, decimal leverage, CancellationToken cancellationToken = default)
    {
        return Task.FromResult((true, $"{VenueId} simulated leverage set to {leverage}x"));
    }

    public Task<OrderAck> PlaceOrderAsync(string symbol, string side, decimal qty, decimal? price, CancellationToken cancellationToken = default)
    {
        var ack = new OrderAck(DateTimeOffset.UtcNow, Guid.NewGuid().ToString("N"), true, $"{VenueId} simulated order accepted");
        return Task.FromResult(ack);
    }

    public Task<OrderAck> PlaceCloseOrderAsync(string symbol, string side, decimal positionQty, decimal? price, CancellationToken cancellationToken = default)
    {
        var ack = new OrderAck(DateTimeOffset.UtcNow, Guid.NewGuid().ToString("N"), true, $"{VenueId} simulated close order accepted");
        return Task.FromResult(ack);
    }

    public Task<OrderAck> CancelOrderAsync(string symbol, string orderId, CancellationToken cancellationToken = default)
    {
        var ack = new OrderAck(DateTimeOffset.UtcNow, orderId, true, $"{VenueId} simulated cancel accepted");
        return Task.FromResult(ack);
    }

    public virtual Task<(bool IsSuccess, string Message)> ValidateConnectionAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult((true, $"{VenueId} stub connection ok"));
    }

    public async IAsyncEnumerable<MarketEvent> MarketEvents([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_connected)
            {
                yield return new TradeTick(
                    DateTimeOffset.UtcNow,
                    _random.Next(90000, 120000),
                    _random.Next(1, 20));
            }

            await Task.Delay(1000, cancellationToken);
        }
    }

    public ValueTask DisposeAsync()
    {
        _connected = false;
        return ValueTask.CompletedTask;
    }
}
