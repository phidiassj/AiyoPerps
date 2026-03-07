using AiyoPerps.Core;
using AiyoPerps.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace AiyoPerps.Services.Api;

public sealed class TradingApiService : IAsyncDisposable
{
    private readonly AccountStore _accountStore;
    private readonly IVenueFactory _venueFactory;
    private readonly SymbolCatalogRepository _symbolCatalogRepository;
    private readonly AppLogger _logger;
    private readonly ConcurrentDictionary<string, ApiConnectionSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    public event Action<ApiConnectionDto>? ConnectionOpened;
    public event Action<Guid, string>? ConnectionClosed;

    public TradingApiService(AccountStore accountStore, IVenueFactory venueFactory, SymbolCatalogRepository symbolCatalogRepository, AppLogger logger)
    {
        _accountStore = accountStore;
        _venueFactory = venueFactory;
        _symbolCatalogRepository = symbolCatalogRepository;
        _logger = logger;
    }

    public IReadOnlyList<ApiAccountDto> ListAccounts()
        => _accountStore.Snapshot().Select(ToDto).ToList();

    public ApiAccountDto GetAccount(Guid accountId)
    {
        var account = _accountStore.Find(accountId)
            ?? throw new ApiNotFoundException($"Account not found: {accountId}");

        return ToDto(account);
    }

    public ApiAccountDto CreateAccount(ApiAccountUpsertRequest request)
    {
        ValidateAccountRequest(request);
        _accountStore.Add(
            request.VenueId,
            request.DisplayName,
            request.Environment,
            request.Summary,
            request.AuthMode ?? "Both",
            request.ApiKey,
            request.ApiSecret,
            request.AccountAddress,
            request.SubAccountId,
            request.WalletAddress,
            request.PrivateKey);

        var created = _accountStore
            .Snapshot()
            .OrderByDescending(x => x.AccountId)
            .FirstOrDefault(x =>
                string.Equals(x.VenueId, request.VenueId.Trim(), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.DisplayName, request.DisplayName.Trim(), StringComparison.Ordinal));

        if (created is null)
        {
            throw new ApiConflictException("Account created but could not be reloaded.");
        }

        _logger.Info("Api", $"Account created accountId={created.AccountId}, venue={created.VenueId}, env={created.Environment}");
        return ToDto(created);
    }

    public ApiAccountDto UpdateAccount(Guid accountId, ApiAccountUpsertRequest request)
    {
        ValidateAccountRequest(request);
        var existing = _accountStore.Find(accountId)
            ?? throw new ApiNotFoundException($"Account not found: {accountId}");

        _accountStore.UpdateAccount(
            existing.AccountId,
            request.VenueId,
            request.DisplayName,
            request.Environment,
            request.Summary,
            request.AuthMode,
            request.ApiKey,
            request.ApiSecret,
            request.AccountAddress,
            request.SubAccountId,
            request.WalletAddress,
            request.PrivateKey,
            request.IsEnabled ?? existing.IsEnabled);

        var updated = _accountStore.Find(accountId)
            ?? throw new ApiConflictException($"Account update failed: {accountId}");

        _logger.Info("Api", $"Account updated accountId={updated.AccountId}, venue={updated.VenueId}, env={updated.Environment}");
        return ToDto(updated);
    }

    public async Task DeleteAccountAsync(Guid accountId)
    {
        var existing = _accountStore.Find(accountId)
            ?? throw new ApiNotFoundException($"Account not found: {accountId}");

        var sessions = _sessions.Values
            .Where(x => x.Account.AccountId == accountId)
            .ToList();

        foreach (var session in sessions)
        {
            await CloseConnectionAsync(session.Account.AccountId, session.Symbol);
        }

        _accountStore.Remove(existing.AccountId);
        _logger.Info("Api", $"Account deleted accountId={existing.AccountId}");
    }

    public IReadOnlyList<string> ListSymbols(Guid accountId)
    {
        var account = _accountStore.Find(accountId)
            ?? throw new ApiNotFoundException($"Account not found: {accountId}");

        var list = _symbolCatalogRepository.GetActiveSymbols(account.VenueId, account.Environment)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return list;
    }

    public IReadOnlyList<ApiConnectionDto> ListConnections()
        => _sessions.Values
            .OrderBy(x => x.Account.DisplayName, StringComparer.Ordinal)
            .ThenBy(x => x.Symbol, StringComparer.Ordinal)
            .Select(x => x.ToDto())
            .ToList();

    public async Task<ApiConnectionDto> OpenConnectionAsync(Guid accountId, string symbol, string interval, CancellationToken cancellationToken = default)
    {
        var normalizedSymbol = NormalizeSymbol(symbol);
        var key = BuildSessionKey(accountId, normalizedSymbol);

        await _sessionGate.WaitAsync(cancellationToken);
        try
        {
            if (_sessions.TryGetValue(key, out var existing))
            {
                await existing.EnsureIntervalLoadedAsync(ApiIntervalParser.ParseOrDefault(interval), cancellationToken);
                _logger.Info("Api", $"Connection reused key={key}");
                return existing.ToDto();
            }

            var account = _accountStore.Find(accountId)
                ?? throw new ApiNotFoundException($"Account not found: {accountId}");

            if (!account.IsEnabled)
            {
                throw new ApiBadRequestException("Account is disabled.");
            }

            var credentials = _accountStore.GetCredentials(accountId);
            var venue = _venueFactory.Create(account, credentials);
            var parsedInterval = ApiIntervalParser.ParseOrDefault(interval);
            var session = new ApiConnectionSession(
                Guid.NewGuid().ToString("N"),
                account,
                normalizedSymbol,
                parsedInterval,
                venue,
                _logger);

            try
            {
                await session.StartAsync(cancellationToken);
                await session.EnsureIntervalLoadedAsync(parsedInterval, cancellationToken);
                _sessions[key] = session;
                _logger.Info("Api", $"Connection opened key={key}, connectionId={session.ConnectionId}");
                var dto = session.ToDto();
                try
                {
                    ConnectionOpened?.Invoke(dto);
                }
                catch (Exception ex)
                {
                    _logger.Warn("Api", $"ConnectionOpened event warning key={key}: {ex.Message}");
                }

                return dto;
            }
            catch
            {
                await session.DisposeAsync();
                throw;
            }
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    public async Task<bool> CloseConnectionAsync(Guid accountId, string symbol)
    {
        var normalizedSymbol = NormalizeSymbol(symbol);
        var key = BuildSessionKey(accountId, normalizedSymbol);
        if (!_sessions.TryRemove(key, out var session))
        {
            return false;
        }

        await session.DisposeAsync();
        _logger.Info("Api", $"Connection closed key={key}, connectionId={session.ConnectionId}");
        try
        {
            ConnectionClosed?.Invoke(accountId, normalizedSymbol);
        }
        catch (Exception ex)
        {
            _logger.Warn("Api", $"ConnectionClosed event warning key={key}: {ex.Message}");
        }

        return true;
    }

    public async Task<ApiMarketDataResponse> GetMarketDataAsync(Guid accountId, string symbol, string interval, long? cursor, CancellationToken cancellationToken = default)
    {
        var session = await GetSessionAsync(accountId, symbol, cancellationToken);
        var parsedInterval = ApiIntervalParser.ParseOrDefault(interval);
        await session.EnsureIntervalLoadedAsync(parsedInterval, cancellationToken);

        var payload = session.GetMarketData(parsedInterval, cursor);
        return new ApiMarketDataResponse(
            accountId,
            NormalizeSymbol(symbol),
            ApiIntervalParser.ToText(parsedInterval),
            payload.Cursor,
            payload.LatestPrice,
            payload.InitialCandles,
            payload.DeltaCandles,
            payload.HasDelta);
    }

    public async Task<IReadOnlyList<ApiPositionDto>> ListPositionsAsync(Guid accountId, string? symbol, CancellationToken cancellationToken = default)
    {
        var snapshot = await GetSnapshotAsync(accountId, symbol, cancellationToken);
        return snapshot.Positions.Select(p => new ApiPositionDto(
            p.Symbol,
            p.Symbol,
            p.Quantity,
            p.NotionalUsd,
            p.Leverage,
            p.EntryPrice,
            p.MarkPrice,
            p.UnrealizedPnlPct,
            p.UnrealizedPnlUsd,
            p.RealizedPnlUsd)).ToList();
    }

    public async Task<IReadOnlyList<ApiOpenOrderDto>> ListOpenOrdersAsync(Guid accountId, string? symbol, CancellationToken cancellationToken = default)
    {
        var snapshot = await GetSnapshotAsync(accountId, symbol, cancellationToken);
        return snapshot.OpenOrders.Select(o => new ApiOpenOrderDto(
            o.Symbol,
            o.NotionalUsd,
            o.Leverage,
            o.LimitPrice,
            o.Status,
            o.OrderId)).ToList();
    }

    public async Task<IReadOnlyList<ApiBalanceDto>> ListBalancesAsync(Guid accountId, string? symbol, CancellationToken cancellationToken = default)
    {
        var snapshot = await GetSnapshotAsync(accountId, symbol, cancellationToken);
        return snapshot.Balances.Select(b => new ApiBalanceDto(b.Asset, b.Quantity, b.UsdValue)).ToList();
    }

    public async Task<object> OpenPositionAsync(ApiOpenPositionRequest request, CancellationToken cancellationToken = default)
    {
        var session = await GetSessionAsync(request.AccountId, request.Symbol, cancellationToken);
        var side = ParseOrderSide(request.Side);
        var isLimit = IsLimitOrderType(request.OrderType);

        if (request.Leverage <= 0)
        {
            throw new ApiBadRequestException("Leverage must be positive.");
        }

        var leverageResult = await session.Venue.ConfigureLeverageAsync(session.Symbol, request.Leverage, cancellationToken);
        if (!leverageResult.IsSuccess)
        {
            throw new ApiConflictException(leverageResult.Message);
        }

        var marketPrice = request.LimitPrice ?? session.LatestPrice;
        if (!marketPrice.HasValue || marketPrice.Value <= 0)
        {
            throw new ApiConflictException("No market reference price available.");
        }

        var unit = (request.AmountUnit ?? "USD").Trim().ToUpperInvariant();
        var baseQty = unit == "USD"
            ? request.Amount / marketPrice.Value
            : request.Amount;

        if (baseQty <= 0)
        {
            throw new ApiBadRequestException("Computed order quantity is invalid.");
        }

        var price = isLimit ? request.LimitPrice : null;
        if (isLimit && (!price.HasValue || price.Value <= 0))
        {
            throw new ApiBadRequestException("Limit price is required for limit order.");
        }

        var ack = await session.Venue.PlaceOrderAsync(session.Symbol, side, baseQty, price, cancellationToken);
        if (!ack.Success)
        {
            throw new ApiConflictException(ack.Message ?? "Order rejected by venue.");
        }

        return new
        {
            accountId = request.AccountId,
            symbol = session.Symbol,
            orderId = ack.ClientOrderId,
            side,
            orderType = isLimit ? "limit" : "market",
            leverage = request.Leverage,
            amount = request.Amount,
            amountUnit = unit,
            baseQuantity = baseQty,
            price,
            venueMessage = ack.Message
        };
    }

    public async Task<object> ClosePositionAsync(ApiClosePositionRequest request, CancellationToken cancellationToken = default)
    {
        var snapshot = await GetSnapshotAsync(request.AccountId, request.PositionId, cancellationToken);
        var position = snapshot.Positions.FirstOrDefault(x =>
            string.Equals(x.Symbol, request.PositionId, StringComparison.OrdinalIgnoreCase));

        if (position is null)
        {
            throw new ApiNotFoundException($"Position not found: {request.PositionId}");
        }

        var session = await GetSessionAsync(request.AccountId, position.Symbol, cancellationToken);
        var closeSide = position.Quantity < 0 ? "Buy" : "Sell";
        var qty = Math.Abs(position.Quantity);

        if (qty <= 0)
        {
            throw new ApiBadRequestException("Position quantity is zero.");
        }

        var isLimit = IsLimitOrderType(request.OrderType);
        var price = isLimit ? request.LimitPrice : null;
        if (isLimit && (!price.HasValue || price.Value <= 0))
        {
            throw new ApiBadRequestException("Limit price is required for limit close.");
        }

        var ack = await session.Venue.PlaceCloseOrderAsync(position.Symbol, closeSide, qty, price, cancellationToken);
        if (!ack.Success)
        {
            throw new ApiConflictException(ack.Message ?? "Close order rejected by venue.");
        }

        return new
        {
            accountId = request.AccountId,
            symbol = position.Symbol,
            positionId = request.PositionId,
            orderId = ack.ClientOrderId,
            side = closeSide,
            orderType = isLimit ? "limit" : "market",
            quantity = qty,
            price,
            venueMessage = ack.Message
        };
    }

    public async Task<object> CancelOrderAsync(ApiCancelOrderRequest request, CancellationToken cancellationToken = default)
    {
        var session = await GetSessionAsync(request.AccountId, request.Symbol, cancellationToken);
        var ack = await session.Venue.CancelOrderAsync(session.Symbol, request.OrderId, cancellationToken);
        if (!ack.Success)
        {
            throw new ApiConflictException(ack.Message ?? "Cancel rejected by venue.");
        }

        return new
        {
            accountId = request.AccountId,
            symbol = session.Symbol,
            orderId = ack.ClientOrderId,
            venueMessage = ack.Message,
            canceled = true
        };
    }

    public async Task<object> RunStressAsync(ApiStressRunRequest request, CancellationToken cancellationToken = default)
    {
        var accountId = request.AccountId;
        var symbol = NormalizeSymbol(request.Symbol);
        var interval = request.Interval ?? "5m";
        var concurrency = Math.Clamp(request.Concurrency ?? 8, 1, 64);
        var iterations = Math.Clamp(request.Iterations ?? 200, 1, 20000);

        await OpenConnectionAsync(accountId, symbol, interval, cancellationToken);

        var total = concurrency * iterations;
        var success = 0;
        var failed = 0;
        long latencySumMs = 0;
        long latencyMaxMs = 0;
        var gate = new SemaphoreSlim(concurrency, concurrency);
        var tasks = new List<Task>(total);
        var start = Stopwatch.StartNew();

        for (var i = 0; i < total; i++)
        {
            await gate.WaitAsync(cancellationToken);
            tasks.Add(Task.Run(async () =>
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    var _ = await GetMarketDataAsync(accountId, symbol, interval, null, cancellationToken);
                    Interlocked.Increment(ref success);
                }
                catch
                {
                    Interlocked.Increment(ref failed);
                }
                finally
                {
                    sw.Stop();
                    var elapsed = sw.ElapsedMilliseconds;
                    Interlocked.Add(ref latencySumMs, elapsed);
                    var currentMax = Interlocked.Read(ref latencyMaxMs);
                    while (elapsed > currentMax)
                    {
                        var prev = Interlocked.CompareExchange(ref latencyMaxMs, elapsed, currentMax);
                        if (prev == currentMax)
                        {
                            break;
                        }

                        currentMax = prev;
                    }

                    gate.Release();
                }
            }, cancellationToken));
        }

        await Task.WhenAll(tasks);
        start.Stop();

        var avgLatency = total > 0 ? latencySumMs / (double)total : 0d;
        var rps = start.Elapsed.TotalSeconds > 0 ? total / start.Elapsed.TotalSeconds : 0d;
        return new
        {
            accountId,
            symbol,
            interval,
            concurrency,
            iterationsPerWorker = iterations,
            totalRequests = total,
            success,
            failed,
            elapsedMs = start.ElapsedMilliseconds,
            avgLatencyMs = Math.Round(avgLatency, 2),
            maxLatencyMs = latencyMaxMs,
            requestsPerSecond = Math.Round(rps, 2)
        };
    }

    public async ValueTask DisposeAsync()
    {
        var sessions = _sessions.Values.ToList();
        _sessions.Clear();
        foreach (var session in sessions)
        {
            try
            {
                await session.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.Warn("Api", $"Session dispose warning id={session.ConnectionId}: {ex.Message}");
            }
        }

        _sessionGate.Dispose();
    }

    private async Task<VenueAccountSnapshot> GetSnapshotAsync(Guid accountId, string? symbol, CancellationToken cancellationToken)
    {
        var session = await GetSessionForSnapshotAsync(accountId, symbol, cancellationToken);
        if (session.Venue is not IAccountStateProvider provider)
        {
            throw new ApiBadRequestException($"Venue does not expose account state: {session.Venue.VenueId}");
        }

        return await provider.GetAccountSnapshotAsync(cancellationToken);
    }

    private async Task<ApiConnectionSession> GetSessionAsync(Guid accountId, string symbol, CancellationToken cancellationToken)
    {
        var normalizedSymbol = NormalizeSymbol(symbol);
        var key = BuildSessionKey(accountId, normalizedSymbol);

        if (_sessions.TryGetValue(key, out var existing))
        {
            return existing;
        }

        await OpenConnectionAsync(accountId, normalizedSymbol, "5m", cancellationToken);
        if (_sessions.TryGetValue(key, out var created))
        {
            return created;
        }

        throw new ApiConflictException($"Connection is not available for account={accountId}, symbol={normalizedSymbol}");
    }

    private async Task<ApiConnectionSession> GetSessionForSnapshotAsync(Guid accountId, string? symbol, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(symbol))
        {
            return await GetSessionAsync(accountId, symbol, cancellationToken);
        }

        var existing = _sessions.Values.FirstOrDefault(x => x.Account.AccountId == accountId);
        if (existing is not null)
        {
            return existing;
        }

        var account = _accountStore.Find(accountId)
            ?? throw new ApiNotFoundException($"Account not found: {accountId}");
        var fallbackSymbol = _symbolCatalogRepository.GetActiveSymbols(account.VenueId, account.Environment).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(fallbackSymbol))
        {
            throw new ApiBadRequestException($"No symbol available for account {accountId}.");
        }

        return await GetSessionAsync(accountId, fallbackSymbol, cancellationToken);
    }

    private static ApiAccountDto ToDto(AccountProfile account)
        => new(
            account.AccountId,
            account.VenueId,
            account.DisplayName,
            account.Environment,
            account.Summary,
            account.AuthMode,
            account.SubAccountId,
            account.IsEnabled,
            account.HasApiCredentials,
            account.HasWalletCredentials);

    private static string BuildSessionKey(Guid accountId, string symbol)
        => $"{accountId:N}|{NormalizeSymbol(symbol)}";

    private static string NormalizeSymbol(string symbol)
    {
        var normalized = (symbol ?? string.Empty).Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ApiBadRequestException("Symbol is required.");
        }

        return normalized;
    }

    private static string ParseOrderSide(string rawSide)
    {
        var side = (rawSide ?? string.Empty).Trim().ToLowerInvariant();
        return side switch
        {
            "buy" or "long" => "Buy",
            "sell" or "short" => "Sell",
            _ => throw new ApiBadRequestException("Side must be one of: buy, sell, long, short.")
        };
    }

    private static bool IsLimitOrderType(string raw)
    {
        var value = (raw ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value is "limit" or "limitorder";
    }

    private static void ValidateAccountRequest(ApiAccountUpsertRequest request)
    {
        if (request is null)
        {
            throw new ApiBadRequestException("Request body is required.");
        }

        if (string.IsNullOrWhiteSpace(request.VenueId))
        {
            throw new ApiBadRequestException("venueId is required.");
        }

        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            throw new ApiBadRequestException("displayName is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Environment))
        {
            throw new ApiBadRequestException("environment is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Summary))
        {
            throw new ApiBadRequestException("summary is required.");
        }

        var mode = (request.AuthMode ?? "Both").Trim();
        if (!mode.Equals("ApiKey", StringComparison.OrdinalIgnoreCase) &&
            !mode.Equals("Wallet", StringComparison.OrdinalIgnoreCase) &&
            !mode.Equals("Both", StringComparison.OrdinalIgnoreCase))
        {
            throw new ApiBadRequestException("authMode must be one of: ApiKey, Wallet, Both.");
        }

        var requiresApi = mode.Equals("ApiKey", StringComparison.OrdinalIgnoreCase) ||
                          mode.Equals("Both", StringComparison.OrdinalIgnoreCase);
        var requiresWallet = mode.Equals("Wallet", StringComparison.OrdinalIgnoreCase) ||
                             mode.Equals("Both", StringComparison.OrdinalIgnoreCase);

        if (requiresApi && (string.IsNullOrWhiteSpace(request.ApiKey) || string.IsNullOrWhiteSpace(request.ApiSecret)))
        {
            throw new ApiBadRequestException("apiKey and apiSecret are required by authMode.");
        }

        if (requiresWallet && (string.IsNullOrWhiteSpace(request.WalletAddress) || string.IsNullOrWhiteSpace(request.PrivateKey)))
        {
            throw new ApiBadRequestException("walletAddress and privateKey are required by authMode.");
        }
    }
}

public sealed class ApiBadRequestException : Exception
{
    public ApiBadRequestException(string message) : base(message)
    {
    }
}

public sealed class ApiNotFoundException : Exception
{
    public ApiNotFoundException(string message) : base(message)
    {
    }
}

public sealed class ApiConflictException : Exception
{
    public ApiConflictException(string message) : base(message)
    {
    }
}
