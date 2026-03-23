using AiyoPerps.Core;
using AiyoPerps.Models;
using Nethereum.ABI;
using Nethereum.ABI.Model;
using Nethereum.Signer;
using Nethereum.Util;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace AiyoPerps.Services;

public sealed class AsterVenueAdapter : IPerpVenue, IHistoricalCandleProvider, IAccountStateProvider
{
    private readonly string _environment;
    private readonly string _restBase;
    private readonly string _wsBasePrimary;
    private readonly string? _wsBaseFallback;
    private readonly AccountCredentials _credentials;
    private readonly AppLogger _logger;
    private readonly HttpClient _httpClient = new();
    private readonly Channel<MarketEvent> _channel = Channel.CreateUnbounded<MarketEvent>();
    private readonly Sha3Keccack _keccak = Sha3Keccack.Current;

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _wsCts;
    private Task? _wsTask;
    private readonly SemaphoreSlim _positionModeGate = new(1, 1);
    private readonly SemaphoreSlim _symbolRulesGate = new(1, 1);
    private readonly Dictionary<string, AsterSymbolRule> _symbolRules = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _positionModeCheckedAt = DateTimeOffset.MinValue;
    private DateTimeOffset _symbolRulesCheckedAt = DateTimeOffset.MinValue;
    private bool _isOneWayMode = true;
    private int? _selectedSigningVariant;
    private static readonly SigningVariant[] SigningVariants =
    [
        new(0, false),
        new(1, true)
    ];

    public AsterVenueAdapter(string environment, AccountCredentials credentials, AppLogger logger)
    {
        _environment = environment;
        _credentials = credentials;
        _logger = logger;

        var isTestnet = string.Equals(environment, "testnet", StringComparison.OrdinalIgnoreCase);
        _restBase = isTestnet ? "https://fapi.asterdex-testnet.com" : "https://fapi.asterdex.com";
        _wsBasePrimary = isTestnet ? "wss://fstream5.asterdex-testnet.com/ws" : "wss://fstream.asterdex.com/ws";
        _wsBaseFallback = isTestnet ? "wss://fstream.asterdex-testnet.com/ws" : null;

        _logger.Info("Aster", $"Adapter created. env={environment}, rest={_restBase}, wsPrimary={_wsBasePrimary}, wsFallback={_wsBaseFallback ?? "(none)"}");
        TryLogSignerKeyMatch();
    }

    public string VenueId => "Aster";

    public async Task ConnectMarketDataAsync(IEnumerable<string> subscriptions, CancellationToken cancellationToken = default)
    {
        await DisconnectMarketDataAsync(cancellationToken);

        var symbol = NormalizeSymbol(subscriptions.FirstOrDefault() ?? "BTCUSDT");
        var stream = symbol.ToLowerInvariant() + "@aggTrade";

        _ws = await ConnectWebSocketAsync(stream, cancellationToken);
        _wsCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _wsTask = Task.Run(() => ReceiveLoopAsync(_ws, _wsCts.Token), _wsCts.Token);

        _logger.Info("Aster", $"WS connected symbol={symbol}, stream={stream}");
    }

    public async Task DisconnectMarketDataAsync(CancellationToken cancellationToken = default)
    {
        _logger.Info("Aster", "DisconnectMarketDataAsync called");

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
                    _logger.Warn("Aster", $"WS close warning: {ex.Message}");
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
                _logger.Warn("Aster", $"WS task warning: {ex.Message}");
            }

            _wsTask = null;
        }

        _wsCts?.Dispose();
        _wsCts = null;
    }

    public async Task<(bool IsSuccess, string Message)> ConfigureLeverageAsync(string symbol, decimal leverage, MarginMode marginMode, CancellationToken cancellationToken = default)
    {
        if (!HasAsterSignerCredentials())
        {
            return (false, "Aster requires AccountAddress + WalletAddress + PrivateKey");
        }

        var mode = await EnsureOneWayModeAsync(cancellationToken);
        if (!mode.IsSuccess)
        {
            return mode;
        }

        var marginModeResult = await EnsureMarginModeAsync(symbol, marginMode, cancellationToken);
        if (!marginModeResult.IsSuccess)
        {
            return marginModeResult;
        }

        var lev = Math.Max(1, (int)Math.Round(leverage, MidpointRounding.AwayFromZero));
        var form = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["symbol"] = NormalizeSymbol(symbol),
            ["leverage"] = lev.ToString(CultureInfo.InvariantCulture)
        };

        var (ok, body, _) = await SendSignedAsync(HttpMethod.Post, "/fapi/v3/leverage", form, cancellationToken);
        if (!ok)
        {
            return (false, body);
        }

        return (true, "ok");
    }

    public Task<OrderAck> PlaceOrderAsync(string symbol, string side, decimal qty, decimal? price, CancellationToken cancellationToken = default)
        => PlaceOrderCoreAsync(symbol, side, qty, price, reduceOnly: false, cancellationToken);

    public Task<OrderAck> PlaceCloseOrderAsync(string symbol, string side, decimal positionQty, decimal? price, CancellationToken cancellationToken = default)
        => PlaceOrderCoreAsync(symbol, side, Math.Abs(positionQty), price, reduceOnly: true, cancellationToken);

    public async Task<OrderAck> CancelOrderAsync(string symbol, string orderId, CancellationToken cancellationToken = default)
    {
        if (!HasAsterSignerCredentials())
        {
            return new OrderAck(DateTimeOffset.UtcNow, string.Empty, false, "Aster requires AccountAddress + WalletAddress + PrivateKey");
        }

        if (string.IsNullOrWhiteSpace(orderId))
        {
            return new OrderAck(DateTimeOffset.UtcNow, string.Empty, false, "orderId is required");
        }

        var form = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["symbol"] = NormalizeSymbol(symbol),
            ["orderId"] = orderId.Trim()
        };

        var (ok, body, _) = await SendSignedAsync(HttpMethod.Delete, "/fapi/v3/order", form, cancellationToken);
        if (!ok)
        {
            return new OrderAck(DateTimeOffset.UtcNow, orderId.Trim(), false, body);
        }

        return new OrderAck(DateTimeOffset.UtcNow, orderId.Trim(), true, "ok");
    }

    public async Task<(bool IsSuccess, string Message)> ValidateConnectionAsync(CancellationToken cancellationToken = default)
    {
        using var ping = await _httpClient.GetAsync(_restBase + "/fapi/v3/ping", cancellationToken);
        if (!ping.IsSuccessStatusCode)
        {
            return (false, $"Aster public ping failed: {(int)ping.StatusCode}");
        }

        if (!HasAsterSignerCredentials())
        {
            return (true, "Aster public connection ok (signer credentials not configured)");
        }

        var (ok, body, _) = await SendSignedAsync(HttpMethod.Get, "/fapi/v3/balance", [], cancellationToken);
        if (!ok)
        {
            return (false, body);
        }

        return (true, "Aster auth ok");
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
        var (baseInterval, factor) = IntervalToAster(interval);
        var fetchCount = Math.Max(60, count * factor + factor);
        var url = $"{_restBase}/fapi/v3/klines?symbol={NormalizeSymbol(symbol)}&interval={baseInterval}&limit={fetchCount}";

        using var resp = await _httpClient.GetAsync(url, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.Error("Aster", $"GetRecentCandles failed status={(int)resp.StatusCode}, body={Trim(body)}");
            return [];
        }

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var baseCandles = new List<Candle>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() < 6)
            {
                continue;
            }

            var openTime = DateTimeOffset.FromUnixTimeMilliseconds(item[0].GetInt64());
            var open = ParseDecimal(item[1]);
            var high = ParseDecimal(item[2]);
            var low = ParseDecimal(item[3]);
            var close = ParseDecimal(item[4]);
            var volume = ParseDecimal(item[5]);

            if (open <= 0 || high <= 0 || low <= 0 || close <= 0)
            {
                continue;
            }

            baseCandles.Add(new Candle(VenueId, NormalizeSymbol(symbol), BaseIntervalFromAster(baseInterval), openTime, open, high, low, close, volume, true));
        }

        if (baseCandles.Count == 0)
        {
            return [];
        }

        var sorted = baseCandles.OrderBy(x => x.OpenTime).ToList();
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
        if (!HasAsterSignerCredentials())
        {
            return new VenueAccountSnapshot(DateTimeOffset.UtcNow, [], [], []);
        }

        if (sections == AccountSnapshotSections.None)
        {
            return new VenueAccountSnapshot(DateTimeOffset.UtcNow, [], [], []);
        }

        try
        {
            List<VenuePosition> positions = [];
            List<VenueBalance> balances = [];
            if (sections.HasFlag(AccountSnapshotSections.Positions) || sections.HasFlag(AccountSnapshotSections.Balances))
            {
                var snapshot = await FetchAccountSnapshotCoreAsync(cancellationToken);
                if (sections.HasFlag(AccountSnapshotSections.Positions))
                {
                    positions = snapshot.Positions;
                }

                if (sections.HasFlag(AccountSnapshotSections.Balances))
                {
                    balances = snapshot.Balances;
                }
            }

            var orders = sections.HasFlag(AccountSnapshotSections.Orders)
                ? await FetchOpenOrdersAsync(cancellationToken)
                : [];
            return new VenueAccountSnapshot(DateTimeOffset.UtcNow, positions, orders, balances);
        }
        catch (Exception ex)
        {
            _logger.Error("Aster", "GetAccountSnapshot failed", ex);
            return new VenueAccountSnapshot(DateTimeOffset.UtcNow, [], [], []);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectMarketDataAsync();
        _httpClient.Dispose();
        _positionModeGate.Dispose();
    }

    private async Task<(bool IsSuccess, string Message)> EnsureOneWayModeAsync(CancellationToken cancellationToken)
    {
        if (_isOneWayMode && DateTimeOffset.UtcNow - _positionModeCheckedAt < TimeSpan.FromMinutes(3))
        {
            return (true, "ok");
        }

        await _positionModeGate.WaitAsync(cancellationToken);
        try
        {
            if (_isOneWayMode && DateTimeOffset.UtcNow - _positionModeCheckedAt < TimeSpan.FromMinutes(3))
            {
                return (true, "ok");
            }

            var (ok, body, root) = await SendSignedAsync(HttpMethod.Get, "/fapi/v3/positionSide/dual", [], cancellationToken);
            if (!ok)
            {
                return (false, $"Aster position mode check failed: {Trim(body)}");
            }

            var isDual = ParseBool(root, "dualSidePosition");
            if (!isDual)
            {
                _isOneWayMode = true;
                _positionModeCheckedAt = DateTimeOffset.UtcNow;
                return (true, "ok");
            }

            var switchParams = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["dualSidePosition"] = "false"
            };
            var (switchOk, switchBody, _) = await SendSignedAsync(HttpMethod.Post, "/fapi/v3/positionSide/dual", switchParams, cancellationToken);
            if (!switchOk)
            {
                _isOneWayMode = false;
                _positionModeCheckedAt = DateTimeOffset.UtcNow;
                return (false, $"Aster hedge mode is not supported by this app. Switch to one-way mode failed: {Trim(switchBody)}");
            }

            _logger.Info("Aster", "Position mode switched to one-way (dualSidePosition=false)");
            _isOneWayMode = true;
            _positionModeCheckedAt = DateTimeOffset.UtcNow;
            return (true, "ok");
        }
        finally
        {
            _positionModeGate.Release();
        }
    }

    private async Task<(bool IsSuccess, string Message)> EnsureMarginModeAsync(string symbol, MarginMode marginMode, CancellationToken cancellationToken)
    {
        if (marginMode == MarginMode.Unknown)
        {
            return (true, "ok");
        }

        var form = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["symbol"] = NormalizeSymbol(symbol),
            ["marginType"] = marginMode == MarginMode.Isolated ? "ISOLATED" : "CROSSED"
        };

        var (ok, body, _) = await SendSignedAsync(HttpMethod.Post, "/fapi/v1/marginType", form, cancellationToken);
        if (ok || IsAsterMarginModeNoOp(body))
        {
            return (true, "ok");
        }

        return (false, NormalizeAsterMarginModeError(body, marginMode));
    }

    private static bool IsAsterMarginModeNoOp(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        return body.Contains("No need to change margin type", StringComparison.OrdinalIgnoreCase) ||
               body.Contains("\"code\":-4046", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeAsterMarginModeError(string body, MarginMode marginMode)
    {
        var modeText = marginMode == MarginMode.Isolated ? "Isolated" : "Cross";
        var message = TryExtractJsonMessage(body) ?? Trim(body);
        if (string.IsNullOrWhiteSpace(message))
        {
            message = "Unknown Aster margin-mode error.";
        }

        if (message.Contains("Multi-Assets mode", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("multi-asset", StringComparison.OrdinalIgnoreCase))
        {
            return "Aster Multi-Asset Mode supports Cross only. Switch the exchange account back to single-asset mode before using Isolated.";
        }

        if (message.Contains("open orders", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("position", StringComparison.OrdinalIgnoreCase))
        {
            return $"Aster rejected switching to {modeText}: close existing positions and cancel open orders for this symbol first.";
        }

        return $"Aster rejected switching to {modeText}: {message}";
    }

    private static string? TryExtractJsonMessage(string body)
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
                if (root.TryGetProperty("msg", out var msg) && msg.ValueKind == JsonValueKind.String)
                {
                    return msg.GetString();
                }

                if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                {
                    return message.GetString();
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private async Task<OrderAck> PlaceOrderCoreAsync(string symbol, string side, decimal qty, decimal? price, bool reduceOnly, CancellationToken cancellationToken)
    {
        if (!HasAsterSignerCredentials())
        {
            return new OrderAck(DateTimeOffset.UtcNow, string.Empty, false, "Aster requires AccountAddress + WalletAddress + PrivateKey");
        }

        if (qty <= 0)
        {
            return new OrderAck(DateTimeOffset.UtcNow, string.Empty, false, "qty must be positive");
        }

        var mode = await EnsureOneWayModeAsync(cancellationToken);
        if (!mode.IsSuccess)
        {
            return new OrderAck(DateTimeOffset.UtcNow, string.Empty, false, mode.Message);
        }

        var normalizedSymbol = NormalizeSymbol(symbol);
        var normalizedSide = string.Equals(side, "Buy", StringComparison.OrdinalIgnoreCase) ? "BUY" : "SELL";
        var isLimit = price.HasValue && price.Value > 0;
        var rule = await GetSymbolRuleAsync(normalizedSymbol, cancellationToken);
        var normalizedQty = NormalizeByRule(Math.Abs(qty), rule?.QuantityStep, rule?.QuantityPrecision);
        if (normalizedQty <= 0)
        {
            _logger.Warn("Aster", $"PlaceOrder rejected after precision normalize qty<=0 symbol={normalizedSymbol}, qtyRaw={qty}, step={rule?.QuantityStep}, qtyPrecision={rule?.QuantityPrecision}");
            return new OrderAck(DateTimeOffset.UtcNow, string.Empty, false, "quantity became zero after precision normalization");
        }

        decimal? normalizedPrice = null;
        if (isLimit)
        {
            normalizedPrice = NormalizeByRule(price!.Value, rule?.PriceTick, rule?.PricePrecision);
            if (!normalizedPrice.HasValue || normalizedPrice.Value <= 0)
            {
                _logger.Warn("Aster", $"PlaceOrder rejected invalid normalized price symbol={normalizedSymbol}, priceRaw={price}, tick={rule?.PriceTick}, pricePrecision={rule?.PricePrecision}");
                return new OrderAck(DateTimeOffset.UtcNow, string.Empty, false, "invalid normalized limit price");
            }
        }

        _logger.Info("Aster", $"PlaceOrder submit symbol={normalizedSymbol}, side={normalizedSide}, qtyRaw={qty}, qtyNorm={normalizedQty}, pxRaw={(price?.ToString(CultureInfo.InvariantCulture) ?? "MKT")}, pxNorm={(normalizedPrice?.ToString(CultureInfo.InvariantCulture) ?? "MKT")}, reduceOnly={reduceOnly}, step={rule?.QuantityStep}, tick={rule?.PriceTick}, qtyPrecision={rule?.QuantityPrecision}, pricePrecision={rule?.PricePrecision}");

        var form = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["symbol"] = normalizedSymbol,
            ["side"] = normalizedSide,
            ["positionSide"] = "BOTH",
            ["type"] = isLimit ? "LIMIT" : "MARKET",
            ["quantity"] = ConvertToAsterValue(normalizedQty)
        };

        if (reduceOnly)
        {
            form["reduceOnly"] = "true";
        }

        if (isLimit)
        {
            // For close limit orders, prefer post-only to avoid immediate taker fill.
            form["timeInForce"] = reduceOnly ? "GTX" : "GTC";
            form["price"] = ConvertToAsterValue(normalizedPrice!.Value);
        }

        var (ok, body, root) = await SendSignedAsync(HttpMethod.Post, "/fapi/v3/order", form, cancellationToken);
        if (!ok)
        {
            return new OrderAck(DateTimeOffset.UtcNow, string.Empty, false, body);
        }

        var orderId = TryReadString(root, "orderId") ?? string.Empty;
        return new OrderAck(DateTimeOffset.UtcNow, orderId, true, "ok");
    }

    private async Task<(List<VenuePosition> Positions, List<VenueBalance> Balances)> FetchAccountSnapshotCoreAsync(CancellationToken cancellationToken)
    {
        var (ok, body, root) = await SendSignedAsync(HttpMethod.Get, "/fapi/v3/account", [], cancellationToken);
        if (ok && root.ValueKind == JsonValueKind.Object)
        {
            var positions = ParsePositionsFromAccount(root);
            var balances = ParseBalancesFromAccount(root);
            if (positions.Count > 0 || balances.Count > 0)
            {
                _logger.Info("Aster", $"FetchAccountSnapshotCore via /account positions={positions.Count}, balances={balances.Count}");
                return (positions, balances);
            }
        }
        else
        {
            _logger.Warn("Aster", $"FetchAccountSnapshotCore /account failed: {Trim(body)}");
        }

        var fallbackPositions = await FetchPositionsAsync(cancellationToken);
        var fallbackBalances = await FetchBalancesAsync(cancellationToken);
        return (fallbackPositions, fallbackBalances);
    }

    private async Task<AsterSymbolRule?> GetSymbolRuleAsync(string symbol, CancellationToken cancellationToken)
    {
        if (_symbolRules.TryGetValue(symbol, out var existing))
        {
            return existing;
        }

        await _symbolRulesGate.WaitAsync(cancellationToken);
        try
        {
            if (_symbolRules.TryGetValue(symbol, out existing))
            {
                return existing;
            }

            var now = DateTimeOffset.UtcNow;
            if ((now - _symbolRulesCheckedAt) > TimeSpan.FromMinutes(15) || _symbolRules.Count == 0)
            {
                await RefreshSymbolRulesAsync(cancellationToken);
                _symbolRulesCheckedAt = now;
            }

            return _symbolRules.TryGetValue(symbol, out var loaded) ? loaded : null;
        }
        finally
        {
            _symbolRulesGate.Release();
        }
    }

    private async Task RefreshSymbolRulesAsync(CancellationToken cancellationToken)
    {
        var endpoints = new[] { "/fapi/v1/exchangeInfo", "/fapi/v3/exchangeInfo" };
        foreach (var endpoint in endpoints)
        {
            try
            {
                using var resp = await _httpClient.GetAsync(_restBase + endpoint, cancellationToken);
                var body = await resp.Content.ReadAsStringAsync(cancellationToken);
                if (!resp.IsSuccessStatusCode)
                {
                    continue;
                }

                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("symbols", out var symbolsNode) || symbolsNode.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var loaded = 0;
                foreach (var item in symbolsNode.EnumerateArray())
                {
                    var symbol = (TryReadString(item, "symbol") ?? string.Empty).ToUpperInvariant();
                    if (string.IsNullOrWhiteSpace(symbol))
                    {
                        continue;
                    }

                    decimal? qtyStep = null;
                    decimal? priceTick = null;
                    if (item.TryGetProperty("filters", out var filtersNode) && filtersNode.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var filter in filtersNode.EnumerateArray())
                        {
                            var filterType = TryReadString(filter, "filterType") ?? string.Empty;
                            if (string.Equals(filterType, "LOT_SIZE", StringComparison.OrdinalIgnoreCase))
                            {
                                var step = ParseDecimal(filter, "stepSize");
                                if (step > 0)
                                {
                                    qtyStep = step;
                                }
                            }
                            else if (string.Equals(filterType, "PRICE_FILTER", StringComparison.OrdinalIgnoreCase))
                            {
                                var tick = ParseDecimal(filter, "tickSize");
                                if (tick > 0)
                                {
                                    priceTick = tick;
                                }
                            }
                        }
                    }

                    var qtyPrecision = TryReadInt(item, "quantityPrecision");
                    var pricePrecision = TryReadInt(item, "pricePrecision");
                    _symbolRules[symbol] = new AsterSymbolRule(symbol, qtyStep, priceTick, qtyPrecision, pricePrecision);
                    loaded++;
                }

                _logger.Info("Aster", $"ExchangeInfo loaded endpoint={endpoint}, symbolRules={loaded}");
                return;
            }
            catch (Exception ex)
            {
                _logger.Warn("Aster", $"ExchangeInfo load failed endpoint={endpoint}, msg={ex.Message}");
            }
        }
    }

    private static decimal NormalizeByRule(decimal value, decimal? stepOrTick, int? precision)
    {
        if (value <= 0)
        {
            return 0m;
        }

        if (stepOrTick.HasValue && stepOrTick.Value > 0)
        {
            var step = stepOrTick.Value;
            var units = Math.Floor(value / step);
            var normalized = units * step;
            return normalized > 0 ? normalized : 0m;
        }

        if (precision.HasValue && precision.Value >= 0)
        {
            var p = Math.Min(18, precision.Value);
            return decimal.Round(value, p, MidpointRounding.ToZero);
        }

        return value;
    }

    private async Task<List<VenuePosition>> FetchPositionsAsync(CancellationToken cancellationToken)
    {
        var (ok, body, root) = await SendSignedAsync(HttpMethod.Get, "/fapi/v3/positionRisk", [], cancellationToken);
        if (!ok)
        {
            _logger.Warn("Aster", $"FetchPositions failed: {Trim(body)}");
            return [];
        }

        if (root.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var rows = new List<VenuePosition>();
        foreach (var item in root.EnumerateArray())
        {
            var symbol = (TryReadString(item, "symbol") ?? string.Empty).ToUpperInvariant();
            var qty = ParseDecimal(item, "positionAmt");
            if (string.IsNullOrWhiteSpace(symbol) || qty == 0)
            {
                continue;
            }

            var entry = ParseDecimal(item, "entryPrice");
            var mark = ParseDecimal(item, "markPrice");
            var leverage = ParseDecimal(item, "leverage");
            var unrealized = ParseDecimal(item, "unRealizedProfit");
            var notional = ParseDecimal(item, "notional");
            if (notional == 0 && mark > 0)
            {
                notional = Math.Abs(qty) * mark;
            }

            var pct = notional > 0 ? unrealized / notional * 100m : 0m;
            rows.Add(new VenuePosition(
                symbol,
                qty,
                Math.Abs(notional),
                leverage <= 0 ? 1 : leverage,
                entry,
                mark,
                pct,
                unrealized,
                0m,
                MarginModeText.ParseOrDefault(TryReadString(item, "marginType"), MarginMode.Unknown)));
        }

        return rows;
    }

    private async Task<List<VenueOpenOrder>> FetchOpenOrdersAsync(CancellationToken cancellationToken)
    {
        var (ok, body, root) = await SendSignedAsync(HttpMethod.Get, "/fapi/v3/openOrders", [], cancellationToken);
        if (!ok)
        {
            _logger.Warn("Aster", $"FetchOpenOrders failed: {Trim(body)}");
            return [];
        }

        if (root.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var rows = new List<VenueOpenOrder>();
        foreach (var item in root.EnumerateArray())
        {
            var symbol = (TryReadString(item, "symbol") ?? string.Empty).ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(symbol))
            {
                continue;
            }

            var origQty = ParseDecimal(item, "origQty");
            var price = ParseDecimal(item, "price");
            var status = TryReadString(item, "status") ?? string.Empty;
            var orderId = TryReadString(item, "orderId");
            var notional = Math.Abs(origQty * (price > 0 ? price : ParseDecimal(item, "avgPrice")));
            rows.Add(new VenueOpenOrder(
                symbol,
                notional,
                0m,
                price > 0 ? price : null,
                status,
                orderId,
                MarginModeText.ParseOrDefault(TryReadString(item, "marginType"), MarginMode.Unknown)));
        }

        return rows;
    }

    private async Task<List<VenueBalance>> FetchBalancesAsync(CancellationToken cancellationToken)
    {
        var (ok, body, root) = await SendSignedAsync(HttpMethod.Get, "/fapi/v3/balance", [], cancellationToken);
        if (!ok)
        {
            _logger.Warn("Aster", $"FetchBalances failed: {Trim(body)}");
            return [];
        }

        if (root.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var rows = new List<VenueBalance>();
        foreach (var item in root.EnumerateArray())
        {
            var asset = (TryReadString(item, "asset") ?? string.Empty).ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(asset))
            {
                continue;
            }

            var qty = ParseDecimal(item, "balance");
            var availableQty = ParseDecimal(item, "availableBalance");
            if (availableQty <= 0m)
            {
                availableQty = ParseDecimal(item, "withdrawAvailable");
            }

            var crossWallet = ParseDecimal(item, "crossWalletBalance");
            var usd = asset is "USDT" or "USDC" or "USD" ? qty : Math.Max(0m, crossWallet);
            var availableUsd = asset is "USDT" or "USDC" or "USD"
                ? (availableQty > 0m ? availableQty : qty)
                : Math.Max(0m, crossWallet);
            if (qty <= 0m)
            {
                continue;
            }

            rows.Add(new VenueBalance(
                asset,
                qty,
                usd,
                availableQty > 0m ? availableQty : qty,
                availableUsd));
        }

        return rows;
    }

    private static List<VenuePosition> ParsePositionsFromAccount(JsonElement root)
    {
        if (!root.TryGetProperty("positions", out var positionsNode) || positionsNode.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var rows = new List<VenuePosition>();
        foreach (var item in positionsNode.EnumerateArray())
        {
            var symbol = (TryReadString(item, "symbol") ?? string.Empty).ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(symbol))
            {
                continue;
            }

            var qty = ParseDecimal(item, "positionAmt");
            if (qty == 0m)
            {
                continue;
            }

            var entry = ParseDecimal(item, "entryPrice");
            var mark = ParseDecimal(item, "markPrice");
            var leverage = ParseDecimal(item, "leverage");
            var unrealized = ParseDecimal(item, "unrealizedProfit");
            if (unrealized == 0m)
            {
                unrealized = ParseDecimal(item, "unRealizedProfit");
            }

            var notional = Math.Abs(qty) * (mark > 0m ? mark : entry);
            var pct = notional > 0m ? unrealized / notional * 100m : 0m;
            rows.Add(new VenuePosition(
                symbol,
                qty,
                notional,
                leverage <= 0 ? 1m : leverage,
                entry,
                mark,
                pct,
                unrealized,
                0m,
                MarginModeText.ParseOrDefault(TryReadString(item, "marginType"), MarginMode.Unknown)));
        }

        return rows;
    }

    private static List<VenueBalance> ParseBalancesFromAccount(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out var assetsNode) || assetsNode.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var rows = new List<VenueBalance>();
        foreach (var item in assetsNode.EnumerateArray())
        {
            var asset = (TryReadString(item, "asset") ?? string.Empty).ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(asset))
            {
                continue;
            }

            var qty = ParseDecimal(item, "walletBalance");
            if (qty == 0m)
            {
                qty = ParseDecimal(item, "availableBalance");
            }

            var availableQty = ParseDecimal(item, "availableBalance");
            if (availableQty <= 0m)
            {
                availableQty = ParseDecimal(item, "withdrawAvailable");
            }
            if (availableQty <= 0m)
            {
                availableQty = qty;
            }

            decimal usd;
            decimal availableUsd;
            if (asset is "USDT" or "USDC" or "USD")
            {
                usd = qty;
                availableUsd = availableQty;
            }
            else
            {
                usd = ParseDecimal(item, "marginBalance");
                if (usd <= 0m)
                {
                    usd = ParseDecimal(item, "crossWalletBalance");
                }

                availableUsd = usd;
            }

            if (qty <= 0m)
            {
                continue;
            }

            rows.Add(new VenueBalance(
                asset,
                qty,
                Math.Max(0m, usd),
                availableQty,
                Math.Max(0m, availableUsd)));
        }

        return rows;
    }

    private async Task<ClientWebSocket> ConnectWebSocketAsync(string stream, CancellationToken cancellationToken)
    {
        var attempts = new List<(string Base, bool IsFallback)> { (_wsBasePrimary, false) };
        if (!string.IsNullOrWhiteSpace(_wsBaseFallback))
        {
            attempts.Add((_wsBaseFallback!, true));
        }

        Exception? last = null;
        foreach (var attempt in attempts)
        {
            var ws = new ClientWebSocket();
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            var url = $"{attempt.Base}/{stream}";
            try
            {
                await ws.ConnectAsync(new Uri(url), cancellationToken);
                if (attempt.IsFallback)
                {
                    _logger.Warn("Aster", $"WS primary unavailable; using fallback={attempt.Base}");
                }

                return ws;
            }
            catch (Exception ex)
            {
                ws.Dispose();
                last = ex;
                _logger.Warn("Aster", $"WS connect failed url={url}, msg={ex.Message}");
            }
        }

        throw new InvalidOperationException($"Aster WS connect failed: {last?.Message}", last);
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
            _logger.Error("Aster", "WS receive loop failed", ex);
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
            var evt = TryReadString(root, "e");
            if (!string.Equals(evt, "aggTrade", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var price = ParseDecimal(root, "p");
            var size = ParseDecimal(root, "q");
            if (price <= 0 || size <= 0)
            {
                return;
            }

            var ts = root.TryGetProperty("T", out var tsProp) && tsProp.ValueKind == JsonValueKind.Number
                ? DateTimeOffset.FromUnixTimeMilliseconds(tsProp.GetInt64())
                : DateTimeOffset.UtcNow;

            _channel.Writer.TryWrite(new TradeTick(ts, price, size));
        }
        catch
        {
            // Ignore malformed WS messages to keep stream alive.
        }
    }

    private async Task<(bool Ok, string Body, JsonElement Root)> SendSignedAsync(HttpMethod method, string path, Dictionary<string, object> parameters, CancellationToken cancellationToken)
    {
        var variants = _selectedSigningVariant.HasValue
            ? SigningVariants.Where(x => x.Id == _selectedSigningVariant.Value).ToArray()
            : SigningVariants;

        foreach (var variant in variants)
        {
            using var bodyDoc = BuildSignedBody(parameters, variant);
            var formParams = ToFlatStringMap(bodyDoc.RootElement);
            var encoded = BuildFormContent(formParams);
            var url = _restBase + path;

            using var req = method == HttpMethod.Get
                ? new HttpRequestMessage(method, url + "?" + encoded)
                : new HttpRequestMessage(method, url)
                {
                    Content = new StringContent(encoded, Encoding.UTF8, "application/x-www-form-urlencoded")
                };

            using var resp = await _httpClient.SendAsync(req, cancellationToken);
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                if (ShouldRetrySigningVariant(body))
                {
                    _logger.Warn("Aster", $"Signed request variant={DescribeVariant(variant)} failed and will retry method={method.Method}, path={path}, status={(int)resp.StatusCode}, body={Trim(body)}");
                    continue;
                }

                _logger.Warn("Aster", $"Signed request failed variant={DescribeVariant(variant)}, method={method.Method}, path={path}, status={(int)resp.StatusCode}, body={Trim(body)}");
                return (false, body, default);
            }

            using var json = JsonDocument.Parse(body);
            if (json.RootElement.ValueKind == JsonValueKind.Object &&
                json.RootElement.TryGetProperty("code", out var codeProp) &&
                codeProp.ValueKind == JsonValueKind.Number && codeProp.GetInt32() < 0)
            {
                if (ShouldRetrySigningVariant(body))
                {
                    _logger.Warn("Aster", $"Signed request variant={DescribeVariant(variant)} rejected and will retry method={method.Method}, path={path}, body={Trim(body)}");
                    continue;
                }

                _logger.Warn("Aster", $"Signed request rejected variant={DescribeVariant(variant)}, method={method.Method}, path={path}, body={Trim(body)}");
                return (false, body, json.RootElement.Clone());
            }

            if (_selectedSigningVariant != variant.Id)
            {
                _selectedSigningVariant = variant.Id;
                _logger.Info("Aster", $"Signed request selected variant={DescribeVariant(variant)}");
            }

            return (true, body, json.RootElement.Clone());
        }

        return (false, "{\"code\":-1000,\"msg\":\"Signature check failed\"}", default);
    }

    private JsonDocument BuildSignedBody(Dictionary<string, object> parameters, SigningVariant variant)
    {
        if (!HasAsterSignerCredentials())
        {
            throw new InvalidOperationException("Aster signer credentials are not configured.");
        }

        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in parameters)
        {
            if (pair.Value is null)
            {
                continue;
            }

            normalized[pair.Key] = ConvertToAsterValue(pair.Value);
        }

        normalized["recvWindow"] = "50000";
        normalized["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        var nonce = GetUnixMicrosecondsNow();

        var signingJson = SerializeSigningObject(normalized);
        var rawUser = RequireAddress(_credentials.AccountAddress, "AccountAddress");
        var rawSigner = RequireAddress(_credentials.WalletAddress, "WalletAddress");
        var user = variant.SwapUserSigner ? rawSigner : rawUser;
        var signer = variant.SwapUserSigner ? rawUser : rawSigner;
        var digestInput = BuildAbiEncodedSigningPayload(signingJson, user, signer, nonce);
        var digest = _keccak.CalculateHash(digestInput);
        var signature = SignDigest(digest, NormalizePrivateKey(_credentials.PrivateKey!));

        normalized["nonce"] = nonce.ToString(CultureInfo.InvariantCulture);
        normalized["user"] = user;
        normalized["signer"] = signer;
        normalized["signature"] = signature;

        var serialized = JsonSerializer.Serialize(normalized);
        return JsonDocument.Parse(serialized);
    }

    private static string BuildFormContent(Dictionary<string, string> values)
    {
        return string.Join("&", values.Select(pair =>
            Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
    }

    private static Dictionary<string, string> ToFlatStringMap(JsonElement root)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.ValueKind != JsonValueKind.Object)
        {
            return map;
        }

        foreach (var p in root.EnumerateObject())
        {
            map[p.Name] = p.Value.ValueKind switch
            {
                JsonValueKind.String => p.Value.GetString() ?? string.Empty,
                JsonValueKind.Number => p.Value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => p.Value.GetRawText()
            };
        }

        return map;
    }

    private static string ConvertToAsterValue(object value)
    {
        return value switch
        {
            string s => s,
            bool b => b ? "true" : "false",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
            _ => value.ToString() ?? string.Empty
        };
    }

    private static string SerializeSigningObject(Dictionary<string, string> values)
    {
        var ordered = new SortedDictionary<string, string>(values, StringComparer.Ordinal);
        var sb = new StringBuilder();
        sb.Append('{');
        var first = true;
        foreach (var pair in ordered)
        {
            if (!first)
            {
                sb.Append(',');
            }

            first = false;
            sb.Append(JsonSerializer.Serialize(pair.Key));
            sb.Append(':');
            sb.Append(JsonSerializer.Serialize(pair.Value));
        }

        sb.Append('}');
        return sb.ToString();
    }

    private static byte[] BuildAbiEncodedSigningPayload(string payload, string userAddress, string signerAddress, ulong nonce)
    {
        var encoder = new ABIEncode();
        return encoder.GetABIEncoded(
            new ABIValue("string", payload),
            new ABIValue("address", userAddress),
            new ABIValue("address", signerAddress),
            new ABIValue("uint256", new BigInteger(nonce)));
    }

    private static string SignDigest(byte[] digest, string privateKey)
    {
        var key = new EthECKey(privateKey);
        var signer = new EthereumMessageSigner();
        return signer.Sign(digest, key);
    }

    private void TryLogSignerKeyMatch()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_credentials.PrivateKey) || string.IsNullOrWhiteSpace(_credentials.WalletAddress))
            {
                return;
            }

            var key = new EthECKey(NormalizePrivateKey(_credentials.PrivateKey!));
            var derived = key.GetPublicAddress();
            if (!string.Equals(derived, _credentials.WalletAddress, StringComparison.OrdinalIgnoreCase))
            {
                _logger.Warn("Aster", $"Signer mismatch: walletAddress={MaskAddress(_credentials.WalletAddress)}, derivedFromPrivateKey={MaskAddress(derived)}");
            }
            else
            {
                _logger.Info("Aster", $"Signer key validated: {MaskAddress(derived)}");
            }
        }
        catch (Exception ex)
        {
            _logger.Warn("Aster", $"Signer key validation failed: {ex.Message}");
        }
    }

    private static string MaskAddress(string? address)
    {
        var v = (address ?? string.Empty).Trim();
        if (v.Length < 10)
        {
            return v;
        }

        return $"{v[..6]}...{v[^4..]}";
    }

    private static ulong GetUnixMicrosecondsNow()
    {
        var utc = DateTime.UtcNow;
        var epochTicks = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
        return (ulong)((utc.Ticks - epochTicks) / 10);
    }

    private static bool ShouldRetrySigningVariant(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        return body.Contains("Signature check failed", StringComparison.OrdinalIgnoreCase) ||
               body.Contains("No agent found", StringComparison.OrdinalIgnoreCase);
    }

    private static string DescribeVariant(SigningVariant variant)
    {
        return $"#{variant.Id}(swap={variant.SwapUserSigner})";
    }

    private bool HasAsterSignerCredentials()
    {
        return !string.IsNullOrWhiteSpace(_credentials.AccountAddress) &&
               !string.IsNullOrWhiteSpace(_credentials.WalletAddress) &&
               !string.IsNullOrWhiteSpace(_credentials.PrivateKey);
    }

    private static string RequireAddress(string? value, string name)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new InvalidOperationException($"{name} is required");
        }

        return trimmed;
    }

    private static string NormalizePrivateKey(string value)
    {
        var key = value.Trim();
        if (key.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            key = key[2..];
        }

        return key;
    }

    private static string NormalizeSymbol(string symbol)
    {
        var normalized = (symbol ?? string.Empty).Trim().ToUpperInvariant();
        return string.IsNullOrWhiteSpace(normalized) ? "BTCUSDT" : normalized;
    }

    private static (string Interval, int Factor) IntervalToAster(CandleInterval interval)
    {
        return interval switch
        {
            CandleInterval.M5 => ("5m", 1),
            CandleInterval.M10 => ("5m", 2),
            CandleInterval.M15 => ("15m", 1),
            CandleInterval.M30 => ("30m", 1),
            CandleInterval.H1 => ("1h", 1),
            CandleInterval.H2 => ("1h", 2),
            CandleInterval.H4 => ("4h", 1),
            CandleInterval.H6 => ("1h", 6),
            CandleInterval.H12 => ("1h", 12),
            CandleInterval.D1 => ("1d", 1),
            CandleInterval.D7 => ("1d", 7),
            CandleInterval.D30 => ("1d", 30),
            _ => ("5m", 1)
        };
    }

    private static CandleInterval BaseIntervalFromAster(string interval)
    {
        return interval switch
        {
            "5m" => CandleInterval.M5,
            "15m" => CandleInterval.M15,
            "30m" => CandleInterval.M30,
            "1h" => CandleInterval.H1,
            "4h" => CandleInterval.H4,
            "1d" => CandleInterval.D1,
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

        var grouped = baseCandles
            .GroupBy(c => BucketStart(c.OpenTime, span))
            .OrderBy(g => g.Key);

        var result = new List<Candle>();
        foreach (var g in grouped)
        {
            var candles = g.OrderBy(x => x.OpenTime).ToList();
            var first = candles[0];
            var last = candles[^1];
            result.Add(new Candle(
                first.VenueId,
                first.Symbol,
                target,
                g.Key,
                first.Open,
                candles.Max(x => x.High),
                candles.Min(x => x.Low),
                last.Close,
                candles.Sum(x => x.Volume),
                true));
        }

        return result;
    }

    private static DateTimeOffset BucketStart(DateTimeOffset ts, TimeSpan span)
    {
        var utc = ts.ToUniversalTime();
        var ticks = utc.Ticks - (utc.Ticks % span.Ticks);
        return new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    private static decimal ParseDecimal(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var prop))
        {
            return 0m;
        }

        return ParseDecimal(prop);
    }

    private static decimal ParseDecimal(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var n))
        {
            return n;
        }

        if (value.ValueKind == JsonValueKind.String &&
            decimal.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var s))
        {
            return s;
        }

        return 0m;
    }

    private static string? TryReadString(JsonElement obj, string name)
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

    private static int? TryReadInt(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var prop))
        {
            return null;
        }

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var n))
        {
            return n;
        }

        if (prop.ValueKind == JsonValueKind.String &&
            int.TryParse(prop.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static bool ParseBool(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var prop))
        {
            return false;
        }

        return prop.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(prop.GetString(), out var b) && b,
            _ => false
        };
    }

    private static string Trim(string value)
    {
        return value.Length > 300 ? value[..300] : value;
    }

    private readonly record struct SigningVariant(
        int Id,
        bool SwapUserSigner);

    private readonly record struct AsterSymbolRule(
        string Symbol,
        decimal? QuantityStep,
        decimal? PriceTick,
        int? QuantityPrecision,
        int? PricePrecision);
}
