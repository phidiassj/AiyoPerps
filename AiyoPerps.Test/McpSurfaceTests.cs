using AiyoPerps.Core;
using AiyoPerps.Models;
using AiyoPerps.Services;
using AiyoPerps.Services.Api;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace AiyoPerps.Test;

public sealed class McpSurfaceTests
{
    [Fact]
    public async Task Create_AsterAccount_ReturnsAsterVenueAdapter()
    {
        var factory = new VenueFactory(new AppLogger());
        var account = CreateAccount("Aster");

        await using var venue = factory.Create(account, new AccountCredentials());

        Assert.IsType<AsterVenueAdapter>(venue);
    }

    [Fact]
    public async Task Create_GrvtAccount_ReturnsGrvtVenueAdapter()
    {
        var factory = new VenueFactory(new AppLogger());
        var account = CreateAccount("GRVT");

        await using var venue = factory.Create(account, new AccountCredentials());

        Assert.IsType<GrvtVenueAdapter>(venue);
    }

    [Fact]
    public void BuildMcpTools_AccountVenueEnum_ContainsAsterAndGrvt()
    {
        var buildMcpTools = typeof(LocalApiServer).GetMethod(
            "BuildMcpTools",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(buildMcpTools);

        var tools = buildMcpTools!.Invoke(null, null);
        Assert.NotNull(tools);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(tools));
        var root = document.RootElement;

        var accountsCreate = root.EnumerateArray()
            .First(x => x.GetProperty("name").GetString() == "accounts_create");
        var accountsUpdate = root.EnumerateArray()
            .First(x => x.GetProperty("name").GetString() == "accounts_update");

        Assert.Contains("Aster", ReadVenueEnum(accountsCreate));
        Assert.Contains("GRVT", ReadVenueEnum(accountsCreate));
        Assert.Contains("Aster", ReadVenueEnum(accountsUpdate));
        Assert.Contains("GRVT", ReadVenueEnum(accountsUpdate));
    }

    [Fact]
    public void BuildMcpTools_ContainsMarketAndBalanceTools()
    {
        var buildMcpTools = typeof(LocalApiServer).GetMethod(
            "BuildMcpTools",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(buildMcpTools);

        var tools = buildMcpTools!.Invoke(null, null);
        Assert.NotNull(tools);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(tools));
        var toolNames = document.RootElement.EnumerateArray()
            .Select(x => x.GetProperty("name").GetString())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray();

        Assert.Contains("symbols_list", toolNames);
        Assert.Contains("market_data_get", toolNames);
        Assert.Contains("balances_list", toolNames);
        Assert.Contains("connections_list", toolNames);
        Assert.Contains("dashboard_status_get", toolNames);
        Assert.Contains("dashboard_config_set", toolNames);
        Assert.Contains("dashboard_snapshot_get", toolNames);
        Assert.Contains("ai_agent_settings_get", toolNames);
        Assert.Contains("ai_agent_settings_set", toolNames);
        Assert.DoesNotContain("dashboard_market_info_refresh", toolNames);
        Assert.DoesNotContain("dashboard_market_info_set_enabled", toolNames);
    }

    [Fact]
    public async Task ExecuteMcpToolAsync_PositionsList_DoesNotRaiseLifecycleEvents()
    {
        var logger = new AppLogger();
        var repository = new AccountRepository(new AesSecretProtector());
        var accountStore = new AccountStore(repository);
        var symbols = new SymbolCatalogRepository();
        var trading = new TradingApiService(accountStore, new SnapshotVenueFactory(), symbols, logger);
        await using var aiService = new AIAgentExecutionService(new UserPreferenceRepository(), new AIAgentRunRepository(), new HttpApiStateService(), trading, logger);
        await using var dashboard = new DashboardService(accountStore.Accounts, trading, symbols, logger);
        await using var server = new LocalApiServer(trading, dashboard, aiService, logger);
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

            var execute = typeof(LocalApiServer).GetMethod("ExecuteMcpToolAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(execute);

            var args = JsonSerializer.SerializeToElement(new { accountId = account.AccountId });
            var task = (Task<object>)execute!.Invoke(server, [ "positions_list", args, CancellationToken.None ])!;
            var result = await task;

            Assert.NotNull(result);
            Assert.Equal(0, openedEvents);
        }
        finally
        {
            await trading.DisposeAsync();
            ClearAccounts(accountStore);
        }
    }

    private static AccountProfile CreateAccount(string venueId)
        => new()
        {
            VenueId = venueId,
            DisplayName = $"{venueId}-test",
            Environment = "testnet",
            Summary = "test"
        };

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

    private static string[] ReadVenueEnum(JsonElement tool)
    {
        return tool
            .GetProperty("inputSchema")
            .GetProperty("properties")
            .GetProperty("venueId")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(x => x.GetString())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Cast<string>()
            .ToArray();
    }

    private sealed class SnapshotVenueFactory : IVenueFactory
    {
        public IPerpVenue Create(AccountProfile account, AccountCredentials credentials) => new SnapshotVenue();
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
}
