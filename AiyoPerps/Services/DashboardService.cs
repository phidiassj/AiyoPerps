using AiyoPerps.Core;
using AiyoPerps.Models;
using AiyoPerps.Services.Api;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AiyoPerps.Services;

public sealed class DashboardService : IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(800);
    private static readonly IReadOnlyDictionary<string, int> MarketCapRanks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["BTC"] = 1,
        ["ETH"] = 2,
        ["XRP"] = 3,
        ["BNB"] = 4,
        ["SOL"] = 5,
        ["DOGE"] = 6,
        ["ADA"] = 7,
        ["TRX"] = 8,
        ["AVAX"] = 9,
        ["LINK"] = 10,
        ["DOT"] = 11,
        ["TON"] = 12,
        ["SUI"] = 13,
        ["LTC"] = 14,
        ["BCH"] = 15,
        ["UNI"] = 16,
        ["APT"] = 17,
        ["HBAR"] = 18,
        ["ETC"] = 19,
        ["ICP"] = 20,
        ["NEAR"] = 21,
        ["FIL"] = 22,
        ["AAVE"] = 23,
        ["ATOM"] = 24,
        ["ARB"] = 25,
        ["OP"] = 26,
        ["INJ"] = 27,
        ["MKR"] = 28,
        ["CRV"] = 29,
        ["PEPE"] = 30,
        ["WIF"] = 31,
        ["BONK"] = 32,
        ["ENA"] = 33,
        ["FET"] = 34,
        ["RENDER"] = 35,
        ["RUNE"] = 36,
        ["SEI"] = 37,
        ["TAO"] = 38,
        ["HYPE"] = 39,
        ["PENDLE"] = 40,
        ["JUP"] = 41,
        ["PYTH"] = 42,
        ["TIA"] = 43,
        ["WLD"] = 44,
        ["ONDO"] = 45,
        ["0G"] = 46
    };

    private readonly ObservableCollection<AccountProfile> _accounts;
    private readonly TradingApiService _tradingApiService;
    private readonly SymbolCatalogRepository _symbolCatalogRepository;
    private readonly AppLogger _logger;
    private readonly object _sync = new();
    private readonly CancellationTokenSource _disposeCts = new();

    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private DashboardConfiguration _configuration = new([], null, "5m", false);
    private DashboardSnapshot _snapshot = DashboardSnapshot.Empty;
    private Dictionary<Guid, long> _cursors = new();
    private Dictionary<Guid, decimal> _selectedSymbolPrices = new();
    private bool _isRunning;
    private int _runGeneration;

    public DashboardService(
        ObservableCollection<AccountProfile> accounts,
        TradingApiService tradingApiService,
        SymbolCatalogRepository symbolCatalogRepository,
        AppLogger logger)
    {
        _accounts = accounts;
        _tradingApiService = tradingApiService;
        _symbolCatalogRepository = symbolCatalogRepository;
        _logger = logger;
        _accounts.CollectionChanged += OnAccountsCollectionChanged;
    }

    public event Action<DashboardSnapshot>? SnapshotChanged;

    public IReadOnlyList<DashboardAccountOptionDto> GetSelectableAccounts(bool showTestnet)
    {
        lock (_sync)
        {
            return GetSelectableAccountsUnsafe(showTestnet);
        }
    }

    public IReadOnlyList<DashboardSymbolOptionDto> GetAvailableSymbolOptions(DashboardConfiguration? configuration = null)
    {
        lock (_sync)
        {
            var effectiveConfiguration = configuration ?? _configuration;
            return GetAvailableSymbolOptionsUnsafe(effectiveConfiguration);
        }
    }

    public DashboardConfiguration GetConfiguration()
    {
        lock (_sync)
        {
            return _configuration;
        }
    }

    public DashboardSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            return _snapshot;
        }
    }

    public async Task<DashboardSnapshot> UpdateConfigurationAsync(DashboardConfiguration configuration, CancellationToken cancellationToken = default)
    {
        DashboardConfiguration normalizedConfiguration;
        DashboardSnapshot nextSnapshot;
        lock (_sync)
        {
            normalizedConfiguration = configuration with
            {
                Interval = string.IsNullOrWhiteSpace(configuration.Interval) ? "5m" : configuration.Interval.Trim(),
                Symbol = string.IsNullOrWhiteSpace(configuration.Symbol) ? null : configuration.Symbol.Trim().ToUpperInvariant(),
                SelectedAccountIds = configuration.SelectedAccountIds.Distinct().ToArray()
            };
            _configuration = normalizedConfiguration;
            _snapshot = _snapshot with { Configuration = normalizedConfiguration, UpdatedAt = DateTimeOffset.UtcNow };
            nextSnapshot = _snapshot;
        }

        RaiseSnapshotChanged(nextSnapshot);

        if (_isRunning)
        {
            await RestartAsync(cancellationToken);
        }

        return GetSnapshot();
    }

    public async Task<DashboardSnapshot> StartAsync(CancellationToken cancellationToken = default)
    {
        DashboardConfiguration configuration;
        DashboardSnapshot snapshot;
        int runGeneration;
        lock (_sync)
        {
            configuration = _configuration;
            if (_isRunning)
            {
                return _snapshot;
            }
        }

        var selectedAccounts = ResolveSelectedAccounts(configuration).ToList();
        if (selectedAccounts.Count == 0)
        {
            throw new ApiBadRequestException("Dashboard requires at least one enabled account.");
        }

        if (string.IsNullOrWhiteSpace(configuration.Symbol))
        {
            throw new ApiBadRequestException("Dashboard symbol is required.");
        }

        var supportedAccounts = selectedAccounts
            .Where(x => x.IsSymbolSupported)
            .ToList();

        var openedSymbols = new List<ResolvedDashboardAccountSymbol>(supportedAccounts.Count);
        try
        {
            foreach (var selected in supportedAccounts)
            {
                await _tradingApiService.OpenConnectionAsync(selected.Account.AccountId, selected.RawSymbol!, configuration.Interval, cancellationToken, notifyLifecycleEvents: false);
                openedSymbols.Add(selected);
            }
        }
        catch
        {
            foreach (var selected in openedSymbols)
            {
                try
                {
                    await _tradingApiService.CloseConnectionAsync(selected.Account.AccountId, selected.RawSymbol!, cancellationToken, notifyLifecycleEvents: false);
                }
                catch (Exception ex)
                {
                    _logger.Warn("Dashboard", $"Dashboard start cleanup warning accountId={selected.Account.AccountId}: {ex.Message}");
                }
            }

            throw;
        }

        lock (_sync)
        {
            _isRunning = true;
            _runGeneration++;
            runGeneration = _runGeneration;
            _cursors = supportedAccounts.ToDictionary(x => x.Account.AccountId, _ => 0L);
            _selectedSymbolPrices = supportedAccounts.ToDictionary(x => x.Account.AccountId, _ => 0m);
            _runCts?.Cancel();
            _runCts?.Dispose();
            _runCts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
            _snapshot = _snapshot with { IsRunning = true };
            snapshot = _snapshot;
        }

        RaiseSnapshotChanged(snapshot);
        _runTask = Task.Run(() => RunLoopAsync(runGeneration, _runCts.Token), _runCts.Token);
        await RefreshAsyncCoreAsync(runGeneration, cancellationToken);
        return GetSnapshot();
    }

    public async Task<DashboardSnapshot> StopAsync(CancellationToken cancellationToken = default)
    {
        DashboardConfiguration configuration;
        CancellationTokenSource? runCts;
        Task? runTask;
        lock (_sync)
        {
            configuration = _configuration;
            _isRunning = false;
            _runGeneration++;
            runCts = _runCts;
            runTask = _runTask;
            _runCts = null;
            _runTask = null;
            _cursors.Clear();
            _selectedSymbolPrices.Clear();
            _snapshot = new DashboardSnapshot(false, configuration, [], [], [], DateTimeOffset.UtcNow);
        }

        runCts?.Cancel();
        if (runTask is not null)
        {
            try
            {
                await runTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        runCts?.Dispose();

        if (!string.IsNullOrWhiteSpace(configuration.Symbol))
        {
            foreach (var selected in ResolveSelectedAccounts(configuration).Where(x => x.IsSymbolSupported))
            {
                await _tradingApiService.CloseConnectionAsync(selected.Account.AccountId, selected.RawSymbol!, cancellationToken, notifyLifecycleEvents: false);
            }
        }

        RaiseSnapshotChanged(GetSnapshot());
        return GetSnapshot();
    }

    public async Task<DashboardSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        int runGeneration;
        lock (_sync)
        {
            runGeneration = _runGeneration;
        }

        return await RefreshAsyncCoreAsync(runGeneration, cancellationToken);
    }

    public async Task<object> OpenPositionAsync(ApiOpenPositionRequest request, CancellationToken cancellationToken = default)
    {
        var translatedRequest = request with { Symbol = ResolveRawSymbol(request.AccountId, request.Symbol) };
        var result = await _tradingApiService.OpenPositionAsync(translatedRequest, cancellationToken, notifyLifecycleEvents: false);
        await RefreshIfRunningAsync(cancellationToken);
        return result;
    }

    public async Task<object> ClosePositionAsync(ApiClosePositionRequest request, CancellationToken cancellationToken = default)
    {
        var translatedRequest = request with { PositionId = ResolveRawSymbol(request.AccountId, request.PositionId) };
        var result = await _tradingApiService.ClosePositionAsync(translatedRequest, cancellationToken, notifyLifecycleEvents: false);
        await RefreshIfRunningAsync(cancellationToken);
        return result;
    }

    public async Task<object> CancelOrderAsync(ApiCancelOrderRequest request, CancellationToken cancellationToken = default)
    {
        var translatedRequest = request with { Symbol = ResolveRawSymbol(request.AccountId, request.Symbol) };
        var result = await _tradingApiService.CancelOrderAsync(translatedRequest, cancellationToken, notifyLifecycleEvents: false);
        await RefreshIfRunningAsync(cancellationToken);
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        _accounts.CollectionChanged -= OnAccountsCollectionChanged;
        _disposeCts.Cancel();
        await StopAsync(CancellationToken.None);
        _disposeCts.Dispose();
    }

    private async Task RestartAsync(CancellationToken cancellationToken)
    {
        await StopAsync(cancellationToken);
        await StartAsync(cancellationToken);
    }

    private async Task RefreshIfRunningAsync(CancellationToken cancellationToken)
    {
        if (_isRunning)
        {
            await RefreshAsync(cancellationToken);
        }
    }

    private async Task RunLoopAsync(int runGeneration, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            try
            {
                await RefreshAsyncCoreAsync(runGeneration, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Error("Dashboard", "Dashboard refresh loop failed", ex);
            }
        }
    }

    private async Task<DashboardSnapshot> RefreshAsyncCoreAsync(int expectedRunGeneration, CancellationToken cancellationToken)
    {
        DashboardConfiguration configuration;
        lock (_sync)
        {
            configuration = _configuration;
            if (!_isRunning || expectedRunGeneration != _runGeneration)
            {
                return _snapshot;
            }
        }

        var selectedAccounts = ResolveSelectedAccounts(configuration).ToList();
        if (selectedAccounts.Count == 0 || string.IsNullOrWhiteSpace(configuration.Symbol))
        {
            return GetSnapshot();
        }

        var marketRows = new List<DashboardMarketDto>(selectedAccounts.Count);
        var positions = new List<DashboardPositionDto>();
        var orders = new List<DashboardPendingOrderDto>();

        foreach (var selected in selectedAccounts)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var account = selected.Account;
            ApiMarketDataResponse? market = null;
            if (selected.IsSymbolSupported)
            {
                var cursor = _cursors.TryGetValue(account.AccountId, out var existingCursor) ? existingCursor : 0;
                market = await _tradingApiService.GetMarketDataAsync(account.AccountId, selected.RawSymbol!, configuration.Interval, cursor <= 0 ? null : cursor, cancellationToken, notifyLifecycleEvents: false);
                _cursors[account.AccountId] = market.Cursor;
                _selectedSymbolPrices[account.AccountId] = market.LatestPrice ?? _selectedSymbolPrices.GetValueOrDefault(account.AccountId);
            }
            else
            {
                _cursors.Remove(account.AccountId);
                _selectedSymbolPrices.Remove(account.AccountId);
            }

            var accountPositions = await _tradingApiService.ListPositionsAsync(account.AccountId, null, cancellationToken, notifyLifecycleEvents: false);
            var accountOrders = await _tradingApiService.ListOpenOrdersAsync(account.AccountId, null, cancellationToken, notifyLifecycleEvents: false);
            var accountBalances = await _tradingApiService.ListBalancesAsync(account.AccountId, null, cancellationToken, notifyLifecycleEvents: false);

            var balanceUsd = accountBalances.Sum(x => x.UsdValue);
            var availableUsd = ResolveAvailableUsd(accountBalances, balanceUsd);
            var accountPnl = accountPositions.Sum(x => x.UnrealizedPnlUsd);

            marketRows.Add(new DashboardMarketDto(
                account.AccountId,
                account.VenueId,
                account.DisplayName,
                selected.DisplaySymbol,
                selected.RawSymbol ?? string.Empty,
                decimal.Round(market?.LatestPrice ?? 0m, 2, MidpointRounding.AwayFromZero),
                decimal.Round(accountPnl, 2, MidpointRounding.AwayFromZero),
                decimal.Round(balanceUsd, 2, MidpointRounding.AwayFromZero),
                decimal.Round(availableUsd, 2, MidpointRounding.AwayFromZero),
                ResolveMaxLeverage(account.VenueId)));

            positions.AddRange(accountPositions.Select(x => new DashboardPositionDto(
                account.AccountId,
                account.VenueId,
                SymbolCanonicalizer.Format(account.VenueId, x.Symbol),
                x.Symbol,
                NormalizeMarginMode(x.MarginMode),
                decimal.Round(x.NotionalUsd, 2, MidpointRounding.AwayFromZero),
                decimal.Round(x.EntryPrice, 2, MidpointRounding.AwayFromZero),
                decimal.Round(x.MarkPrice, 2, MidpointRounding.AwayFromZero),
                decimal.Round(x.UnrealizedPnlUsd, 2, MidpointRounding.AwayFromZero),
                decimal.Round(x.UnrealizedPnlPct, 2, MidpointRounding.AwayFromZero),
                x.Quantity < 0 ? "Short" : "Long")));

            orders.AddRange(accountOrders.Select(x => new DashboardPendingOrderDto(
                account.AccountId,
                account.VenueId,
                SymbolCanonicalizer.Format(account.VenueId, x.Symbol),
                x.Symbol,
                NormalizeMarginMode(x.MarginMode),
                decimal.Round(x.NotionalUsd, 2, MidpointRounding.AwayFromZero),
                decimal.Round(x.LimitPrice ?? 0m, 2, MidpointRounding.AwayFromZero),
                ResolveOrderReferencePrice(account.AccountId, selected.RawSymbol ?? string.Empty, x.Symbol, x.LimitPrice),
                x.OrderId)));
        }

        var snapshot = new DashboardSnapshot(true, configuration, marketRows, positions, orders, DateTimeOffset.UtcNow);
        lock (_sync)
        {
            if (!_isRunning || expectedRunGeneration != _runGeneration)
            {
                return _snapshot;
            }

            _snapshot = snapshot;
        }

        RaiseSnapshotChanged(snapshot);
        return snapshot;
    }

    private IReadOnlyList<DashboardAccountOptionDto> GetSelectableAccountsUnsafe(bool showTestnet)
    {
        return _accounts
            .Where(x => x.IsEnabled)
            .Where(x => showTestnet || !string.Equals(x.Environment, "testnet", StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(x => new DashboardAccountOptionDto(
                x.AccountId,
                x.VenueId,
                x.DisplayName,
                x.Environment,
                x.Label))
            .ToList();
    }

    private IReadOnlyList<DashboardSymbolOptionDto> GetAvailableSymbolOptionsUnsafe(DashboardConfiguration configuration)
    {
        return GetSymbolOptionAccounts(configuration)
            .SelectMany(x => _symbolCatalogRepository.GetActiveSymbolEntries(x.VenueId, x.Environment))
            .GroupBy(BuildDashboardSymbolKey, StringComparer.OrdinalIgnoreCase)
            .Select(x => SelectPreferredDashboardEntry(x))
            .OrderBy(x => ResolveMarketCapRank(x.BaseAsset))
            .ThenBy(x => BuildDashboardDisplaySymbol(x), StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.RawSymbol, StringComparer.OrdinalIgnoreCase)
            .Select(x => new DashboardSymbolOptionDto(BuildDashboardSymbolKey(x), BuildDashboardDisplaySymbol(x)))
            .ToList();
    }

    private IEnumerable<AccountProfile> GetSymbolOptionAccounts(DashboardConfiguration configuration)
    {
        var accounts = _accounts
            .Where(x => x.IsEnabled)
            .Where(x => configuration.ShowTestnet || !string.Equals(x.Environment, "testnet", StringComparison.OrdinalIgnoreCase));

        if (configuration.SelectedAccountIds.Count > 0)
        {
            accounts = accounts.Where(x => configuration.SelectedAccountIds.Contains(x.AccountId));
        }

        return accounts
            .GroupBy(x => x.VenueId, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First());
    }

    private IEnumerable<AccountProfile> GetEffectiveAccounts(DashboardConfiguration configuration)
    {
        var selected = _accounts
            .Where(x => configuration.SelectedAccountIds.Contains(x.AccountId))
            .Where(x => x.IsEnabled)
            .Where(x => configuration.ShowTestnet || !string.Equals(x.Environment, "testnet", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return selected
            .GroupBy(x => x.VenueId, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First());
    }

    private IEnumerable<ResolvedDashboardAccountSymbol> ResolveSelectedAccounts(DashboardConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration.Symbol))
        {
            return [];
        }

        var key = configuration.Symbol.Trim().ToUpperInvariant();
        var fallbackDisplaySymbol = ResolveConfiguredSymbolDisplay(key);
        return GetEffectiveAccounts(configuration)
            .Select(account =>
            {
                var entry = ResolveSymbolEntry(account, key);
                var displaySymbol = entry is not null
                    ? BuildDashboardDisplaySymbol(entry)
                    : fallbackDisplaySymbol;
                return new ResolvedDashboardAccountSymbol(account, entry, displaySymbol);
            })
            .ToList();
    }

    private string ResolveRawSymbol(Guid accountId, string symbol)
    {
        var account = _accounts.FirstOrDefault(x => x.AccountId == accountId)
            ?? throw new ApiNotFoundException($"Account not found: {accountId}");
        var entry = ResolveSymbolEntry(account, symbol);
        return entry?.RawSymbol ?? symbol.Trim().ToUpperInvariant();
    }

    private SymbolCatalogEntry? ResolveSymbolEntry(AccountProfile account, string symbolKey)
    {
        var normalized = (symbolKey ?? string.Empty).Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        return _symbolCatalogRepository.GetActiveSymbolEntries(account.VenueId, account.Environment)
            .Where(x =>
                string.Equals(BuildDashboardSymbolKey(x), normalized, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(BuildDashboardDisplaySymbol(x), normalized, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(x.CanonicalKey, normalized, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(x.RawSymbol, normalized, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(x.DisplaySymbol, normalized, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => ResolveQuotePreference(x))
            .ThenBy(x => x.DisplaySymbol, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.RawSymbol, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private decimal ResolveOrderReferencePrice(Guid accountId, string selectedSymbol, string orderSymbol, decimal? limitPrice)
    {
        if (string.Equals(selectedSymbol, orderSymbol, StringComparison.OrdinalIgnoreCase) &&
            _selectedSymbolPrices.TryGetValue(accountId, out var livePrice) &&
            livePrice > 0)
        {
            return decimal.Round(livePrice, 2, MidpointRounding.AwayFromZero);
        }

        return decimal.Round(limitPrice ?? 0m, 2, MidpointRounding.AwayFromZero);
    }

    private static bool IsStableDisplayAsset(string asset)
    {
        if (string.IsNullOrWhiteSpace(asset))
        {
            return false;
        }

        var upper = asset.Trim().ToUpperInvariant();
        return upper.StartsWith("USD", StringComparison.Ordinal) ||
               upper.StartsWith("USDT", StringComparison.Ordinal) ||
               upper.StartsWith("USDC", StringComparison.Ordinal);
    }

    private static double ResolveMaxLeverage(string venueId)
    {
        return venueId.Trim().ToUpperInvariant() switch
        {
            "BITMEX" => 50,
            "HYPERLIQUID" => 40,
            "DYDX" => 20,
            _ => 25
        };
    }

    private static string NormalizeMarginMode(string? marginMode)
    {
        return MarginModeText.ParseOrDefault(marginMode, MarginMode.Unknown) switch
        {
            MarginMode.Cross => "Cross",
            MarginMode.Isolated => "Isolated",
            _ => "-"
        };
    }

    private static SymbolCatalogEntry SelectPreferredDashboardEntry(IEnumerable<SymbolCatalogEntry> entries)
    {
        return entries
            .OrderBy(ResolveQuotePreference)
            .ThenBy(x => x.DisplaySymbol, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.RawSymbol, StringComparer.OrdinalIgnoreCase)
            .First();
    }

    private static string BuildDashboardSymbolKey(SymbolCatalogEntry entry)
    {
        var baseAsset = NormalizeSymbolPart(entry.BaseAsset);
        if (string.IsNullOrWhiteSpace(baseAsset))
        {
            return entry.CanonicalKey;
        }

        var contractType = NormalizeSymbolPart(entry.ContractType);
        if (string.IsNullOrWhiteSpace(contractType))
        {
            contractType = "PERP";
        }

        return $"{contractType}:{baseAsset}";
    }

    private static string BuildDashboardDisplaySymbol(SymbolCatalogEntry entry)
    {
        var baseAsset = NormalizeSymbolPart(entry.BaseAsset);
        if (string.IsNullOrWhiteSpace(baseAsset))
        {
            return entry.DisplaySymbol;
        }

        var contractType = NormalizeSymbolPart(entry.ContractType);
        return string.IsNullOrWhiteSpace(contractType) || string.Equals(contractType, "PERP", StringComparison.OrdinalIgnoreCase)
            ? baseAsset
            : $"{baseAsset} ({contractType})";
    }

    private static int ResolveQuotePreference(SymbolCatalogEntry entry)
    {
        return NormalizeSymbolPart(entry.QuoteAsset) switch
        {
            "USDT" => 0,
            "USDC" => 1,
            "USD" => 2,
            _ => 3
        };
    }

    private static int ResolveMarketCapRank(string? baseAsset)
    {
        if (!string.IsNullOrWhiteSpace(baseAsset) && MarketCapRanks.TryGetValue(baseAsset, out var rank))
        {
            return rank;
        }

        return int.MaxValue;
    }

    private static decimal ResolveAvailableUsd(IReadOnlyList<ApiBalanceDto> balances, decimal balanceUsd)
    {
        var explicitAvailableUsd = balances
            .Where(x => x.AvailableUsdValue.HasValue)
            .Sum(x => x.AvailableUsdValue ?? 0m);
        if (explicitAvailableUsd > 0m)
        {
            return explicitAvailableUsd;
        }

        var stableBalanceUsd = balances
            .Where(x => IsStableDisplayAsset(x.Asset))
            .Sum(x => x.UsdValue);
        if (stableBalanceUsd > 0m)
        {
            return stableBalanceUsd;
        }

        return balanceUsd;
    }

    private static string ResolveConfiguredSymbolDisplay(string symbolKey)
    {
        var normalized = NormalizeSymbolPart(symbolKey);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        if (normalized.StartsWith("RAW:", StringComparison.Ordinal))
        {
            normalized = normalized["RAW:".Length..];
        }

        var parts = normalized.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length >= 2)
        {
            return parts[1];
        }

        var descriptor = SymbolCanonicalizer.Describe(null, normalized);
        if (!string.IsNullOrWhiteSpace(descriptor.BaseAsset))
        {
            return descriptor.BaseAsset;
        }

        return descriptor.DisplaySymbol;
    }

    private static string NormalizeSymbolPart(string? value)
        => (value ?? string.Empty).Trim().ToUpperInvariant();

    private void RaiseSnapshotChanged(DashboardSnapshot snapshot)
    {
        SnapshotChanged?.Invoke(snapshot);
    }

    private async void OnAccountsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        try
        {
            await UpdateConfigurationAsync(GetConfiguration());
        }
        catch (Exception ex)
        {
            _logger.Warn("Dashboard", $"Account collection update warning: {ex.Message}");
        }
    }
}

public sealed record DashboardConfiguration(
    IReadOnlyList<Guid> SelectedAccountIds,
    string? Symbol,
    string Interval,
    bool ShowTestnet);

public sealed record DashboardSnapshot(
    bool IsRunning,
    DashboardConfiguration Configuration,
    IReadOnlyList<DashboardMarketDto> Markets,
    IReadOnlyList<DashboardPositionDto> Positions,
    IReadOnlyList<DashboardPendingOrderDto> Orders,
    DateTimeOffset UpdatedAt)
{
    public static DashboardSnapshot Empty { get; } = new(false, new DashboardConfiguration([], null, "5m", false), [], [], [], DateTimeOffset.MinValue);
}

public sealed record DashboardMarketDto(
    Guid AccountId,
    string Exchange,
    string AccountDisplayName,
    string Symbol,
    string RawSymbol,
    decimal Price,
    decimal Pnl,
    decimal Balance,
    decimal AvailableBalance,
    double MaxLeverage);

public sealed record DashboardPositionDto(
    Guid AccountId,
    string Exchange,
    string Symbol,
    string RawSymbol,
    string Mode,
    decimal Amount,
    decimal EntryPrice,
    decimal Price,
    decimal PnlUsd,
    decimal PnlPct,
    string Side);

public sealed record DashboardPendingOrderDto(
    Guid AccountId,
    string Exchange,
    string Symbol,
    string RawSymbol,
    string Mode,
    decimal Amount,
    decimal LimitPrice,
    decimal Price,
    string? OrderId);

public sealed record DashboardAccountOptionDto(
    Guid AccountId,
    string VenueId,
    string DisplayName,
    string Environment,
    string Label);

public sealed record DashboardSymbolOptionDto(
    string Value,
    string Display)
{
    public override string ToString() => Display;
}

internal sealed record ResolvedDashboardAccountSymbol(
    AccountProfile Account,
    SymbolCatalogEntry? Entry,
    string DisplaySymbol)
{
    public bool IsSymbolSupported => Entry is not null;

    public string? RawSymbol => Entry?.RawSymbol;
}
