using AiyoPerps.Core;
using AiyoPerps.Models;
using Nethereum.Signer;
using Nethereum.Util;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace AiyoPerps.Services;

public sealed class GrvtVenueAdapter : IPerpVenue, IHistoricalCandleProvider, IAccountStateProvider
{
    private readonly string _environment;
    private readonly string _edgeBase;
    private readonly string _tradingBase;
    private readonly string _marketBase;
    private readonly string _marketWsBase;
    private readonly AccountCredentials _credentials;
    private readonly AppLogger _logger;
    private readonly HttpClient _httpClient = new();
    private readonly Channel<MarketEvent> _channel = Channel.CreateUnbounded<MarketEvent>();
    private readonly Sha3Keccack _keccak = Sha3Keccack.Current;
    private readonly SemaphoreSlim _instrumentSpecsGate = new(1, 1);
    private readonly Dictionary<string, GrvtInstrumentSpec> _instrumentSpecs = new(StringComparer.OrdinalIgnoreCase);

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _wsCts;
    private Task? _wsTask;
    private CancellationTokenSource? _tradePollCts;
    private Task? _tradePollTask;
    private string? _tradePollSymbol;

    private string? _sessionCookie;
    private string? _accountId;
    private DateTimeOffset _sessionAt;

    public GrvtVenueAdapter(string environment, AccountCredentials credentials, AppLogger logger)
    {
        _environment = environment;
        _credentials = credentials;
        _logger = logger;

        var isTestnet = string.Equals(environment, "testnet", StringComparison.OrdinalIgnoreCase);
        _edgeBase = isTestnet ? "https://edge.testnet.grvt.io" : "https://edge.grvt.io";
        _tradingBase = isTestnet ? "https://trades.testnet.grvt.io" : "https://trades.grvt.io";
        _marketBase = isTestnet ? "https://market-data.testnet.grvt.io" : "https://market-data.grvt.io";
        _marketWsBase = isTestnet ? "wss://market-data.testnet.grvt.io/ws/full" : "wss://market-data.grvt.io/ws/full";

        _logger.Info("GRVT", $"Adapter created env={environment}, edge={_edgeBase}, trading={_tradingBase}, market={_marketBase}, ws={_marketWsBase}");
    }

    public string VenueId => "GRVT";

    public async Task ConnectMarketDataAsync(IEnumerable<string> subscriptions, CancellationToken cancellationToken = default)
    {
        await DisconnectMarketDataAsync(cancellationToken);

        var symbol = NormalizeSymbol(subscriptions.FirstOrDefault() ?? "BTC_USDT-PERP");
        _ws = new ClientWebSocket();
        _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        await _ws.ConnectAsync(new Uri(_marketWsBase), cancellationToken);

        var subscribeId = (int)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % int.MaxValue);
        var subscribe = new
        {
            // GRVT WS request id/request_id is uint32. Large 13-digit unix ms can be rejected with code=1003.
            id = subscribeId,
            jsonrpc = "2.0",
            method = "subscribe",
            @params = new
            {
                stream = "v1.trade",
                selectors = new[] { symbol + "@500" }
            }
        };

        var payload = JsonSerializer.Serialize(subscribe);
        await _ws.SendAsync(Encoding.UTF8.GetBytes(payload), WebSocketMessageType.Text, true, cancellationToken);
        _logger.Info("GRVT", $"WS subscribe payload={payload}");

        _wsCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _wsTask = Task.Run(() => ReceiveLoopAsync(_ws, _wsCts.Token), _wsCts.Token);
        _tradePollSymbol = symbol;
        _tradePollCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _tradePollTask = Task.Run(() => TradePollLoopAsync(symbol, _tradePollCts.Token), _tradePollCts.Token);
        _logger.Info("GRVT", $"WS connected symbol={symbol}, stream=v1.trade, selectors=[{symbol}@500]");
    }

    public async Task DisconnectMarketDataAsync(CancellationToken cancellationToken = default)
    {
        _logger.Info("GRVT", "DisconnectMarketDataAsync called");

        if (_wsCts is not null)
        {
            _wsCts.Cancel();
        }
        if (_tradePollCts is not null)
        {
            _tradePollCts.Cancel();
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
                    _logger.Warn("GRVT", $"WS close warning: {ex.Message}");
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
                _logger.Warn("GRVT", $"WS task warning: {ex.Message}");
            }

            _wsTask = null;
        }
        if (_tradePollTask is not null)
        {
            try
            {
                await _tradePollTask;
            }
            catch (Exception ex)
            {
                _logger.Warn("GRVT", $"Trade poll task warning: {ex.Message}");
            }

            _tradePollTask = null;
        }

        _wsCts?.Dispose();
        _wsCts = null;
        _tradePollCts?.Dispose();
        _tradePollCts = null;
        _tradePollSymbol = null;
    }

    public async Task<(bool IsSuccess, string Message)> ConfigureLeverageAsync(string symbol, decimal leverage, MarginMode marginMode, CancellationToken cancellationToken = default)
    {
        var auth = await EnsureAuthenticatedAsync(cancellationToken);
        if (!auth.IsSuccess)
        {
            return auth;
        }

        var subAccountId = (_credentials.SubAccountId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(subAccountId))
        {
            return (false, "GRVT requires subAccountId");
        }

        if (marginMode == MarginMode.Unknown)
        {
            _logger.Info("GRVT", $"ConfigureLeverage skipped symbol={symbol}, leverage={leverage}, marginMode=unknown");
            return (true, "GRVT leverage update skipped (unknown margin mode)");
        }

        var request = new
        {
            sub_account_id = subAccountId,
            instrument = NormalizeSymbol(symbol),
            leverage = decimal.Round(Math.Max(1m, leverage), 2, MidpointRounding.AwayFromZero),
            margin_type = marginMode == MarginMode.Isolated ? "ISOLATED" : "CROSS"
        };

        var (ok, _, body) = await PostTradingAsync("/full/v1/set_position_config", request, cancellationToken);
        if (!ok)
        {
            _logger.Warn("GRVT", $"ConfigureLeverage failed symbol={symbol}, leverage={leverage}, marginMode={marginMode.ToApiValue()}, body={Trim(body)}");
            return (false, NormalizeGrvtMarginModeError(body, marginMode));
        }

        _logger.Info("GRVT", $"ConfigureLeverage applied symbol={symbol}, leverage={leverage}, marginMode={marginMode.ToApiValue()}, subAccountId={subAccountId}");
        return (true, "ok");
    }

    public Task<OrderAck> PlaceOrderAsync(string symbol, string side, decimal qty, decimal? price, CancellationToken cancellationToken = default)
        => PlaceOrderCoreAsync(symbol, side, qty, price, reduceOnly: false, cancellationToken);

    public Task<OrderAck> PlaceCloseOrderAsync(string symbol, string side, decimal positionQty, decimal? price, CancellationToken cancellationToken = default)
        => PlaceOrderCoreAsync(symbol, side, Math.Abs(positionQty), price, reduceOnly: true, cancellationToken);

    public async Task<OrderAck> CancelOrderAsync(string symbol, string orderId, CancellationToken cancellationToken = default)
    {
        var auth = await EnsureAuthenticatedAsync(cancellationToken);
        if (!auth.IsSuccess)
        {
            return new OrderAck(DateTimeOffset.UtcNow, orderId ?? string.Empty, false, auth.Message);
        }

        var sub = (_credentials.SubAccountId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(sub))
        {
            return new OrderAck(DateTimeOffset.UtcNow, orderId ?? string.Empty, false, "GRVT requires sub_account_id for cancel.");
        }

        if (string.IsNullOrWhiteSpace(orderId))
        {
            return new OrderAck(DateTimeOffset.UtcNow, string.Empty, false, "OrderId is required.");
        }

        var req = new
        {
            sub_account_id = sub,
            order_id = orderId.Trim()
        };

        var (ok, _, body) = await PostTradingAsync("/full/v1/cancel_order", req, cancellationToken);
        if (!ok)
        {
            _logger.Warn("GRVT", $"CancelOrder failed symbol={symbol}, orderId={orderId}, body={Trim(body)}");
            return new OrderAck(DateTimeOffset.UtcNow, orderId, false, body);
        }

        _logger.Info("GRVT", $"CancelOrder ok symbol={symbol}, orderId={orderId}");
        return new OrderAck(DateTimeOffset.UtcNow, orderId, true, "GRVT order canceled");
    }

    public async Task<(bool IsSuccess, string Message)> ValidateConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, _marketBase + "/full/v1/all_instruments")
            {
                Content = new StringContent("{\"is_active\":true}", Encoding.UTF8, "application/json")
            };
            using var resp = await _httpClient.SendAsync(req, cancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                return (false, $"GRVT market ping failed: {(int)resp.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            return (false, $"GRVT market ping exception: {ex.Message}");
        }

        var auth = await EnsureAuthenticatedAsync(cancellationToken);
        if (!auth.IsSuccess)
        {
            return auth;
        }

        if (!_credentials.HasWalletCredentials)
        {
            return (true, "GRVT API connection ok (read-only). Trading requires WalletAddress + PrivateKey for order.signature.");
        }

        return (true, "GRVT connection ok. Trading signature credentials are available.");
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
        var requestedSymbol = (symbol ?? string.Empty).Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(requestedSymbol))
        {
            requestedSymbol = "BTC_USDT_Perp";
        }

        var normalizedSymbol = NormalizeSymbol(requestedSymbol);
        var (klineInterval, klineType) = interval switch
        {
            CandleInterval.M5 => ("CI_5_M", "TRADE"),
            CandleInterval.M10 => ("CI_10_M", "TRADE"),
            CandleInterval.M15 => ("CI_15_M", "TRADE"),
            CandleInterval.M30 => ("CI_30_M", "TRADE"),
            CandleInterval.H1 => ("CI_1_H", "TRADE"),
            CandleInterval.H2 => ("CI_2_H", "TRADE"),
            CandleInterval.H4 => ("CI_4_H", "TRADE"),
            CandleInterval.H6 => ("CI_6_H", "TRADE"),
            CandleInterval.H12 => ("CI_12_H", "TRADE"),
            CandleInterval.D1 => ("CI_1_D", "TRADE"),
            CandleInterval.D7 => ("CI_1_W", "TRADE"),
            CandleInterval.D30 => ("CI_4_W", "TRADE"),
            _ => ("CI_5_M", "TRADE")
        };

        var nowNs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L;
        var spanMs = IntervalToMilliseconds(interval);
        var lookbackMs = Math.Max(spanMs * Math.Clamp(count + 50, 80, 1200), spanMs * 120);
        var fromNs = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - lookbackMs) * 1_000_000L;
        var pageLimit = 1000;
        var maxPages = 24;
        var page = 0;
        var cursor = string.Empty;
        var rows = new List<Candle>(Math.Max(256, count + 64));

        while (!cancellationToken.IsCancellationRequested && page < maxPages)
        {
            page++;
            var req = new
            {
                instrument = normalizedSymbol,
                interval = klineInterval,
                type = klineType,
                start_time = fromNs.ToString(CultureInfo.InvariantCulture),
                end_time = nowNs.ToString(CultureInfo.InvariantCulture),
                limit = pageLimit,
                cursor
            };

            var (ok, root, body) = await PostMarketRawAsync("/full/v1/kline", req, cancellationToken);
            if (!ok)
            {
                _logger.Warn("GRVT", $"GetRecentCandles kline endpoint failed symbol={symbol}, interval={interval}, page={page}, body={Trim(body)}");
                return await GetRecentCandlesFromTradesAsync(requestedSymbol, normalizedSymbol, interval, count, cancellationToken);
            }

            var bodyRoot = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("result", out var resultNode)
                ? resultNode
                : root;

            var arr = ExtractArray(bodyRoot, "result") ??
                      ExtractArray(bodyRoot, "candles") ??
                      ExtractArray(bodyRoot, "items") ??
                      (bodyRoot.ValueKind == JsonValueKind.Array ? bodyRoot : default);
            if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0)
            {
                break;
            }

            foreach (var item in arr.EnumerateArray())
            {
                var ts = ReadDateTime(item, "open_time") ??
                         ReadDateTime(item, "openTime") ??
                         ReadDateTime(item, "timestamp") ??
                         ReadDateTime(item, "event_time") ??
                         DateTimeOffset.MinValue;
                if (ts == DateTimeOffset.MinValue)
                {
                    continue;
                }

                var alignedTs = AlignToIntervalStart(ts, interval);

                var o = ReadDecimal(item, "open");
                var h = ReadDecimal(item, "high");
                var l = ReadDecimal(item, "low");
                var c = ReadDecimal(item, "close");
                var v = ReadDecimal(item, "volume");
                if (o <= 0 || h <= 0 || l <= 0 || c <= 0)
                {
                    o = ReadDecimal(item, "o");
                    h = ReadDecimal(item, "h");
                    l = ReadDecimal(item, "l");
                    c = ReadDecimal(item, "c");
                    v = ReadDecimal(item, "v");
                }

                if (o <= 0 || h <= 0 || l <= 0 || c <= 0)
                {
                    continue;
                }

                rows.Add(new Candle(VenueId, requestedSymbol, interval, alignedTs, o, h, l, c, v, true));
            }

            var next = ReadString(root, "next");
            if (string.IsNullOrWhiteSpace(next))
            {
                break;
            }

            cursor = next;
        }

        if (rows.Count == 0)
        {
            return await GetRecentCandlesFromTradesAsync(requestedSymbol, normalizedSymbol, interval, count, cancellationToken);
        }

        var normalized = NormalizeCandlesToInterval(rows, interval);
        var filled = FillMissingCandles(normalized, interval, count);
        _logger.Info("GRVT", $"GetRecentCandles kline ok symbol={normalizedSymbol}, interval={interval}, candles={filled.Count}, rows={rows.Count}, normalized={normalized.Count}, pages={page}");
        return filled;
    }

    public Task<VenueAccountSnapshot> GetAccountSnapshotAsync(CancellationToken cancellationToken = default)
    {
        return GetAccountSnapshotAsync(AccountSnapshotSections.All, cancellationToken);
    }

    public async Task<VenueAccountSnapshot> GetAccountSnapshotAsync(AccountSnapshotSections sections, CancellationToken cancellationToken = default)
    {
        var auth = await EnsureAuthenticatedAsync(cancellationToken);
        if (!auth.IsSuccess)
        {
            _logger.Warn("GRVT", $"GetAccountSnapshot auth failed: {auth.Message}");
            return new VenueAccountSnapshot(DateTimeOffset.UtcNow, [], [], []);
        }

        if (sections == AccountSnapshotSections.None)
        {
            return new VenueAccountSnapshot(DateTimeOffset.UtcNow, [], [], []);
        }

        var sub = (_credentials.SubAccountId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(sub))
        {
            _logger.Warn("GRVT", "GetAccountSnapshot requires subAccountId");
            return new VenueAccountSnapshot(DateTimeOffset.UtcNow, [], [], []);
        }

        var positions = sections.HasFlag(AccountSnapshotSections.Positions)
            ? await FetchPositionsAsync(sub, cancellationToken)
            : [];
        var orders = sections.HasFlag(AccountSnapshotSections.Orders)
            ? await FetchOpenOrdersAsync(sub, cancellationToken)
            : [];
        var balances = sections.HasFlag(AccountSnapshotSections.Balances)
            ? await FetchBalancesAsync(sub, cancellationToken)
            : [];
        return new VenueAccountSnapshot(DateTimeOffset.UtcNow, positions, orders, balances);
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectMarketDataAsync();
        _httpClient.Dispose();
    }

    private async Task<OrderAck> PlaceOrderCoreAsync(string symbol, string side, decimal qty, decimal? price, bool reduceOnly, CancellationToken cancellationToken)
    {
        var auth = await EnsureAuthenticatedAsync(cancellationToken);
        if (!auth.IsSuccess)
        {
            return new OrderAck(DateTimeOffset.UtcNow, string.Empty, false, auth.Message);
        }

        var sub = (_credentials.SubAccountId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(sub))
        {
            return new OrderAck(DateTimeOffset.UtcNow, string.Empty, false, "GRVT requires sub_account_id for order placement.");
        }

        if (qty <= 0)
        {
            return new OrderAck(DateTimeOffset.UtcNow, string.Empty, false, "Quantity must be greater than 0.");
        }

        if (!_credentials.HasWalletCredentials)
        {
            var msg = "GRVT trading requires WalletAddress + PrivateKey in Account Manager. " +
                      "Current account is API-key only, so order.signature cannot be generated.";
            _logger.Warn("GRVT", $"PlaceOrder rejected symbol={symbol}, reason={msg}");
            return new OrderAck(DateTimeOffset.UtcNow, string.Empty, false, msg);
        }

        var normalizedSide = NormalizeOrderSide(side);
        if (normalizedSide is not ("BUY" or "SELL"))
        {
            return new OrderAck(DateTimeOffset.UtcNow, string.Empty, false, $"Unsupported side: {side}");
        }

        var instrument = NormalizeSymbol(symbol);
        var spec = await GetInstrumentSpecAsync(instrument, cancellationToken);
        var normalizedQty = NormalizeByStep(qty, spec?.MinSize ?? 0m, roundUp: false);
        if (normalizedQty <= 0)
        {
            return new OrderAck(DateTimeOffset.UtcNow, string.Empty, false, "Order size becomes zero after instrument min_size normalization.");
        }

        var isMarket = !(price.HasValue && price.Value > 0);
        var tif = isMarket ? "IMMEDIATE_OR_CANCEL" : "GOOD_TILL_TIME";
        var isBuyingAsset = normalizedSide == "BUY";
        var leg = new Dictionary<string, object?>
        {
            ["instrument"] = instrument,
            ["size"] = normalizedQty.ToString(CultureInfo.InvariantCulture),
            ["is_buying_asset"] = isBuyingAsset
        };

        var normalizedLimitPrice = 0m;
        if (!isMarket)
        {
            normalizedLimitPrice = NormalizeByStep(price!.Value, spec?.TickSize ?? 0m, roundUp: isBuyingAsset);
            if (normalizedLimitPrice <= 0)
            {
                return new OrderAck(DateTimeOffset.UtcNow, string.Empty, false, "Invalid limit price after instrument tick_size normalization.");
            }

            leg["limit_price"] = normalizedLimitPrice.ToString(CultureInfo.InvariantCulture);
        }

        var nonce = NextUInt32();
        var expirationNs = GetExpiryNanoseconds(3);
        var signature = BuildGrvtOrderSignature(
            instrument,
            spec?.InstrumentHash,
            spec?.BaseDecimals ?? 9,
            sub,
            isMarket,
            tif,
            postOnly: false,
            reduceOnly: reduceOnly,
            normalizedQty,
            normalizedLimitPrice,
            isBuyingAsset,
            nonce,
            expirationNs);
        if (signature is null)
        {
            var msg = "GRVT order signature generation is not implemented yet. " +
                      "Trading API requires order.signature for create_order.";
            _logger.Warn("GRVT", $"PlaceOrder rejected symbol={instrument}, reason={msg}");
            return new OrderAck(DateTimeOffset.UtcNow, string.Empty, false, msg);
        }

        var createTimeNs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L;
        var clientOrderId = NextUInt32().ToString(CultureInfo.InvariantCulture);
        var order = new Dictionary<string, object?>
        {
            ["sub_account_id"] = sub,
            ["is_market"] = isMarket,
            ["time_in_force"] = tif,
            ["post_only"] = false,
            ["reduce_only"] = reduceOnly,
            ["legs"] = new[] { leg },
            ["signature"] = signature,
            ["metadata"] = new Dictionary<string, object?>
            {
                ["client_order_id"] = clientOrderId,
                ["create_time"] = createTimeNs.ToString(CultureInfo.InvariantCulture)
            }
        };

        var payload = new Dictionary<string, object?> { ["order"] = order };
        _logger.Info("GRVT", $"PlaceOrder submit symbol={instrument}, side={normalizedSide}, qtyRaw={qty}, qtyNorm={normalizedQty}, pxRaw={(price?.ToString(CultureInfo.InvariantCulture) ?? "MKT")}, pxNorm={(isMarket ? "MKT" : normalizedLimitPrice.ToString(CultureInfo.InvariantCulture))}, reduceOnly={reduceOnly}, hasInstrumentHash={!string.IsNullOrWhiteSpace(spec?.InstrumentHash)}, payload={Trim(JsonSerializer.Serialize(payload))}");
        var (ok, root, body) = await PostTradingAsync("/full/v1/create_order", payload, cancellationToken);
        if (!ok)
        {
            _logger.Warn("GRVT", $"PlaceOrder failed symbol={instrument}, side={normalizedSide}, qty={qty}, reduceOnly={reduceOnly}, body={Trim(body)}");
            return new OrderAck(DateTimeOffset.UtcNow, string.Empty, false, body);
        }

        var metadataNode = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("metadata", out var metadata)
            ? metadata
            : default;
        var orderId = ReadString(root, "order_id") ??
                      ReadString(root, "id") ??
                      ReadString(root, "client_order_id") ??
                      (metadataNode.ValueKind == JsonValueKind.Object ? ReadString(metadataNode, "client_order_id") : null) ??
                      clientOrderId;

        _logger.Info("GRVT", $"PlaceOrder ok symbol={instrument}, orderId={orderId}, reduceOnly={reduceOnly}");
        return new OrderAck(DateTimeOffset.UtcNow, orderId, true, "GRVT order accepted");
    }

    private Dictionary<string, object?>? BuildGrvtOrderSignature(
        string instrument,
        string? instrumentHash,
        int baseDecimals,
        string subAccountId,
        bool isMarket,
        string timeInForce,
        bool postOnly,
        bool reduceOnly,
        decimal size,
        decimal limitPrice,
        bool isBuyingAsset,
        uint nonce,
        long expirationNs)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_credentials.PrivateKey))
            {
                return null;
            }

            if (!ulong.TryParse(subAccountId, NumberStyles.Any, CultureInfo.InvariantCulture, out var subId))
            {
                _logger.Warn("GRVT", $"Build signature failed: invalid sub_account_id={subAccountId}");
                return null;
            }

            var assetId = ParseAssetId(instrumentHash, instrument);
            if (assetId == 0)
            {
                _logger.Warn("GRVT", $"Build signature failed: missing asset id symbol={instrument}, instrumentHash={instrumentHash}");
                return null;
            }

            var tifCode = ToTimeInForceCode(timeInForce);
            if (tifCode == 0)
            {
                _logger.Warn("GRVT", $"Build signature failed: unsupported TIF={timeInForce}");
                return null;
            }

            var configuredWallet = (_credentials.WalletAddress ?? string.Empty).Trim();
            var key = new EthECKey(NormalizePrivateKey(_credentials.PrivateKey));
            var signer = key.GetPublicAddress();
            if (!string.IsNullOrWhiteSpace(configuredWallet) &&
                !string.Equals(configuredWallet, signer, StringComparison.OrdinalIgnoreCase))
            {
                _logger.Warn("GRVT", $"Wallet mismatch configured={configuredWallet}, derived={signer}");
            }

            var sizeMultiplier = Pow10(Math.Max(0, baseDecimals));
            var contractSize = ToScaledUInt64(size, sizeMultiplier);
            var limitPriceScaled = ToScaledUInt64(limitPrice, 1_000_000_000m);
            var digest = BuildGrvtOrderDigest(
                subId,
                isMarket,
                tifCode,
                postOnly,
                reduceOnly,
                assetId,
                contractSize,
                limitPriceScaled,
                isBuyingAsset,
                nonce,
                expirationNs);

            var sig = key.SignAndCalculateV(digest);
            var vByte = sig.V is { Length: > 0 } ? sig.V[0] : (byte)27;
            var v = vByte < 27 ? vByte + 27 : vByte;
            var rHex = "0x" + Convert.ToHexString(sig.R).ToLowerInvariant().PadLeft(64, '0');
            var sHex = "0x" + Convert.ToHexString(sig.S).ToLowerInvariant().PadLeft(64, '0');
            signer = signer.ToLowerInvariant();
            var chainId = GetGrvtChainId();

            return new Dictionary<string, object?>
            {
                ["r"] = rHex,
                ["s"] = sHex,
                ["v"] = v,
                ["expiration"] = expirationNs.ToString(CultureInfo.InvariantCulture),
                ["nonce"] = nonce,
                ["signer"] = signer,
                ["chain_id"] = chainId.ToString(CultureInfo.InvariantCulture)
            };
        }
        catch (Exception ex)
        {
            _logger.Error("GRVT", "Build signature failed", ex);
            return null;
        }
    }

    private static string NormalizeOrderSide(string side)
    {
        var s = (side ?? string.Empty).Trim().ToUpperInvariant();
        return s switch
        {
            "BUY" or "LONG" => "BUY",
            "SELL" or "SHORT" => "SELL",
            _ => s
        };
    }

    private async Task<GrvtInstrumentSpec?> GetInstrumentSpecAsync(string symbol, CancellationToken cancellationToken)
    {
        if (_instrumentSpecs.TryGetValue(symbol, out var cached))
        {
            return cached;
        }

        await _instrumentSpecsGate.WaitAsync(cancellationToken);
        try
        {
            if (_instrumentSpecs.TryGetValue(symbol, out cached))
            {
                return cached;
            }

            var (ok, root, body) = await PostMarketRawAsync("/full/v1/all_instruments", new { is_active = true }, cancellationToken);
            if (!ok)
            {
                _logger.Warn("GRVT", $"GetInstrumentSpec failed symbol={symbol}, body={Trim(body)}");
                return null;
            }

            var arr = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("result", out var resultNode) && resultNode.ValueKind == JsonValueKind.Array
                ? resultNode
                : (root.ValueKind == JsonValueKind.Array ? root : default);
            if (arr.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var item in arr.EnumerateArray())
            {
                var instrument = ReadString(item, "instrument");
                if (string.IsNullOrWhiteSpace(instrument))
                {
                    continue;
                }

                var hash = ReadString(item, "instrument_hash");
                var tick = ReadDecimal(item, "tick_size");
                var minSize = ReadDecimal(item, "min_size");
                var baseDecimals = ReadInt32(item, "base_decimals");
                _instrumentSpecs[instrument] = new GrvtInstrumentSpec(instrument, hash, tick, minSize, baseDecimals);
            }

            return _instrumentSpecs.TryGetValue(symbol, out var spec) ? spec : null;
        }
        finally
        {
            _instrumentSpecsGate.Release();
        }
    }

    private async Task<(bool IsSuccess, string Message)> EnsureAuthenticatedAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_sessionCookie) &&
            (DateTimeOffset.UtcNow - _sessionAt) < TimeSpan.FromMinutes(30))
        {
            return (true, "GRVT auth session active");
        }

        var authMode = (_credentials.AuthMode ?? "Both").Trim();
        var hasApi = _credentials.HasApiCredentials;
        var hasWallet = _credentials.HasWalletCredentials;

        if ((authMode.Equals("ApiKey", StringComparison.OrdinalIgnoreCase) ||
             authMode.Equals("Both", StringComparison.OrdinalIgnoreCase)) && hasApi)
        {
            var apiLogin = await LoginWithApiKeyAsync(cancellationToken);
            if (apiLogin.IsSuccess)
            {
                return apiLogin;
            }

            _logger.Warn("GRVT", $"API key login failed: {apiLogin.Message}");
            if (!authMode.Equals("Both", StringComparison.OrdinalIgnoreCase))
            {
                return apiLogin;
            }
        }

        if ((authMode.Equals("Wallet", StringComparison.OrdinalIgnoreCase) ||
             authMode.Equals("Both", StringComparison.OrdinalIgnoreCase)) && hasWallet)
        {
            return (false, "GRVT wallet login requires EIP-712 WalletLogin signature flow and is not enabled yet in this build.");
        }

        return (false, "GRVT credentials are incomplete for selected auth mode.");
    }

    private async Task<(bool IsSuccess, string Message)> LoginWithApiKeyAsync(CancellationToken cancellationToken)
    {
        if (!_credentials.HasApiCredentials)
        {
            return (false, "Missing API key/secret");
        }

        try
        {
            var req = new
            {
                api_key = _credentials.ApiKey,
                secret_key = _credentials.ApiSecret
            };

            using var message = new HttpRequestMessage(HttpMethod.Post, _edgeBase + "/auth/api_key/login")
            {
                Content = new StringContent(JsonSerializer.Serialize(req), Encoding.UTF8, "application/json")
            };

            using var resp = await _httpClient.SendAsync(message, cancellationToken);
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                return (false, body);
            }

            _sessionCookie = ExtractCookie(resp, "gravity") ?? ExtractCookie(resp, "grvt") ?? ExtractCookie(resp, "session");
            _accountId = ExtractAccountId(resp, body);
            _sessionAt = DateTimeOffset.UtcNow;

            if (string.IsNullOrWhiteSpace(_sessionCookie))
            {
                return (false, "Login succeeded but no session cookie returned.");
            }

            _logger.Info("GRVT", $"API key login ok, accountId={_accountId ?? "(none)"}");
            return (true, "GRVT API key auth ok");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private async Task<List<VenuePosition>> FetchPositionsAsync(string subAccountId, CancellationToken cancellationToken)
    {
        var req = new { sub_account_id = subAccountId };
        var (ok, root, body) = await PostTradingAsync("/full/v1/positions", req, cancellationToken);
        if (!ok)
        {
            _logger.Warn("GRVT", $"FetchPositions failed: {Trim(body)}");
            return [];
        }

        var arr = ExtractArray(root, "positions") ?? ExtractArray(root, "items") ?? (root.ValueKind == JsonValueKind.Array ? root : default);
        if (arr.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var rows = new List<VenuePosition>();
        foreach (var item in arr.EnumerateArray())
        {
            var symbol = ReadString(item, "instrument") ?? ReadString(item, "symbol") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(symbol))
            {
                continue;
            }

            var qty = ReadDecimal(item, "size");
            if (qty == 0)
            {
                qty = ReadDecimal(item, "quantity");
            }

            if (qty == 0)
            {
                continue;
            }

            var entry = ReadDecimal(item, "entry_price");
            var mark = ReadDecimal(item, "mark_price");
            var lev = ReadDecimal(item, "leverage");
            var unreal = ReadDecimal(item, "unrealized_pnl");
            var realized = ReadDecimal(item, "realized_pnl");
            var notional = Math.Abs(ReadDecimal(item, "notional"));
            if (notional <= 0 && mark > 0)
            {
                notional = Math.Abs(qty) * mark;
            }

            var pct = PositionPnlMath.ComputeUnrealizedPnlPct(notional, unreal);
            rows.Add(new VenuePosition(
                NormalizeSymbol(symbol),
                qty,
                notional,
                lev <= 0 ? 1m : lev,
                entry,
                mark,
                pct,
                unreal,
                realized,
                ParseMarginMode(item)));
        }

        return rows;
    }

    private async Task<List<VenueOpenOrder>> FetchOpenOrdersAsync(string subAccountId, CancellationToken cancellationToken)
    {
        var req = new { sub_account_id = subAccountId };
        var (ok, root, body) = await PostTradingAsync("/full/v1/open_orders", req, cancellationToken);
        if (!ok)
        {
            _logger.Warn("GRVT", $"FetchOpenOrders failed: {Trim(body)}");
            return [];
        }

        var arr = ExtractArray(root, "result") ?? ExtractArray(root, "orders") ?? ExtractArray(root, "items") ?? (root.ValueKind == JsonValueKind.Array ? root : default);
        if (arr.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var rows = new List<VenueOpenOrder>();
        foreach (var item in arr.EnumerateArray())
        {
            string? legInstrument = null;
            JsonElement leg = default;
            if (item.TryGetProperty("legs", out var legsNode) &&
                legsNode.ValueKind == JsonValueKind.Array &&
                legsNode.GetArrayLength() > 0)
            {
                leg = legsNode[0];
                legInstrument = ReadString(leg, "instrument");
            }

            var status = ReadString(item, "status") ??
                         (item.TryGetProperty("state", out var stateNode) ? ReadString(stateNode, "status") : null) ??
                         string.Empty;
            var symbol = ReadString(item, "instrument") ?? ReadString(item, "symbol") ?? legInstrument ?? string.Empty;
            if (string.IsNullOrWhiteSpace(symbol))
            {
                continue;
            }

            var size = Math.Abs(ReadDecimal(item, "size"));
            var price = ReadDecimal(item, "limit_price");
            if (price <= 0)
            {
                price = ReadDecimal(item, "price");
            }

            if ((size <= 0 || price <= 0) && leg.ValueKind != JsonValueKind.Undefined)
            {
                if (size <= 0)
                {
                    size = Math.Abs(ReadDecimal(leg, "size"));
                }

                if (price <= 0)
                {
                    price = ReadDecimal(leg, "limit_price");
                }
            }

            var notional = size * (price > 0 ? price : 0m);
            rows.Add(new VenueOpenOrder(
                NormalizeSymbol(symbol),
                notional,
                0m,
                price > 0 ? price : null,
                status,
                ReadString(item, "order_id") ?? ReadString(item, "id") ?? ReadString(item, "client_order_id"),
                ParseMarginMode(item)));
        }

        return rows;
    }

    private async Task<List<VenueBalance>> FetchBalancesAsync(string subAccountId, CancellationToken cancellationToken)
    {
        var req = new { sub_account_id = subAccountId };
        var rows = new List<VenueBalance>();

        var (okTrading, tradingRoot, tradingBody) = await PostTradingAsync("/full/v1/account_summary", req, cancellationToken);
        if (okTrading)
        {
            rows.AddRange(ParseBalancesFromNode(tradingRoot, "TRADING", useAvailableBalanceForSettleCurrency: true));
        }
        else
        {
            _logger.Warn("GRVT", $"FetchBalances trading summary failed: {Trim(tradingBody)}");
        }

        var (okFunding, fundingRoot, fundingBody) = await PostTradingAsync("/full/v1/funding_account_summary", req, cancellationToken);
        if (okFunding)
        {
            rows.AddRange(ParseBalancesFromNode(fundingRoot, "FUNDING", useAvailableBalanceForSettleCurrency: false));
        }
        else
        {
            _logger.Warn("GRVT", $"FetchBalances funding summary failed: {Trim(fundingBody)}");
        }

        if (rows.Count > 0)
        {
            var tradingCount = rows.Count(x => x.Asset.Contains("(TRADING)", StringComparison.OrdinalIgnoreCase));
            var fundingCount = rows.Count(x => x.Asset.Contains("(FUNDING)", StringComparison.OrdinalIgnoreCase));
            _logger.Info("GRVT", $"FetchBalances done tradingRows={tradingCount}, fundingRows={fundingCount}");
            return rows;
        }

        var histReq = new { sub_account_id = subAccountId, limit = 1 };
        var (okHist, histRoot, histBody) = await PostTradingAsync("/full/v1/account_history", histReq, cancellationToken);
        if (!okHist)
        {
            _logger.Warn("GRVT", $"FetchBalances history failed: {Trim(histBody)}");
            return [];
        }

        var arr = ExtractArray(histRoot, "items") ?? ExtractArray(histRoot, "history") ?? (histRoot.ValueKind == JsonValueKind.Array ? histRoot : default);
        if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0)
        {
            return [];
        }

        var latest = arr.EnumerateArray().First();
        return ParseBalancesFromNode(latest, "TRADING", useAvailableBalanceForSettleCurrency: true);
    }

    private static List<VenueBalance> ParseBalancesFromNode(JsonElement root, string scopeLabel, bool useAvailableBalanceForSettleCurrency)
    {
        var balanceNode = ExtractArray(root, "balances") ??
                          ExtractArray(root, "assets") ??
                          ExtractArray(root, "spot_balances") ??
                          default;
        if (balanceNode.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var rows = new List<VenueBalance>();
        var settleCurrency = (ReadString(root, "settle_currency") ?? "USDT").ToUpperInvariant();
        var availableBalance = ReadDecimal(root, "available_balance");
        foreach (var item in balanceNode.EnumerateArray())
        {
            var asset = (ReadString(item, "asset") ??
                         ReadString(item, "coin") ??
                         ReadString(item, "currency") ??
                         string.Empty).ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(asset))
            {
                continue;
            }

            var qty = ReadDecimal(item, "quantity");
            if (qty == 0)
            {
                qty = ReadDecimal(item, "balance");
            }

             if (useAvailableBalanceForSettleCurrency &&
                 availableBalance > 0 &&
                 string.Equals(asset, settleCurrency, StringComparison.OrdinalIgnoreCase))
             {
                 qty = availableBalance;
             }

            var usd = ReadDecimal(item, "usd_value");
            if (usd == 0)
            {
                usd = ReadDecimal(item, "usdValue");
            }
            if (usd == 0)
            {
                var index = ReadDecimal(item, "index_price");
                if (index > 0 && qty != 0)
                {
                    usd = qty * index;
                }
            }

            if (asset is "USD" or "USDC" or "USDT")
            {
                usd = qty;
            }

            if (qty == 0 && usd == 0)
            {
                continue;
            }

            rows.Add(new VenueBalance($"{asset} ({scopeLabel})", qty, usd > 0 ? usd : qty));
        }

        return rows;
    }

    private async Task TradePollLoopAsync(string symbol, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var (tickerOk, tickerRoot, tickerBody) = await PostMarketAsync("/full/v1/ticker", new { instrument = symbol }, cancellationToken);
                if (tickerOk)
                {
                    var ts = ReadDateTime(tickerRoot, "event_time") ?? DateTimeOffset.UtcNow;
                    var px = ReadDecimal(tickerRoot, "last_price");
                    if (px <= 0)
                    {
                        px = ReadDecimal(tickerRoot, "mark_price");
                    }
                    if (px > 0)
                    {
                        _channel.Writer.TryWrite(new TradeTick(ts, px, 0m));
                    }
                    else
                    {
                        _channel.Writer.TryWrite(new VenueHeartbeat(ts, "ticker"));
                    }
                }
                else
                {
                    _logger.Warn("GRVT", $"Ticker poll failed symbol={symbol}, body={Trim(tickerBody)}");
                    _channel.Writer.TryWrite(new VenueHeartbeat(DateTimeOffset.UtcNow, "ticker_fail"));
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Warn("GRVT", $"Trade poll loop warning symbol={symbol}: {ex.Message}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        using var frameBuffer = new MemoryStream();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    continue;
                }

                frameBuffer.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                {
                    continue;
                }

                var payload = Encoding.UTF8.GetString(frameBuffer.GetBuffer(), 0, (int)frameBuffer.Length);
                frameBuffer.SetLength(0);
                HandleWsMessage(payload);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.Error("GRVT", "WS receive loop failed", ex);
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

            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("error", out var errNode))
            {
                _logger.Warn("GRVT", $"WS message error: {Trim(errNode.GetRawText())}");
                return;
            }

            // GRVT trade push shape:
            // { "stream":"v1.trade", "selector":"BTC_USDT_Perp@500", "feed": { event_time, price, size, ... } }
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("feed", out var feedNode) &&
                feedNode.ValueKind == JsonValueKind.Object)
            {
                var price = ReadDecimal(feedNode, "price");
                var size = ReadDecimal(feedNode, "size");
                if (price > 0 && size > 0)
                {
                    var ts = ReadDateTime(feedNode, "event_time") ??
                             ReadDateTime(feedNode, "timestamp") ??
                             DateTimeOffset.UtcNow;
                    _channel.Writer.TryWrite(new TradeTick(ts, price, size));
                }

                return;
            }

            // Try direct trade array in params.data
            if (root.TryGetProperty("params", out var paramsNode) &&
                paramsNode.ValueKind == JsonValueKind.Object &&
                paramsNode.TryGetProperty("data", out var dataNode) &&
                dataNode.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in dataNode.EnumerateArray())
                {
                    var price = ReadDecimal(item, "p");
                    var size = ReadDecimal(item, "s");
                    if (price <= 0 || size <= 0)
                    {
                        price = ReadDecimal(item, "price");
                        size = ReadDecimal(item, "size");
                    }

                    if (price <= 0 || size <= 0)
                    {
                        continue;
                    }

                    var ts = ReadDateTime(item, "et") ?? ReadDateTime(item, "timestamp") ?? DateTimeOffset.UtcNow;
                    _channel.Writer.TryWrite(new TradeTick(ts, price, size));
                }

                return;
            }

            // Try single trade payload.
            var p = ReadDecimal(root, "p");
            var q = ReadDecimal(root, "s");
            if (p > 0 && q > 0)
            {
                var ts = ReadDateTime(root, "et") ?? DateTimeOffset.UtcNow;
                _channel.Writer.TryWrite(new TradeTick(ts, p, q));
            }
        }
        catch
        {
            // ignore malformed
        }
    }

    private async Task<(bool Ok, JsonElement Root, string Body)> PostTradingAsync(string path, object request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_sessionCookie))
        {
            return (false, default, "No GRVT session cookie");
        }

        var url = _tradingBase + path;
        var json = JsonSerializer.Serialize(request);
        using var msg = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        msg.Headers.TryAddWithoutValidation("Cookie", "gravity=" + _sessionCookie);
        if (!string.IsNullOrWhiteSpace(_accountId))
        {
            msg.Headers.TryAddWithoutValidation("X-Grvt-Account-Id", _accountId);
        }

        using var resp = await _httpClient.SendAsync(msg, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            return (false, default, body);
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement.Clone();

        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var err))
        {
            return (false, root, err.GetRawText());
        }

        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("result", out var result))
        {
            return (true, result.Clone(), body);
        }

        return (true, root, body);
    }

    private static MarginMode ParseMarginMode(JsonElement item)
    {
        var raw = ReadString(item, "margin_type") ??
                  ReadString(item, "position_margin_type");

        if (string.IsNullOrWhiteSpace(raw) &&
            item.TryGetProperty("config", out var configNode) &&
            configNode.ValueKind == JsonValueKind.Object)
        {
            raw = ReadString(configNode, "margin_type") ??
                  ReadString(configNode, "position_margin_type");
        }

        return MarginModeText.ParseOrDefault(raw, MarginMode.Unknown);
    }

    private async Task<(bool Ok, JsonElement Root, string Body)> PostMarketAsync(string path, object request, CancellationToken cancellationToken)
    {
        var (ok, root, body) = await PostMarketRawAsync(path, request, cancellationToken);
        if (!ok)
        {
            return (false, root, body);
        }

        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("result", out var result))
        {
            return (true, result.Clone(), body);
        }

        return (true, root, body);
    }

    private async Task<(bool Ok, JsonElement Root, string Body)> PostMarketRawAsync(string path, object request, CancellationToken cancellationToken)
    {
        var url = _marketBase + path;
        var json = JsonSerializer.Serialize(request);
        using var msg = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        using var resp = await _httpClient.SendAsync(msg, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            return (false, default, body);
        }

        using var doc = JsonDocument.Parse(body);
        return (true, doc.RootElement.Clone(), body);
    }

    private static string NormalizeSymbol(string symbol)
    {
        var normalized = (symbol ?? string.Empty).Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "BTC_USDT_Perp";
        }

        normalized = normalized.Replace('/', '_').Replace('-', '_');
        if (normalized.EndsWith("_PERP", StringComparison.Ordinal))
        {
            return normalized[..^5] + "_Perp";
        }

        if (normalized.EndsWith("USDT", StringComparison.Ordinal) && !normalized.Contains('_'))
        {
            var baseAsset = normalized[..^4];
            if (!string.IsNullOrWhiteSpace(baseAsset))
            {
                return $"{baseAsset}_USDT_Perp";
            }
        }

        if (normalized.EndsWith("USDC", StringComparison.Ordinal) && !normalized.Contains('_'))
        {
            var baseAsset = normalized[..^4];
            if (!string.IsNullOrWhiteSpace(baseAsset))
            {
                return $"{baseAsset}_USDC_Perp";
            }
        }

        return normalized;
    }

    private static string? ExtractCookie(HttpResponseMessage response, string cookieName)
    {
        foreach (var value in response.Headers.TryGetValues("Set-Cookie", out var setCookies) ? setCookies : [])
        {
            var parts = value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                if (!part.StartsWith(cookieName + "=", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var idx = part.IndexOf('=');
                if (idx > 0 && idx < part.Length - 1)
                {
                    return part[(idx + 1)..];
                }
            }
        }

        return null;
    }

    private static string? ExtractAccountId(HttpResponseMessage response, string body)
    {
        if (response.Headers.TryGetValues("x-grvt-account-id", out var accountValues))
        {
            return accountValues.FirstOrDefault();
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                return ReadString(root, "account_id") ??
                       ReadString(root, "accountId") ??
                       (root.TryGetProperty("result", out var result) ? ReadString(result, "account_id") ?? ReadString(result, "accountId") : null);
            }
        }
        catch
        {
        }

        return null;
    }

    private static JsonElement? ExtractArray(JsonElement root, string property)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(property, out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            return arr;
        }

        return null;
    }

    private static decimal ReadDecimal(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var prop))
        {
            return 0m;
        }

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDecimal(out var d))
        {
            return d;
        }

        if (prop.ValueKind == JsonValueKind.String &&
            decimal.TryParse(prop.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out d))
        {
            return d;
        }

        return 0m;
    }

    private static long ReadLong(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var prop))
        {
            return 0L;
        }

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var l))
        {
            return l;
        }

        if (prop.ValueKind == JsonValueKind.String &&
            long.TryParse(prop.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out l))
        {
            return l;
        }

        return 0L;
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

    private static int ReadInt32(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var prop))
        {
            return 0;
        }

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var i))
        {
            return i;
        }

        if (prop.ValueKind == JsonValueKind.String &&
            int.TryParse(prop.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out i))
        {
            return i;
        }

        return 0;
    }

    private static DateTimeOffset? ReadDateTime(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var prop))
        {
            return null;
        }

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var l))
        {
            return FromUnixAny(l);
        }

        if (prop.ValueKind == JsonValueKind.String)
        {
            var s = prop.GetString();
            if (string.IsNullOrWhiteSpace(s))
            {
                return null;
            }

            if (long.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var n))
            {
                return FromUnixAny(n);
            }

            if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt))
            {
                return dt;
            }
        }

        return null;
    }

    private static DateTimeOffset? FromUnixAny(long value)
    {
        try
        {
            // GRVT returns event_time in nanoseconds; support ns/us/ms/s heuristically.
            if (value > 9_999_999_999_999_999) // ns
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(value / 1_000_000);
            }

            if (value > 9_999_999_999_999) // us
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(value / 1_000);
            }

            if (value > 9_999_999_999) // ms
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(value);
            }

            return DateTimeOffset.FromUnixTimeSeconds(value);
        }
        catch
        {
            return null;
        }
    }

    private byte[] BuildGrvtOrderDigest(
        ulong subAccountId,
        bool isMarket,
        byte timeInForce,
        bool postOnly,
        bool reduceOnly,
        BigInteger assetId,
        ulong contractSize,
        ulong limitPrice,
        bool isBuyingContract,
        uint nonce,
        long expirationNs)
    {
        var domainTypeHash = _keccak.CalculateHash(Encoding.UTF8.GetBytes("EIP712Domain(string name,string version,uint256 chainId)"));
        var nameHash = _keccak.CalculateHash(Encoding.UTF8.GetBytes("GRVT Exchange"));
        var versionHash = _keccak.CalculateHash(Encoding.UTF8.GetBytes("0"));
        var chainIdWord = UInt256Word(new BigInteger(GetGrvtChainId()));
        var domainSeparator = _keccak.CalculateHash(Concat(domainTypeHash, nameHash, versionHash, chainIdWord));

        var orderLegTypeHash = _keccak.CalculateHash(Encoding.UTF8.GetBytes("OrderLeg(uint256 assetID,uint64 contractSize,uint64 limitPrice,bool isBuyingContract)"));
        var legHash = _keccak.CalculateHash(
            Concat(
                orderLegTypeHash,
                UInt256Word(assetId),
                UInt256Word(new BigInteger(contractSize)),
                UInt256Word(new BigInteger(limitPrice)),
                BoolWord(isBuyingContract)));
        var legsHash = _keccak.CalculateHash(legHash);

        var orderTypeHash = _keccak.CalculateHash(Encoding.UTF8.GetBytes(
            "Order(uint64 subAccountID,bool isMarket,uint8 timeInForce,bool postOnly,bool reduceOnly,OrderLeg[] legs,uint32 nonce,int64 expiration)OrderLeg(uint256 assetID,uint64 contractSize,uint64 limitPrice,bool isBuyingContract)"));
        var orderHash = _keccak.CalculateHash(
            Concat(
                orderTypeHash,
                UInt256Word(new BigInteger(subAccountId)),
                BoolWord(isMarket),
                UInt256Word(new BigInteger(timeInForce)),
                BoolWord(postOnly),
                BoolWord(reduceOnly),
                legsHash,
                UInt256Word(new BigInteger(nonce)),
                Int256Word(new BigInteger(expirationNs))));

        var prefix = new byte[] { 0x19, 0x01 };
        return _keccak.CalculateHash(Concat(prefix, domainSeparator, orderHash));
    }

    private async Task<IReadOnlyList<Candle>> GetRecentCandlesFromTradesAsync(string requestedSymbol, string instrumentSymbol, CandleInterval interval, int count, CancellationToken cancellationToken)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var spanMs = IntervalToMilliseconds(interval);
        var lookbackMs = Math.Max(spanMs * Math.Clamp(count + 50, 80, 1200), spanMs * 120);
        var fromMs = nowMs - lookbackMs;
        var pageLimit = 1000; // GRVT server cap
        var page = 0;
        var maxPages = 24;
        var cursor = string.Empty;
        var trades = new List<(long TsMs, decimal Price, decimal Size)>(4096);

        while (!cancellationToken.IsCancellationRequested && page < maxPages)
        {
            page++;
            var req = new
            {
                instrument = instrumentSymbol,
                start_time = (fromMs * 1_000_000L).ToString(CultureInfo.InvariantCulture),
                end_time = (nowMs * 1_000_000L).ToString(CultureInfo.InvariantCulture),
                limit = pageLimit,
                cursor
            };

            var (ok, root, body) = await PostMarketRawAsync("/full/v1/trade_history", req, cancellationToken);
            if (!ok)
            {
                _logger.Warn("GRVT", $"GetRecentCandles trade fallback failed symbol={instrumentSymbol}, interval={interval}, page={page}, body={Trim(body)}");
                break;
            }

            var bodyRoot = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("result", out var resultNode)
                ? resultNode
                : root;

            var arr = ExtractArray(bodyRoot, "result") ?? ExtractArray(bodyRoot, "items") ?? ExtractArray(bodyRoot, "trades") ?? (bodyRoot.ValueKind == JsonValueKind.Array ? bodyRoot : default);
            if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0)
            {
                break;
            }

            var rowsInPage = 0;
            foreach (var item in arr.EnumerateArray())
            {
                var p = ReadDecimal(item, "price");
                var q = ReadDecimal(item, "size");
                if (p <= 0 || q <= 0)
                {
                    p = ReadDecimal(item, "p");
                    q = ReadDecimal(item, "s");
                }

                if (p <= 0 || q <= 0)
                {
                    continue;
                }

                var ts = ReadDateTime(item, "event_time") ?? ReadDateTime(item, "et") ?? ReadDateTime(item, "timestamp");
                if (ts is null)
                {
                    continue;
                }

                var tms = ts.Value.ToUnixTimeMilliseconds();
                if (tms < fromMs || tms > nowMs)
                {
                    continue;
                }

                trades.Add((tms, p, q));
                rowsInPage++;
            }

            if (rowsInPage == 0)
            {
                break;
            }

            var next = ReadString(root, "next");
            if (string.IsNullOrWhiteSpace(next))
            {
                break;
            }

            cursor = next;
        }

        if (trades.Count == 0)
        {
            return [];
        }

        trades.Sort((a, b) => a.TsMs.CompareTo(b.TsMs));

        var bucketMs = Math.Max(60_000L, spanMs);
        var map = new Dictionary<long, (decimal O, decimal H, decimal L, decimal C, decimal V)>();
        foreach (var tr in trades)
        {
            var bucket = (tr.TsMs / bucketMs) * bucketMs;
            if (!map.TryGetValue(bucket, out var c))
            {
                map[bucket] = (tr.Price, tr.Price, tr.Price, tr.Price, tr.Size);
                continue;
            }

            c.H = Math.Max(c.H, tr.Price);
            c.L = Math.Min(c.L, tr.Price);
            c.C = tr.Price;
            c.V += tr.Size;
            map[bucket] = c;
        }

        var rows = map
            .OrderBy(kv => kv.Key)
            .Select(kv =>
            {
                var x = kv.Value;
                return new Candle(
                    VenueId,
                    requestedSymbol,
                    interval,
                    DateTimeOffset.FromUnixTimeMilliseconds(kv.Key),
                    x.O, x.H, x.L, x.C, x.V, true);
            })
            .TakeLast(Math.Clamp(count, 20, 500))
            .ToList();

        var filled = FillMissingCandles(rows, interval, count);
        _logger.Info("GRVT", $"GetRecentCandles trade fallback ok symbol={instrumentSymbol}, interval={interval}, candles={filled.Count}, buckets={map.Count}, trades={trades.Count}, pages={page}");
        return filled;
    }

    private static string Trim(string text)
    {
        return text.Length > 300 ? text[..300] : text;
    }

    private static string NormalizeGrvtMarginModeError(string body, MarginMode marginMode)
    {
        var modeText = marginMode == MarginMode.Isolated ? "Isolated" : "Cross";
        var message = TryExtractGrvtErrorMessage(body) ?? Trim(body);
        if (string.IsNullOrWhiteSpace(message))
        {
            message = "Unknown GRVT margin-mode error.";
        }

        if (message.Contains("position", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("open order", StringComparison.OrdinalIgnoreCase))
        {
            return $"GRVT rejected switching to {modeText}: close existing positions and cancel open orders for this instrument first.";
        }

        return $"GRVT rejected switching to {modeText}: {message}";
    }

    private static string? TryExtractGrvtErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                {
                    return message.GetString();
                }

                if (root.TryGetProperty("error", out var error))
                {
                    if (error.ValueKind == JsonValueKind.String)
                    {
                        return error.GetString();
                    }

                    if (error.ValueKind == JsonValueKind.Object &&
                        error.TryGetProperty("message", out var nestedMessage) &&
                        nestedMessage.ValueKind == JsonValueKind.String)
                    {
                        return nestedMessage.GetString();
                    }
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static DateTimeOffset AlignToIntervalStart(DateTimeOffset ts, CandleInterval interval)
    {
        var ms = ts.ToUnixTimeMilliseconds();
        var step = IntervalToMilliseconds(interval);
        if (step <= 0)
        {
            return ts;
        }

        var aligned = (ms / step) * step;
        return DateTimeOffset.FromUnixTimeMilliseconds(aligned);
    }

    private static IReadOnlyList<Candle> NormalizeCandlesToInterval(IReadOnlyList<Candle> rows, CandleInterval interval)
    {
        if (rows.Count == 0)
        {
            return rows;
        }

        var grouped = rows
            .GroupBy(x => AlignToIntervalStart(x.OpenTime, interval))
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var ordered = g.OrderBy(x => x.OpenTime).ToList();
                var first = ordered[0];
                var last = ordered[^1];
                return new Candle(
                    first.VenueId,
                    first.Symbol,
                    interval,
                    g.Key,
                    first.Open,
                    ordered.Max(x => x.High),
                    ordered.Min(x => x.Low),
                    last.Close,
                    ordered.Sum(x => x.Volume),
                    true);
            })
            .ToList();

        return grouped;
    }

    private static IReadOnlyList<Candle> FillMissingCandles(IReadOnlyList<Candle> rows, CandleInterval interval, int count)
    {
        if (rows.Count == 0)
        {
            return rows;
        }

        var sorted = rows.OrderBy(x => x.OpenTime).ToList();
        var stepMs = IntervalToMilliseconds(interval);
        if (stepMs <= 0)
        {
            return sorted.TakeLast(Math.Clamp(count, 20, 500)).ToList();
        }

        var step = TimeSpan.FromMilliseconds(stepMs);
        var from = sorted.First().OpenTime;
        var to = sorted.Last().OpenTime;
        var byTime = sorted.ToDictionary(x => x.OpenTime);
        var filled = new List<Candle>(sorted.Count + 128);
        Candle? prev = null;

        for (var t = from; t <= to; t = t.Add(step))
        {
            if (byTime.TryGetValue(t, out var c))
            {
                filled.Add(c);
                prev = c;
                continue;
            }

            if (prev is null)
            {
                continue;
            }

            var flat = new Candle(
                prev.VenueId,
                prev.Symbol,
                prev.Interval,
                t,
                prev.Close,
                prev.Close,
                prev.Close,
                prev.Close,
                0m,
                true);
            filled.Add(flat);
            prev = flat;
        }

        return filled.TakeLast(Math.Clamp(count, 20, 500)).ToList();
    }

    private int GetGrvtChainId()
    {
        return string.Equals(_environment, "testnet", StringComparison.OrdinalIgnoreCase) ? 326 : 325;
    }

    private static byte ToTimeInForceCode(string timeInForce)
    {
        return (timeInForce ?? string.Empty).Trim().ToUpperInvariant() switch
        {
            "GOOD_TILL_TIME" => 1,
            "ALL_OR_NONE" => 2,
            "IMMEDIATE_OR_CANCEL" => 3,
            "FILL_OR_KILL" => 4,
            _ => 0
        };
    }

    private static decimal Pow10(int exponent)
    {
        if (exponent <= 0)
        {
            return 1m;
        }

        decimal value = 1m;
        for (var i = 0; i < exponent; i++)
        {
            value *= 10m;
        }

        return value;
    }

    private static BigInteger ParseAssetId(string? instrumentHash, string instrument)
    {
        var hash = (instrumentHash ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(hash))
        {
            if (hash.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                hash = hash[2..];
            }

            if (BigInteger.TryParse("0" + hash, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var fromHex))
            {
                return fromHex;
            }
        }

        return instrument.ToUpperInvariant() switch
        {
            "BTC_USDT_PERP" => new BigInteger(0x030501),
            "ETH_USDT_PERP" => new BigInteger(0x030401),
            _ => BigInteger.Zero
        };
    }

    private static ulong ToScaledUInt64(decimal value, decimal multiplier)
    {
        var scaled = decimal.Round(value * multiplier, 0, MidpointRounding.AwayFromZero);
        if (scaled <= 0)
        {
            return 0UL;
        }

        if (scaled > ulong.MaxValue)
        {
            return ulong.MaxValue;
        }

        return (ulong)scaled;
    }

    private static decimal NormalizeByStep(decimal value, decimal step, bool roundUp)
    {
        if (value <= 0)
        {
            return 0m;
        }

        if (step <= 0)
        {
            return value;
        }

        var units = value / step;
        units = roundUp ? decimal.Ceiling(units) : decimal.Floor(units);
        if (units <= 0)
        {
            return 0m;
        }

        return units * step;
    }

    private static uint NextUInt32()
    {
        Span<byte> bytes = stackalloc byte[4];
        RandomNumberGenerator.Fill(bytes);
        return BitConverter.ToUInt32(bytes);
    }

    private static long GetExpiryNanoseconds(int hoursFromNow)
    {
        var nowNs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L;
        var deltaNs = (long)TimeSpan.FromHours(hoursFromNow).TotalMilliseconds * 1_000_000L;
        return nowNs + deltaNs;
    }

    private static string NormalizePrivateKey(string? privateKey)
    {
        if (string.IsNullOrWhiteSpace(privateKey))
        {
            throw new InvalidOperationException("PrivateKey is required.");
        }

        return privateKey.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? privateKey[2..]
            : privateKey;
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
        if (bytes.Length > 32)
        {
            throw new InvalidOperationException("Value exceeds uint256.");
        }

        var word = new byte[32];
        Buffer.BlockCopy(bytes, 0, word, 32 - bytes.Length, bytes.Length);
        return word;
    }

    private static byte[] Int256Word(BigInteger value)
    {
        if (value.Sign >= 0)
        {
            return UInt256Word(value);
        }

        var twos = (BigInteger.One << 256) + value;
        return UInt256Word(twos);
    }

    private static byte[] BoolWord(bool value)
    {
        var word = new byte[32];
        if (value)
        {
            word[31] = 1;
        }

        return word;
    }

    private static long IntervalToMilliseconds(CandleInterval interval)
    {
        return interval switch
        {
            CandleInterval.M5 => 5L * 60 * 1000,
            CandleInterval.M10 => 10L * 60 * 1000,
            CandleInterval.M15 => 15L * 60 * 1000,
            CandleInterval.M30 => 30L * 60 * 1000,
            CandleInterval.H1 => 60L * 60 * 1000,
            CandleInterval.H2 => 2L * 60 * 60 * 1000,
            CandleInterval.H4 => 4L * 60 * 60 * 1000,
            CandleInterval.H6 => 6L * 60 * 60 * 1000,
            CandleInterval.H12 => 12L * 60 * 60 * 1000,
            CandleInterval.D1 => 24L * 60 * 60 * 1000,
            CandleInterval.D7 => 7L * 24 * 60 * 60 * 1000,
            CandleInterval.D30 => 30L * 24 * 60 * 60 * 1000,
            _ => 5L * 60 * 1000
        };
    }

    private sealed record GrvtInstrumentSpec(
        string Instrument,
        string? InstrumentHash,
        decimal TickSize,
        decimal MinSize,
        int BaseDecimals);
}
