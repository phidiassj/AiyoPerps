using AiyoPerps.Core;
using AiyoPerps.Models;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace AiyoPerps.Services;

public sealed class DydxVenueAdapter : IPerpVenue, IHistoricalCandleProvider, IAccountStateProvider
{
    private readonly string _environment;
    private readonly string _indexerBase;
    private readonly string _wsBase;
    private readonly AccountCredentials _credentials;
    private readonly AppLogger _logger;
    private readonly HttpClient _httpClient = new();
    private readonly Channel<MarketEvent> _channel = Channel.CreateUnbounded<MarketEvent>();
    private readonly DydxNodeBridge _nodeBridge;
    private readonly SemaphoreSlim _marketsGate = new(1, 1);
    private readonly ConcurrentDictionary<string, DydxConfiguredTradeContext> _configuredTrades = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DydxTrackedContext> _positionContexts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DydxTrackedContext> _orderContexts = new(StringComparer.OrdinalIgnoreCase);

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _wsCts;
    private Task? _wsTask;
    private string _symbol = "BTC-USD";
    private Dictionary<string, DydxMarketSpec> _markets = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _marketsLoadedAt = DateTimeOffset.MinValue;
    private DydxAuthProfile? _cachedAuthProfile;
    private DateTimeOffset _cachedAuthProfileAt = DateTimeOffset.MinValue;

    public DydxVenueAdapter(string environment, AccountCredentials credentials, AppLogger logger)
    {
        _environment = environment;
        _credentials = credentials;
        _logger = logger;
        _nodeBridge = new DydxNodeBridge(logger);

        var isTestnet = string.Equals(environment, "testnet", StringComparison.OrdinalIgnoreCase);
        _indexerBase = isTestnet ? "https://indexer.v4testnet.dydx.exchange" : "https://indexer.dydx.trade";
        _wsBase = isTestnet ? "wss://indexer.v4testnet.dydx.exchange/v4/ws" : "wss://indexer.dydx.trade/v4/ws";

        _logger.Info("dYdX", $"Adapter created env={environment}, indexer={_indexerBase}, ws={_wsBase}");
    }

    public string VenueId => "dYdX";

    public async Task ConnectMarketDataAsync(IEnumerable<string> subscriptions, CancellationToken cancellationToken = default)
    {
        await DisconnectMarketDataAsync(cancellationToken);

        _symbol = NormalizeSymbol(subscriptions.FirstOrDefault() ?? "BTC-USD");
        _ws = new ClientWebSocket();
        _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        _wsCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await _ws.ConnectAsync(new Uri(_wsBase), _wsCts.Token);

        await SendWsAsync(_ws, new { type = "subscribe", channel = "v4_trades", id = _symbol, batched = true }, _wsCts.Token);
        await SendWsAsync(_ws, new { type = "subscribe", channel = "v4_orderbook", id = _symbol, batched = true }, _wsCts.Token);

        _wsTask = Task.Run(() => ReceiveLoopAsync(_ws, _wsCts.Token), _wsCts.Token);
        _logger.Info("dYdX", $"WS connected symbol={_symbol}");
    }

    public async Task DisconnectMarketDataAsync(CancellationToken cancellationToken = default)
    {
        if (_wsCts is not null)
        {
            _wsCts.Cancel();
        }

        if (_ws is not null)
        {
            if (_ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.Warn("dYdX", $"WS close warning: {ex.Message}");
                }
            }

            _ws.Dispose();
            _ws = null;
        }

        if (_wsTask is not null)
        {
            try
            {
                await _wsTask;
            }
            catch (Exception ex)
            {
                _logger.Warn("dYdX", $"WS task warning: {ex.Message}");
            }

            _wsTask = null;
        }

        _wsCts?.Dispose();
        _wsCts = null;
    }

    public async Task<(bool IsSuccess, string Message)> ConfigureLeverageAsync(string symbol, decimal leverage, MarginMode marginMode, CancellationToken cancellationToken = default)
    {
        if (!HasTradingCredentials())
        {
            return (false, "dYdX requires a dydx Account Address or Wallet Address, plus PrivateKey.");
        }

        if (ResolveConfiguredSubaccountNumber() > 127)
        {
            return (false, "dYdX Sub Account Id should be the parent subaccount number (0-127). Child subaccounts are managed automatically for Isolated mode.");
        }

        if (leverage <= 0)
        {
            return (false, "Leverage must be greater than 0.");
        }

        var market = await GetMarketSpecAsync(symbol, cancellationToken);
        if (market is null)
        {
            return (false, $"dYdX market not found: {NormalizeSymbol(symbol)}");
        }

        if (market.InitialMarginFraction > 0)
        {
            var maxLeverage = decimal.Round(1m / market.InitialMarginFraction, 2, MidpointRounding.AwayFromZero);
            if (leverage > maxLeverage)
            {
                return (false, $"dYdX market {market.Ticker} max leverage is {FormatDecimal(maxLeverage)}x.");
            }
        }

        if (!string.Equals(market.Status, "ACTIVE", StringComparison.OrdinalIgnoreCase))
        {
            return (false, $"dYdX market {market.Ticker} is not active ({market.Status}).");
        }

        if (!string.Equals(market.MarketType, "CROSS", StringComparison.OrdinalIgnoreCase))
        {
            return (false, $"dYdX market {market.Ticker} is protocol-level {market.MarketType}. This app currently supports only dYdX CROSS markets.");
        }

        var normalizedSymbol = NormalizeSymbol(symbol);
        var familyState = await LoadFamilyStateAsync(cancellationToken);
        var exposure = AnalyzeSymbolExposure(familyState, normalizedSymbol);
        if (!exposure.IsSupported)
        {
            return (false, exposure.Message ?? $"dYdX symbol state is ambiguous for {normalizedSymbol}.");
        }

        if (marginMode == MarginMode.Isolated)
        {
            var auth = await GetAuthProfileAsync(cancellationToken);
            if (auth.Permissioned)
            {
                return (false, "dYdX isolated mode requires the owner wallet. API Trading Keys on dYdX only support cross-subaccount order actions.");
            }

            if (exposure.CrossSubaccountNumber.HasValue)
            {
                return (false, $"dYdX already has Cross exposure on {normalizedSymbol} in subaccount {exposure.CrossSubaccountNumber.Value}. Close or cancel it before switching to Isolated.");
            }

            _configuredTrades[normalizedSymbol] = new DydxConfiguredTradeContext(marginMode, leverage);
            return (true, "dYdX isolated mode ready.");
        }

        if (exposure.UniqueIsolatedSubaccountNumber.HasValue)
        {
            return (false, $"dYdX already has Isolated exposure on {normalizedSymbol} in subaccount {exposure.UniqueIsolatedSubaccountNumber.Value}. Close or cancel it before switching to Cross.");
        }

        _configuredTrades[normalizedSymbol] = new DydxConfiguredTradeContext(marginMode, leverage);
        return (true, "dYdX cross mode ready.");
    }

    public Task<OrderAck> PlaceOrderAsync(string symbol, string side, decimal qty, decimal? price, CancellationToken cancellationToken = default)
        => PlaceOrderCoreAsync(symbol, side, qty, price, reduceOnly: false, null, cancellationToken);

    public Task<OrderAck> PlaceCloseOrderAsync(string symbol, string side, decimal positionQty, decimal? price, CancellationToken cancellationToken = default)
        => PlaceOrderCoreAsync(symbol, side, Math.Abs(positionQty), price, reduceOnly: true, null, cancellationToken);

    public async Task<OrderAck> CancelOrderAsync(string symbol, string orderId, CancellationToken cancellationToken = default)
    {
        if (!HasTradingCredentials())
        {
            return new OrderAck(DateTimeOffset.UtcNow, orderId ?? string.Empty, false, "dYdX requires a dydx Account Address or Wallet Address, plus PrivateKey.");
        }

        if (string.IsNullOrWhiteSpace(orderId))
        {
            return new OrderAck(DateTimeOffset.UtcNow, string.Empty, false, "orderId is required.");
        }

        try
        {
            using var detail = await GetJsonAsync($"/v4/orders/{Uri.EscapeDataString(orderId.Trim())}", cancellationToken);
            var root = detail.RootElement;
            var market = ReadString(root, "ticker");
            var clientId = ReadInt(root, "clientId");
            var clobPairId = ReadInt(root, "clobPairId");
            var orderFlags = ReadInt(root, "orderFlags");
            var goodTilBlock = ReadInt(root, "goodTilBlock");
            var goodTilBlockTime = ReadDateTimeOffset(root, "goodTilBlockTime");
            var subaccountNumber = ReadInt(root, "subaccountNumber");

            if (string.IsNullOrWhiteSpace(market) || clientId <= 0)
            {
                return new OrderAck(DateTimeOffset.UtcNow, orderId.Trim(), false, "dYdX order details are incomplete. Cancel cannot be submitted.");
            }

            var payload = new
            {
                environment = _environment,
                accountAddress = RequireAccountAddress(),
                walletAddress = _credentials.WalletAddress,
                privateKey = RequirePrivateKey(),
                subaccountNumber = subaccountNumber >= 0 ? subaccountNumber : ResolveParentSubaccountNumber(),
                clientId,
                clobPairId,
                orderFlags,
                goodTilBlock = goodTilBlock > 0 ? goodTilBlock : (int?)null,
                goodTilBlockTime = goodTilBlockTime.HasValue ? ToUnixSeconds(goodTilBlockTime.Value) : (long?)null
            };

            var helper = await _nodeBridge.RunAsync(GetHelperRoot(), "cancel", payload, cancellationToken);
            if (!helper.IsSuccess)
            {
                return new OrderAck(DateTimeOffset.UtcNow, orderId.Trim(), false, helper.Message);
            }

            return new OrderAck(DateTimeOffset.UtcNow, orderId.Trim(), true, "dYdX order cancel submitted.");
        }
        catch (Exception ex)
        {
            _logger.Error("dYdX", $"CancelOrder exception orderId={orderId}", ex);
            return new OrderAck(DateTimeOffset.UtcNow, orderId.Trim(), false, ex.Message);
        }
    }

    public async Task<(bool IsSuccess, string Message)> ValidateConnectionAsync(CancellationToken cancellationToken = default)
    {
        if (ResolveConfiguredSubaccountNumber() > 127)
        {
            return (false, "dYdX Sub Account Id should be the parent subaccount number (0-127). Child subaccounts are managed automatically for Isolated mode.");
        }

        try
        {
            using var publicDoc = await GetJsonAsync("/v4/perpetualMarkets", cancellationToken);
            if (!publicDoc.RootElement.TryGetProperty("markets", out _))
            {
                return (false, "dYdX public market check returned an unexpected payload.");
            }
        }
        catch (Exception ex)
        {
            return (false, $"dYdX public check failed: {ex.Message}");
        }

        if (!HasTradingCredentials())
        {
            return (true, "dYdX public connection ok (trading credentials not fully configured).");
        }

        var helper = await _nodeBridge.RunAsync(GetHelperRoot(), "validate", new
        {
            environment = _environment,
            accountAddress = RequireAccountAddress(),
            walletAddress = _credentials.WalletAddress,
            privateKey = RequirePrivateKey(),
            subaccountNumber = ResolveParentSubaccountNumber()
        }, cancellationToken);

        if (!helper.IsSuccess)
        {
            return (false, helper.Message);
        }

        _cachedAuthProfile = ParseAuthProfile(helper.Root);
        _cachedAuthProfileAt = DateTimeOffset.UtcNow;

        return (true, "dYdX auth ok.");
    }

    public async IAsyncEnumerable<MarketEvent> MarketEvents([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var evt in _channel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return evt;
        }
    }

    public async Task<IReadOnlyList<Candle>> GetRecentCandlesAsync(string symbol, CandleInterval interval, int count, CancellationToken cancellationToken = default)
    {
        var normalizedSymbol = NormalizeSymbol(symbol);
        var (resolution, factor) = IntervalToDydx(interval);
        var limit = Math.Max(60, count * factor + factor);

        using var doc = await GetJsonAsync(
            $"/v4/candles/perpetualMarkets/{Uri.EscapeDataString(normalizedSymbol)}?resolution={Uri.EscapeDataString(resolution)}&limit={limit}",
            cancellationToken);
        if (!doc.RootElement.TryGetProperty("candles", out var candlesNode) || candlesNode.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var baseInterval = BaseIntervalFromDydxResolution(resolution);
        var candles = new List<Candle>();
        foreach (var item in candlesNode.EnumerateArray())
        {
            var openTime = ReadDateTimeOffset(item, "startedAt");
            var open = ReadDecimal(item, "open");
            var high = ReadDecimal(item, "high");
            var low = ReadDecimal(item, "low");
            var close = ReadDecimal(item, "close");
            var volume = ReadDecimal(item, "baseTokenVolume");

            if (!openTime.HasValue || open <= 0 || high <= 0 || low <= 0 || close <= 0)
            {
                continue;
            }

            candles.Add(new Candle(
                VenueId,
                normalizedSymbol,
                baseInterval,
                openTime.Value,
                open,
                high,
                low,
                close,
                volume,
                true));
        }

        if (candles.Count == 0)
        {
            return [];
        }

        var sorted = candles.OrderBy(x => x.OpenTime).ToList();
        var resampled = factor == 1
            ? sorted.Select(x => x with { Interval = interval }).ToList()
            : ResampleCandles(sorted, interval);
        return resampled.TakeLast(count).ToList();
    }

    public Task<VenueAccountSnapshot> GetAccountSnapshotAsync(CancellationToken cancellationToken = default)
    {
        return GetAccountSnapshotAsync(AccountSnapshotSections.All, cancellationToken);
    }

    public async Task<VenueAccountSnapshot> GetAccountSnapshotAsync(AccountSnapshotSections sections, CancellationToken cancellationToken = default)
    {
        if (!TryGetEffectiveAccountAddress(out _))
        {
            return new VenueAccountSnapshot(DateTimeOffset.UtcNow, [], [], []);
        }

        if (sections == AccountSnapshotSections.None)
        {
            return new VenueAccountSnapshot(DateTimeOffset.UtcNow, [], [], []);
        }

        try
        {
            var marketMap = await GetMarketMapAsync(cancellationToken);
            var familyState = await LoadFamilyStateAsync(cancellationToken);
            var positions = sections.HasFlag(AccountSnapshotSections.Positions)
                ? BuildPositions(familyState, marketMap)
                : [];
            var balances = sections.HasFlag(AccountSnapshotSections.Balances)
                ? BuildBalances(familyState, marketMap)
                : [];
            var orders = sections.HasFlag(AccountSnapshotSections.Orders)
                ? BuildOpenOrders(familyState, marketMap)
                : [];
            RefreshTrackedContexts(
                sections.HasFlag(AccountSnapshotSections.Positions) ? positions : null,
                sections.HasFlag(AccountSnapshotSections.Orders) ? orders : null);
            return new VenueAccountSnapshot(DateTimeOffset.UtcNow, positions, orders, balances);
        }
        catch (Exception ex)
        {
            _logger.Error("dYdX", "GetAccountSnapshot failed", ex);
            return new VenueAccountSnapshot(DateTimeOffset.UtcNow, [], [], []);
        }
    }

    private async Task<OrderAck> PlaceOrderCoreAsync(
        string symbol,
        string side,
        decimal qty,
        decimal? price,
        bool reduceOnly,
        DydxOrderInstruction? instruction,
        CancellationToken cancellationToken)
    {
        if (!HasTradingCredentials())
        {
            return new OrderAck(DateTimeOffset.UtcNow, string.Empty, false, "dYdX requires a dydx Account Address or Wallet Address, plus PrivateKey.");
        }

        if (ResolveConfiguredSubaccountNumber() > 127)
        {
            return new OrderAck(DateTimeOffset.UtcNow, string.Empty, false, "dYdX Sub Account Id should be the parent subaccount number (0-127). Child subaccounts are managed automatically for Isolated mode.");
        }

        if (qty <= 0)
        {
            return new OrderAck(DateTimeOffset.UtcNow, string.Empty, false, "Quantity must be greater than 0.");
        }

        try
        {
            var normalizedSymbol = NormalizeSymbol(symbol);
            var market = await GetMarketSpecAsync(normalizedSymbol, cancellationToken);
            if (market is null)
            {
                return new OrderAck(DateTimeOffset.UtcNow, string.Empty, false, $"dYdX market not found: {normalizedSymbol}");
            }

            if (!string.Equals(market.Status, "ACTIVE", StringComparison.OrdinalIgnoreCase))
            {
                return new OrderAck(DateTimeOffset.UtcNow, string.Empty, false, $"dYdX market {market.Ticker} is not active ({market.Status}).");
            }

            if (!string.Equals(market.MarketType, "CROSS", StringComparison.OrdinalIgnoreCase))
            {
                return new OrderAck(DateTimeOffset.UtcNow, string.Empty, false, $"dYdX market {market.Ticker} is protocol-level {market.MarketType}. This app currently supports only dYdX CROSS markets.");
            }

            var configuredTrade = reduceOnly
                ? new DydxConfiguredTradeContext(MarginMode.Unknown, 0m)
                : _configuredTrades.TryGetValue(normalizedSymbol, out var trade)
                    ? trade
                    : new DydxConfiguredTradeContext(MarginMode.Cross, Math.Max(1m, market.InitialMarginFraction > 0 ? decimal.Round(1m / market.InitialMarginFraction, 2, MidpointRounding.AwayFromZero) : 1m));

            var normalizedQty = NormalizeByStep(Math.Abs(qty), market.StepSize);
            if (normalizedQty <= 0)
            {
                return new OrderAck(DateTimeOffset.UtcNow, string.Empty, false, "Order size becomes zero after dYdX step-size normalization.");
            }

            var orderInstruction = instruction ?? DydxOrderInstruction.CreateDefault(price.HasValue, reduceOnly);

            decimal? normalizedPrice = null;
            if (price.HasValue)
            {
                normalizedPrice = NormalizeByStep(price.Value, market.TickSize);
                if (!normalizedPrice.HasValue || normalizedPrice.Value <= 0)
                {
                    return new OrderAck(DateTimeOffset.UtcNow, string.Empty, false, "Limit price becomes invalid after dYdX tick-size normalization.");
                }
            }

            decimal? normalizedTriggerPrice = null;
            if (orderInstruction.TriggerPrice.HasValue)
            {
                normalizedTriggerPrice = NormalizeByStep(orderInstruction.TriggerPrice.Value, market.TickSize);
                if (!normalizedTriggerPrice.HasValue || normalizedTriggerPrice.Value <= 0)
                {
                    return new OrderAck(DateTimeOffset.UtcNow, string.Empty, false, "Trigger price becomes invalid after dYdX tick-size normalization.");
                }
            }

            var familyState = await LoadFamilyStateAsync(cancellationToken);
            var exposure = AnalyzeSymbolExposure(familyState, normalizedSymbol);
            if (!exposure.IsSupported)
            {
                return new OrderAck(DateTimeOffset.UtcNow, string.Empty, false, exposure.Message ?? $"dYdX symbol state is ambiguous for {normalizedSymbol}.");
            }

            var targetContext = reduceOnly
                ? ResolveReduceOnlyContext(exposure, normalizedSymbol)
                : await ResolveEntryContextAsync(
                    familyState,
                    exposure,
                    normalizedSymbol,
                    normalizedQty,
                    normalizedPrice,
                    configuredTrade,
                    market,
                    cancellationToken);
            if (!targetContext.IsSuccess)
            {
                return new OrderAck(DateTimeOffset.UtcNow, string.Empty, false, targetContext.Message ?? "Unable to resolve dYdX subaccount context.");
            }

            var clientId = NextClientId();
            var payload = new
            {
                environment = _environment,
                accountAddress = RequireAccountAddress(),
                walletAddress = _credentials.WalletAddress,
                privateKey = RequirePrivateKey(),
                subaccountNumber = targetContext.SubaccountNumber,
                marketId = market.Ticker,
                side = NormalizeOrderSide(side),
                size = normalizedQty.ToString("0.########", CultureInfo.InvariantCulture),
                orderType = orderInstruction.HelperOrderType,
                price = normalizedPrice?.ToString("0.########", CultureInfo.InvariantCulture),
                reduceOnly,
                clientId,
                triggerPrice = normalizedTriggerPrice?.ToString("0.########", CultureInfo.InvariantCulture),
                execution = orderInstruction.Execution,
                goodTilTimeInSeconds = orderInstruction.GoodTilTimeInSeconds
            };

            var helper = await _nodeBridge.RunAsync(GetHelperRoot(), "place", payload, cancellationToken);
            if (!helper.IsSuccess)
            {
                return new OrderAck(DateTimeOffset.UtcNow, clientId.ToString(CultureInfo.InvariantCulture), false, helper.Message);
            }

            return new OrderAck(
                DateTimeOffset.UtcNow,
                clientId.ToString(CultureInfo.InvariantCulture),
                true,
                targetContext.MarginMode == MarginMode.Isolated
                    ? normalizedPrice.HasValue
                        ? orderInstruction.SuccessMessageWhenIsolatedLimit
                        : "dYdX isolated market order submitted."
                    : normalizedPrice.HasValue
                        ? orderInstruction.SuccessMessageWhenCrossLimit
                        : "dYdX market order submitted.");
        }
        catch (Exception ex)
        {
            _logger.Error("dYdX", $"PlaceOrder exception symbol={symbol}, side={side}, qty={qty}, price={price}, reduceOnly={reduceOnly}", ex);
            return new OrderAck(DateTimeOffset.UtcNow, string.Empty, false, ex.Message);
        }
    }


    private IReadOnlyList<VenueOpenOrder> BuildOpenOrders(
        DydxFamilyState familyState,
        IReadOnlyDictionary<string, DydxMarketSpec> marketMap)
    {
        var rows = new List<VenueOpenOrder>();
        foreach (var item in familyState.OpenOrders)
        {
            var ticker = item.Symbol;
            if (string.IsNullOrWhiteSpace(ticker))
            {
                continue;
            }

            var size = item.Size;
            var filled = item.TotalFilled;
            var remaining = Math.Max(0m, size - filled);
            if (remaining <= 0)
            {
                continue;
            }

            var price = item.Price;
            if ((!marketMap.TryGetValue(ticker, out var market) || market.OraclePrice <= 0) && price <= 0)
            {
                continue;
            }

            var referencePrice = SelectReasonableOrderPrice(price, market?.OraclePrice ?? 0m);
            var notionalUsd = remaining * referencePrice;
            var orderId = item.OrderId;
            var status = item.Status;
            var marginMode = item.SubaccountNumber == familyState.ParentSubaccountNumber ? MarginMode.Cross : MarginMode.Isolated;

            rows.Add(new VenueOpenOrder(
                ticker,
                notionalUsd,
                0m,
                price > 0 ? price : null,
                status,
                orderId,
                marginMode));
        }

        return rows;
    }

    private List<VenuePosition> BuildPositions(DydxFamilyState familyState, IReadOnlyDictionary<string, DydxMarketSpec> marketMap)
    {
        var grouped = new Dictionary<string, List<(VenuePosition Position, int SubaccountNumber)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var subaccount in familyState.Subaccounts)
        {
            var marginMode = subaccount.SubaccountNumber == familyState.ParentSubaccountNumber ? MarginMode.Cross : MarginMode.Isolated;
            foreach (var position in subaccount.Positions)
            {
                var markPrice = marketMap.TryGetValue(position.Symbol, out var market) ? market.OraclePrice : 0m;
                var notional = Math.Abs(position.Quantity) * (markPrice > 0 ? markPrice : position.EntryPrice);
                var pnlPct = PositionPnlMath.ComputeUnrealizedPnlPctOrDirectional(
                    notional,
                    position.UnrealizedPnlUsd,
                    position.Quantity,
                    position.EntryPrice,
                    markPrice);

                var row = new VenuePosition(
                    position.Symbol,
                    position.Quantity,
                    notional,
                    0m,
                    position.EntryPrice,
                    markPrice,
                    pnlPct,
                    position.UnrealizedPnlUsd,
                    position.RealizedPnlUsd,
                    marginMode);

                if (!grouped.TryGetValue(position.Symbol, out var list))
                {
                    list = [];
                    grouped[position.Symbol] = list;
                }

                list.Add((row, subaccount.SubaccountNumber));
            }
        }

        var rows = new List<VenuePosition>();
        foreach (var entry in grouped.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (entry.Value.Count == 1)
            {
                rows.Add(entry.Value[0].Position);
                continue;
            }

            _logger.Warn("dYdX", $"Multiple dYdX position contexts detected for {entry.Key}; collapsing to Unknown margin mode.");
            var totalQty = entry.Value.Sum(x => x.Position.Quantity);
            var totalNotional = entry.Value.Sum(x => x.Position.NotionalUsd);
            var entryPrice = entry.Value.Sum(x => Math.Abs(x.Position.Quantity) * x.Position.EntryPrice);
            var qtyWeight = entry.Value.Sum(x => Math.Abs(x.Position.Quantity));
            var weightedEntry = qtyWeight > 0 ? entryPrice / qtyWeight : 0m;
            var markPrice = entry.Value.Select(x => x.Position.MarkPrice).FirstOrDefault(x => x > 0);
            rows.Add(new VenuePosition(
                entry.Key,
                totalQty,
                totalNotional,
                0m,
                weightedEntry,
                markPrice,
                0m,
                entry.Value.Sum(x => x.Position.UnrealizedPnlUsd),
                entry.Value.Sum(x => x.Position.RealizedPnlUsd),
                MarginMode.Unknown));
        }

        return rows;
    }

    private List<VenueBalance> BuildBalances(DydxFamilyState familyState, IReadOnlyDictionary<string, DydxMarketSpec> marketMap)
    {
        var totals = new Dictionary<string, (decimal Quantity, decimal UsdValue)>(StringComparer.OrdinalIgnoreCase);
        foreach (var subaccount in familyState.Subaccounts)
        {
            foreach (var balance in subaccount.Balances)
            {
                totals.TryGetValue(balance.Asset, out var existing);
                totals[balance.Asset] = (
                    existing.Quantity + balance.Quantity,
                    existing.UsdValue + balance.UsdValue);
            }
        }

        var rows = new List<VenueBalance>();
        foreach (var entry in totals.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            var asset = entry.Key;
            var qty = entry.Value.Quantity;
            var usdValue = entry.Value.UsdValue;
            if (qty == 0m && usdValue == 0m)
            {
                continue;
            }

            if (usdValue == 0m)
            {
                usdValue = IsStableAsset(asset)
                    ? qty
                    : qty * (marketMap.TryGetValue($"{asset}-USD", out var market) ? market.OraclePrice : 0m);
            }

            rows.Add(new VenueBalance(asset, qty, Math.Max(0m, usdValue)));
        }

        return rows;
    }

    private async Task<DydxResolvedTradeContext> ResolveEntryContextAsync(
        DydxFamilyState familyState,
        DydxSymbolExposure exposure,
        string normalizedSymbol,
        decimal normalizedQty,
        decimal? normalizedPrice,
        DydxConfiguredTradeContext configuredTrade,
        DydxMarketSpec market,
        CancellationToken cancellationToken)
    {
        if (configuredTrade.MarginMode == MarginMode.Isolated)
        {
            var auth = await GetAuthProfileAsync(cancellationToken);
            if (auth.Permissioned)
            {
                return new DydxResolvedTradeContext(false, 0, MarginMode.Unknown, "dYdX isolated mode requires the owner wallet. API Trading Keys only support cross-subaccount order actions.");
            }

            if (exposure.CrossSubaccountNumber.HasValue)
            {
                return new DydxResolvedTradeContext(false, 0, MarginMode.Unknown, $"dYdX already has Cross exposure on {normalizedSymbol} in subaccount {exposure.CrossSubaccountNumber.Value}. Close or cancel it before switching to Isolated.");
            }

            var targetSubaccount = exposure.UniqueIsolatedSubaccountNumber ?? SelectReusableChildSubaccountNumber(familyState);
            if (targetSubaccount <= 0)
            {
                return new DydxResolvedTradeContext(false, 0, MarginMode.Unknown, "No reusable dYdX isolated child subaccount is available for this parent account.");
            }

            var referencePrice = normalizedPrice ?? market.OraclePrice;
            if (referencePrice <= 0)
            {
                return new DydxResolvedTradeContext(false, 0, MarginMode.Unknown, "dYdX isolated mode needs a valid market reference price.");
            }

            var requiredCollateral = CalculateRequiredIsolatedCollateral(normalizedQty * referencePrice, configuredTrade.Leverage, market.InitialMarginFraction);
            var childState = familyState.Subaccounts.FirstOrDefault(x => x.SubaccountNumber == targetSubaccount);
            var childFreeCollateral = childState?.FreeCollateral ?? 0m;
            var additionalCollateral = RoundUpUsdc(Math.Max(0m, requiredCollateral - childFreeCollateral));
            if (additionalCollateral > 0m)
            {
                var parentState = familyState.Subaccounts.FirstOrDefault(x => x.SubaccountNumber == familyState.ParentSubaccountNumber);
                var parentFreeCollateral = parentState?.FreeCollateral ?? 0m;
                if (parentFreeCollateral < additionalCollateral)
                {
                    return new DydxResolvedTradeContext(false, 0, MarginMode.Unknown, $"dYdX parent subaccount {familyState.ParentSubaccountNumber} free collateral is insufficient. Need {FormatDecimal(additionalCollateral)} USDC for isolated margin.");
                }

                var transfer = await TransferBetweenSubaccountsAsync(familyState.ParentSubaccountNumber, targetSubaccount, additionalCollateral, cancellationToken);
                if (!transfer.IsSuccess)
                {
                    return new DydxResolvedTradeContext(false, 0, MarginMode.Unknown, transfer.Message);
                }
            }

            return new DydxResolvedTradeContext(true, targetSubaccount, MarginMode.Isolated, null);
        }

        if (exposure.UniqueIsolatedSubaccountNumber.HasValue)
        {
            return new DydxResolvedTradeContext(false, 0, MarginMode.Unknown, $"dYdX already has Isolated exposure on {normalizedSymbol} in subaccount {exposure.UniqueIsolatedSubaccountNumber.Value}. Close or cancel it before switching to Cross.");
        }

        var crossAuth = await GetAuthProfileAsync(cancellationToken);
        if (!crossAuth.Permissioned)
        {
            await SweepIdleChildSubaccountsAsync(familyState, cancellationToken);
        }

        return new DydxResolvedTradeContext(true, familyState.ParentSubaccountNumber, MarginMode.Cross, null);
    }

    private static DydxResolvedTradeContext ResolveReduceOnlyContext(DydxSymbolExposure exposure, string normalizedSymbol)
    {
        if (exposure.CrossSubaccountNumber.HasValue && !exposure.UniqueIsolatedSubaccountNumber.HasValue)
        {
            return new DydxResolvedTradeContext(true, exposure.CrossSubaccountNumber.Value, MarginMode.Cross, null);
        }

        if (!exposure.CrossSubaccountNumber.HasValue && exposure.UniqueIsolatedSubaccountNumber.HasValue)
        {
            return new DydxResolvedTradeContext(true, exposure.UniqueIsolatedSubaccountNumber.Value, MarginMode.Isolated, null);
        }

        if (exposure.CrossSubaccountNumber.HasValue && exposure.UniqueIsolatedSubaccountNumber.HasValue)
        {
            return new DydxResolvedTradeContext(false, 0, MarginMode.Unknown, $"dYdX has both Cross and Isolated exposure on {normalizedSymbol}. This app needs one mode at a time per symbol.");
        }

        return new DydxResolvedTradeContext(false, 0, MarginMode.Unknown, $"No dYdX position context was found for {normalizedSymbol}.");
    }

    private async Task<DydxAuthProfile> GetAuthProfileAsync(CancellationToken cancellationToken)
    {
        if (_cachedAuthProfile is not null &&
            DateTimeOffset.UtcNow - _cachedAuthProfileAt < TimeSpan.FromMinutes(1))
        {
            return _cachedAuthProfile;
        }

        var helper = await _nodeBridge.RunAsync(GetHelperRoot(), "validate", new
        {
            environment = _environment,
            accountAddress = RequireAccountAddress(),
            walletAddress = _credentials.WalletAddress,
            privateKey = RequirePrivateKey(),
            subaccountNumber = ResolveParentSubaccountNumber()
        }, cancellationToken);

        if (!helper.IsSuccess)
        {
            throw new InvalidOperationException(helper.Message);
        }

        _cachedAuthProfile = ParseAuthProfile(helper.Root);
        _cachedAuthProfileAt = DateTimeOffset.UtcNow;
        return _cachedAuthProfile;
    }

    private static DydxAuthProfile ParseAuthProfile(JsonElement root)
    {
        return new DydxAuthProfile(
            ReadBoolean(root, "permissioned"),
            ReadString(root, "walletAddress") ?? string.Empty);
    }

    private async Task<DydxFamilyState> LoadFamilyStateAsync(CancellationToken cancellationToken)
    {
        var parentSubaccountNumber = ResolveParentSubaccountNumber();
        var accountAddress = RequireAccountAddress();
        using var subaccountsDoc = await GetJsonOrNullAsync(
            $"/v4/addresses/{Uri.EscapeDataString(accountAddress)}",
            cancellationToken);
        if (subaccountsDoc is null)
        {
            _logger.Info("dYdX", $"No subaccounts found for address={accountAddress}. Returning an empty account snapshot.");
            return new DydxFamilyState(parentSubaccountNumber, [], []);
        }

        var subaccounts = ParseFamilySubaccounts(subaccountsDoc.RootElement, parentSubaccountNumber);
        using var ordersDoc = await GetJsonOrNullAsync(
            $"/v4/orders/parentSubaccountNumber?address={Uri.EscapeDataString(accountAddress)}&parentSubaccountNumber={parentSubaccountNumber}&status=OPEN&returnLatestOrders=true",
            cancellationToken);

        var openOrders = ordersDoc is null
            ? []
            : ParseParentOrders(ordersDoc.RootElement);
        return new DydxFamilyState(parentSubaccountNumber, subaccounts, openOrders);
    }

    private List<DydxSubaccountState> ParseFamilySubaccounts(JsonElement root, int parentSubaccountNumber)
    {
        var rows = new List<DydxSubaccountState>();
        if (root.TryGetProperty("subaccounts", out var subaccountsNode) && subaccountsNode.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in subaccountsNode.EnumerateArray())
            {
                TryAddSubaccountState(rows, item, parentSubaccountNumber);
            }

            return rows;
        }

        if (root.TryGetProperty("subaccount", out var singleSubaccount) && singleSubaccount.ValueKind == JsonValueKind.Object)
        {
            TryAddSubaccountState(rows, singleSubaccount, parentSubaccountNumber);
        }

        return rows;
    }

    private void TryAddSubaccountState(List<DydxSubaccountState> rows, JsonElement item, int parentSubaccountNumber)
    {
        var subaccountNumber = ReadInt(item, "subaccountNumber");
        if (subaccountNumber < 0 || NormalizeParentSubaccountNumber(subaccountNumber) != parentSubaccountNumber)
        {
            return;
        }

        rows.Add(new DydxSubaccountState(
            subaccountNumber,
            ReadDecimal(item, "freeCollateral"),
            ReadDecimal(item, "equity"),
            ParseSubaccountPositions(item),
            ParseSubaccountBalances(item)));
    }

    private static IReadOnlyList<DydxPositionSnapshot> ParseSubaccountPositions(JsonElement subaccount)
    {
        if (!subaccount.TryGetProperty("openPerpetualPositions", out var positionsNode) || positionsNode.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var rows = new List<DydxPositionSnapshot>();
        foreach (var prop in positionsNode.EnumerateObject())
        {
            var item = prop.Value;
            var symbol = NormalizeSymbol(ReadString(item, "market") ?? prop.Name);
            var quantity = ReadDecimal(item, "size");
            if (string.IsNullOrWhiteSpace(symbol) || quantity == 0m)
            {
                continue;
            }

            rows.Add(new DydxPositionSnapshot(
                symbol,
                quantity,
                ReadDecimal(item, "entryPrice"),
                ReadDecimal(item, "unrealizedPnl"),
                ReadDecimal(item, "realizedPnl")));
        }

        return rows;
    }

    private static IReadOnlyList<VenueBalance> ParseSubaccountBalances(JsonElement subaccount)
    {
        var rows = new List<VenueBalance>();
        if (subaccount.TryGetProperty("assetPositions", out var positionsNode))
        {
            if (positionsNode.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in positionsNode.EnumerateObject())
                {
                    TryAddBalanceRow(rows, prop.Value, prop.Name);
                }
            }
            else if (positionsNode.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in positionsNode.EnumerateArray())
                {
                    TryAddBalanceRow(rows, item, null);
                }
            }
        }

        if (rows.Count > 0)
        {
            return rows;
        }

        var equity = ReadDecimal(subaccount, "equity");
        var freeCollateral = ReadDecimal(subaccount, "freeCollateral");
        var fallbackAmount = equity != 0m ? equity : freeCollateral;
        if (fallbackAmount != 0m)
        {
            rows.Add(new VenueBalance("USDC", fallbackAmount, Math.Max(0m, fallbackAmount)));
        }

        return rows;
    }

    private static void TryAddBalanceRow(List<VenueBalance> rows, JsonElement item, string? fallbackAsset)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var asset = (ReadString(item, "symbol") ??
                     ReadString(item, "asset") ??
                     fallbackAsset ??
                     string.Empty).Trim().ToUpperInvariant();
        decimal qty;
        if (IsStableAsset(asset))
        {
            qty = ReadDecimal(item, "balance");
            if (qty == 0m)
            {
                qty = ReadDecimal(item, "equity");
            }
        }
        else
        {
            qty = ReadDecimal(item, "size");
            if (qty == 0m)
            {
                qty = ReadDecimal(item, "balance");
            }

            if (qty == 0m)
            {
                qty = ReadDecimal(item, "equity");
            }
        }

        if (string.IsNullOrWhiteSpace(asset) || qty == 0m)
        {
            return;
        }

        rows.Add(new VenueBalance(asset, qty, IsStableAsset(asset) ? Math.Max(0m, qty) : 0m));
    }

    private static IReadOnlyList<DydxOpenOrderState> ParseParentOrders(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var rows = new List<DydxOpenOrderState>();
        foreach (var item in root.EnumerateArray())
        {
            rows.Add(new DydxOpenOrderState(
                NormalizeSymbol(ReadString(item, "ticker")),
                ReadDecimal(item, "size"),
                ReadDecimal(item, "totalFilled"),
                ReadDecimal(item, "price"),
                ReadString(item, "status") ?? "OPEN",
                ReadString(item, "id"),
                ReadInt(item, "subaccountNumber")));
        }

        return rows;
    }

    private static DydxSymbolExposure AnalyzeSymbolExposure(DydxFamilyState familyState, string normalizedSymbol)
    {
        var crossHasExposure = familyState.Subaccounts
            .Where(x => x.SubaccountNumber == familyState.ParentSubaccountNumber)
            .Any(x => x.Positions.Any(p => string.Equals(p.Symbol, normalizedSymbol, StringComparison.OrdinalIgnoreCase))) ||
            familyState.OpenOrders.Any(x =>
                string.Equals(x.Symbol, normalizedSymbol, StringComparison.OrdinalIgnoreCase) &&
                x.SubaccountNumber == familyState.ParentSubaccountNumber);

        var isolatedSubaccounts = familyState.Subaccounts
            .Where(x => x.SubaccountNumber != familyState.ParentSubaccountNumber)
            .Where(x => x.Positions.Any(p => string.Equals(p.Symbol, normalizedSymbol, StringComparison.OrdinalIgnoreCase)))
            .Select(x => x.SubaccountNumber)
            .Concat(familyState.OpenOrders
                .Where(x =>
                    x.SubaccountNumber != familyState.ParentSubaccountNumber &&
                    string.Equals(x.Symbol, normalizedSymbol, StringComparison.OrdinalIgnoreCase))
                .Select(x => x.SubaccountNumber))
            .Distinct()
            .OrderBy(x => x)
            .ToList();

        if (crossHasExposure && isolatedSubaccounts.Count > 0)
        {
            return new DydxSymbolExposure(false, $"dYdX has both Cross and Isolated exposure on {normalizedSymbol}. This app needs one mode at a time per symbol.", familyState.ParentSubaccountNumber, null);
        }

        if (isolatedSubaccounts.Count > 1)
        {
            return new DydxSymbolExposure(false, $"dYdX has multiple isolated subaccounts on {normalizedSymbol}. This app currently supports one isolated context per symbol.", null, null);
        }

        return new DydxSymbolExposure(true, null, crossHasExposure ? familyState.ParentSubaccountNumber : null, isolatedSubaccounts.Count == 1 ? isolatedSubaccounts[0] : null);
    }

    private static int SelectReusableChildSubaccountNumber(DydxFamilyState familyState)
    {
        var busySubaccounts = new HashSet<int>(
            familyState.Subaccounts
                .Where(x => x.SubaccountNumber != familyState.ParentSubaccountNumber && x.Positions.Count > 0)
                .Select(x => x.SubaccountNumber)
                .Concat(familyState.OpenOrders
                    .Where(x => x.SubaccountNumber != familyState.ParentSubaccountNumber)
                    .Select(x => x.SubaccountNumber)));

        var reusableExisting = familyState.Subaccounts
            .Where(x => x.SubaccountNumber != familyState.ParentSubaccountNumber)
            .Where(x => !busySubaccounts.Contains(x.SubaccountNumber))
            .OrderBy(x => x.SubaccountNumber)
            .FirstOrDefault();
        if (reusableExisting is not null)
        {
            return reusableExisting.SubaccountNumber;
        }

        var existing = familyState.Subaccounts
            .Select(x => x.SubaccountNumber)
            .ToHashSet();
        for (var multiplier = 1; multiplier <= 1000; multiplier++)
        {
            var candidate = familyState.ParentSubaccountNumber + (128 * multiplier);
            if (candidate > 128000)
            {
                break;
            }

            if (!existing.Contains(candidate))
            {
                return candidate;
            }
        }

        return 0;
    }

    private async Task SweepIdleChildSubaccountsAsync(DydxFamilyState familyState, CancellationToken cancellationToken)
    {
        foreach (var subaccount in familyState.Subaccounts
                     .Where(x => x.SubaccountNumber != familyState.ParentSubaccountNumber)
                     .OrderBy(x => x.SubaccountNumber))
        {
            if (subaccount.Positions.Count > 0 ||
                familyState.OpenOrders.Any(x => x.SubaccountNumber == subaccount.SubaccountNumber))
            {
                continue;
            }

            var transferable = FloorUsdc(subaccount.FreeCollateral);
            if (transferable <= 0m)
            {
                continue;
            }

            var transfer = await TransferBetweenSubaccountsAsync(
                subaccount.SubaccountNumber,
                familyState.ParentSubaccountNumber,
                transferable,
                cancellationToken);
            if (!transfer.IsSuccess)
            {
                _logger.Warn("dYdX", $"Idle collateral sweep failed child={subaccount.SubaccountNumber}, parent={familyState.ParentSubaccountNumber}, msg={transfer.Message}");
            }
        }
    }

    private async Task<(bool IsSuccess, string Message)> TransferBetweenSubaccountsAsync(
        int sourceSubaccountNumber,
        int recipientSubaccountNumber,
        decimal amount,
        CancellationToken cancellationToken)
    {
        var roundedAmount = FloorUsdc(amount);
        if (sourceSubaccountNumber == recipientSubaccountNumber || roundedAmount <= 0m)
        {
            return (true, "ok");
        }

        var helper = await _nodeBridge.RunAsync(GetHelperRoot(), "transfer", new
        {
            environment = _environment,
            accountAddress = RequireAccountAddress(),
            walletAddress = _credentials.WalletAddress,
            privateKey = RequirePrivateKey(),
            subaccountNumber = sourceSubaccountNumber,
            recipientSubaccountNumber,
            amount = roundedAmount.ToString("0.######", CultureInfo.InvariantCulture)
        }, cancellationToken);

        return helper.IsSuccess
            ? (true, "ok")
            : (false, helper.Message);
    }

    private void RefreshTrackedContexts(IReadOnlyList<VenuePosition>? positions, IReadOnlyList<VenueOpenOrder>? orders)
    {
        if (positions is not null)
        {
            _positionContexts.Clear();
            foreach (var position in positions.Where(x => x.MarginMode == MarginMode.Cross))
            {
                _positionContexts[position.Symbol] = new DydxTrackedContext(ResolveParentSubaccountNumber(), MarginMode.Cross);
            }
        }

        if (orders is not null)
        {
            _orderContexts.Clear();
            foreach (var order in orders.Where(x => !string.IsNullOrWhiteSpace(x.OrderId) && x.MarginMode == MarginMode.Cross))
            {
                _orderContexts[order.OrderId!] = new DydxTrackedContext(ResolveParentSubaccountNumber(), MarginMode.Cross);
            }
        }
    }

    private int ResolveParentSubaccountNumber()
    {
        return NormalizeParentSubaccountNumber(ResolveConfiguredSubaccountNumber());
    }

    private int ResolveConfiguredSubaccountNumber()
    {
        return int.TryParse((_credentials.SubAccountId ?? string.Empty).Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var value) && value >= 0
            ? value
            : 0;
    }

    private static int NormalizeParentSubaccountNumber(int subaccountNumber)
    {
        if (subaccountNumber < 0)
        {
            return 0;
        }

        return subaccountNumber % 128;
    }

    private static string ResolveConditionalCloseOrderType(string side, decimal targetPrice, decimal referencePrice)
    {
        var normalizedSide = NormalizeOrderSide(side);
        return normalizedSide switch
        {
            "sell" => targetPrice >= referencePrice ? "take_profit_limit" : "stop_limit",
            "buy" => targetPrice <= referencePrice ? "take_profit_limit" : "stop_limit",
            _ => targetPrice >= referencePrice ? "take_profit_limit" : "stop_limit"
        };
    }

    private static decimal CalculateRequiredIsolatedCollateral(decimal notionalUsd, decimal leverage, decimal initialMarginFraction)
    {
        if (notionalUsd <= 0m)
        {
            return 0m;
        }

        var targetCollateral = leverage > 0m ? notionalUsd / leverage : notionalUsd;
        var minimumCollateral = initialMarginFraction > 0m ? notionalUsd * initialMarginFraction : 0m;
        return RoundUpUsdc(Math.Max(targetCollateral, minimumCollateral) * 1.02m);
    }

    private static decimal FloorUsdc(decimal value)
    {
        return value <= 0m
            ? 0m
            : decimal.Floor(value * 1_000_000m) / 1_000_000m;
    }

    private static decimal RoundUpUsdc(decimal value)
    {
        return value <= 0m
            ? 0m
            : decimal.Ceiling(value * 1_000_000m) / 1_000_000m;
    }

    private static string FormatDecimal(decimal value)
    {
        return value.ToString("0.######", CultureInfo.InvariantCulture);
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(32 * 1024);
        using var frameBuffer = new MemoryStream();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                frameBuffer.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }

                    if (result.Count > 0)
                    {
                        frameBuffer.Write(buffer, 0, result.Count);
                    }
                }
                while (!result.EndOfMessage);

                if (frameBuffer.Length <= 0)
                {
                    continue;
                }

                var payload = Encoding.UTF8.GetString(frameBuffer.GetBuffer(), 0, (int)frameBuffer.Length);
                if (string.Equals(payload, "PING", StringComparison.Ordinal))
                {
                    await SendRawWsAsync(ws, "PONG", cancellationToken);
                    continue;
                }

                HandleWsMessage(payload);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.Error("dYdX", "WS receive loop failed", ex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void HandleWsMessage(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            var type = ReadString(root, "type");
            if (string.Equals(type, "connected", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, "subscribed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, "pong", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var channel = ReadString(root, "channel");
            if (!root.TryGetProperty("contents", out var contents))
            {
                return;
            }

            if (string.Equals(channel, "v4_trades", StringComparison.OrdinalIgnoreCase))
            {
                EmitTradeTicks(contents);
                return;
            }

            if (string.Equals(channel, "v4_orderbook", StringComparison.OrdinalIgnoreCase))
            {
                EmitOrderBook(contents);
            }
        }
        catch
        {
        }
    }

    private void EmitTradeTicks(JsonElement contents)
    {
        foreach (var trade in EnumerateTradeItems(contents))
        {
            var price = ReadDecimal(trade, "price");
            var size = ReadDecimal(trade, "size");
            if (price <= 0 || size <= 0)
            {
                continue;
            }

            var ts = ReadDateTimeOffset(trade, "createdAt") ??
                     ReadDateTimeOffset(trade, "updatedAt") ??
                     DateTimeOffset.UtcNow;
            _channel.Writer.TryWrite(new TradeTick(ts, price, size));
        }
    }

    private IEnumerable<JsonElement> EnumerateTradeItems(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object &&
                    item.TryGetProperty("trades", out var tradesNode))
                {
                    foreach (var trade in EnumerateTradeItems(tradesNode))
                    {
                        yield return trade;
                    }

                    continue;
                }

                if (item.ValueKind == JsonValueKind.Object &&
                    item.TryGetProperty("price", out _))
                {
                    yield return item;
                }
            }

            yield break;
        }

        if (node.ValueKind == JsonValueKind.Object &&
            node.TryGetProperty("trades", out var nestedTrades))
        {
            foreach (var trade in EnumerateTradeItems(nestedTrades))
            {
                yield return trade;
            }

            yield break;
        }

        if (node.ValueKind == JsonValueKind.Object &&
            node.TryGetProperty("price", out _))
        {
            yield return node;
        }
    }

    private void EmitOrderBook(JsonElement contents)
    {
        var timestamp = DateTimeOffset.UtcNow;
        if (contents.ValueKind == JsonValueKind.Object &&
            contents.TryGetProperty("bids", out _) &&
            contents.TryGetProperty("asks", out _))
        {
            var snapshot = ParseLevelObjects(contents);
            _channel.Writer.TryWrite(new OrderBookSnapshot(timestamp, snapshot.Asks, snapshot.Bids));
            return;
        }

        if (contents.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var asks = new List<(decimal Price, decimal Size)>();
        var bids = new List<(decimal Price, decimal Size)>();
        foreach (var item in contents.EnumerateArray())
        {
            AppendDeltaLevels(item, "asks", asks);
            AppendDeltaLevels(item, "bids", bids);
        }

        if (asks.Count > 0 || bids.Count > 0)
        {
            _channel.Writer.TryWrite(new OrderBookDelta(timestamp, asks, bids));
        }
    }

    private static (IReadOnlyList<(decimal Price, decimal Size)> Asks, IReadOnlyList<(decimal Price, decimal Size)> Bids) ParseLevelObjects(JsonElement node)
    {
        var asks = ParseLevelObjectArray(node, "asks");
        var bids = ParseLevelObjectArray(node, "bids");
        return (asks, bids);
    }

    private static List<(decimal Price, decimal Size)> ParseLevelObjectArray(JsonElement node, string propertyName)
    {
        var rows = new List<(decimal Price, decimal Size)>();
        if (!node.TryGetProperty(propertyName, out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return rows;
        }

        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var price = ReadDecimal(item, "price");
            var size = ReadDecimal(item, "size");
            if (price <= 0)
            {
                continue;
            }

            rows.Add((price, size));
        }

        return rows;
    }

    private static void AppendDeltaLevels(JsonElement entry, string propertyName, List<(decimal Price, decimal Size)> output)
    {
        if (!entry.TryGetProperty(propertyName, out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Array && item.GetArrayLength() >= 2)
            {
                var price = ReadDecimal(item[0]);
                var size = ReadDecimal(item[1]);
                if (price > 0)
                {
                    output.Add((price, size));
                }

                continue;
            }

            if (item.ValueKind == JsonValueKind.Object)
            {
                var price = ReadDecimal(item, "price");
                var size = ReadDecimal(item, "size");
                if (price > 0)
                {
                    output.Add((price, size));
                }
            }
        }
    }

    private async Task<Dictionary<string, DydxMarketSpec>> GetMarketMapAsync(CancellationToken cancellationToken)
    {
        if (_markets.Count > 0 && DateTimeOffset.UtcNow - _marketsLoadedAt < TimeSpan.FromMinutes(2))
        {
            return _markets;
        }

        await _marketsGate.WaitAsync(cancellationToken);
        try
        {
            if (_markets.Count > 0 && DateTimeOffset.UtcNow - _marketsLoadedAt < TimeSpan.FromMinutes(2))
            {
                return _markets;
            }

            using var doc = await GetJsonAsync("/v4/perpetualMarkets", cancellationToken);
            if (!doc.RootElement.TryGetProperty("markets", out var marketsNode) || marketsNode.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("dYdX markets payload is invalid.");
            }

            var next = new Dictionary<string, DydxMarketSpec>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in marketsNode.EnumerateObject())
            {
                var item = prop.Value;
                var ticker = NormalizeSymbol(prop.Name);
                next[ticker] = new DydxMarketSpec(
                    ticker,
                    ReadInt(item, "clobPairId"),
                    ReadDecimal(item, "tickSize"),
                    ReadDecimal(item, "stepSize"),
                    ReadDecimal(item, "oraclePrice"),
                    ReadDecimal(item, "initialMarginFraction"),
                    ReadString(item, "marketType") ?? string.Empty,
                    ReadString(item, "status") ?? string.Empty);
            }

            _markets = next;
            _marketsLoadedAt = DateTimeOffset.UtcNow;
            return _markets;
        }
        finally
        {
            _marketsGate.Release();
        }
    }

    private async Task<DydxMarketSpec?> GetMarketSpecAsync(string symbol, CancellationToken cancellationToken)
    {
        var normalized = NormalizeSymbol(symbol);
        var markets = await GetMarketMapAsync(cancellationToken);
        return markets.TryGetValue(normalized, out var market) ? market : null;
    }

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(_indexerBase + path, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"dYdX request failed {(int)response.StatusCode}: {Trim(body)}");
        }

        return JsonDocument.Parse(body);
    }

    private async Task<JsonDocument?> GetJsonOrNullAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(_indexerBase + path, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"dYdX request failed {(int)response.StatusCode}: {Trim(body)}");
        }

        return JsonDocument.Parse(body);
    }

    private static async Task SendWsAsync(ClientWebSocket ws, object payload, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload);
        await SendRawWsAsync(ws, json, cancellationToken);
    }

    private static async Task SendRawWsAsync(ClientWebSocket ws, string payload, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken);
    }

    private string GetHelperRoot()
    {
        return Path.Combine(AppContext.BaseDirectory, "Tools", "DydxNodeHelper");
    }

    private bool HasTradingCredentials()
    {
        return TryGetEffectiveAccountAddress(out _) &&
               !string.IsNullOrWhiteSpace(_credentials.PrivateKey);
    }

    private string RequireAccountAddress()
    {
        if (!TryGetEffectiveAccountAddress(out var value))
        {
            throw new InvalidOperationException("dYdX requires a dydx owner Account Address or Wallet Address.");
        }

        return value;
    }

    private bool TryGetEffectiveAccountAddress(out string address)
    {
        var configuredAccountAddress = NormalizeDydxChainAddress(_credentials.AccountAddress);
        if (!string.IsNullOrWhiteSpace(configuredAccountAddress))
        {
            address = configuredAccountAddress;
            return true;
        }

        var walletAddress = NormalizeDydxChainAddress(_credentials.WalletAddress);
        if (!string.IsNullOrWhiteSpace(walletAddress))
        {
            address = walletAddress;
            return true;
        }

        address = string.Empty;
        return false;
    }

    private static string? NormalizeDydxChainAddress(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed) ||
            !trimmed.StartsWith("dydx", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return trimmed.ToLowerInvariant();
    }

    private string RequirePrivateKey()
    {
        var value = (_credentials.PrivateKey ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("dYdX PrivateKey is required.");
        }

        return value;
    }

    private static string NormalizeOrderSide(string side)
    {
        return string.Equals(side, "Sell", StringComparison.OrdinalIgnoreCase) ? "sell" : "buy";
    }

    private static string NormalizeSymbol(string? symbol)
    {
        var normalized = (symbol ?? string.Empty).Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "BTC-USD";
        }

        if (!normalized.Contains('-', StringComparison.Ordinal) &&
            normalized.EndsWith("USD", StringComparison.Ordinal) &&
            normalized.Length > 3)
        {
            return $"{normalized[..^3]}-USD";
        }

        return normalized;
    }

    private static decimal NormalizeByStep(decimal value, decimal step)
    {
        if (value <= 0)
        {
            return 0m;
        }

        if (step <= 0)
        {
            return value;
        }

        var units = decimal.Floor(value / step);
        return units <= 0 ? 0m : units * step;
    }

    private static int NextClientId()
    {
        return RandomNumberGenerator.GetInt32(1, int.MaxValue);
    }

    private static long ToUnixSeconds(DateTimeOffset value)
    {
        return value.ToUnixTimeSeconds();
    }

    private static bool IsStableAsset(string asset)
    {
        return asset is "USDC" or "USDT" or "USD";
    }

    private static decimal SelectReasonableOrderPrice(decimal orderPrice, decimal marketPrice)
    {
        if (orderPrice > 0 && marketPrice > 0)
        {
            var ratio = orderPrice / marketPrice;
            if (ratio >= 0.05m && ratio <= 20m)
            {
                return orderPrice;
            }
        }

        if (marketPrice > 0)
        {
            return marketPrice;
        }

        return orderPrice > 0 ? orderPrice : 0m;
    }

    private static (string Resolution, int Factor) IntervalToDydx(CandleInterval interval)
    {
        return interval switch
        {
            CandleInterval.M5 => ("5MINS", 1),
            CandleInterval.M10 => ("5MINS", 2),
            CandleInterval.M15 => ("15MINS", 1),
            CandleInterval.M30 => ("30MINS", 1),
            CandleInterval.H1 => ("1HOUR", 1),
            CandleInterval.H2 => ("1HOUR", 2),
            CandleInterval.H4 => ("4HOURS", 1),
            CandleInterval.H6 => ("1HOUR", 6),
            CandleInterval.H12 => ("4HOURS", 3),
            CandleInterval.D1 => ("1DAY", 1),
            CandleInterval.D7 => ("1DAY", 7),
            CandleInterval.D30 => ("1DAY", 30),
            _ => ("5MINS", 1)
        };
    }

    private static CandleInterval BaseIntervalFromDydxResolution(string resolution)
    {
        return resolution switch
        {
            "5MINS" => CandleInterval.M5,
            "15MINS" => CandleInterval.M15,
            "30MINS" => CandleInterval.M30,
            "1HOUR" => CandleInterval.H1,
            "4HOURS" => CandleInterval.H4,
            "1DAY" => CandleInterval.D1,
            _ => CandleInterval.M5
        };
    }

    private static List<Candle> ResampleCandles(IReadOnlyList<Candle> baseCandles, CandleInterval target)
    {
        if (baseCandles.Count == 0)
        {
            return [];
        }

        var span = target switch
        {
            CandleInterval.M10 => TimeSpan.FromMinutes(10),
            CandleInterval.H2 => TimeSpan.FromHours(2),
            CandleInterval.H6 => TimeSpan.FromHours(6),
            CandleInterval.H12 => TimeSpan.FromHours(12),
            CandleInterval.D7 => TimeSpan.FromDays(7),
            CandleInterval.D30 => TimeSpan.FromDays(30),
            _ => TimeSpan.Zero
        };

        if (span == TimeSpan.Zero)
        {
            return baseCandles.Select(x => x with { Interval = target }).ToList();
        }

        return baseCandles
            .GroupBy(x => BucketStart(x.OpenTime, span))
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var ordered = g.OrderBy(x => x.OpenTime).ToList();
                var first = ordered[0];
                var last = ordered[^1];
                return new Candle(
                    first.VenueId,
                    first.Symbol,
                    target,
                    g.Key,
                    first.Open,
                    ordered.Max(x => x.High),
                    ordered.Min(x => x.Low),
                    last.Close,
                    ordered.Sum(x => x.Volume),
                    true);
            })
            .ToList();
    }

    private static DateTimeOffset BucketStart(DateTimeOffset ts, TimeSpan span)
    {
        var utc = ts.ToUniversalTime();
        var ticks = utc.Ticks - (utc.Ticks % span.Ticks);
        return new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    private static decimal ReadDecimal(JsonElement obj, string name)
    {
        return obj.TryGetProperty(name, out var prop) ? ReadDecimal(prop) : 0m;
    }

    private static decimal ReadDecimal(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String &&
            decimal.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return 0m;
    }

    private static int ReadInt(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var prop))
        {
            return 0;
        }

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var number))
        {
            return number;
        }

        if (prop.ValueKind == JsonValueKind.String &&
            int.TryParse(prop.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out number))
        {
            return number;
        }

        return 0;
    }

    private static bool ReadBoolean(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var prop))
        {
            return false;
        }

        return prop.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(prop.GetString(), out var parsed) => parsed,
            _ => false
        };
    }

    private static string? ReadString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var prop))
        {
            return null;
        }

        return prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString(),
            JsonValueKind.Number => prop.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static DateTimeOffset? ReadDateTimeOffset(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var prop))
        {
            return null;
        }

        if (prop.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(prop.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return parsed;
        }

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var number))
        {
            if (number > 9_999_999_999)
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(number);
            }

            return DateTimeOffset.FromUnixTimeSeconds(number);
        }

        return null;
    }

    private static string Trim(string text)
    {
        return text.Length > 320 ? text[..320] : text;
    }

    private sealed record DydxAuthProfile(bool Permissioned, string WalletAddress);

    private sealed record DydxConfiguredTradeContext(MarginMode MarginMode, decimal Leverage);

    private sealed record DydxTrackedContext(int SubaccountNumber, MarginMode MarginMode);

    private sealed record DydxOrderInstruction(
        string HelperOrderType,
        decimal? TriggerPrice,
        string Execution,
        int GoodTilTimeInSeconds,
        string SuccessMessageWhenCrossLimit,
        string SuccessMessageWhenIsolatedLimit)
    {
        public static DydxOrderInstruction CreateDefault(bool isLimitOrder, bool reduceOnly)
        {
            if (!isLimitOrder)
            {
                return new DydxOrderInstruction(
                    "market",
                    null,
                    "IOC",
                    0,
                    "dYdX market order submitted.",
                    "dYdX isolated market order submitted.");
            }

            return new DydxOrderInstruction(
                "limit",
                null,
                reduceOnly ? "IOC" : "DEFAULT",
                reduceOnly ? 0 : 604800,
                reduceOnly ? "dYdX reduce-only limit order submitted as IOC." : "dYdX limit order submitted.",
                reduceOnly ? "dYdX isolated reduce-only limit order submitted as IOC." : "dYdX isolated limit order submitted.");
        }
    }

    private sealed record DydxResolvedTradeContext(bool IsSuccess, int SubaccountNumber, MarginMode MarginMode, string? Message);

    private sealed record DydxSymbolExposure(bool IsSupported, string? Message, int? CrossSubaccountNumber, int? UniqueIsolatedSubaccountNumber);

    private sealed record DydxPositionSnapshot(
        string Symbol,
        decimal Quantity,
        decimal EntryPrice,
        decimal UnrealizedPnlUsd,
        decimal RealizedPnlUsd);

    private sealed record DydxOpenOrderState(
        string Symbol,
        decimal Size,
        decimal TotalFilled,
        decimal Price,
        string Status,
        string? OrderId,
        int SubaccountNumber);

    private sealed record DydxSubaccountState(
        int SubaccountNumber,
        decimal FreeCollateral,
        decimal Equity,
        IReadOnlyList<DydxPositionSnapshot> Positions,
        IReadOnlyList<VenueBalance> Balances);

    private sealed record DydxFamilyState(
        int ParentSubaccountNumber,
        IReadOnlyList<DydxSubaccountState> Subaccounts,
        IReadOnlyList<DydxOpenOrderState> OpenOrders);

    private sealed record DydxMarketSpec(
        string Ticker,
        int ClobPairId,
        decimal TickSize,
        decimal StepSize,
        decimal OraclePrice,
        decimal InitialMarginFraction,
        string MarketType,
        string Status);

    public async ValueTask DisposeAsync()
    {
        await DisconnectMarketDataAsync();
        _httpClient.Dispose();
        _marketsGate.Dispose();
    }
}
