using AiyoPerps.Core;
using AiyoPerps.Models;
using System;
using System.Buffers;
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

public sealed class BitMexVenueAdapter : IPerpVenue, IHistoricalCandleProvider, IAccountStateProvider
{
    private readonly string _restBase;
    private readonly string _wsBase;
    private readonly AccountCredentials _credentials;
    private readonly AppLogger _logger;
    private readonly HttpClient _httpClient = new();
    private readonly Channel<MarketEvent> _channel = Channel.CreateUnbounded<MarketEvent>();
    private readonly Dictionary<string, (decimal Price, DateTimeOffset At)> _assetUsdPriceCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _unknownBalanceAssetLogged = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _assetScaleGate = new(1, 1);
    private readonly SemaphoreSlim _instrumentSpecGate = new(1, 1);
    private static readonly TimeSpan WsReconnectDelay = TimeSpan.FromSeconds(2);

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _wsCts;
    private Task? _wsTask;
    private string? _subscribedSymbol;
    private Dictionary<string, int> _assetScaleByCurrency = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _assetScaleFetchedAt = DateTimeOffset.MinValue;
    private readonly Dictionary<string, (BitMexInstrumentSpec Spec, DateTimeOffset At)> _instrumentSpecCache = new(StringComparer.OrdinalIgnoreCase);

    public BitMexVenueAdapter(string environment, AccountCredentials credentials, AppLogger logger)
    {
        _credentials = credentials;
        _logger = logger;

        var isTestnet = string.Equals(environment, "testnet", StringComparison.OrdinalIgnoreCase);
        _restBase = isTestnet ? "https://testnet.bitmex.com" : "https://www.bitmex.com";
        _wsBase = isTestnet ? "wss://ws.testnet.bitmex.com/realtime" : "wss://ws.bitmex.com/realtime";

        _logger.Info("BitMEX", $"Adapter created. env={environment}, rest={_restBase}");
    }

    public string VenueId => "BitMEX";

    public async Task ConnectMarketDataAsync(IEnumerable<string> subscriptions, CancellationToken cancellationToken = default)
    {
        await DisconnectMarketDataAsync(cancellationToken);

        var symbol = (subscriptions.FirstOrDefault() ?? "XBTUSD").Trim().ToUpperInvariant();
        _subscribedSymbol = symbol;
        _wsCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        await EnsureWebSocketConnectedAsync(symbol, _wsCts.Token);
        _wsTask = Task.Run(() => ReceiveLoopAsync(symbol, _wsCts.Token), _wsCts.Token);
    }

    public async Task DisconnectMarketDataAsync(CancellationToken cancellationToken = default)
    {
        _logger.Info("BitMEX", "DisconnectMarketDataAsync called");

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
                    _logger.Info("BitMEX", "WS closed normally");
                }
                catch (Exception ex)
                {
                    _logger.Warn("BitMEX", $"WS close exception: {ex.Message}");
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
                _logger.Warn("BitMEX", $"WS task end with exception: {ex.Message}");
            }

            _wsTask = null;
        }

        _wsCts?.Dispose();
        _wsCts = null;
        _subscribedSymbol = null;
    }

    public async Task<(bool IsSuccess, string Message)> ConfigureLeverageAsync(string symbol, decimal leverage, MarginMode marginMode, CancellationToken cancellationToken = default)
    {
        if (marginMode == MarginMode.Unknown)
        {
            _logger.Info("BitMEX", $"ConfigureLeverage skipped symbol={symbol}, leverage={leverage}, marginMode=unknown");
            return (true, "BitMEX leverage configuration skipped");
        }

        var normalizedSymbol = (symbol ?? string.Empty).Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(normalizedSymbol))
        {
            return (false, "BitMEX symbol is required");
        }

        var normalizedLeverage = Math.Max(1m, decimal.Round(leverage, 2, MidpointRounding.AwayFromZero));
        var existingState = await TryGetExistingSymbolMarginStateAsync(normalizedSymbol, cancellationToken);
        if (existingState is not null &&
            existingState.HasExposureOrOrder &&
            existingState.MarginMode == marginMode &&
            IsSameLeverage(existingState.Leverage, normalizedLeverage))
        {
            _logger.Info("BitMEX", $"ConfigureLeverage skipped symbol={normalizedSymbol}, leverage={normalizedLeverage}, marginMode={marginMode.ToApiValue()}, reason=already-configured");
            return (true, "BitMEX leverage configuration skipped");
        }

        var path = marginMode == MarginMode.Isolated
            ? "/api/v1/position/leverage"
            : "/api/v1/position/crossLeverage";
        var payload = JsonSerializer.Serialize(new
        {
            symbol = normalizedSymbol,
            leverage = normalizedLeverage
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, _restBase + path)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        ApplyAuthHeaders(request, HttpMethod.Post.Method, path, payload);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.Warn("BitMEX", $"ConfigureLeverage failed symbol={normalizedSymbol}, leverage={normalizedLeverage}, marginMode={marginMode.ToApiValue()}, status={(int)response.StatusCode}, body={Trim(body)}");
            return (false, NormalizeBitMexMarginModeError(body, marginMode, (int)response.StatusCode));
        }

        _logger.Info("BitMEX", $"ConfigureLeverage applied symbol={normalizedSymbol}, leverage={normalizedLeverage}, marginMode={marginMode.ToApiValue()}");
        return (true, "ok");
    }

    public Task<OrderAck> PlaceOrderAsync(string symbol, string side, decimal qty, decimal? price, CancellationToken cancellationToken = default)
    {
        return PlaceOrderCoreAsync(symbol, side, qty, price, reduceOnly: false, quantityIsContracts: false, cancellationToken);
    }

    public Task<OrderAck> PlaceCloseOrderAsync(string symbol, string side, decimal positionQty, decimal? price, CancellationToken cancellationToken = default)
    {
        return PlaceOrderCoreAsync(symbol, side, positionQty, price, reduceOnly: true, quantityIsContracts: true, cancellationToken);
    }

    public async Task<OrderAck> CancelOrderAsync(string symbol, string orderId, CancellationToken cancellationToken = default)
    {
        if (!_credentials.HasApiCredentials)
        {
            _logger.Warn("BitMEX", $"CancelOrder rejected: missing API credentials symbol={symbol}, orderId={orderId}");
            return new OrderAck(DateTimeOffset.UtcNow, string.Empty, false, "BitMEX API credentials are required");
        }

        if (string.IsNullOrWhiteSpace(orderId))
        {
            return new OrderAck(DateTimeOffset.UtcNow, string.Empty, false, "orderId is required");
        }

        var path = "/api/v1/order";
        var payload = JsonSerializer.Serialize(new { orderID = orderId });
        using var request = new HttpRequestMessage(HttpMethod.Delete, _restBase + path)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        ApplyAuthHeaders(request, HttpMethod.Delete.Method, path, payload);

        _logger.Info("BitMEX", $"CancelOrder start symbol={symbol}, orderId={orderId}");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.Error("BitMEX", $"CancelOrder failed status={(int)response.StatusCode}, body={Trim(body)}");
            return new OrderAck(DateTimeOffset.UtcNow, orderId, false, body);
        }

        _logger.Info("BitMEX", $"CancelOrder success orderId={orderId}");
        return new OrderAck(DateTimeOffset.UtcNow, orderId, true, "ok");
    }

    public async Task<(bool IsSuccess, string Message)> ValidateConnectionAsync(CancellationToken cancellationToken = default)
    {
        _logger.Info("BitMEX", "ValidateConnection start");

        var ping = await _httpClient.GetAsync(_restBase + "/api/v1/instrument?symbol=XBTUSD&count=1", cancellationToken);
        if (!ping.IsSuccessStatusCode)
        {
            _logger.Error("BitMEX", $"Public ping failed status={(int)ping.StatusCode}");
            return (false, $"BitMEX public ping failed: {(int)ping.StatusCode}");
        }

        _logger.Info("BitMEX", "Public ping ok");

        if (!_credentials.HasApiCredentials)
        {
            _logger.Warn("BitMEX", "No API credentials; auth check skipped");
            return (true, "BitMEX public connection ok (no API key provided)");
        }

        var path = "/api/v1/user";
        var req = new HttpRequestMessage(HttpMethod.Get, _restBase + path);
        ApplyAuthHeaders(req, HttpMethod.Get.Method, path, string.Empty);
        using var authResp = await _httpClient.SendAsync(req, cancellationToken);
        if (!authResp.IsSuccessStatusCode)
        {
            var body = await authResp.Content.ReadAsStringAsync(cancellationToken);
            _logger.Error("BitMEX", $"Auth check failed status={(int)authResp.StatusCode}, body={body}");
            return (false, $"BitMEX auth failed: {body}");
        }

        _logger.Info("BitMEX", "Auth check ok");
        return (true, "BitMEX auth ok");
    }

    private async Task<OrderAck> PlaceOrderCoreAsync(
        string symbol,
        string side,
        decimal qty,
        decimal? price,
        bool reduceOnly,
        bool quantityIsContracts,
        CancellationToken cancellationToken)
    {
        _logger.Info("BitMEX", $"PlaceOrder start symbol={symbol}, side={side}, qty={qty}, price={(price?.ToString() ?? "MKT")}, reduceOnly={reduceOnly}, qtyIsContracts={quantityIsContracts}");

        if (!_credentials.HasApiCredentials)
        {
            _logger.Warn("BitMEX", "PlaceOrder rejected: missing API credentials");
            return new OrderAck(DateTimeOffset.UtcNow, string.Empty, false, "BitMEX API credentials are required");
        }

        var normalizedSymbol = symbol.Trim().ToUpperInvariant();
        var instrument = await GetInstrumentSpecAsync(normalizedSymbol, cancellationToken);
        var orderQty = quantityIsContracts
            ? ConvertPositionQtyToOrderQty(qty, instrument)
            : ConvertBaseSizeToOrderQty(qty, price, instrument);
        if (orderQty <= 0)
        {
            _logger.Warn("BitMEX", $"PlaceOrder rejected: computed orderQty<=0 symbol={normalizedSymbol}, qty={qty}, px={price}, inverse={instrument.IsInverse}, utp={instrument.UnderlyingToPositionMultiplier}, lot={instrument.LotSize}, reduceOnly={reduceOnly}, qtyIsContracts={quantityIsContracts}");
            return new OrderAck(DateTimeOffset.UtcNow, string.Empty, false, "Computed order quantity is invalid");
        }

        _logger.Info("BitMEX", $"PlaceOrder converted symbol={normalizedSymbol}, qty={qty}, orderQty={orderQty}, inverse={instrument.IsInverse}, utp={instrument.UnderlyingToPositionMultiplier}, lot={instrument.LotSize}, reduceOnly={reduceOnly}");
        var path = "/api/v1/order";
        var payload = BuildOrderPayload(normalizedSymbol, side, orderQty, price, reduceOnly);
        using var request = new HttpRequestMessage(HttpMethod.Post, _restBase + path)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        ApplyAuthHeaders(request, HttpMethod.Post.Method, path, payload);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.Error("BitMEX", $"PlaceOrder failed status={(int)response.StatusCode}, body={Trim(body)}");
            return new OrderAck(DateTimeOffset.UtcNow, string.Empty, false, body);
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var orderId = root.TryGetProperty("orderID", out var oid) ? oid.GetString() ?? string.Empty : string.Empty;
        _logger.Info("BitMEX", $"PlaceOrder success orderId={orderId}, symbol={normalizedSymbol}, orderQty={orderQty}, reduceOnly={reduceOnly}");
        return new OrderAck(DateTimeOffset.UtcNow, orderId, true, "ok");
    }

    public async Task<IReadOnlyList<Candle>> GetRecentCandlesAsync(string symbol, CandleInterval interval, int count, CancellationToken cancellationToken = default)
    {
        var (binSize, factor) = IntervalToBitMex(interval);
        var fetchCount = Math.Max(50, count * factor + factor);
        var path = $"/api/v1/trade/bucketed?binSize={binSize}&partial=false&symbol={symbol}&count={fetchCount}&reverse=true";
        var url = _restBase + path;
        _logger.Info("BitMEX", $"Fetch historical candles: {url}, targetInterval={interval}, factor={factor}, targetCount={count}");

        using var resp = await _httpClient.GetAsync(url, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.Error("BitMEX", $"Fetch historical candles failed status={(int)resp.StatusCode}, body={body}");
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
            if (!item.TryGetProperty("timestamp", out var ts) ||
                !item.TryGetProperty("open", out var o) ||
                !item.TryGetProperty("high", out var h) ||
                !item.TryGetProperty("low", out var l) ||
                !item.TryGetProperty("close", out var c) ||
                !item.TryGetProperty("volume", out var v))
            {
                continue;
            }

            baseCandles.Add(new Candle(
                VenueId,
                symbol,
                BaseIntervalFromBitMex(binSize),
                ts.GetDateTimeOffset(),
                o.GetDecimal(),
                h.GetDecimal(),
                l.GetDecimal(),
                c.GetDecimal(),
                v.GetDecimal(),
                true));
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
        _logger.Info("BitMEX", $"Fetched historical candles base={sorted.Count}, resampled={resampled.Count}, returned={finalList.Count}, interval={interval}");
        return finalList;
    }

    public Task<VenueAccountSnapshot> GetAccountSnapshotAsync(CancellationToken cancellationToken = default)
    {
        return GetAccountSnapshotAsync(AccountSnapshotSections.All, cancellationToken);
    }

    public async Task<VenueAccountSnapshot> GetAccountSnapshotAsync(AccountSnapshotSections sections, CancellationToken cancellationToken = default)
    {
        if (!_credentials.HasApiCredentials)
        {
            return new VenueAccountSnapshot(DateTimeOffset.UtcNow, [], [], []);
        }

        if (sections == AccountSnapshotSections.None)
        {
            return new VenueAccountSnapshot(DateTimeOffset.UtcNow, [], [], []);
        }

        try
        {
            var requiresXbtUsd = sections.HasFlag(AccountSnapshotSections.Positions) || sections.HasFlag(AccountSnapshotSections.Balances);
            var xbtUsd = requiresXbtUsd
                ? await FetchXbtUsdPriceAsync(cancellationToken)
                : 0m;
            var positions = sections.HasFlag(AccountSnapshotSections.Positions)
                ? await FetchPositionsAsync(xbtUsd, cancellationToken)
                : [];
            var openOrders = sections.HasFlag(AccountSnapshotSections.Orders)
                ? await FetchOpenOrdersAsync(cancellationToken)
                : [];
            var balances = sections.HasFlag(AccountSnapshotSections.Balances)
                ? await FetchBalancesAsync(xbtUsd, cancellationToken)
                : [];

            return new VenueAccountSnapshot(DateTimeOffset.UtcNow, positions, openOrders, balances);
        }
        catch (Exception ex)
        {
            _logger.Error("BitMEX", "GetAccountSnapshot failed", ex);
            return new VenueAccountSnapshot(DateTimeOffset.UtcNow, [], [], []);
        }
    }

    public IAsyncEnumerable<MarketEvent> MarketEvents(CancellationToken cancellationToken = default)
    {
        return _channel.Reader.ReadAllAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _logger.Info("BitMEX", "DisposeAsync called");
        await DisconnectMarketDataAsync(CancellationToken.None);
        _assetScaleGate.Dispose();
        _instrumentSpecGate.Dispose();
        _httpClient.Dispose();
    }

    private async Task ReceiveLoopAsync(string symbol, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        var messageCount = 0;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var ws = _ws;
                if (ws is null || ws.State != WebSocketState.Open)
                {
                    await EnsureWebSocketConnectedAsync(symbol, cancellationToken);
                    ws = _ws;
                    if (ws is null)
                    {
                        await Task.Delay(WsReconnectDelay, cancellationToken);
                        continue;
                    }
                }

                var shouldReconnect = false;
                try
                {
                    while (!cancellationToken.IsCancellationRequested &&
                           ReferenceEquals(ws, _ws) &&
                           ws.State == WebSocketState.Open)
                    {
                        using var payload = new MemoryStream();
                        WebSocketReceiveResult result;
                        do
                        {
                            result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                            if (result.MessageType == WebSocketMessageType.Close)
                            {
                                _logger.Warn("BitMEX", "WS received close frame");
                                shouldReconnect = true;
                                break;
                            }

                            if (result.Count > 0)
                            {
                                payload.Write(buffer, 0, result.Count);
                            }
                        }
                        while (!result.EndOfMessage);

                        if (shouldReconnect)
                        {
                            break;
                        }

                        if (payload.Length == 0)
                        {
                            continue;
                        }

                        var json = Encoding.UTF8.GetString(payload.GetBuffer(), 0, (int)payload.Length);
                        ParseMessage(json);
                        _channel.Writer.TryWrite(new VenueHeartbeat(DateTimeOffset.UtcNow, "ws_message"));

                        messageCount++;
                        if (messageCount % 50 == 0)
                        {
                            _logger.Info("BitMEX", $"WS processed messages={messageCount}");
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.Info("BitMEX", "WS receive loop canceled");
                    break;
                }
                catch (Exception ex)
                {
                    shouldReconnect = true;
                    _logger.Error("BitMEX", "WS receive loop exception", ex);
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if (!shouldReconnect)
                {
                    shouldReconnect = ws.State != WebSocketState.Open || !ReferenceEquals(ws, _ws);
                }

                if (!shouldReconnect)
                {
                    continue;
                }

                if (ReferenceEquals(_ws, ws))
                {
                    try
                    {
                        ws.Dispose();
                    }
                    catch
                    {
                    }

                    _ws = null;
                }

                _logger.Warn("BitMEX", $"WS reconnect scheduled symbol={symbol}, delayMs={(int)WsReconnectDelay.TotalMilliseconds}");
                await Task.Delay(WsReconnectDelay, cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            _logger.Info("BitMEX", "WS receive loop exited");
        }
    }

    private async Task EnsureWebSocketConnectedAsync(string symbol, CancellationToken cancellationToken)
    {
        if (_ws is { State: WebSocketState.Open })
        {
            return;
        }

        var url = $"{_wsBase}?subscribe=trade:{symbol},instrument:{symbol}";
        _logger.Info("BitMEX", $"Connecting WS: {url}");

        var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);

        try
        {
            await ws.ConnectAsync(new Uri(url), cancellationToken);
        }
        catch
        {
            ws.Dispose();
            throw;
        }

        _ws = ws;
        _logger.Info("BitMEX", $"WS connected symbol={symbol}");
    }

    private async Task<IReadOnlyList<VenuePosition>> FetchPositionsAsync(decimal xbtUsd, CancellationToken cancellationToken)
    {
        var filter = Uri.EscapeDataString("{\"isOpen\":true}");
        var root = await SendAuthedGetJsonAsync($"/api/v1/position?filter={filter}&count=200", cancellationToken);
        if (root.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var output = new List<VenuePosition>();
        foreach (var item in root.EnumerateArray())
        {
            var symbol = ReadString(item, "symbol");
            if (string.IsNullOrWhiteSpace(symbol))
            {
                continue;
            }

            var qty = ReadDecimal(item, "currentQty");
            var entryPrice = ReadDecimal(item, "avgEntryPrice");
            var markPrice = ReadDecimal(item, "markPrice");
            if (markPrice <= 0)
            {
                markPrice = entryPrice;
            }

            var notionalUsd = Math.Abs(ReadDecimal(item, "foreignNotional"));
            if (notionalUsd <= 0 && qty != 0 && markPrice > 0)
            {
                notionalUsd = Math.Abs(qty * markPrice);
            }

            var leverage = Math.Abs(ReadDecimal(item, "leverage"));
            if (leverage <= 0)
            {
                var initMarginReq = ReadDecimal(item, "initMarginReq");
                if (initMarginReq > 0)
                {
                    leverage = Math.Round(1m / initMarginReq, 2, MidpointRounding.AwayFromZero);
                }
            }

            var settleCurrency = ReadString(item, "settlCurrency")
                ?? ReadString(item, "quoteCurrency")
                ?? "XBT";
            var unrealizedRaw = ReadDecimal(item, "unrealisedPnl");
            var unrealizedUsd = unrealizedRaw != 0
                ? ConvertBitmexRawToUsd(unrealizedRaw, settleCurrency, xbtUsd)
                : 0m;
            if (unrealizedUsd == 0m)
            {
                unrealizedUsd = ReadDecimal(item, "funding");
            }

            var unrealizedPct = NormalizePct(ReadDecimal(item, "unrealisedPnlPcnt"));
            if (unrealizedPct == 0m && unrealizedUsd != 0m && notionalUsd > 0m)
            {
                unrealizedPct = (unrealizedUsd / notionalUsd) * 100m;
            }

            var realizedPct = NormalizePct(ReadDecimal(item, "realisedPnlPcnt"));
            var realizedUsd = 0m;
            var realizedRaw = ReadDecimal(item, "realisedPnl");
            if (realizedRaw != 0)
            {
                realizedUsd = ConvertBitmexRawToUsd(realizedRaw, settleCurrency, xbtUsd);
            }
            else if (realizedPct != 0 && notionalUsd > 0)
            {
                realizedUsd = notionalUsd * (realizedPct / 100m);
            }

            var marginMode = item.TryGetProperty("crossMargin", out var crossMarginElement) && crossMarginElement.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? (ReadBool(item, "crossMargin") ? MarginMode.Cross : MarginMode.Isolated)
                : MarginMode.Unknown;

            output.Add(new VenuePosition(
                symbol.ToUpperInvariant(),
                qty,
                notionalUsd,
                leverage,
                entryPrice,
                markPrice,
                unrealizedPct,
                unrealizedUsd,
                realizedUsd,
                marginMode));
        }

        return output;
    }

    private async Task<IReadOnlyList<VenueOpenOrder>> FetchOpenOrdersAsync(CancellationToken cancellationToken)
    {
        var filter = Uri.EscapeDataString("{\"open\":true}");
        var root = await SendAuthedGetJsonAsync($"/api/v1/order?filter={filter}&count=200&reverse=true", cancellationToken);
        if (root.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var output = new List<VenueOpenOrder>();
        foreach (var item in root.EnumerateArray())
        {
            var symbol = ReadString(item, "symbol");
            if (string.IsNullOrWhiteSpace(symbol))
            {
                continue;
            }

            var orderQty = Math.Abs(ReadDecimal(item, "leavesQty"));
            if (orderQty <= 0)
            {
                orderQty = Math.Abs(ReadDecimal(item, "orderQty"));
            }

            var price = ReadDecimal(item, "price");
            var normalizedSymbol = symbol.ToUpperInvariant();
            BitMexInstrumentSpec? instrument = null;
            try
            {
                instrument = await GetInstrumentSpecAsync(normalizedSymbol, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.Warn("BitMEX", $"FetchOpenOrders instrument lookup warning symbol={normalizedSymbol}: {ex.Message}");
            }

            var simpleQty = Math.Abs(ReadDecimal(item, "simpleLeavesQty"));
            if (simpleQty <= 0)
            {
                simpleQty = Math.Abs(ReadDecimal(item, "simpleOrderQty"));
            }

            var notionalUsd = orderQty;
            if (simpleQty > 0 && price > 0)
            {
                notionalUsd = simpleQty * price;
            }
            else if (IsInverseUsdSymbol(normalizedSymbol))
            {
                notionalUsd = orderQty;
            }
            else if (instrument is not null && instrument.UnderlyingToPositionMultiplier > 0 && price > 0)
            {
                notionalUsd = (orderQty / instrument.UnderlyingToPositionMultiplier) * price;
            }
            else if (price > 0)
            {
                notionalUsd = orderQty * price;
            }

            var status = ReadString(item, "ordStatus");
            if (string.IsNullOrWhiteSpace(status))
            {
                status = "Open";
            }
            var orderId = ReadString(item, "orderID");
            if (string.IsNullOrWhiteSpace(orderId))
            {
                orderId = ReadString(item, "clOrdID");
            }

            output.Add(new VenueOpenOrder(
                normalizedSymbol,
                notionalUsd,
                0m,
                price > 0 ? price : null,
                status,
                orderId,
                item.TryGetProperty("crossMargin", out var crossMarginElement) && crossMarginElement.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? (ReadBool(item, "crossMargin") ? MarginMode.Cross : MarginMode.Isolated)
                    : MarginMode.Unknown));
        }

        return output;
    }

    private async Task<IReadOnlyList<VenueBalance>> FetchBalancesAsync(decimal xbtUsd, CancellationToken cancellationToken)
    {
        var root = await SendAuthedGetJsonAsync("/api/v1/user/margin?currency=all", cancellationToken);
        var rows = new List<VenueBalance>();
        var scaleByCurrency = await GetBitMexAssetScaleMapAsync(cancellationToken);
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                var balance = await TryBuildBitMexBalanceAsync(item, xbtUsd, scaleByCurrency, cancellationToken);
                if (balance is not null)
                {
                    rows.Add(balance);
                }
            }
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            var balance = await TryBuildBitMexBalanceAsync(root, xbtUsd, scaleByCurrency, cancellationToken);
            if (balance is not null)
            {
                rows.Add(balance);
            }
        }

        // BitMEX can include a synthetic USD summary row together with stable-coin wallet rows.
        // Prefer real wallet currencies in that case to avoid duplicate USD-like entries.
        if (rows.Any(x => string.Equals(x.Asset, "USDT", StringComparison.OrdinalIgnoreCase) || string.Equals(x.Asset, "USDC", StringComparison.OrdinalIgnoreCase)) &&
            rows.Any(x => string.Equals(x.Asset, "USD", StringComparison.OrdinalIgnoreCase)))
        {
            rows = rows.Where(x => !string.Equals(x.Asset, "USD", StringComparison.OrdinalIgnoreCase)).ToList();
        }

        var merged = rows
            .GroupBy(x => x.Asset, StringComparer.OrdinalIgnoreCase)
            .Select(g => new VenueBalance(
                g.Key.ToUpperInvariant(),
                g.Sum(x => x.Quantity),
                g.Sum(x => x.UsdValue)))
            .Where(ShouldIncludeBalanceRow)
            .ToList();

        return merged;
    }

    private async Task<decimal> FetchXbtUsdPriceAsync(CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, _restBase + "/api/v1/instrument?symbol=XBTUSD&count=1&reverse=true");
        using var resp = await _httpClient.SendAsync(req, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            return 0m;
        }

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
        {
            return 0m;
        }

        var row = doc.RootElement[0];
        return ReadDecimal(row, "lastPrice");
    }

    private async Task<JsonElement> SendAuthedGetJsonAsync(string pathAndQuery, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, _restBase + pathAndQuery);
        ApplyAuthHeaders(req, HttpMethod.Get.Method, pathAndQuery, string.Empty);
        using var resp = await _httpClient.SendAsync(req, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"BitMEX GET {pathAndQuery} failed {(int)resp.StatusCode}: {Trim(body)}");
        }

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }

    private async Task<VenueBalance?> TryBuildBitMexBalanceAsync(
        JsonElement item,
        decimal xbtUsd,
        IReadOnlyDictionary<string, int> scaleByCurrency,
        CancellationToken cancellationToken)
    {
        var rawCurrency = ReadString(item, "currency");
        if (string.IsNullOrWhiteSpace(rawCurrency))
        {
            return null;
        }

        var rawWallet = ReadDecimal(item, "walletBalance");
        var rawMargin = ReadDecimal(item, "marginBalance");
        var rawValue = rawWallet != 0 ? rawWallet : rawMargin;
        var explicitScale = ReadInt(item, "scale") ?? ReadInt(item, "currencyScale");
        var lookupScale = scaleByCurrency.TryGetValue(rawCurrency.Trim().ToUpperInvariant(), out var resolved) ? resolved : (int?)null;
        var scale = explicitScale ?? lookupScale;
        var quantity = ConvertBitmexRawToAsset(rawValue, rawCurrency, scale);
        var asset = NormalizeBitmexAsset(rawCurrency);
        var usd = 0m;
        if (IsStableAsset(asset))
        {
            usd = quantity;
        }
        else if (asset == "BTC")
        {
            usd = quantity * xbtUsd;
        }
        else
        {
            var assetUsd = await FetchAssetUsdPriceAsync(asset, cancellationToken);
            if (assetUsd > 0)
            {
                usd = quantity * assetUsd;
            }
            else if (_unknownBalanceAssetLogged.Add(asset))
            {
                _logger.Warn("BitMEX", $"Balance asset USD price unavailable: asset={asset}, rawCurrency={rawCurrency}, scale={scale}, raw={rawValue}");
            }
        }

        _logger.Info("BitMEX", $"Balance row currency={rawCurrency}, asset={asset}, raw={rawValue}, scale={scale?.ToString() ?? "n/a"}, qty={quantity}, usd={usd}");
        return new VenueBalance(asset, quantity, usd);
    }

    private async Task<IReadOnlyDictionary<string, int>> GetBitMexAssetScaleMapAsync(CancellationToken cancellationToken)
    {
        if (_assetScaleByCurrency.Count > 0 && DateTimeOffset.UtcNow - _assetScaleFetchedAt < TimeSpan.FromMinutes(30))
        {
            return _assetScaleByCurrency;
        }

        await _assetScaleGate.WaitAsync(cancellationToken);
        try
        {
            if (_assetScaleByCurrency.Count > 0 && DateTimeOffset.UtcNow - _assetScaleFetchedAt < TimeSpan.FromMinutes(30))
            {
                return _assetScaleByCurrency;
            }

            var next = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var root = await SendAuthedGetJsonAsync("/api/v1/wallet/assets", cancellationToken);
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in root.EnumerateArray())
                {
                    var currency = ReadString(row, "currency");
                    var scale = ReadInt(row, "scale");
                    if (string.IsNullOrWhiteSpace(currency) || scale is null)
                    {
                        continue;
                    }

                    next[currency.Trim().ToUpperInvariant()] = scale.Value;
                }
            }

            if (next.Count > 0)
            {
                _assetScaleByCurrency = next;
                _assetScaleFetchedAt = DateTimeOffset.UtcNow;
                _logger.Info("BitMEX", $"Loaded wallet asset scales rows={next.Count}");
            }
            else
            {
                _logger.Warn("BitMEX", "wallet/assets returned no scale rows");
            }

            return _assetScaleByCurrency;
        }
        catch (Exception ex)
        {
            _logger.Warn("BitMEX", $"Load wallet asset scales failed: {ex.Message}");
            return _assetScaleByCurrency;
        }
        finally
        {
            _assetScaleGate.Release();
        }
    }

    private async Task<decimal> FetchAssetUsdPriceAsync(string asset, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(asset) || asset == "USD" || asset == "BTC")
        {
            return 0m;
        }

        if (_assetUsdPriceCache.TryGetValue(asset, out var cached) &&
            DateTimeOffset.UtcNow - cached.At < TimeSpan.FromMinutes(1))
        {
            return cached.Price;
        }

        var candidates = new[] { $"{asset}USDT", $"{asset}USDC", $"{asset}USD" };
        foreach (var symbol in candidates)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, _restBase + $"/api/v1/instrument?symbol={symbol}&count=1&reverse=true");
                using var resp = await _httpClient.SendAsync(req, cancellationToken);
                var body = await resp.Content.ReadAsStringAsync(cancellationToken);
                if (!resp.IsSuccessStatusCode)
                {
                    continue;
                }

                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                {
                    continue;
                }

                var row = doc.RootElement[0];
                var px = ReadDecimal(row, "lastPrice");
                if (px <= 0)
                {
                    px = ReadDecimal(row, "markPrice");
                }

                if (px > 0)
                {
                    _assetUsdPriceCache[asset] = (px, DateTimeOffset.UtcNow);
                    return px;
                }
            }
            catch
            {
                // Ignore and try next candidate.
            }
        }

        _assetUsdPriceCache[asset] = (0m, DateTimeOffset.UtcNow);
        return 0m;
    }

    private static string NormalizeBitmexAsset(string currency)
    {
        if (string.IsNullOrWhiteSpace(currency))
        {
            return "UNKNOWN";
        }

        var upper = currency.Trim().ToUpperInvariant();
        return upper switch
        {
            "XBT" or "XBTT" or "XBTM" => "BTC",
            "XBTUSD" => "BTC",
            _ => upper
        };
    }

    private static decimal ConvertBitmexRawToAsset(decimal raw, string currency, int? scale = null)
    {
        if (scale is >= 0 and <= 18)
        {
            return raw / Pow10(scale.Value);
        }

        var upper = currency.Trim().ToUpperInvariant();
        if (upper.StartsWith("XBT", StringComparison.Ordinal))
        {
            return raw / 100_000_000m;
        }

        if (IsStableAsset(upper))
        {
            return raw / 1_000_000m;
        }

        if (string.Equals(upper, "BMEX", StringComparison.Ordinal))
        {
            return raw / 1_000_000m;
        }

        return raw;
    }

    private static decimal Pow10(int scale)
    {
        var result = 1m;
        for (var i = 0; i < scale; i++)
        {
            result *= 10m;
        }

        return result;
    }

    private static decimal ConvertBitmexRawToUsd(decimal raw, string currency, decimal xbtUsd)
    {
        var asset = NormalizeBitmexAsset(currency);
        var quantity = ConvertBitmexRawToAsset(raw, currency);
        if (IsStableAsset(asset))
        {
            return quantity;
        }

        return quantity * xbtUsd;
    }

    private static bool IsStableAsset(string asset)
    {
        if (string.IsNullOrWhiteSpace(asset))
        {
            return false;
        }

        return asset.Trim().ToUpperInvariant() switch
        {
            "USD" => true,
            "USDT" => true,
            "USDC" => true,
            "USDTT" => true,
            "USDTM" => true,
            "USDTF0" => true,
            "USDTF0:USTF0" => true,
            _ => false
        };
    }

    private static bool IsInverseUsdSymbol(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return false;
        }

        var upper = symbol.Trim().ToUpperInvariant();
        return upper.EndsWith("USD", StringComparison.Ordinal) &&
               !upper.EndsWith("USDT", StringComparison.Ordinal) &&
               !upper.EndsWith("USDC", StringComparison.Ordinal);
    }

    private bool ShouldIncludeBalanceRow(VenueBalance row)
    {
        if (row.Quantity == 0m)
        {
            return false;
        }

        var asset = row.Asset?.Trim().ToUpperInvariant() ?? string.Empty;
        if (IsStableAsset(asset) || asset is "BTC" or "BMEX")
        {
            return true;
        }

        if (row.UsdValue > 0m)
        {
            return true;
        }

        _logger.Info("BitMEX", $"Balance row filtered asset={asset}, qty={row.Quantity}, usd={row.UsdValue}");
        return false;
    }

    private static decimal NormalizePct(decimal raw)
    {
        if (raw == 0)
        {
            return 0m;
        }

        return Math.Abs(raw) <= 2m ? raw * 100m : raw;
    }

    private static decimal ReadDecimal(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var prop))
        {
            return 0m;
        }

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDecimal(out var num))
        {
            return num;
        }

        if (prop.ValueKind == JsonValueKind.String &&
            decimal.TryParse(prop.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out num))
        {
            return num;
        }

        return 0m;
    }

    private static int? ReadInt(JsonElement obj, string name)
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
            int.TryParse(prop.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out n))
        {
            return n;
        }

        return null;
    }

    private static bool ReadBool(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var prop))
        {
            return false;
        }

        if (prop.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        if (prop.ValueKind == JsonValueKind.False)
        {
            return false;
        }

        if (prop.ValueKind == JsonValueKind.String &&
            bool.TryParse(prop.GetString(), out var parsed))
        {
            return parsed;
        }

        return false;
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

    private static string Trim(string text)
    {
        return text.Length > 240 ? text[..240] : text;
    }

    private async Task<BitMexSymbolMarginState?> TryGetExistingSymbolMarginStateAsync(string normalizedSymbol, CancellationToken cancellationToken)
    {
        try
        {
            var positions = await FetchPositionsAsync(0m, cancellationToken);
            var matchedPosition = positions.FirstOrDefault(x => string.Equals(x.Symbol, normalizedSymbol, StringComparison.OrdinalIgnoreCase));
            if (matchedPosition is not null)
            {
                return new BitMexSymbolMarginState(true, matchedPosition.MarginMode, matchedPosition.Leverage);
            }

            var openOrders = await FetchOpenOrdersAsync(cancellationToken);
            var matchedOrder = openOrders.FirstOrDefault(x => string.Equals(x.Symbol, normalizedSymbol, StringComparison.OrdinalIgnoreCase));
            if (matchedOrder is not null)
            {
                return new BitMexSymbolMarginState(true, matchedOrder.MarginMode, matchedOrder.Leverage);
            }

            return new BitMexSymbolMarginState(false, MarginMode.Unknown, 0m);
        }
        catch (Exception ex)
        {
            _logger.Warn("BitMEX", $"Margin state lookup warning symbol={normalizedSymbol}: {ex.Message}");
            return null;
        }
    }

    private static bool IsSameLeverage(decimal current, decimal target)
    {
        if (current <= 0m || target <= 0m)
        {
            return false;
        }

        return Math.Abs(current - target) <= 0.01m;
    }

    private static string NormalizeBitMexMarginModeError(string body, MarginMode marginMode, int? statusCode = null)
    {
        var modeText = marginMode == MarginMode.Isolated ? "Isolated" : "Cross";
        var message = TryExtractBitMexErrorMessage(body) ?? Trim(body);
        if (string.IsNullOrWhiteSpace(message))
        {
            message = "Unknown BitMEX margin-mode error.";
        }

        if (message.Contains("multi-asset", StringComparison.OrdinalIgnoreCase))
        {
            return "BitMEX Multi-Asset Margin accounts support Cross only. Switch the account out of Multi-Asset Margin before using Isolated.";
        }

        if (message.Contains("inconsistent strategy with position mode", StringComparison.OrdinalIgnoreCase))
        {
            return $"BitMEX rejected switching to {modeText}: the account's current position mode is incompatible with this margin-mode change. Try the other mode for this symbol, or adjust the position mode in BitMEX first.";
        }

        if (statusCode is 401 or 403 ||
            message.Contains("access denied", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("permission", StringComparison.OrdinalIgnoreCase))
        {
            return $"BitMEX rejected switching to {modeText}: this API key or account does not have permission to change leverage / margin mode.";
        }

        if (message.Contains("position", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("open order", StringComparison.OrdinalIgnoreCase))
        {
            return $"BitMEX rejected switching to {modeText}: close existing positions and cancel open orders for this symbol first.";
        }

        return $"BitMEX rejected switching to {modeText}: {message}";
    }

    private static string? TryExtractBitMexErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.Object &&
                error.TryGetProperty("message", out var message) &&
                message.ValueKind == JsonValueKind.String)
            {
                return message.GetString();
            }
        }
        catch
        {
        }

        return null;
    }

    private sealed record BitMexSymbolMarginState(bool HasExposureOrOrder, MarginMode MarginMode, decimal Leverage);

    private void ParseMessage(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("table", out var tableElement))
        {
            return;
        }

        var table = tableElement.GetString();
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        if (table == "trade")
        {
            foreach (var item in data.EnumerateArray())
            {
                if (!item.TryGetProperty("timestamp", out var tsElem) ||
                    !item.TryGetProperty("price", out var priceElem) ||
                    !item.TryGetProperty("size", out var sizeElem))
                {
                    continue;
                }

                var ts = tsElem.GetDateTimeOffset();
                var price = priceElem.GetDecimal();
                var size = sizeElem.GetDecimal();
                _channel.Writer.TryWrite(new TradeTick(ts, price, size));
            }

            return;
        }

        if (table == "instrument")
        {
            foreach (var item in data.EnumerateArray())
            {
                if (!item.TryGetProperty("lastPrice", out var priceElem) || priceElem.ValueKind != JsonValueKind.Number)
                {
                    continue;
                }

                var price = priceElem.GetDecimal();
                _channel.Writer.TryWrite(new TradeTick(DateTimeOffset.UtcNow, price, 0m));
            }
        }
    }

    private void ApplyAuthHeaders(HttpRequestMessage request, string method, string path, string body)
    {
        var expires = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 5;
        var signTarget = method + path + expires + body;
        var signature = ComputeSignature(signTarget, _credentials.ApiSecret!);

        request.Headers.Add("api-key", _credentials.ApiKey);
        request.Headers.Add("api-expires", expires.ToString());
        request.Headers.Add("api-signature", signature);
    }

    private async Task<BitMexInstrumentSpec> GetInstrumentSpecAsync(string symbol, CancellationToken cancellationToken)
    {
        var normalizedSymbol = symbol.Trim().ToUpperInvariant();
        if (_instrumentSpecCache.TryGetValue(normalizedSymbol, out var cached) &&
            DateTimeOffset.UtcNow - cached.At < TimeSpan.FromMinutes(5))
        {
            return cached.Spec;
        }

        await _instrumentSpecGate.WaitAsync(cancellationToken);
        try
        {
            if (_instrumentSpecCache.TryGetValue(normalizedSymbol, out cached) &&
                DateTimeOffset.UtcNow - cached.At < TimeSpan.FromMinutes(5))
            {
                return cached.Spec;
            }

            using var req = new HttpRequestMessage(HttpMethod.Get, _restBase + $"/api/v1/instrument?symbol={Uri.EscapeDataString(normalizedSymbol)}&count=1&reverse=true");
            using var resp = await _httpClient.SendAsync(req, cancellationToken);
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"instrument lookup failed {(int)resp.StatusCode}: {Trim(body)}");
            }

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            {
                throw new InvalidOperationException("instrument lookup returned empty payload");
            }

            var row = doc.RootElement[0];
            var isInverse = ReadBool(row, "isInverse");
            var lotSize = ReadDecimal(row, "lotSize");
            if (lotSize <= 0)
            {
                lotSize = 1m;
            }

            var underlyingToPositionMultiplier = ReadDecimal(row, "underlyingToPositionMultiplier");
            if (underlyingToPositionMultiplier < 0)
            {
                underlyingToPositionMultiplier = Math.Abs(underlyingToPositionMultiplier);
            }

            var referencePrice = ReadDecimal(row, "lastPrice");
            if (referencePrice <= 0)
            {
                referencePrice = ReadDecimal(row, "markPrice");
            }

            var spec = new BitMexInstrumentSpec(
                normalizedSymbol,
                isInverse,
                lotSize,
                underlyingToPositionMultiplier,
                referencePrice);

            _instrumentSpecCache[normalizedSymbol] = (spec, DateTimeOffset.UtcNow);
            _logger.Info("BitMEX", $"Instrument spec symbol={normalizedSymbol}, inverse={spec.IsInverse}, lot={spec.LotSize}, utp={spec.UnderlyingToPositionMultiplier}, refPx={spec.ReferencePrice}");
            return spec;
        }
        finally
        {
            _instrumentSpecGate.Release();
        }
    }

    private static int ConvertBaseSizeToOrderQty(decimal baseQty, decimal? price, BitMexInstrumentSpec spec)
    {
        var absBaseQty = Math.Abs(baseQty);
        if (absBaseQty <= 0m)
        {
            return 0;
        }

        var referencePrice = price.GetValueOrDefault();
        if (referencePrice <= 0)
        {
            referencePrice = spec.ReferencePrice;
        }

        decimal contracts;
        if (spec.IsInverse)
        {
            if (referencePrice <= 0)
            {
                return 0;
            }

            contracts = absBaseQty * referencePrice;
        }
        else if (spec.UnderlyingToPositionMultiplier > 0)
        {
            contracts = absBaseQty * spec.UnderlyingToPositionMultiplier;
        }
        else
        {
            contracts = referencePrice > 0 ? absBaseQty * referencePrice : absBaseQty;
        }

        if (spec.LotSize > 0)
        {
            contracts = Math.Round(contracts / spec.LotSize, MidpointRounding.AwayFromZero) * spec.LotSize;
        }

        if (contracts <= 0m)
        {
            return 0;
        }

        var rounded = Math.Round(contracts, MidpointRounding.AwayFromZero);
        if (rounded > int.MaxValue)
        {
            throw new OverflowException($"orderQty overflow: {rounded}");
        }

        return Math.Max(1, (int)rounded);
    }

    private static int ConvertPositionQtyToOrderQty(decimal positionQty, BitMexInstrumentSpec spec)
    {
        var absPositionQty = Math.Abs(positionQty);
        if (absPositionQty <= 0)
        {
            return 0;
        }

        var contracts = absPositionQty;
        if (spec.LotSize > 0)
        {
            contracts = Math.Round(contracts / spec.LotSize, MidpointRounding.AwayFromZero) * spec.LotSize;
        }

        if (contracts <= 0)
        {
            return 0;
        }

        var rounded = Math.Round(contracts, MidpointRounding.AwayFromZero);
        if (rounded > int.MaxValue)
        {
            throw new OverflowException($"orderQty overflow: {rounded}");
        }

        return Math.Max(1, (int)rounded);
    }

    private static string BuildOrderPayload(string symbol, string side, int orderQty, decimal? price, bool reduceOnly)
    {
        if (price.HasValue)
        {
            var payload = new Dictionary<string, object?>
            {
                ["symbol"] = symbol,
                ["side"] = side,
                ["orderQty"] = orderQty,
                ["ordType"] = "Limit",
                ["price"] = price.Value
            };
            if (reduceOnly)
            {
                // For close limit orders, enforce post-only to avoid taker execution.
                payload["execInst"] = "ReduceOnly,ParticipateDoNotInitiate";
            }

            return JsonSerializer.Serialize(payload);
        }

        var marketPayload = new Dictionary<string, object?>
        {
            ["symbol"] = symbol,
            ["side"] = side,
            ["orderQty"] = orderQty,
            ["ordType"] = "Market"
        };
        if (reduceOnly)
        {
            marketPayload["execInst"] = "ReduceOnly";
        }

        return JsonSerializer.Serialize(marketPayload);
    }

    private static string ComputeSignature(string message, string secret)
    {
        var key = Encoding.UTF8.GetBytes(secret);
        var payload = Encoding.UTF8.GetBytes(message);
        using var hmac = new HMACSHA256(key);
        var hash = hmac.ComputeHash(payload);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static (string BinSize, int Factor) IntervalToBitMex(CandleInterval interval)
    {
        return interval switch
        {
            CandleInterval.M5 => ("5m", 1),
            CandleInterval.M10 => ("5m", 2),
            CandleInterval.M15 => ("5m", 3),
            CandleInterval.M30 => ("5m", 6),
            CandleInterval.H1 => ("1h", 1),
            CandleInterval.H2 => ("1h", 2),
            CandleInterval.H4 => ("1h", 4),
            CandleInterval.H6 => ("1h", 6),
            CandleInterval.H12 => ("1h", 12),
            CandleInterval.D1 => ("1d", 1),
            CandleInterval.D7 => ("1d", 7),
            CandleInterval.D30 => ("1d", 30),
            _ => ("5m", 1)
        };
    }

    private static CandleInterval BaseIntervalFromBitMex(string binSize)
    {
        return binSize switch
        {
            "5m" => CandleInterval.M5,
            "1h" => CandleInterval.H1,
            "1d" => CandleInterval.D1,
            _ => CandleInterval.M5
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

    private sealed record BitMexInstrumentSpec(
        string Symbol,
        bool IsInverse,
        decimal LotSize,
        decimal UnderlyingToPositionMultiplier,
        decimal ReferencePrice);
}
