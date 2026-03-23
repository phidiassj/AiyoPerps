using AiyoPerps.Models;
using AiyoPerps.Services;
using AiyoPerps.Services.Api;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace AiyoPerps.Test;

public sealed class DashboardServiceTests
{
    [Fact]
    public async Task CloseConnectionAsync_SharedReference_KeepsSessionUntilLastRelease()
    {
        var fixture = await TestFixture.CreateAsync();
        await using var _ = fixture;

        var account = fixture.AddAccount("FakeSharedRef", "mainnet", "BTCUSDT");

        await fixture.Trading.OpenConnectionAsync(account.AccountId, "BTCUSDT", "5m");
        await fixture.Trading.OpenConnectionAsync(account.AccountId, "BTCUSDT", "15m");

        Assert.Single(fixture.Trading.ListConnections());

        var firstClose = await fixture.Trading.CloseConnectionAsync(account.AccountId, "BTCUSDT");
        Assert.True(firstClose);
        Assert.Single(fixture.Trading.ListConnections());

        var secondClose = await fixture.Trading.CloseConnectionAsync(account.AccountId, "BTCUSDT");
        Assert.True(secondClose);
        Assert.Empty(fixture.Trading.ListConnections());
    }

    [Fact]
    public async Task DashboardService_StartRefreshTradeFlow_UpdatesSnapshot()
    {
        var fixture = await TestFixture.CreateAsync();
        await using var _ = fixture;

        var accountA = fixture.AddAccount("FakeDashA", "mainnet", "BTCUSDT");
        var accountB = fixture.AddAccount("FakeDashB", "mainnet", "BTCUSDT");
        var connectionOpenedCount = 0;
        fixture.Trading.ConnectionOpened += _ => connectionOpenedCount++;

        await fixture.Dashboard.UpdateConfigurationAsync(new DashboardConfiguration(
            [accountA.AccountId, accountB.AccountId],
            "BTCUSDT",
            "5m",
            false));

        var started = await fixture.Dashboard.StartAsync();
        Assert.True(started.IsRunning);
        Assert.Equal(2, started.Markets.Count);
        Assert.Equal(0, connectionOpenedCount);

        await fixture.Dashboard.OpenPositionAsync(new ApiOpenPositionRequest(
            accountA.AccountId,
            "BTCUSDT",
            "long",
            "market",
            5m,
            1000m,
            "USD",
            null,
            "cross"));

        var afterOpen = await fixture.Dashboard.RefreshAsync();
        Assert.Contains(afterOpen.Positions, x => x.AccountId == accountA.AccountId && x.RawSymbol == "BTCUSDT");

        await fixture.Dashboard.OpenPositionAsync(new ApiOpenPositionRequest(
            accountA.AccountId,
            "BTCUSDT",
            "long",
            "limit",
            5m,
            500m,
            "USD",
            68010m,
            "cross"));

        var afterLimitOrder = await fixture.Dashboard.RefreshAsync();
        var order = Assert.Single(afterLimitOrder.Orders, x => x.AccountId == accountA.AccountId && x.RawSymbol == "BTCUSDT");
        Assert.False(string.IsNullOrWhiteSpace(order.OrderId));

        await fixture.Dashboard.CancelOrderAsync(new ApiCancelOrderRequest(accountA.AccountId, "BTCUSDT", order.OrderId!));

        var afterCancel = await fixture.Dashboard.RefreshAsync();
        Assert.DoesNotContain(afterCancel.Orders, x => x.AccountId == accountA.AccountId && x.RawSymbol == "BTCUSDT");

        await fixture.Dashboard.ClosePositionAsync(new ApiClosePositionRequest(accountA.AccountId, "BTCUSDT", "market", null));

        var afterClose = await fixture.Dashboard.RefreshAsync();
        Assert.DoesNotContain(afterClose.Positions, x => x.AccountId == accountA.AccountId && x.RawSymbol == "BTCUSDT");
        Assert.Equal(0, connectionOpenedCount);

        var stopped = await fixture.Dashboard.StopAsync();
        Assert.False(stopped.IsRunning);
        Assert.Empty(stopped.Markets);
    }

    [Fact]
    public async Task DashboardService_UnsupportedSymbolRefresh_DoesNotRaiseConnectionOpened()
    {
        var fixture = await TestFixture.CreateAsync();
        await using var _ = fixture;

        var account = fixture.AddAccount("FakeUnsupported", "mainnet", "BTCUSDT");
        var connectionOpenedCount = 0;
        fixture.Trading.ConnectionOpened += _ => connectionOpenedCount++;

        await fixture.Dashboard.UpdateConfigurationAsync(new DashboardConfiguration(
            [account.AccountId],
            "PERP:ETH",
            "5m",
            false));

        var started = await fixture.Dashboard.StartAsync();
        Assert.True(started.IsRunning);

        var refreshed = await fixture.Dashboard.RefreshAsync();
        Assert.Single(refreshed.Markets);
        Assert.Equal(0m, refreshed.Markets[0].Price);
        Assert.Equal(0, connectionOpenedCount);
    }

    [Fact]
    public async Task DashboardService_OpenPositionWithoutSession_DoesNotRaiseConnectionOpened()
    {
        var fixture = await TestFixture.CreateAsync();
        await using var _ = fixture;

        var account = fixture.AddAccount("FakeDirectTrade", "mainnet", "BTCUSDT");
        var connectionOpenedCount = 0;
        fixture.Trading.ConnectionOpened += _ => connectionOpenedCount++;

        await fixture.Dashboard.OpenPositionAsync(new ApiOpenPositionRequest(
            account.AccountId,
            "BTCUSDT",
            "long",
            "market",
            5m,
            1000m,
            "USD",
            null,
            "cross"));

        var positions = await fixture.Trading.ListPositionsAsync(account.AccountId, null, notifyLifecycleEvents: false);
        Assert.Single(positions);
        Assert.Equal(0, connectionOpenedCount);
    }

    [Fact]
    public async Task GetAvailableSymbolOptions_DeduplicatesSymbolsAndSortsByMarketCap()
    {
        var fixture = await TestFixture.CreateAsync();
        await using var _ = fixture;

        var btcUsdt = fixture.AddAccount("FakeSymbolA", "mainnet", "BTCUSDT");
        var btcUsdc = fixture.AddAccount("FakeSymbolB", "mainnet", "BTC-USDC");
        var ethUsdt = fixture.AddAccount("FakeSymbolC", "mainnet", "ETHUSDT");

        var options = fixture.Dashboard.GetAvailableSymbolOptions(new DashboardConfiguration(
            [btcUsdt.AccountId, btcUsdc.AccountId, ethUsdt.AccountId],
            null,
            "5m",
            false));

        Assert.Collection(
            options,
            option =>
            {
                Assert.Equal("PERP:BTC", option.Value);
                Assert.Equal("BTC", option.Display);
            },
            option =>
            {
                Assert.Equal("PERP:ETH", option.Value);
                Assert.Equal("ETH", option.Display);
            });
    }

    [Fact]
    public async Task StartAsync_DashboardSymbolKey_MapsToVenueSymbolsAndPopulatesMarkets()
    {
        var fixture = await TestFixture.CreateAsync();
        await using var _ = fixture;

        var accountA = fixture.AddAccount("FakeKeyA", "mainnet", "BTCUSDT");
        var accountB = fixture.AddAccount("FakeKeyB", "mainnet", "BTC-USDC");

        await fixture.Dashboard.UpdateConfigurationAsync(new DashboardConfiguration(
            [accountA.AccountId, accountB.AccountId],
            "PERP:BTC",
            "5m",
            false));

        var snapshot = await fixture.Dashboard.StartAsync();

        Assert.True(snapshot.IsRunning);
        Assert.Equal(2, snapshot.Markets.Count);
        Assert.Contains(snapshot.Markets, x => x.AccountId == accountA.AccountId && x.RawSymbol == "BTCUSDT" && x.Symbol == "BTC");
        Assert.Contains(snapshot.Markets, x => x.AccountId == accountB.AccountId && x.RawSymbol == "BTC-USDC" && x.Symbol == "BTC");
    }

    private sealed class TestFixture : IAsyncDisposable
    {
        private readonly AccountStore _accountStore;

        private TestFixture(AccountStore accountStore, SymbolCatalogRepository symbols, TradingApiService trading, DashboardService dashboard)
        {
            _accountStore = accountStore;
            Symbols = symbols;
            Trading = trading;
            Dashboard = dashboard;
        }

        public SymbolCatalogRepository Symbols { get; }

        public TradingApiService Trading { get; }

        public DashboardService Dashboard { get; }

        public static Task<TestFixture> CreateAsync()
        {
            var logger = new AppLogger();
            var repository = new AccountRepository(new AesSecretProtector());
            var accountStore = new AccountStore(repository);
            ClearAccounts(accountStore);
            var symbols = new SymbolCatalogRepository();
            var trading = new TradingApiService(accountStore, new VenueFactory(logger), symbols, logger);
            var dashboard = new DashboardService(accountStore.Accounts, trading, symbols, logger);
            return Task.FromResult(new TestFixture(accountStore, symbols, trading, dashboard));
        }

        public AccountProfile AddAccount(string venueId, string environment, string symbol)
        {
            var displayName = $"{venueId}-{Guid.NewGuid():N}";
            _accountStore.Add(venueId, displayName, environment, "test", "Both", null, null, null, null, null, null);
            SyncObservableAccounts(_accountStore);

            var account = _accountStore.Snapshot().Single(x => x.DisplayName == displayName);
            Symbols.MarkActivated(venueId, environment, symbol);
            return account;
        }

        public async ValueTask DisposeAsync()
        {
            await Dashboard.DisposeAsync();
            await Trading.DisposeAsync();
            ClearAccounts(_accountStore);
        }

        private static void ClearAccounts(AccountStore accountStore)
        {
            foreach (var account in accountStore.Snapshot())
            {
                accountStore.Remove(account.AccountId);
            }

            SyncObservableAccounts(accountStore);
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
    }
}
