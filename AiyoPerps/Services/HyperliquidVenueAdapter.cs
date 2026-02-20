using AiyoPerps.Core;
using AiyoPerps.Models;
using MessagePack;
using Nethereum.Signer;
using Nethereum.Util;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace AiyoPerps.Services;

public sealed class HyperliquidVenueAdapter : IPerpVenue, IHistoricalCandleProvider, IAccountStateProvider
{
    private readonly string _restBase;
    private readonly string _wsBase;
    private readonly AccountCredentials _credentials;
    private readonly AppLogger _logger;
    private readonly HttpClient _httpClient = new();
    private readonly Channel<MarketEvent> _channel = Channel.CreateUnbounded<MarketEvent>();
    private readonly SemaphoreSlim _metaGate = new(1, 1);
    private readonly Sha3Keccack _keccak = Sha3Keccack.Current;

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _wsCts;
    private Task? _wsTask;
    private string _coin = "BTC";
    private Dictionary<string, int>? _assetByCoin;
    private Dictionary<string, int>? _sizeDecimalsByCoin;

    public HyperliquidVenueAdapter(string environment, AccountCredentials credentials, AppLogger logger)
    {
        _credentials = credentials;
        _logger = logger;

        var isTestnet = string.Equals(environment, "testnet", StringComparison.OrdinalIgnoreCase);
        _restBase = isTestnet ? "https://api.hyperliquid-testnet.xyz" : "https://api.hyperliquid.xyz";
        _wsBase = isTestnet ? "wss://api.hyperliquid-testnet.xyz/ws" : "wss://api.hyperliquid.xyz/ws";

        _logger.Info("Hyperliquid", $"Adapter created. env={environment}, rest={_restBase}");
    }

    public string VenueId => "Hyperliquid";

    public async Task ConnectMarketDataAsync(IEnumerable<string> subscriptions, CancellationToken cancellationToken = default)
    {
        await DisconnectMarketDataAsync(cancellationToken);

        _coin = NormalizeCoin(subscriptions.FirstOrDefault() ?? "BTC");
        _logger.Info("Hyperliquid", $"ConnectMarketData start coin={_coin}, ws={_wsBase}");

        _ws = new ClientWebSocket();
        _wsCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await _ws.ConnectAsync(new Uri(_wsBase), _wsCts.Token);

        await SendSubscribeAsync(_ws, new { type = "trades", coin = _coin }, _wsCts.Token);
        await SendSubscribeAsync(_ws, new { type = "l2Book", coin = _coin }, _wsCts.Token);

        _wsTask = Task.Run(() => ReceiveLoopAsync(_ws, _wsCts.Token), _wsCts.Token);
        _logger.Info("Hyperliquid", $"ConnectMarketData done coin={_coin}");
    }

    public async Task DisconnectMarketDataAsync(CancellationToken cancellationToken = default)
    {
        _logger.Info("Hyperliquid", "DisconnectMarketDataAsync called");

        if (_wsCts is not null)
        {
            _wsCts.Cancel();
        }

        if (_ws is not null)
        {
            if (_ws.State == WebSocketState.Open || _ws.State == WebSocketState.CloseReceived)
            {
                try
                {
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.Warn("Hyperliquid", $"WS close warning: {ex.Message}");
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
                _logger.Warn("Hyperliquid", $"WS task end warning: {ex.Message}");
            }

            _wsTask = null;
        }

        _wsCts?.Dispose();
        _wsCts = null;
    }

    public async Task<(bool IsSuccess, string Message)> ConfigureLeverageAsync(string symbol, decimal leverage, CancellationToken cancellationToken = default)
    {
        if (!_credentials.HasWalletCredentials)
        {
            _logger.Warn("Hyperliquid", $"ConfigureLeverage rejected: missing wallet credentials symbol={symbol}, leverage={leverage}");
            return (false, "Hyperliquid 需要 Wallet Address + Private Key");
        }

        if (leverage <= 0)
        {
            return (false, "leverage must be positive");
        }

        try
        {
            var coin = NormalizeCoin(symbol);
            var asset = await ResolveAssetIndexAsync(coin, cancellationToken);
            var leverageInt = Math.Max(1, (int)Math.Round(leverage, MidpointRounding.AwayFromZero));
            var action = new
            {
                type = "updateLeverage",
                asset,
                isCross = true,
                leverage = leverageInt
            };

            var nonce = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var isMainnet = _restBase.Contains("api.hyperliquid.xyz", StringComparison.Ordinal);
            var signature = SignL1Action(action, nonce, isMainnet);
            var payload = new
            {
                action,
                nonce,
                signature,
                vaultAddress = (string?)null,
                expiresAfter = (long?)null
            };

            var reqBody = JsonSerializer.Serialize(payload);
            using var req = new HttpRequestMessage(HttpMethod.Post, _restBase + "/exchange")
            {
                Content = new StringContent(reqBody, Encoding.UTF8, "application/json")
            };

            _logger.Info("Hyperliquid", $"ConfigureLeverage submit coin={coin}, asset={asset}, leverage={leverageInt}, rawLeverage={leverage}");
            using var resp = await _httpClient.SendAsync(req, cancellationToken);
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.Error("Hyperliquid", $"ConfigureLeverage failed status={(int)resp.StatusCode}, body={Trim(body)}");
                return (false, body);
            }

            var result = EvaluateExchangeResponse(body);
            if (!result.IsSuccess)
            {
                _logger.Warn("Hyperliquid", $"ConfigureLeverage rejected leverage={leverageInt}, reason={result.Message}, body={Trim(body)}");
                return (false, result.Message);
            }

            _logger.Info("Hyperliquid", $"ConfigureLeverage done coin={coin}, leverage={leverageInt}, body={Trim(body)}");
            return (true, "ok");
        }
        catch (Exception ex)
        {
            _logger.Error("Hyperliquid", $"ConfigureLeverage exception symbol={symbol}, leverage={leverage}", ex);
            return (false, ex.Message);
        }
    }

    public Task<OrderAck> PlaceOrderAsync(string symbol, string side, decimal qty, decimal? price, CancellationToken cancellationToken = default)
    {
        return PlaceOrderCoreAsync(symbol, side, qty, price, reduceOnly: false, cancellationToken);
    }

    public Task<OrderAck> PlaceCloseOrderAsync(string symbol, string side, decimal positionQty, decimal? price, CancellationToken cancellationToken = default)
    {
        return PlaceOrderCoreAsync(symbol, side, Math.Abs(positionQty), price, reduceOnly: true, cancellationToken);
    }

    public async Task<OrderAck> CancelOrderAsync(string symbol, string orderId, CancellationToken cancellationToken = default)
    {
        if (!_credentials.HasWalletCredentials)
        {
            _logger.Warn("Hyperliquid", $"CancelOrder rejected: missing wallet credentials symbol={symbol}, orderId={orderId}");
            return new OrderAck(DateTimeOffset.UtcNow, string.Empty, false, "Hyperliquid 需要 Wallet Address + Private Key");
        }

        var coin = NormalizeCoin(symbol);
        if (string.IsNullOrWhiteSpace(orderId) || !long.TryParse(orderId, NumberStyles.Any, CultureInfo.InvariantCulture, out var oid))
        {
            _logger.Warn("Hyperliquid", $"CancelOrder rejected invalid orderId symbol={symbol}, orderId={orderId}");
            return new OrderAck(DateTimeOffset.UtcNow, string.Empty, false, "invalid orderId");
        }

        try
        {
            var asset = await ResolveAssetIndexAsync(coin, cancellationToken);
            var action = new
            {
                type = "cancel",
                cancels = new[]
                {
                    new
                    {
                        a = asset,
                        o = oid
                    }
                }
            };

            var nonce = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var isMainnet = _restBase.Contains("api.hyperliquid.xyz", StringComparison.Ordinal);
            var signature = SignL1Action(action, nonce, isMainnet);
            var payload = new
            {
                action,
                nonce,
                signature,
                vaultAddress = (string?)null,
                expiresAfter = (long?)null
            };

            var reqBody = JsonSerializer.Serialize(payload);
            using var req = new HttpRequestMessage(HttpMethod.Post, _restBase + "/exchange")
            {
                Content = new StringContent(reqBody, Encoding.UTF8, "application/json")
            };

            _logger.Info("Hyperliquid", $"CancelOrder submit coin={coin}, orderId={orderId}");
            using var resp = await _httpClient.SendAsync(req, cancellationToken);
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.Error("Hyperliquid", $"CancelOrder failed status={(int)resp.StatusCode}, body={Trim(body)}");
                return new OrderAck(DateTimeOffset.UtcNow, orderId, false, body);
            }

            var result = EvaluateExchangeResponse(body);
            if (!result.IsSuccess)
            {
                _logger.Warn("Hyperliquid", $"CancelOrder rejected orderId={orderId}, reason={result.Message}, body={Trim(body)}");
                return new OrderAck(DateTimeOffset.UtcNow, orderId, false, result.Message);
            }

            _logger.Info("Hyperliquid", $"CancelOrder done orderId={orderId}, success=true, body={Trim(body)}");
            return new OrderAck(DateTimeOffset.UtcNow, orderId, true, "ok");
        }
        catch (Exception ex)
        {
            _logger.Error("Hyperliquid", $"CancelOrder exception symbol={symbol}, orderId={orderId}", ex);
            return new OrderAck(DateTimeOffset.UtcNow, orderId, false, ex.Message);
        }
    }

    private async Task<OrderAck> PlaceOrderCoreAsync(string symbol, string side, decimal qty, decimal? price, bool reduceOnly, CancellationToken cancellationToken)
    {
        if (!_credentials.HasWalletCredentials)
        {
            _logger.Warn("Hyperliquid", "PlaceOrder rejected: missing wallet credentials");
            return new OrderAck(DateTimeOffset.UtcNow, string.Empty, false, "Hyperliquid 需要 Wallet Address + Private Key");
        }

        if (qty <= 0)
        {
            return new OrderAck(DateTimeOffset.UtcNow, string.Empty, false, "qty must be positive");
        }

        try
        {
            var coin = NormalizeCoin(symbol);
            var isBuy = string.Equals(side, "Buy", StringComparison.OrdinalIgnoreCase);
            var asset = await ResolveAssetIndexAsync(coin, cancellationToken);
            var sizeDecimals = ResolveSizeDecimals(coin);

            var limitPx = price ?? await ComputeMarketLikePriceAsync(coin, isBuy, cancellationToken);
            var normalizedPrice = NormalizePerpPrice(limitPx, sizeDecimals);
            var normalizedSize = NormalizeSize(Math.Abs(qty), sizeDecimals);
            if (normalizedPrice <= 0)
            {
                return new OrderAck(DateTimeOffset.UtcNow, string.Empty, false, "invalid order price");
            }

            if (normalizedSize <= 0)
            {
                return new OrderAck(DateTimeOffset.UtcNow, string.Empty, false, "invalid order size");
            }

            var tif = price.HasValue ? "Gtc" : "Ioc";
            var orderWire = new
            {
                a = asset,
                b = isBuy,
                p = DecimalToWire(normalizedPrice),
                s = DecimalToWire(normalizedSize),
                r = reduceOnly,
                t = new
                {
                    limit = new
                    {
                        tif
                    }
                }
            };
            var action = new
            {
                type = "order",
                orders = new[] { orderWire },
                grouping = "na"
            };

            var nonce = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var isMainnet = _restBase.Contains("api.hyperliquid.xyz", StringComparison.Ordinal);
            var signature = SignL1Action(action, nonce, isMainnet);
            var payload = new
            {
                action,
                nonce,
                signature,
                vaultAddress = (string?)null,
                expiresAfter = (long?)null
            };

            var reqBody = JsonSerializer.Serialize(payload);
            using var req = new HttpRequestMessage(HttpMethod.Post, _restBase + "/exchange")
            {
                Content = new StringContent(reqBody, Encoding.UTF8, "application/json")
            };

            _logger.Info("Hyperliquid", $"PlaceOrder submit coin={coin}, side={side}, qtyRaw={qty}, qtyNorm={normalizedSize}, pxRaw={limitPx}, pxNorm={normalizedPrice}, szDecimals={sizeDecimals}, tif={tif}, reduceOnly={reduceOnly}");
            using var resp = await _httpClient.SendAsync(req, cancellationToken);
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);

            if (!resp.IsSuccessStatusCode)
            {
                _logger.Error("Hyperliquid", $"PlaceOrder failed status={(int)resp.StatusCode}, body={Trim(body)}");
                return new OrderAck(DateTimeOffset.UtcNow, string.Empty, false, body);
            }

            var result = EvaluateExchangeResponse(body);
            var orderId = TryReadHyperliquidOrderId(body) ?? nonce.ToString(CultureInfo.InvariantCulture);
            if (!result.IsSuccess || !result.HasAcceptedStatus)
            {
                var reason = !result.IsSuccess
                    ? result.Message
                    : "order was not accepted by exchange";
                _logger.Warn("Hyperliquid", $"PlaceOrder rejected success={result.IsSuccess}, accepted={result.HasAcceptedStatus}, reduceOnly={reduceOnly}, orderId={orderId}, reason={reason}, body={Trim(body)}");
                return new OrderAck(DateTimeOffset.UtcNow, orderId, false, reason);
            }

            _logger.Info("Hyperliquid", $"PlaceOrder done success=true, reduceOnly={reduceOnly}, orderId={orderId}, body={Trim(body)}");
            return new OrderAck(DateTimeOffset.UtcNow, orderId, true, "ok");
        }
        catch (Exception ex)
        {
            _logger.Error("Hyperliquid", $"PlaceOrder exception symbol={symbol}, side={side}, qty={qty}, price={price}, reduceOnly={reduceOnly}", ex);
            return new OrderAck(DateTimeOffset.UtcNow, string.Empty, false, ex.Message);
        }
    }

    public async Task<(bool IsSuccess, string Message)> ValidateConnectionAsync(CancellationToken cancellationToken = default)
    {
        _logger.Info("Hyperliquid", "ValidateConnection start");
        var reqBody = JsonSerializer.Serialize(new { type = "allMids" });
        using var req = new HttpRequestMessage(HttpMethod.Post, _restBase + "/info")
        {
            Content = new StringContent(reqBody, Encoding.UTF8, "application/json")
        };
        using var resp = await _httpClient.SendAsync(req, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            _logger.Error("Hyperliquid", $"ValidateConnection failed status={(int)resp.StatusCode}, body={body}");
            return (false, $"Hyperliquid public check failed: {(int)resp.StatusCode}");
        }

        if (_credentials.HasWalletCredentials)
        {
            var wallet = new EthECKey(NormalizePrivateKey(_credentials.PrivateKey!));
            var derived = wallet.GetPublicAddress();
            if (!string.Equals(derived, _credentials.WalletAddress, StringComparison.OrdinalIgnoreCase))
            {
                _logger.Error("Hyperliquid", $"Wallet mismatch configured={_credentials.WalletAddress}, derived={derived}");
                return (false, $"錢包地址與私鑰不匹配：{derived}");
            }
        }

        _logger.Info("Hyperliquid", "ValidateConnection ok");
        return (true, _credentials.HasWalletCredentials ? "Hyperliquid public + wallet key check ok" : "Hyperliquid public connection ok (未設定錢包私鑰)");
    }

    public async Task<IReadOnlyList<Candle>> GetRecentCandlesAsync(string symbol, CandleInterval interval, int count, CancellationToken cancellationToken = default)
    {
        var coin = NormalizeCoin(symbol);
        var chartSymbol = symbol.ToUpperInvariant();
        var (baseInterval, factor) = IntervalToHyperliquid(interval);
        var fetchCount = Math.Max(80, count * factor + factor);
        var duration = IntervalDuration(baseInterval);
        var now = DateTimeOffset.UtcNow;
        var start = now.AddMilliseconds(-duration.TotalMilliseconds * fetchCount);

        var reqBody = JsonSerializer.Serialize(new
        {
            type = "candleSnapshot",
            req = new
            {
                coin,
                interval = baseInterval,
                startTime = start.ToUnixTimeMilliseconds(),
                endTime = now.ToUnixTimeMilliseconds()
            }
        });

        _logger.Info("Hyperliquid", $"Fetch candles start coin={coin}, interval={interval}, base={baseInterval}, factor={factor}, fetchCount={fetchCount}");

        using var req = new HttpRequestMessage(HttpMethod.Post, _restBase + "/info")
        {
            Content = new StringContent(reqBody, Encoding.UTF8, "application/json")
        };
        using var resp = await _httpClient.SendAsync(req, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.Error("Hyperliquid", $"Fetch candles failed status={(int)resp.StatusCode}, body={body}");
            return [];
        }

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            _logger.Warn("Hyperliquid", "Fetch candles got non-array response");
            return [];
        }

        var baseCandles = new List<Candle>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (!TryReadSnapshotCandle(item, chartSymbol, baseInterval, out var candle))
            {
                continue;
            }

            baseCandles.Add(candle);
        }

        if (baseCandles.Count == 0)
        {
            return [];
        }

        var sorted = baseCandles.OrderBy(x => x.OpenTime).ToList();
        var resampled = factor == 1
            ? sorted.Select(x => x with { Interval = interval }).ToList()
            : ResampleCandles(sorted, interval);

        var finalList = resampled.TakeLast(count).ToList();
        _logger.Info("Hyperliquid", $"Fetch candles done base={sorted.Count}, resampled={resampled.Count}, return={finalList.Count}, interval={interval}");
        return finalList;
    }

    public async Task<VenueAccountSnapshot> GetAccountSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var infoAddress = ResolveInfoAddress();
        var usingFallback = string.IsNullOrWhiteSpace(_credentials.AccountAddress) && !string.IsNullOrWhiteSpace(_credentials.WalletAddress);
        if (string.IsNullOrWhiteSpace(infoAddress))
        {
            _logger.Warn("Hyperliquid", "GetAccountSnapshot skipped: info address missing");
            return new VenueAccountSnapshot(DateTimeOffset.UtcNow, [], [], []);
        }

        if (usingFallback)
        {
            _logger.Warn("Hyperliquid", $"GetAccountSnapshot using wallet fallback for info address={MaskAddress(infoAddress)}. Configure account public address for accurate balances.");
        }

        IReadOnlyDictionary<string, decimal> mids;
        try
        {
            mids = await FetchAllMidsAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.Error("Hyperliquid", "GetAccountSnapshot failed to fetch mids", ex);
            mids = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        }

        IReadOnlyList<VenuePosition> positions;
        try
        {
            positions = await FetchPositionsAsync(infoAddress, mids, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.Error("Hyperliquid", "GetAccountSnapshot failed to fetch positions", ex);
            positions = [];
        }

        IReadOnlyList<VenueOpenOrder> openOrders;
        try
        {
            openOrders = await FetchOpenOrdersAsync(infoAddress, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.Error("Hyperliquid", "GetAccountSnapshot failed to fetch open orders", ex);
            openOrders = [];
        }

        IReadOnlyList<VenueBalance> balances;
        try
        {
            balances = await FetchBalancesAsync(infoAddress, mids, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.Error("Hyperliquid", "GetAccountSnapshot failed to fetch balances", ex);
            balances = [];
        }

        _logger.Info("Hyperliquid", $"GetAccountSnapshot ok info={MaskAddress(infoAddress)}, positions={positions.Count}, orders={openOrders.Count}, balances={balances.Count}");
        return new VenueAccountSnapshot(DateTimeOffset.UtcNow, positions, openOrders, balances);
    }

    public IAsyncEnumerable<MarketEvent> MarketEvents(CancellationToken cancellationToken = default)
    {
        return _channel.Reader.ReadAllAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<VenuePosition>> FetchPositionsAsync(string infoAddress, IReadOnlyDictionary<string, decimal> mids, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new { type = "clearinghouseState", user = infoAddress });
        using var req = new HttpRequestMessage(HttpMethod.Post, _restBase + "/info")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };

        using var resp = await _httpClient.SendAsync(req, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"clearinghouseState failed {(int)resp.StatusCode}: {Trim(body)}");
        }

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("assetPositions", out var positionsElement) ||
            positionsElement.ValueKind != JsonValueKind.Array)
        {
            _logger.Warn("Hyperliquid", "FetchPositions assetPositions missing or not array");
            return [];
        }

        var result = new List<VenuePosition>();
        var totalRows = 0;
        var parsedRows = 0;
        var skippedNoSymbol = 0;
        var skippedZeroQty = 0;
        var skippedMalformed = 0;
        var firstRaw = string.Empty;
        var firstMalformed = string.Empty;
        foreach (var item in positionsElement.EnumerateArray())
        {
            try
            {
                totalRows++;
                if (string.IsNullOrWhiteSpace(firstRaw))
                {
                    firstRaw = Trim(item.ToString());
                }

                JsonElement p;
                if (item.TryGetProperty("position", out var posObj) && posObj.ValueKind == JsonValueKind.Object)
                {
                    p = posObj;
                }
                else if (item.ValueKind == JsonValueKind.Object)
                {
                    p = item;
                }
                else
                {
                    continue;
                }

                var coin = ReadFirstString(p, "coin", "symbol", "name");
                if (string.IsNullOrWhiteSpace(coin))
                {
                    skippedNoSymbol++;
                    continue;
                }

                var quantity = ReadFirstDecimal(p, "szi", "size", "sz", "positionSize", "qty", "positionQty");
                if (quantity == 0)
                {
                    skippedZeroQty++;
                    continue;
                }

                var symbol = coin.ToUpperInvariant();
                var entryPx = ReadFirstDecimal(p, "entryPx", "entryPrice", "avgEntryPrice");
                var markPx = mids.TryGetValue(symbol, out var mid) ? mid : ReadFirstDecimal(p, "markPx", "markPrice");
                if (markPx <= 0)
                {
                    markPx = entryPx;
                }

                var notional = Math.Abs(ReadFirstDecimal(p, "positionValue", "notionalUsd", "notional", "value"));
                if (notional <= 0 && markPx > 0)
                {
                    notional = Math.Abs(quantity * markPx);
                }

                var leverage = ReadDecimal(p, "leverage");
                if (leverage <= 0 && p.TryGetProperty("leverage", out var levObj) && levObj.ValueKind == JsonValueKind.Object)
                {
                    leverage = ReadFirstDecimal(levObj, "value", "leverage");
                }
                if (leverage <= 0)
                {
                    var marginUsed = Math.Abs(ReadFirstDecimal(p, "marginUsed", "initialMargin", "margin"));
                    if (marginUsed > 0 && notional > 0)
                    {
                        leverage = notional / marginUsed;
                    }
                }

                var unrealizedPnlUsd = ReadFirstDecimal(p, "unrealizedPnl", "upl", "funding", "cumFunding");
                if (unrealizedPnlUsd == 0m)
                {
                    unrealizedPnlUsd = ReadDecimal(p, "funding");
                }
                if (unrealizedPnlUsd == 0m)
                {
                    unrealizedPnlUsd = ReadDecimal(p, "cumFunding");
                }
                var unrealizedPct = 0m;
                if (notional > 0 && unrealizedPnlUsd != 0)
                {
                    unrealizedPct = (unrealizedPnlUsd / notional) * 100m;
                }
                else
                {
                    unrealizedPct = ComputeDirectionalPct(quantity, entryPx, markPx);
                }

                var realizedPnlUsd = ReadFirstDecimal(p, "cumRealizedPnl", "realizedPnl");

                result.Add(new VenuePosition(
                    symbol,
                    quantity,
                    notional,
                    leverage,
                    entryPx,
                    markPx,
                    unrealizedPct,
                    unrealizedPnlUsd,
                    realizedPnlUsd));
                parsedRows++;
            }
            catch (Exception ex)
            {
                skippedMalformed++;
                if (string.IsNullOrWhiteSpace(firstMalformed))
                {
                    firstMalformed = $"row={Trim(item.ToString())}; error={ex.Message}";
                }

                _logger.Warn("Hyperliquid", $"FetchPositions skip malformed row: {ex.Message}");
            }
        }

        _logger.Info("Hyperliquid", $"FetchPositions done rows={totalRows}, parsed={parsedRows}, skippedNoSymbol={skippedNoSymbol}, skippedZeroQty={skippedZeroQty}, skippedMalformed={skippedMalformed}, firstRow={firstRaw}, firstMalformed={firstMalformed}");
        return result;
    }

    private async Task<IReadOnlyList<VenueOpenOrder>> FetchOpenOrdersAsync(string infoAddress, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new { type = "openOrders", user = infoAddress });
        using var req = new HttpRequestMessage(HttpMethod.Post, _restBase + "/info")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };

        using var resp = await _httpClient.SendAsync(req, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.Warn("Hyperliquid", $"openOrders failed status={(int)resp.StatusCode}");
            return [];
        }

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<VenueOpenOrder>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var coin = ReadString(item, "coin");
            if (string.IsNullOrWhiteSpace(coin))
            {
                continue;
            }

            var size = Math.Abs(ReadDecimal(item, "sz"));
            if (size <= 0)
            {
                size = Math.Abs(ReadDecimal(item, "origSz"));
            }

            var limitPx = ReadDecimal(item, "limitPx");
            if (limitPx <= 0)
            {
                limitPx = ReadDecimal(item, "px");
            }

            var notional = size;
            if (size > 0 && limitPx > 0)
            {
                notional = size * limitPx;
            }

            var status = ReadString(item, "status");
            if (string.IsNullOrWhiteSpace(status))
            {
                status = "Open";
            }
            var orderId = ReadString(item, "oid");
            if (string.IsNullOrWhiteSpace(orderId))
            {
                orderId = ReadString(item, "orderId");
            }
            if (string.IsNullOrWhiteSpace(orderId))
            {
                var oidRaw = ReadDecimal(item, "oid");
                if (oidRaw > 0)
                {
                    orderId = decimal.Truncate(oidRaw).ToString(CultureInfo.InvariantCulture);
                }
            }

            result.Add(new VenueOpenOrder(
                coin.ToUpperInvariant(),
                notional,
                0m,
                limitPx > 0 ? limitPx : null,
                status,
                orderId));
        }

        return result;
    }

    private async Task<IReadOnlyList<VenueBalance>> FetchBalancesAsync(string infoAddress, IReadOnlyDictionary<string, decimal> mids, CancellationToken cancellationToken)
    {
        var byAsset = new Dictionary<string, (decimal Quantity, decimal Usd)>(StringComparer.OrdinalIgnoreCase);
        var perpUsd = 0m;
        var spotRows = 0;

        try
        {
            var payload = JsonSerializer.Serialize(new { type = "clearinghouseState", user = infoAddress });
            using var req = new HttpRequestMessage(HttpMethod.Post, _restBase + "/info")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };

            using var resp = await _httpClient.SendAsync(req, cancellationToken);
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.Warn("Hyperliquid", $"FetchBalances clearinghouseState failed status={(int)resp.StatusCode}");
            }
            else
            {
                using var doc = JsonDocument.Parse(body);
                perpUsd = ReadMarginAccountValue(doc.RootElement);
                if (perpUsd > 0)
                {
                    MergeBalanceRow(byAsset, "USDC (PERPS)", perpUsd, perpUsd);
                    _logger.Info("Hyperliquid", $"FetchBalances perp row asset=USDC (PERPS) qty={perpUsd}, usd={perpUsd}");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warn("Hyperliquid", $"FetchBalances clearinghouseState parse warning: {ex.Message}");
        }

        try
        {
            var payload = JsonSerializer.Serialize(new { type = "spotClearinghouseState", user = infoAddress });
            using var req = new HttpRequestMessage(HttpMethod.Post, _restBase + "/info")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };

            using var resp = await _httpClient.SendAsync(req, cancellationToken);
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.Warn("Hyperliquid", $"FetchBalances spotClearinghouseState failed status={(int)resp.StatusCode}");
            }
            else
            {
                using var doc = JsonDocument.Parse(body);
                spotRows = MergeSpotBalances(byAsset, doc.RootElement, mids);
            }
        }
        catch (Exception ex)
        {
            _logger.Warn("Hyperliquid", $"FetchBalances spotClearinghouseState parse warning: {ex.Message}");
        }

        var rows = byAsset
            .Where(x => x.Value.Quantity != 0m)
            .OrderBy(x => IsStableCoin(x.Key) ? 0 : 1)
            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => new VenueBalance(x.Key.ToUpperInvariant(), x.Value.Quantity, x.Value.Usd))
            .ToList();

        var rowSummary = string.Join(", ", rows.Select(x => $"{x.Asset}:{x.Quantity:F8}/{x.UsdValue:F4}"));
        _logger.Info("Hyperliquid", $"FetchBalances done info={MaskAddress(infoAddress)}, perpUsd={perpUsd:F4}, spotRows={spotRows}, finalRows={rows.Count}, rows=[{rowSummary}]");
        return rows;
    }

    private string? ResolveInfoAddress()
    {
        var accountAddress = _credentials.AccountAddress?.Trim();
        if (!string.IsNullOrWhiteSpace(accountAddress))
        {
            return accountAddress;
        }

        var walletAddress = _credentials.WalletAddress?.Trim();
        if (!string.IsNullOrWhiteSpace(walletAddress))
        {
            return walletAddress;
        }

        return null;
    }

    private async Task<IReadOnlyDictionary<string, decimal>> FetchAllMidsAsync(CancellationToken cancellationToken)
    {
        var reqBody = JsonSerializer.Serialize(new { type = "allMids" });
        using var req = new HttpRequestMessage(HttpMethod.Post, _restBase + "/info")
        {
            Content = new StringContent(reqBody, Encoding.UTF8, "application/json")
        };

        using var resp = await _httpClient.SendAsync(req, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"allMids failed {(int)resp.StatusCode}: {Trim(body)}");
        }

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        }

        var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            var value = ParseDecimal(prop.Value);
            if (value > 0)
            {
                result[prop.Name.ToUpperInvariant()] = value;
            }
        }

        return result;
    }

    private static decimal ComputeDirectionalPct(decimal quantity, decimal entryPrice, decimal markPrice)
    {
        if (entryPrice <= 0 || markPrice <= 0 || quantity == 0)
        {
            return 0m;
        }

        var isShort = quantity < 0;
        var raw = isShort
            ? ((entryPrice - markPrice) / entryPrice) * 100m
            : ((markPrice - entryPrice) / entryPrice) * 100m;
        return raw;
    }

    private static decimal ReadDecimal(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var prop))
        {
            return 0m;
        }

        return ParseDecimal(prop);
    }

    private static decimal ReadFirstDecimal(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
        {
            var value = ReadDecimal(obj, name);
            if (value != 0m)
            {
                return value;
            }
        }

        return 0m;
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
            _ => prop.GetRawText()
        };
    }

    private static string? ReadFirstString(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
        {
            var value = ReadString(obj, name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static int ReadInt(JsonElement obj, string name, int defaultValue = 0)
    {
        if (!obj.TryGetProperty(name, out var prop))
        {
            return defaultValue;
        }

        return prop.ValueKind switch
        {
            JsonValueKind.Number when prop.TryGetInt32(out var i) => i,
            JsonValueKind.String when int.TryParse(prop.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var s) => s,
            _ => defaultValue
        };
    }

    private static decimal ReadMarginAccountValue(JsonElement root)
    {
        decimal ReadFromSummary(string summaryName)
        {
            if (!root.TryGetProperty(summaryName, out var summary) || summary.ValueKind != JsonValueKind.Object)
            {
                return 0m;
            }

            if (TryReadDecimal(summary, "accountValue", out var accountValue) && accountValue > 0)
            {
                return accountValue;
            }

            if (TryReadDecimal(summary, "totalRawUsd", out var totalRawUsd) && totalRawUsd > 0)
            {
                return totalRawUsd;
            }

            return 0m;
        }

        var value = ReadFromSummary("marginSummary");
        if (value > 0)
        {
            return value;
        }

        value = ReadFromSummary("crossMarginSummary");
        if (value > 0)
        {
            return value;
        }

        return 0m;
    }

    private static int MergeSpotBalances(
        Dictionary<string, (decimal Quantity, decimal Usd)> byAsset,
        JsonElement root,
        IReadOnlyDictionary<string, decimal> mids)
    {
        if (!TryGetBalancesArray(root, out var balances))
        {
            return 0;
        }

        var merged = 0;
        foreach (var item in balances.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var asset = ReadSpotAsset(item);
            if (string.IsNullOrWhiteSpace(asset))
            {
                continue;
            }

            var quantity = ReadSpotQuantity(item);
            if (quantity == 0m)
            {
                continue;
            }

            var normalizedAsset = asset.Trim().ToUpperInvariant();
            var usd = ComputeSpotUsdValue(normalizedAsset, quantity, mids);
            MergeBalanceRow(byAsset, normalizedAsset, quantity, usd);
            merged++;
        }

        return merged;
    }

    private static bool TryGetBalancesArray(JsonElement root, out JsonElement balances)
    {
        if (root.TryGetProperty("balances", out balances) && balances.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        if (root.TryGetProperty("state", out var state) &&
            state.ValueKind == JsonValueKind.Object &&
            state.TryGetProperty("balances", out balances) &&
            balances.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        balances = default;
        return false;
    }

    private static string ReadSpotAsset(JsonElement item)
    {
        var coin = ReadString(item, "coin");
        if (!string.IsNullOrWhiteSpace(coin))
        {
            return coin;
        }

        coin = ReadString(item, "asset");
        if (!string.IsNullOrWhiteSpace(coin))
        {
            return coin;
        }

        coin = ReadString(item, "token");
        return coin ?? string.Empty;
    }

    private static decimal ReadSpotQuantity(JsonElement item)
    {
        if (TryReadDecimal(item, "total", out var total))
        {
            return total;
        }

        if (TryReadDecimal(item, "balance", out var balance))
        {
            return balance;
        }

        if (TryReadDecimal(item, "amount", out var amount))
        {
            return amount;
        }

        if (TryReadDecimal(item, "available", out var available))
        {
            return available;
        }

        return 0m;
    }

    private static decimal ComputeSpotUsdValue(string asset, decimal quantity, IReadOnlyDictionary<string, decimal> mids)
    {
        if (IsStableCoin(asset))
        {
            return quantity;
        }

        if (mids.TryGetValue(asset, out var mid) && mid > 0)
        {
            return quantity * mid;
        }

        return 0m;
    }

    private static void MergeBalanceRow(Dictionary<string, (decimal Quantity, decimal Usd)> byAsset, string asset, decimal quantity, decimal usd)
    {
        if (byAsset.TryGetValue(asset, out var existing))
        {
            byAsset[asset] = (existing.Quantity + quantity, existing.Usd + usd);
            return;
        }

        byAsset[asset] = (quantity, usd);
    }

    private static bool IsStableCoin(string asset)
    {
        if (string.IsNullOrWhiteSpace(asset))
        {
            return false;
        }

        var upper = asset.Trim().ToUpperInvariant();
        if (upper.StartsWith("USD", StringComparison.Ordinal) ||
            upper.StartsWith("USDC", StringComparison.Ordinal) ||
            upper.StartsWith("USDT", StringComparison.Ordinal))
        {
            return true;
        }

        return upper switch
        {
            "USD (PERPS)" => true,
            "USDC (PERPS)" => true,
            "USDT (PERPS)" => true,
            _ => false
        };
    }

    private static string MaskAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return "n/a";
        }

        var trimmed = address.Trim();
        if (trimmed.Length <= 10)
        {
            return trimmed;
        }

        return $"{trimmed[..6]}...{trimmed[^4..]}";
    }

    public async ValueTask DisposeAsync()
    {
        _logger.Info("Hyperliquid", "DisposeAsync called");
        await DisconnectMarketDataAsync(CancellationToken.None);
        _httpClient.Dispose();
        _metaGate.Dispose();
    }

    private static async Task SendSubscribeAsync(ClientWebSocket ws, object subscription, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new { method = "subscribe", subscription });
        var bytes = Encoding.UTF8.GetBytes(payload);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken);
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        var messageCount = 0;

        try
        {
            while (!cancellationToken.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.Warn("Hyperliquid", "WS received close frame");
                    break;
                }

                var total = result.Count;
                while (!result.EndOfMessage)
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer, total, buffer.Length - total), cancellationToken);
                    total += result.Count;
                }

                var json = Encoding.UTF8.GetString(buffer, 0, total);
                ParseMessage(json);
                _channel.Writer.TryWrite(new VenueHeartbeat(DateTimeOffset.UtcNow, "ws_message"));

                messageCount++;
                if (messageCount % 100 == 0)
                {
                    _logger.Info("Hyperliquid", $"WS processed messages={messageCount}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.Info("Hyperliquid", "WS receive loop canceled");
        }
        catch (Exception ex)
        {
            _logger.Error("Hyperliquid", "WS receive loop exception", ex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            _logger.Info("Hyperliquid", "WS receive loop exited");
        }
    }

    private void ParseMessage(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("channel", out var channelElem))
        {
            return;
        }

        var channel = channelElem.GetString();
        if (string.Equals(channel, "trades", StringComparison.Ordinal))
        {
            if (!root.TryGetProperty("data", out var trades) || trades.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var t in trades.EnumerateArray())
            {
                if (!TryReadDecimal(t, "px", out var px) ||
                    !TryReadDecimal(t, "sz", out var sz) ||
                    !TryReadUnixMs(t, "time", out var ts))
                {
                    continue;
                }

                _channel.Writer.TryWrite(new TradeTick(ts, px, sz));
            }

            return;
        }

        if (string.Equals(channel, "l2Book", StringComparison.Ordinal))
        {
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (!data.TryGetProperty("levels", out var levels) || levels.ValueKind != JsonValueKind.Array || levels.GetArrayLength() < 2)
            {
                return;
            }

            var bids = levels[0];
            var asks = levels[1];
            if (bids.ValueKind != JsonValueKind.Array || asks.ValueKind != JsonValueKind.Array || bids.GetArrayLength() == 0 || asks.GetArrayLength() == 0)
            {
                return;
            }

            var bestBid = bids[0];
            var bestAsk = asks[0];
            if (!TryReadDecimal(bestBid, "px", out var bidPx) || !TryReadDecimal(bestAsk, "px", out var askPx))
            {
                return;
            }

            var ts = DateTimeOffset.UtcNow;
            if (TryReadUnixMs(data, "time", out var bookTs))
            {
                ts = bookTs;
            }

            var mid = (bidPx + askPx) / 2m;
            _channel.Writer.TryWrite(new TradeTick(ts, mid, 0m));
        }
    }

    private static bool TryReadSnapshotCandle(JsonElement item, string chartSymbol, string intervalText, out Candle candle)
    {
        candle = default!;
        if (!TryReadUnixMs(item, "t", out var openTime) ||
            !TryReadDecimal(item, "o", out var o) ||
            !TryReadDecimal(item, "h", out var h) ||
            !TryReadDecimal(item, "l", out var l) ||
            !TryReadDecimal(item, "c", out var c) ||
            !TryReadDecimal(item, "v", out var v))
        {
            return false;
        }

        candle = new Candle(
            "Hyperliquid",
            chartSymbol,
            IntervalFromHyperliquid(intervalText),
            openTime,
            o,
            h,
            l,
            c,
            v,
            true);
        return true;
    }

    private static bool TryReadDecimal(JsonElement obj, string name, out decimal value)
    {
        value = 0m;
        if (!obj.TryGetProperty(name, out var prop))
        {
            return false;
        }

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDecimal(out value))
        {
            return true;
        }

        if (prop.ValueKind == JsonValueKind.String &&
            decimal.TryParse(prop.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        return false;
    }

    private static bool TryReadUnixMs(JsonElement obj, string name, out DateTimeOffset value)
    {
        value = default;
        if (!obj.TryGetProperty(name, out var prop))
        {
            return false;
        }

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var ms))
        {
            value = DateTimeOffset.FromUnixTimeMilliseconds(ms);
            return true;
        }

        if (prop.ValueKind == JsonValueKind.String &&
            long.TryParse(prop.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out ms))
        {
            value = DateTimeOffset.FromUnixTimeMilliseconds(ms);
            return true;
        }

        return false;
    }

    private static string NormalizeCoin(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return "BTC";
        }

        var upper = symbol.Trim().ToUpperInvariant();
        if (upper.Contains(':'))
        {
            upper = upper[(upper.LastIndexOf(':') + 1)..];
        }

        if (upper.Contains('-'))
        {
            upper = upper.Split('-', StringSplitOptions.RemoveEmptyEntries)[0];
        }

        if (upper.EndsWith("USDT", StringComparison.Ordinal))
        {
            upper = upper[..^4];
        }
        else if (upper.EndsWith("USDC", StringComparison.Ordinal))
        {
            upper = upper[..^4];
        }
        else if (upper.EndsWith("USD", StringComparison.Ordinal))
        {
            upper = upper[..^3];
        }

        if (upper == "XBT")
        {
            return "BTC";
        }

        return string.IsNullOrWhiteSpace(upper) ? "BTC" : upper;
    }

    private async Task<int> ResolveAssetIndexAsync(string coin, CancellationToken cancellationToken)
    {
        if (_assetByCoin is not null && _assetByCoin.TryGetValue(coin, out var cached))
        {
            return cached;
        }

        await _metaGate.WaitAsync(cancellationToken);
        try
        {
            if (_assetByCoin is not null && _assetByCoin.TryGetValue(coin, out cached))
            {
                return cached;
            }

            var reqBody = JsonSerializer.Serialize(new { type = "meta" });
            using var req = new HttpRequestMessage(HttpMethod.Post, _restBase + "/info")
            {
                Content = new StringContent(reqBody, Encoding.UTF8, "application/json")
            };
            using var resp = await _httpClient.SendAsync(req, cancellationToken);
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"load meta failed {(int)resp.StatusCode}");
            }

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("universe", out var universe) || universe.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("meta response missing universe");
            }

            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var szDecimals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var idx = 0;
            foreach (var item in universe.EnumerateArray())
            {
                if (item.TryGetProperty("name", out var n))
                {
                    var name = n.GetString();
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        map[name] = idx;
                        szDecimals[name] = ReadInt(item, "szDecimals", defaultValue: 0);
                    }
                }

                idx++;
            }

            _assetByCoin = map;
            _sizeDecimalsByCoin = szDecimals;
            _logger.Info("Hyperliquid", $"Meta loaded symbols={map.Count}");

            if (!map.TryGetValue(coin, out var asset))
            {
                throw new InvalidOperationException($"coin not found in meta: {coin}");
            }

            return asset;
        }
        finally
        {
            _metaGate.Release();
        }
    }

    private async Task<decimal> ComputeMarketLikePriceAsync(string coin, bool isBuy, CancellationToken cancellationToken)
    {
        var reqBody = JsonSerializer.Serialize(new { type = "allMids" });
        using var req = new HttpRequestMessage(HttpMethod.Post, _restBase + "/info")
        {
            Content = new StringContent(reqBody, Encoding.UTF8, "application/json")
        };

        using var resp = await _httpClient.SendAsync(req, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"allMids failed {(int)resp.StatusCode}");
        }

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty(coin, out var midElement))
        {
            throw new InvalidOperationException($"mid price missing: {coin}");
        }

        var mid = ParseDecimal(midElement);
        var factor = isBuy ? 1.02m : 0.98m;
        return decimal.Round(mid * factor, 6, MidpointRounding.ToEven);
    }

    private int ResolveSizeDecimals(string coin)
    {
        if (_sizeDecimalsByCoin is not null && _sizeDecimalsByCoin.TryGetValue(coin, out var szDecimals))
        {
            return Math.Max(0, szDecimals);
        }

        return 0;
    }

    // Hyperliquid perp tick rules: <= 5 significant figures, and <= (6 - szDecimals) decimals for non-integer prices.
    private static decimal NormalizePerpPrice(decimal price, int sizeDecimals)
    {
        if (price <= 0)
        {
            return 0;
        }

        var maxPriceDecimals = Math.Max(0, 6 - Math.Max(0, sizeDecimals));
        var normalized = TruncateDecimals(price, maxPriceDecimals);
        if (normalized <= 0)
        {
            return 0;
        }

        if (decimal.Truncate(normalized) != normalized)
        {
            normalized = TruncateToSignificantDigits(normalized, 5);
            normalized = TruncateDecimals(normalized, maxPriceDecimals);
        }

        return normalized <= 0 ? 0 : normalized;
    }

    private static decimal NormalizeSize(decimal size, int sizeDecimals)
    {
        if (size <= 0)
        {
            return 0;
        }

        return TruncateDecimals(size, Math.Max(0, sizeDecimals));
    }

    private object SignL1Action(object action, long nonce, bool isMainnet)
    {
        var packedAction = PackAction(action);
        var nonceBytes = ToUInt64BigEndian((ulong)nonce);

        var hashInput = new byte[packedAction.Length + nonceBytes.Length + 1];
        Buffer.BlockCopy(packedAction, 0, hashInput, 0, packedAction.Length);
        Buffer.BlockCopy(nonceBytes, 0, hashInput, packedAction.Length, nonceBytes.Length);
        hashInput[^1] = 0x00;

        var actionHash = _keccak.CalculateHash(hashInput);
        var source = isMainnet ? "a" : "b";
        var digest = BuildAgentEip712Digest(source, actionHash);

        var key = new EthECKey(NormalizePrivateKey(_credentials.PrivateKey!));
        var sig = key.SignAndCalculateV(digest);
        var vByte = sig.V is { Length: > 0 } ? sig.V[0] : (byte)27;
        var v = vByte < 27 ? vByte + 27 : vByte;
        var rHex = Convert.ToHexString(sig.R).ToLowerInvariant().PadLeft(64, '0');
        var sHex = Convert.ToHexString(sig.S).ToLowerInvariant().PadLeft(64, '0');

        return new
        {
            r = "0x" + rHex,
            s = "0x" + sHex,
            v
        };
    }

    private static byte[] PackAction(object action)
    {
        var element = JsonSerializer.SerializeToElement(action);
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        WriteMessagePack(ref writer, element);
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteMessagePack(ref MessagePackWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var props = element.EnumerateObject().ToList();
                writer.WriteMapHeader(props.Count);
                foreach (var p in props)
                {
                    writer.Write(p.Name);
                    WriteMessagePack(ref writer, p.Value);
                }
                break;
            case JsonValueKind.Array:
                var items = element.EnumerateArray().ToList();
                writer.WriteArrayHeader(items.Count);
                foreach (var item in items)
                {
                    WriteMessagePack(ref writer, item);
                }
                break;
            case JsonValueKind.String:
                writer.Write(element.GetString() ?? string.Empty);
                break;
            case JsonValueKind.Number:
                if (element.TryGetInt64(out var l))
                {
                    writer.Write(l);
                }
                else
                {
                    writer.Write(element.GetDouble());
                }
                break;
            case JsonValueKind.True:
                writer.Write(true);
                break;
            case JsonValueKind.False:
                writer.Write(false);
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                writer.WriteNil();
                break;
        }
    }

    private byte[] BuildAgentEip712Digest(string source, byte[] connectionId)
    {
        var domainTypeHash = _keccak.CalculateHash(Encoding.UTF8.GetBytes("EIP712Domain(string name,string version,uint256 chainId,address verifyingContract)"));
        var nameHash = _keccak.CalculateHash(Encoding.UTF8.GetBytes("Exchange"));
        var versionHash = _keccak.CalculateHash(Encoding.UTF8.GetBytes("1"));
        var chainId = UInt256Word(new BigInteger(1337));
        var verifyingContract = AddressWord("0x0000000000000000000000000000000000000000");

        var domainEncoded = Concat(domainTypeHash, nameHash, versionHash, chainId, verifyingContract);
        var domainSeparator = _keccak.CalculateHash(domainEncoded);

        var agentTypeHash = _keccak.CalculateHash(Encoding.UTF8.GetBytes("Agent(string source,bytes32 connectionId)"));
        var sourceHash = _keccak.CalculateHash(Encoding.UTF8.GetBytes(source));
        var messageEncoded = Concat(agentTypeHash, sourceHash, FixedWord(connectionId));
        var messageHash = _keccak.CalculateHash(messageEncoded);

        var prefix = new byte[] { 0x19, 0x01 };
        var digestInput = Concat(prefix, domainSeparator, messageHash);
        return _keccak.CalculateHash(digestInput);
    }

    private static byte[] Concat(params byte[][] arrays)
    {
        var len = arrays.Sum(x => x.Length);
        var output = new byte[len];
        var pos = 0;
        foreach (var arr in arrays)
        {
            Buffer.BlockCopy(arr, 0, output, pos, arr.Length);
            pos += arr.Length;
        }

        return output;
    }

    private static byte[] UInt256Word(BigInteger value)
    {
        var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        var word = new byte[32];
        Buffer.BlockCopy(bytes, 0, word, 32 - bytes.Length, bytes.Length);
        return word;
    }

    private static byte[] AddressWord(string address)
    {
        var hex = address.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? address[2..] : address;
        var raw = Convert.FromHexString(hex.PadLeft(40, '0'));
        var word = new byte[32];
        Buffer.BlockCopy(raw, 0, word, 12, 20);
        return word;
    }

    private static byte[] FixedWord(byte[] value)
    {
        if (value.Length == 32)
        {
            return value;
        }

        var word = new byte[32];
        Buffer.BlockCopy(value, 0, word, 32 - value.Length, value.Length);
        return word;
    }

    private static byte[] ToUInt64BigEndian(ulong value)
    {
        var bytes = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(bytes);
        }

        return bytes;
    }

    private static string DecimalToWire(decimal value)
    {
        var rounded = decimal.Round(value, 8, MidpointRounding.ToEven);
        var text = rounded.ToString("0.########", CultureInfo.InvariantCulture);
        return text == "-0" ? "0" : text;
    }

    private static decimal TruncateToSignificantDigits(decimal value, int significantDigits)
    {
        if (value == 0 || significantDigits <= 0)
        {
            return 0m;
        }

        var abs = (double)Math.Abs(value);
        if (abs <= 0)
        {
            return 0m;
        }

        var magnitude = (int)Math.Floor(Math.Log10(abs));
        var decimals = significantDigits - magnitude - 1;
        if (decimals >= 0)
        {
            return TruncateDecimals(value, decimals);
        }

        var scale = Pow10(-decimals);
        return decimal.Truncate(value / scale) * scale;
    }

    private static decimal TruncateDecimals(decimal value, int decimals)
    {
        var safeDecimals = Math.Max(0, decimals);
        if (safeDecimals == 0)
        {
            return decimal.Truncate(value);
        }

        var factor = Pow10(safeDecimals);
        return decimal.Truncate(value * factor) / factor;
    }

    private static decimal Pow10(int exp)
    {
        var value = 1m;
        for (var i = 0; i < exp; i++)
        {
            value *= 10m;
        }

        return value;
    }

    private static decimal ParseDecimal(JsonElement element)
    {
        return TryParseDecimal(element, out var value)
            ? value
            : 0m;
    }

    private static bool TryParseDecimal(JsonElement element, out decimal value)
    {
        value = 0m;

        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                if (element.TryGetDecimal(out var d))
                {
                    value = d;
                    return true;
                }

                return decimal.TryParse(element.GetRawText(), NumberStyles.Any, CultureInfo.InvariantCulture, out value);

            case JsonValueKind.String:
                return decimal.TryParse(element.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out value);

            case JsonValueKind.Object:
                // Some Hyperliquid fields (for example leverage) can be object-wrapped.
                if (element.TryGetProperty("value", out var wrapped))
                {
                    return TryParseDecimal(wrapped, out value);
                }

                return false;

            default:
                return false;
        }
    }

    private static (bool IsSuccess, bool HasAcceptedStatus, string Message) EvaluateExchangeResponse(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var rootError))
            {
                return (false, false, ReadJsonText(rootError, "exchange error"));
            }

            if (root.TryGetProperty("status", out var status) &&
                !string.Equals(status.GetString(), "ok", StringComparison.OrdinalIgnoreCase))
            {
                return (false, false, $"exchange status={status.GetString() ?? "unknown"}");
            }

            var hasAccepted = false;
            if (root.TryGetProperty("response", out var responseElement) &&
                responseElement.TryGetProperty("data", out var dataElement) &&
                dataElement.TryGetProperty("statuses", out var statusesElement) &&
                statusesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var statusElement in statusesElement.EnumerateArray())
                {
                    if (statusElement.ValueKind == JsonValueKind.Object)
                    {
                        if (statusElement.TryGetProperty("error", out var statusError))
                        {
                            return (false, false, ReadJsonText(statusError, "order rejected"));
                        }

                        if (statusElement.TryGetProperty("resting", out _) ||
                            statusElement.TryGetProperty("filled", out _) ||
                            statusElement.TryGetProperty("success", out _) ||
                            statusElement.TryGetProperty("cancelled", out _) ||
                            statusElement.TryGetProperty("canceled", out _))
                        {
                            hasAccepted = true;
                        }
                        continue;
                    }

                    var text = ReadJsonText(statusElement, string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        continue;
                    }

                    var normalized = text.Trim('"').Trim().ToLowerInvariant();
                    if (normalized.Contains("error", StringComparison.Ordinal) ||
                        normalized.Contains("fail", StringComparison.Ordinal) ||
                        normalized.Contains("reject", StringComparison.Ordinal) ||
                        normalized.Contains("invalid", StringComparison.Ordinal))
                    {
                        return (false, false, text);
                    }

                    if (normalized.Contains("success", StringComparison.Ordinal) ||
                        normalized.Contains("cancel", StringComparison.Ordinal) ||
                        normalized.Contains("rest", StringComparison.Ordinal) ||
                        normalized.Contains("fill", StringComparison.Ordinal))
                    {
                        hasAccepted = true;
                    }
                }
            }

            return (true, hasAccepted, "ok");
        }
        catch
        {
            return (false, false, "invalid exchange response");
        }
    }

    private static string ReadJsonText(JsonElement element, string fallback)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? fallback,
            JsonValueKind.Number => element.ToString(),
            JsonValueKind.Object => element.ToString(),
            JsonValueKind.Array => element.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => fallback
        };
    }

    private static string? TryReadHyperliquidOrderId(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("response", out var responseElement))
            {
                return null;
            }

            if (!responseElement.TryGetProperty("data", out var dataElement))
            {
                return null;
            }

            if (!dataElement.TryGetProperty("statuses", out var statusesElement) ||
                statusesElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var statusElement in statusesElement.EnumerateArray())
            {
                if (!statusElement.TryGetProperty("resting", out var resting) &&
                    !statusElement.TryGetProperty("filled", out resting))
                {
                    continue;
                }

                if (resting.TryGetProperty("oid", out var oidElement))
                {
                    if (oidElement.ValueKind == JsonValueKind.Number && oidElement.TryGetInt64(out var oidLong))
                    {
                        return oidLong.ToString(CultureInfo.InvariantCulture);
                    }

                    if (oidElement.ValueKind == JsonValueKind.String)
                    {
                        return oidElement.GetString();
                    }
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizePrivateKey(string privateKey)
    {
        var cleaned = privateKey.Trim();
        if (cleaned.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned[2..];
        }

        return cleaned;
    }

    private static string Trim(string text)
    {
        return text.Length > 240 ? text[..240] : text;
    }

    private static (string BaseInterval, int Factor) IntervalToHyperliquid(CandleInterval interval)
    {
        return interval switch
        {
            CandleInterval.M5 => ("5m", 1),
            CandleInterval.M10 => ("5m", 2),
            CandleInterval.M15 => ("15m", 1),
            CandleInterval.M30 => ("30m", 1),
            CandleInterval.H1 => ("1h", 1),
            CandleInterval.H2 => ("2h", 1),
            CandleInterval.H4 => ("4h", 1),
            CandleInterval.H6 => ("2h", 3),
            CandleInterval.H12 => ("12h", 1),
            CandleInterval.D1 => ("1d", 1),
            CandleInterval.D7 => ("1w", 1),
            CandleInterval.D30 => ("1d", 30),
            _ => ("5m", 1)
        };
    }

    private static CandleInterval IntervalFromHyperliquid(string interval)
    {
        return interval switch
        {
            "1m" => CandleInterval.M5,
            "3m" => CandleInterval.M5,
            "5m" => CandleInterval.M5,
            "15m" => CandleInterval.M15,
            "30m" => CandleInterval.M30,
            "1h" => CandleInterval.H1,
            "2h" => CandleInterval.H2,
            "4h" => CandleInterval.H4,
            "8h" => CandleInterval.H6,
            "12h" => CandleInterval.H12,
            "1d" => CandleInterval.D1,
            "3d" => CandleInterval.D1,
            "1w" => CandleInterval.D7,
            "1M" => CandleInterval.D30,
            _ => CandleInterval.M5
        };
    }

    private static TimeSpan IntervalDuration(string interval)
    {
        return interval switch
        {
            "1m" => TimeSpan.FromMinutes(1),
            "3m" => TimeSpan.FromMinutes(3),
            "5m" => TimeSpan.FromMinutes(5),
            "15m" => TimeSpan.FromMinutes(15),
            "30m" => TimeSpan.FromMinutes(30),
            "1h" => TimeSpan.FromHours(1),
            "2h" => TimeSpan.FromHours(2),
            "4h" => TimeSpan.FromHours(4),
            "8h" => TimeSpan.FromHours(8),
            "12h" => TimeSpan.FromHours(12),
            "1d" => TimeSpan.FromDays(1),
            "3d" => TimeSpan.FromDays(3),
            "1w" => TimeSpan.FromDays(7),
            "1M" => TimeSpan.FromDays(30),
            _ => TimeSpan.FromMinutes(5)
        };
    }

    private static IReadOnlyList<Candle> ResampleCandles(IReadOnlyList<Candle> baseCandles, CandleInterval targetInterval)
    {
        if (baseCandles.Count == 0)
        {
            return [];
        }

        var bucketSize = IntervalToTimeSpan(targetInterval);
        var grouped = baseCandles
            .GroupBy(x => FloorToBucket(x.OpenTime, bucketSize))
            .OrderBy(x => x.Key);

        var result = new List<Candle>();
        foreach (var group in grouped)
        {
            var ordered = group.OrderBy(x => x.OpenTime).ToList();
            var first = ordered[0];
            var last = ordered[^1];
            result.Add(new Candle(
                first.VenueId,
                first.Symbol,
                targetInterval,
                group.Key,
                first.Open,
                ordered.Max(x => x.High),
                ordered.Min(x => x.Low),
                last.Close,
                ordered.Sum(x => x.Volume),
                true));
        }

        return result;
    }

    private static DateTimeOffset FloorToBucket(DateTimeOffset timestamp, TimeSpan bucketSize)
    {
        var utc = timestamp.ToUniversalTime();
        var ticks = utc.Ticks - (utc.Ticks % bucketSize.Ticks);
        return new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    private static TimeSpan IntervalToTimeSpan(CandleInterval interval)
    {
        return interval switch
        {
            CandleInterval.M5 => TimeSpan.FromMinutes(5),
            CandleInterval.M10 => TimeSpan.FromMinutes(10),
            CandleInterval.M15 => TimeSpan.FromMinutes(15),
            CandleInterval.M30 => TimeSpan.FromMinutes(30),
            CandleInterval.H1 => TimeSpan.FromHours(1),
            CandleInterval.H2 => TimeSpan.FromHours(2),
            CandleInterval.H4 => TimeSpan.FromHours(4),
            CandleInterval.H6 => TimeSpan.FromHours(6),
            CandleInterval.H12 => TimeSpan.FromHours(12),
            CandleInterval.D1 => TimeSpan.FromDays(1),
            CandleInterval.D7 => TimeSpan.FromDays(7),
            CandleInterval.D30 => TimeSpan.FromDays(30),
            _ => TimeSpan.FromMinutes(5)
        };
    }
}
