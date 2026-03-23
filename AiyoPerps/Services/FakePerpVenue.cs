using AiyoPerps.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace AiyoPerps.Services;

public class FakePerpVenue(string venueId) : IPerpVenue, IAccountStateProvider
{
    private readonly object _sync = new();
    private readonly Random _random = new();
    private readonly Dictionary<string, FakePositionState> _positions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FakeOrderState> _orders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, decimal> _leverages = new(StringComparer.OrdinalIgnoreCase);
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

    public Task<(bool IsSuccess, string Message)> ConfigureLeverageAsync(string symbol, decimal leverage, MarginMode marginMode, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            _leverages[NormalizeSymbol(symbol)] = Math.Max(1m, leverage);
        }

        return Task.FromResult((true, $"{VenueId} simulated {marginMode.ToApiValue()} leverage set to {leverage}x"));
    }

    public Task<OrderAck> PlaceOrderAsync(string symbol, string side, decimal qty, decimal? price, CancellationToken cancellationToken = default)
    {
        var normalizedSymbol = NormalizeSymbol(symbol);
        var leverage = GetLeverage(normalizedSymbol);
        var markPrice = ResolveMarkPrice(normalizedSymbol);
        var orderId = Guid.NewGuid().ToString("N");

        lock (_sync)
        {
            if (price.HasValue && price.Value > 0)
            {
                _orders[orderId] = new FakeOrderState(
                    orderId,
                    normalizedSymbol,
                    decimal.Round((price.Value <= 0 ? markPrice : price.Value) * qty, 2, MidpointRounding.AwayFromZero),
                    leverage,
                    price.Value,
                    "Open");
            }
            else
            {
                ApplyMarketFillUnsafe(normalizedSymbol, side, qty, markPrice, leverage);
            }
        }

        var ack = new OrderAck(DateTimeOffset.UtcNow, orderId, true, $"{VenueId} simulated order accepted");
        return Task.FromResult(ack);
    }

    public Task<OrderAck> PlaceCloseOrderAsync(string symbol, string side, decimal positionQty, decimal? price, CancellationToken cancellationToken = default)
    {
        var normalizedSymbol = NormalizeSymbol(symbol);
        var leverage = GetLeverage(normalizedSymbol);
        var markPrice = ResolveMarkPrice(normalizedSymbol);
        var orderId = Guid.NewGuid().ToString("N");

        lock (_sync)
        {
            if (price.HasValue && price.Value > 0)
            {
                _orders[orderId] = new FakeOrderState(
                    orderId,
                    normalizedSymbol,
                    decimal.Round((price.Value <= 0 ? markPrice : price.Value) * positionQty, 2, MidpointRounding.AwayFromZero),
                    leverage,
                    price.Value,
                    "Open");
            }
            else
            {
                ApplyMarketFillUnsafe(normalizedSymbol, side, positionQty, markPrice, leverage);
            }
        }

        var ack = new OrderAck(DateTimeOffset.UtcNow, orderId, true, $"{VenueId} simulated close order accepted");
        return Task.FromResult(ack);
    }

    public Task<OrderAck> CancelOrderAsync(string symbol, string orderId, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            _orders.Remove(orderId);
        }

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

    public Task<VenueAccountSnapshot> GetAccountSnapshotAsync(AccountSnapshotSections sections, CancellationToken cancellationToken = default)
    {
        List<VenuePosition> positions;
        List<VenueOpenOrder> orders;
        List<VenueBalance> balances;

        lock (_sync)
        {
            positions = sections.HasFlag(AccountSnapshotSections.Positions)
                ? _positions.Values.Select(ToVenuePosition).ToList()
                : [];
            orders = sections.HasFlag(AccountSnapshotSections.Orders)
                ? _orders.Values.Select(ToVenueOpenOrder).ToList()
                : [];
            balances = sections.HasFlag(AccountSnapshotSections.Balances)
                ? [new VenueBalance("USDT", 10000m, 10000m)]
                : [];
        }

        return Task.FromResult(new VenueAccountSnapshot(DateTimeOffset.UtcNow, positions, orders, balances));
    }

    public ValueTask DisposeAsync()
    {
        _connected = false;
        return ValueTask.CompletedTask;
    }

    private decimal GetLeverage(string symbol)
    {
        lock (_sync)
        {
            return _leverages.TryGetValue(symbol, out var leverage) && leverage > 0
                ? leverage
                : 5m;
        }
    }

    private void ApplyMarketFillUnsafe(string symbol, string side, decimal qty, decimal markPrice, decimal leverage)
    {
        if (qty <= 0)
        {
            return;
        }

        var signedQty = IsBuySide(side) ? qty : -qty;
        if (!_positions.TryGetValue(symbol, out var existing))
        {
            _positions[symbol] = new FakePositionState(symbol, signedQty, markPrice, leverage);
            return;
        }

        var nextQty = existing.Quantity + signedQty;
        if (nextQty == 0)
        {
            _positions.Remove(symbol);
            return;
        }

        if (Math.Sign(existing.Quantity) == Math.Sign(signedQty))
        {
            var weightedEntry = ((existing.EntryPrice * Math.Abs(existing.Quantity)) + (markPrice * Math.Abs(signedQty))) / Math.Abs(nextQty);
            _positions[symbol] = existing with
            {
                Quantity = nextQty,
                EntryPrice = decimal.Round(weightedEntry, 2, MidpointRounding.AwayFromZero),
                Leverage = leverage
            };
            return;
        }

        if (Math.Sign(existing.Quantity) == Math.Sign(nextQty))
        {
            _positions[symbol] = existing with
            {
                Quantity = nextQty,
                Leverage = leverage
            };
            return;
        }

        _positions[symbol] = new FakePositionState(symbol, nextQty, markPrice, leverage);
    }

    private static bool IsBuySide(string side)
    {
        return string.Equals(side?.Trim(), "buy", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(side?.Trim(), "long", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeSymbol(string symbol)
    {
        return (symbol ?? string.Empty).Trim().ToUpperInvariant();
    }

    private static decimal ResolveMarkPrice(string symbol)
    {
        var upper = NormalizeSymbol(symbol);
        if (upper.Contains("BTC", StringComparison.Ordinal))
        {
            return 68000m;
        }

        if (upper.Contains("ETH", StringComparison.Ordinal))
        {
            return 3500m;
        }

        if (upper.Contains("SOL", StringComparison.Ordinal))
        {
            return 145m;
        }

        return 120m;
    }

    private static VenuePosition ToVenuePosition(FakePositionState state)
    {
        var markPrice = ResolveMarkPrice(state.Symbol);
        var notionalUsd = decimal.Round(Math.Abs(state.Quantity) * markPrice, 2, MidpointRounding.AwayFromZero);
        var pnlUsd = decimal.Round((markPrice - state.EntryPrice) * state.Quantity, 2, MidpointRounding.AwayFromZero);
        var pnlPct = state.EntryPrice <= 0 || state.Quantity == 0
            ? 0m
            : decimal.Round(((markPrice - state.EntryPrice) / state.EntryPrice) * (state.Quantity > 0 ? 100m : -100m), 2, MidpointRounding.AwayFromZero);
        return new VenuePosition(
            state.Symbol,
            state.Quantity,
            notionalUsd,
            state.Leverage,
            state.EntryPrice,
            markPrice,
            pnlPct,
            pnlUsd,
            0m,
            MarginMode.Cross);
    }

    private static VenueOpenOrder ToVenueOpenOrder(FakeOrderState state)
    {
        return new VenueOpenOrder(
            state.Symbol,
            state.NotionalUsd,
            state.Leverage,
            state.LimitPrice,
            state.Status,
            state.OrderId,
            MarginMode.Cross);
    }

    private sealed record FakePositionState(string Symbol, decimal Quantity, decimal EntryPrice, decimal Leverage);

    private sealed record FakeOrderState(string OrderId, string Symbol, decimal NotionalUsd, decimal Leverage, decimal? LimitPrice, string Status);
}
