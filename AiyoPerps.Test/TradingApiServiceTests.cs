using AiyoPerps.Core;
using AiyoPerps.Models;
using AiyoPerps.Services;
using AiyoPerps.Services.Api;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Xunit;

namespace AiyoPerps.Test;

public sealed class TradingApiServiceTests
{
    [Fact]
    public async Task OpenConnectionAsync_SeedsLatestPriceFromHistoricalCandles()
    {
        var logger = new AppLogger();
        var repository = new AccountRepository(new AesSecretProtector());
        var accountStore = new AccountStore(repository);
        var symbols = new SymbolCatalogRepository();
        var trading = new TradingApiService(accountStore, new HistoryVenueFactory(), symbols, logger);

        try
        {
            ClearAccounts(accountStore);
            var displayName = $"History-{Guid.NewGuid():N}";
            accountStore.Add("HistoryVenue", displayName, "mainnet", "test", "Both", null, null, null, null, null, null);
            SyncObservableAccounts(accountStore);
            var account = accountStore.Snapshot().Single(x => x.DisplayName == displayName);
            symbols.MarkActivated("HistoryVenue", "mainnet", "BTC-USD");

            var dto = await trading.OpenConnectionAsync(account.AccountId, "BTC-USD", "5m", notifyLifecycleEvents: false);
            Assert.Equal(123.45m, dto.LatestPrice);

            var market = await trading.GetMarketDataAsync(account.AccountId, "BTC-USD", "5m", null, notifyLifecycleEvents: false);
            Assert.Equal(123.45m, market.LatestPrice);
        }
        finally
        {
            await trading.DisposeAsync();
            ClearAccounts(accountStore);
        }
    }

    [Fact]
    public async Task ListPositionsAsync_WithoutPriorConnection_DoesNotRaiseLifecycleEventsWhenDisabled()
    {
        var logger = new AppLogger();
        var repository = new AccountRepository(new AesSecretProtector());
        var accountStore = new AccountStore(repository);
        var symbols = new SymbolCatalogRepository();
        var trading = new TradingApiService(accountStore, new SnapshotVenueFactory(), symbols, logger);
        var openedEvents = 0;
        trading.ConnectionOpened += _ => openedEvents++;

        try
        {
            ClearAccounts(accountStore);
            var displayName = $"Snapshot-{Guid.NewGuid():N}";
            accountStore.Add("SnapshotVenue", displayName, "mainnet", "test", "Both", null, null, null, null, null, null);
            SyncObservableAccounts(accountStore);
            var account = accountStore.Snapshot().Single(x => x.DisplayName == displayName);
            symbols.MarkActivated("SnapshotVenue", "mainnet", "BTC-USD");

            var positions = await trading.ListPositionsAsync(account.AccountId, null, notifyLifecycleEvents: false);
            var balances = await trading.ListBalancesAsync(account.AccountId, null, notifyLifecycleEvents: false);

            Assert.Single(positions);
            Assert.Equal("BTC-USD", positions[0].Symbol);
            Assert.Single(balances);
            Assert.Equal("USDC", balances[0].Asset);
            Assert.Equal(0, openedEvents);
        }
        finally
        {
            await trading.DisposeAsync();
            ClearAccounts(accountStore);
        }
    }

    [Fact]
    public async Task GetMarketDataAsync_UsesOrderBookMidPriceWhenNoTradeTicks()
    {
        var logger = new AppLogger();
        var repository = new AccountRepository(new AesSecretProtector());
        var accountStore = new AccountStore(repository);
        var symbols = new SymbolCatalogRepository();
        var venueFactory = new StreamingVenueFactory();
        var trading = new TradingApiService(accountStore, venueFactory, symbols, logger);

        try
        {
            ClearAccounts(accountStore);
            var displayName = $"Streaming-{Guid.NewGuid():N}";
            accountStore.Add("StreamingVenue", displayName, "mainnet", "test", "Both", null, null, null, null, null, null);
            SyncObservableAccounts(accountStore);
            var account = accountStore.Snapshot().Single(x => x.DisplayName == displayName);
            symbols.MarkActivated("StreamingVenue", "mainnet", "BTC-USD");

            await trading.OpenConnectionAsync(account.AccountId, "BTC-USD", "5m", notifyLifecycleEvents: false);
            Assert.NotNull(venueFactory.LastCreated);
            venueFactory.LastCreated!.Publish(new OrderBookSnapshot(
                DateTimeOffset.UtcNow,
                [(101m, 2m), (102m, 1m)],
                [(99m, 3m), (98m, 1m)]));

            var latestPrice = await WaitForLatestPriceAsync(trading, account.AccountId, "BTC-USD");
            Assert.Equal(100m, latestPrice);
        }
        finally
        {
            await trading.DisposeAsync();
            ClearAccounts(accountStore);
        }
    }

    private static void SyncObservableAccounts(AccountStore accountStore)
    {
        var snapshot = accountStore.Snapshot();
        accountStore.Accounts.Clear();
        foreach (var account in snapshot)
        {
            accountStore.Accounts.Add(account);
        }
    }

    private static void ClearAccounts(AccountStore accountStore)
    {
        foreach (var account in accountStore.Snapshot())
        {
            accountStore.Remove(account.AccountId);
        }

        SyncObservableAccounts(accountStore);
    }

    private static async Task<decimal?> WaitForLatestPriceAsync(TradingApiService trading, Guid accountId, string symbol)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var market = await trading.GetMarketDataAsync(accountId, symbol, "5m", null, notifyLifecycleEvents: false);
            if (market.LatestPrice.HasValue && market.LatestPrice.Value > 0)
            {
                return market.LatestPrice.Value;
            }

            await Task.Delay(50);
        }

        return null;
    }

    private sealed class HistoryVenueFactory : IVenueFactory
    {
        public IPerpVenue Create(AccountProfile account, AccountCredentials credentials) => new HistoryVenue();
    }

    private sealed class SnapshotVenueFactory : IVenueFactory
    {
        public IPerpVenue Create(AccountProfile account, AccountCredentials credentials) => new SnapshotVenue();
    }

    private sealed class StreamingVenueFactory : IVenueFactory
    {
        public StreamingVenue? LastCreated { get; private set; }

        public IPerpVenue Create(AccountProfile account, AccountCredentials credentials)
        {
            LastCreated = new StreamingVenue();
            return LastCreated;
        }
    }

    private sealed class HistoryVenue : IPerpVenue, IHistoricalCandleProvider
    {
        public string VenueId => "HistoryVenue";

        public Task ConnectMarketDataAsync(IEnumerable<string> subscriptions, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DisconnectMarketDataAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<(bool IsSuccess, string Message)> ConfigureLeverageAsync(string symbol, decimal leverage, MarginMode marginMode, CancellationToken cancellationToken = default)
            => Task.FromResult((true, string.Empty));

        public Task<OrderAck> PlaceOrderAsync(string symbol, string side, decimal qty, decimal? price, CancellationToken cancellationToken = default)
            => Task.FromResult(new OrderAck(DateTimeOffset.UtcNow, Guid.NewGuid().ToString("N"), true, string.Empty));

        public Task<OrderAck> PlaceCloseOrderAsync(string symbol, string side, decimal positionQty, decimal? price, CancellationToken cancellationToken = default)
            => Task.FromResult(new OrderAck(DateTimeOffset.UtcNow, Guid.NewGuid().ToString("N"), true, string.Empty));

        public Task<OrderAck> CancelOrderAsync(string symbol, string orderId, CancellationToken cancellationToken = default)
            => Task.FromResult(new OrderAck(DateTimeOffset.UtcNow, orderId, true, string.Empty));

        public Task<(bool IsSuccess, string Message)> ValidateConnectionAsync(CancellationToken cancellationToken = default)
            => Task.FromResult((true, string.Empty));

        public async IAsyncEnumerable<MarketEvent> MarketEvents([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<IReadOnlyList<Candle>> GetRecentCandlesAsync(string symbol, CandleInterval interval, int count, CancellationToken cancellationToken = default)
        {
            var end = DateTimeOffset.UtcNow;
            IReadOnlyList<Candle> candles =
            [
                new Candle(VenueId, symbol, interval, end.AddMinutes(-10), 120m, 124m, 119m, 121m, 10m, true),
                new Candle(VenueId, symbol, interval, end.AddMinutes(-5), 121m, 125m, 120m, 123.45m, 12m, true)
            ];
            return Task.FromResult(candles);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SnapshotVenue : IPerpVenue, IAccountStateProvider
    {
        public string VenueId => "SnapshotVenue";

        public Task ConnectMarketDataAsync(IEnumerable<string> subscriptions, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DisconnectMarketDataAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<(bool IsSuccess, string Message)> ConfigureLeverageAsync(string symbol, decimal leverage, MarginMode marginMode, CancellationToken cancellationToken = default)
            => Task.FromResult((true, string.Empty));

        public Task<OrderAck> PlaceOrderAsync(string symbol, string side, decimal qty, decimal? price, CancellationToken cancellationToken = default)
            => Task.FromResult(new OrderAck(DateTimeOffset.UtcNow, Guid.NewGuid().ToString("N"), true, string.Empty));

        public Task<OrderAck> PlaceCloseOrderAsync(string symbol, string side, decimal positionQty, decimal? price, CancellationToken cancellationToken = default)
            => Task.FromResult(new OrderAck(DateTimeOffset.UtcNow, Guid.NewGuid().ToString("N"), true, string.Empty));

        public Task<OrderAck> CancelOrderAsync(string symbol, string orderId, CancellationToken cancellationToken = default)
            => Task.FromResult(new OrderAck(DateTimeOffset.UtcNow, orderId, true, string.Empty));

        public Task<(bool IsSuccess, string Message)> ValidateConnectionAsync(CancellationToken cancellationToken = default)
            => Task.FromResult((true, string.Empty));

        public async IAsyncEnumerable<MarketEvent> MarketEvents([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<VenueAccountSnapshot> GetAccountSnapshotAsync(AccountSnapshotSections sections, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new VenueAccountSnapshot(
                DateTimeOffset.UtcNow,
                [new VenuePosition("BTC-USD", 1m, 1000m, 2m, 50000m, 51000m, 2m, 20m, 5m)],
                [],
                [new VenueBalance("USDC", 100m, 100m, 75m, 75m)]));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StreamingVenue : IPerpVenue, IHistoricalCandleProvider
    {
        private readonly Channel<MarketEvent> _channel = Channel.CreateUnbounded<MarketEvent>();

        public string VenueId => "StreamingVenue";

        public Task ConnectMarketDataAsync(IEnumerable<string> subscriptions, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DisconnectMarketDataAsync(CancellationToken cancellationToken = default)
        {
            _channel.Writer.TryComplete();
            return Task.CompletedTask;
        }

        public Task<(bool IsSuccess, string Message)> ConfigureLeverageAsync(string symbol, decimal leverage, MarginMode marginMode, CancellationToken cancellationToken = default)
            => Task.FromResult((true, string.Empty));

        public Task<OrderAck> PlaceOrderAsync(string symbol, string side, decimal qty, decimal? price, CancellationToken cancellationToken = default)
            => Task.FromResult(new OrderAck(DateTimeOffset.UtcNow, Guid.NewGuid().ToString("N"), true, string.Empty));

        public Task<OrderAck> PlaceCloseOrderAsync(string symbol, string side, decimal positionQty, decimal? price, CancellationToken cancellationToken = default)
            => Task.FromResult(new OrderAck(DateTimeOffset.UtcNow, Guid.NewGuid().ToString("N"), true, string.Empty));

        public Task<OrderAck> CancelOrderAsync(string symbol, string orderId, CancellationToken cancellationToken = default)
            => Task.FromResult(new OrderAck(DateTimeOffset.UtcNow, orderId, true, string.Empty));

        public Task<(bool IsSuccess, string Message)> ValidateConnectionAsync(CancellationToken cancellationToken = default)
            => Task.FromResult((true, string.Empty));

        public IAsyncEnumerable<MarketEvent> MarketEvents(CancellationToken cancellationToken = default)
            => _channel.Reader.ReadAllAsync(cancellationToken);

        public Task<IReadOnlyList<Candle>> GetRecentCandlesAsync(string symbol, CandleInterval interval, int count, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Candle>>([]);

        public void Publish(MarketEvent marketEvent)
        {
            _channel.Writer.TryWrite(marketEvent);
        }

        public ValueTask DisposeAsync()
        {
            _channel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
