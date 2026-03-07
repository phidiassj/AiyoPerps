using AiyoPerps.Core;
using AiyoPerps.Models;
using AiyoPerps.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace AiyoPerps.ViewModels;

public sealed partial class WorkspaceTabViewModel
{
    private void HandleTradeTick(string venueId, string symbol, TradeTick tick)
    {
        // Size can be 0 for quote/ticker heartbeat updates. In that case update price shape but keep volume unchanged.
        Candle? updated = null;

        if (tick.Price > 0)
        {
            lock (_candleLock)
            {
                var interval = ParseInterval(SelectedInterval);
                if (_currentCandle is not null && (_currentCandle.Symbol != symbol || _currentCandle.Interval != interval))
                {
                    _currentCandle = null;
                }

                var update = _candleAggregator.Aggregate(venueId, symbol, interval, tick, _currentCandle);
                updated = update.Candle;
                _currentCandle = updated;
                _candleCache.Upsert(updated);
            }

            _candlePersistChannel.Writer.TryWrite(updated);
            Dispatcher.UIThread.Post(() => CandleStatus = FormatCandleStatus(updated));
        }

        Dispatcher.UIThread.Post(() =>
        {
            UpdateOrderBookSnapshot(tick.Price);
            UpdatePositionMarks(symbol, tick.Price);
            if (tick.Size > 0)
            {
                AppendRecentTrade(tick);
            }
        });

        if (updated is not null)
        {
            Dispatcher.UIThread.Post(UpdateCandleSeriesFromCache);
        }
    }

    private void SeedCandleCacheFromStorage()
    {
        if (Binding is null)
        {
            return;
        }

        var interval = ParseInterval(SelectedInterval);
        var recent = _candleRepository.LoadRecent(Binding.VenueId, Symbol, interval, 300);
        lock (_candleLock)
        {
            foreach (var candle in recent)
            {
                _candleCache.Upsert(candle);
            }

            _currentCandle = recent.LastOrDefault();
        }

        MarkStorageLoaded(Binding.VenueId, Symbol, interval);
        _logger.Info("WorkspaceTab", $"Seed from storage tabId={TabId}, symbol={Symbol}, interval={interval}, count={recent.Count}");
    }

    private async Task TryLoadVenueHistoricalCandlesAsync(CancellationToken cancellationToken)
    {
        if (Binding is null || _venue is not IHistoricalCandleProvider provider)
        {
            return;
        }

        try
        {
            var interval = ParseInterval(SelectedInterval);
            var (requiredCount, since) = CalculateBackfillRequirement(interval, 12.0);
            var dbCount = _candleRepository.CountSince(Binding.VenueId, Symbol, interval, since);
            var inMemoryCount = _candleCache.Get(Binding.VenueId, Symbol, interval)
                .Count(x => x.OpenTime >= since);
            var minAcceptableCount = Math.Max(24, requiredCount - 4);
            var latestFromDb = _candleRepository.LoadRecent(Binding.VenueId, Symbol, interval, 1).LastOrDefault();
            var intervalWindowMs = Math.Max(60_000d, IntervalToHours(interval) * 3_600_000d);
            var freshnessWindowMs = Math.Max(30 * 60_000d, intervalWindowMs * 6);
            var isDbRecentEnough = latestFromDb is not null &&
                                   latestFromDb.OpenTime >= DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(freshnessWindowMs);

            _logger.Info("WorkspaceTab", $"Backfill check tabId={TabId}, symbol={Symbol}, interval={interval}, required={requiredCount}, minAcceptable={minAcceptableCount}, db={dbCount}, mem={inMemoryCount}, dbRecentEnough={isDbRecentEnough}, freshnessWindowMs={freshnessWindowMs}");
            if ((dbCount >= minAcceptableCount && isDbRecentEnough) || inMemoryCount >= minAcceptableCount)
            {
                return;
            }

            var fetchCount = Math.Max(120, requiredCount + 40);
            _logger.Info("WorkspaceTab", $"Load venue historical candles start tabId={TabId}, symbol={Symbol}, interval={interval}, fetchCount={fetchCount}");
            var recent = await provider.GetRecentCandlesAsync(Symbol, interval, fetchCount, cancellationToken);
            lock (_candleLock)
            {
                foreach (var candle in recent)
                {
                    _candleCache.Upsert(candle);
                    _candleRepository.Upsert(candle);
                }

                _currentCandle = recent.LastOrDefault() ?? _currentCandle;
            }
            MarkStorageLoaded(Binding.VenueId, Symbol, interval);

            _logger.Info("WorkspaceTab", $"Load venue historical candles done tabId={TabId}, count={recent.Count}");
        }
        catch (Exception ex)
        {
            _logger.Error("WorkspaceTab", $"Load venue historical candles failed tabId={TabId}", ex);
        }
    }

    private async Task RefreshRecentDataAsync()
    {
        if (!IsConfigured || Binding is null || _venue is not IHistoricalCandleProvider provider)
        {
            return;
        }

        await _settingsReloadGate.WaitAsync();
        try
        {
            var interval = ParseInterval(SelectedInterval);
            var (requiredCount, since) = CalculateBackfillRequirement(interval, 12.0);
            var fetchCount = Math.Max(120, requiredCount + 40);

            _logger.Info("WorkspaceTab", $"Manual refresh start tabId={TabId}, symbol={Symbol}, interval={interval}, since={since:O}, fetchCount={fetchCount}");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            cts.CancelAfter(TimeSpan.FromSeconds(20));

            var recent = await provider.GetRecentCandlesAsync(Symbol, interval, fetchCount, cts.Token);
            var usable = recent
                .Where(c => c.OpenTime >= since)
                .OrderBy(c => c.OpenTime)
                .ToList();

            if (usable.Count == 0)
            {
                _logger.Warn("WorkspaceTab", $"Manual refresh skipped replace tabId={TabId}, symbol={Symbol}, interval={interval}, fetched=0");
                _toastService.ShowWarning(L["Toast_RefreshDataFailed"] + "No historical data returned.");
                return;
            }

            var deleted = _candleRepository.DeleteAll(Binding.VenueId, Symbol, interval);
            _logger.Info("WorkspaceTab", $"Manual refresh deleted all candles tabId={TabId}, deleted={deleted}");

            lock (_candleLock)
            {
                _candleCache.Clear(Binding.VenueId, Symbol, interval);
                _currentCandle = null;
            }

            lock (_candleLock)
            {
                foreach (var candle in usable)
                {
                    _candleCache.Upsert(candle);
                    _candleRepository.Upsert(candle);
                }
            }

            RefreshCurrentCandleFromCache();
            _toastService.ShowInfo(L["Toast_RefreshDataCompleted12h"]);
            _logger.Info("WorkspaceTab", $"Manual refresh completed tabId={TabId}, loaded={usable.Count}");
        }
        catch (Exception ex)
        {
            _toastService.ShowError($"{L["Toast_RefreshDataFailed"]}{ex.Message}");
            _logger.Error("WorkspaceTab", $"Manual refresh failed tabId={TabId}", ex);
        }
        finally
        {
            _settingsReloadGate.Release();
        }
    }

    private async Task ReloadForMarketSettingChangeAsync(string reason)
    {
        if (_venue is null || Binding is null)
        {
            return;
        }

        await _settingsReloadGate.WaitAsync();
        try
        {
            _logger.Info("WorkspaceTab", $"Reload start tabId={TabId}, reason={reason}, symbol={Symbol}, interval={SelectedInterval}");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            cts.CancelAfter(TimeSpan.FromSeconds(15));

            if (reason == "symbol")
            {
                await ReconnectForSymbolChangeAsync(Symbol, cts.Token);
            }

            lock (_candleLock)
            {
                _currentCandle = null;
            }

            await TryLoadVenueHistoricalCandlesAsync(cts.Token);
            RefreshCurrentCandleFromCache();

            if (reason == "symbol" && SelectedAccount is not null)
            {
                _symbolCatalogRepository.MarkActivated(SelectedAccount.VenueId, SelectedAccount.Environment, Symbol);
                LoadSymbolOptions(SelectedAccount, autoSelectSymbol: false);
            }

            _logger.Info("WorkspaceTab", $"Reload completed tabId={TabId}, reason={reason}");
        }
        catch (Exception ex)
        {
            _logger.Error("WorkspaceTab", $"Reload failed tabId={TabId}, reason={reason}", ex);
            _toastService.ShowError($"{L["Toast_ReloadDataFailed"]}{ex.Message}");
        }
        finally
        {
            _settingsReloadGate.Release();
        }
    }

    private static double IntervalToHours(CandleInterval interval)
    {
        return interval switch
        {
            CandleInterval.M5 => 5.0 / 60.0,
            CandleInterval.M10 => 10.0 / 60.0,
            CandleInterval.M15 => 15.0 / 60.0,
            CandleInterval.M30 => 30.0 / 60.0,
            CandleInterval.H1 => 1,
            CandleInterval.H2 => 2,
            CandleInterval.H4 => 4,
            CandleInterval.H6 => 6,
            CandleInterval.H12 => 12,
            CandleInterval.D1 => 24,
            CandleInterval.D7 => 24 * 7,
            CandleInterval.D30 => 24 * 30,
            _ => 1
        };
    }

    private static (int RequiredCount, DateTimeOffset Since) CalculateBackfillRequirement(CandleInterval interval, double requiredHours)
    {
        var intervalHours = Math.Max(1.0 / 60.0, IntervalToHours(interval));
        var requiredCount = Math.Max(1, (int)Math.Ceiling(requiredHours / intervalHours));
        var since = DateTimeOffset.UtcNow.AddHours(-requiredHours);
        return (requiredCount, since);
    }

    private void RefreshCurrentCandleFromCache()
    {
        if (Binding is null)
        {
            CandleStatus = "尚無 K 線資料";
            return;
        }

        Candle? last;
        lock (_candleLock)
        {
            var interval = ParseInterval(SelectedInterval);
            var inMemory = _candleCache.Get(Binding.VenueId, Symbol, interval);

            if (inMemory.Count == 0 && ShouldLoadFromStorage(Binding.VenueId, Symbol, interval))
            {
                var fromStorage = _candleRepository.LoadRecent(Binding.VenueId, Symbol, interval, 300);
                foreach (var candle in fromStorage)
                {
                    _candleCache.Upsert(candle);
                }

                last = fromStorage.LastOrDefault();
                MarkStorageLoaded(Binding.VenueId, Symbol, interval);
                _logger.Info("WorkspaceTab", $"Load from storage for view tabId={TabId}, symbol={Symbol}, interval={interval}, count={fromStorage.Count}");
            }
            else
            {
                last = inMemory.LastOrDefault();
            }

            _currentCandle = last;
        }

        CandleStatus = last is null ? "尚無 K 線資料" : FormatCandleStatus(last);
        UpdateCandleSeriesFromCache();
    }

    private static CandleInterval ParseInterval(string interval)
    {
        return interval switch
        {
            "5m" => CandleInterval.M5,
            "10m" => CandleInterval.M10,
            "15m" => CandleInterval.M15,
            "30m" => CandleInterval.M30,
            "1h" => CandleInterval.H1,
            "2h" => CandleInterval.H2,
            "4h" => CandleInterval.H4,
            "6h" => CandleInterval.H6,
            "12h" => CandleInterval.H12,
            "1d" => CandleInterval.D1,
            "7d" => CandleInterval.D7,
            "30d" => CandleInterval.D30,
            _ => CandleInterval.M5
        };
    }

    private static string FormatCandleStatus(Candle candle)
    {
        return $"{candle.Interval}   O:{NumberText.Trim(candle.Open)}   H:{NumberText.Trim(candle.High)}   L:{NumberText.Trim(candle.Low)}   C:{NumberText.Trim(candle.Close)}   V:{NumberText.Trim(candle.Volume)}";
    }

    private static string BuildOrderBookSummary(decimal mid)
    {
        var ask1 = mid + 1;
        var ask2 = mid + 2;
        var ask3 = mid + 3;
        var bid1 = mid - 1;
        var bid2 = mid - 2;
        var bid3 = mid - 3;

        return
            $"ASK {NumberText.Trim(ask3)} x {NumberText.Trim(2.00m)}\n" +
            $"ASK {NumberText.Trim(ask2)} x {NumberText.Trim(1.50m)}\n" +
            $"ASK {NumberText.Trim(ask1)} x {NumberText.Trim(1.00m)}\n" +
            $"MID {NumberText.Trim(mid)}\n" +
            $"BID {NumberText.Trim(bid1)} x {NumberText.Trim(1.20m)}\n" +
            $"BID {NumberText.Trim(bid2)} x {NumberText.Trim(1.80m)}\n" +
            $"BID {NumberText.Trim(bid3)} x {NumberText.Trim(2.40m)}";
    }

    private void UpdateOrderBookSnapshot(decimal mid)
    {
        OrderBookSummary = BuildOrderBookSummary(mid);
        _lastMidPrice = mid;
        var snapshot = BuildOrderBookLevels(mid, ParseOrderBookTickSize());
        AskLevels = snapshot.Asks;
        BidLevels = snapshot.Bids;
        SpreadText = snapshot.SpreadText;
        RecalculateOrderEstimates();
    }

    private static (IReadOnlyList<OrderBookLevelRow> Asks, IReadOnlyList<OrderBookLevelRow> Bids, string SpreadText) BuildOrderBookLevels(decimal mid, decimal tickSize)
    {
        if (mid <= 0)
        {
            return (Array.Empty<OrderBookLevelRow>(), Array.Empty<OrderBookLevelRow>(), "Spread -");
        }

        const int levels = 8;
        var asks = new List<OrderBookLevelRow>(levels);
        var bids = new List<OrderBookLevelRow>(levels);

        var askTotal = 0m;
        var bidTotal = 0m;
        var bestAsk = decimal.Ceiling(mid / tickSize) * tickSize;
        var bestBid = decimal.Floor(mid / tickSize) * tickSize;
        if (bestAsk <= bestBid)
        {
            bestAsk = bestBid + tickSize;
        }

        for (var i = 0; i < levels; i++)
        {
            var step = i + 1;
            var askPx = bestAsk + (i * tickSize);
            var bidPx = bestBid - (i * tickSize);
            var askSize = Math.Round(0.8m + (step * 0.34m), 2);
            var bidSize = Math.Round(0.85m + (step * 0.32m), 2);
            askTotal += askSize;
            bidTotal += bidSize;

            asks.Add(new OrderBookLevelRow(askPx, askSize, askTotal, IsAsk: true));
            bids.Add(new OrderBookLevelRow(bidPx, bidSize, bidTotal, IsAsk: false));
        }

        var spread = asks[0].Price - bids[0].Price;
        var spreadPct = mid == 0 ? 0 : (spread / mid) * 100m;
        var spreadText = $"Spread {NumberText.Trim(spread)} ({NumberText.Trim(spreadPct)}%)";
        asks.Reverse();
        return (asks, bids, spreadText);
    }

    private void RecalculateOrderEstimates()
    {
        if (!_lastMidPrice.HasValue || _lastMidPrice.Value <= 0)
        {
            EstimatedCostUsd = "-";
            EstimatedLiquidationPrice = "-";
            return;
        }

        if (!decimal.TryParse(OrderQuantity, NumberStyles.Any, CultureInfo.InvariantCulture, out var amountInput) || amountInput <= 0)
        {
            EstimatedCostUsd = "-";
            EstimatedLiquidationPrice = "-";
            return;
        }

        if (!decimal.TryParse(OrderLeverage, NumberStyles.Any, CultureInfo.InvariantCulture, out var leverage) || leverage <= 0)
        {
            EstimatedCostUsd = "-";
            EstimatedLiquidationPrice = "-";
            return;
        }

        decimal entryPrice;
        if (IsLimitOrder)
        {
            if (!decimal.TryParse(OrderPrice, NumberStyles.Any, CultureInfo.InvariantCulture, out var limitPrice) || limitPrice <= 0)
            {
                EstimatedCostUsd = "-";
                EstimatedLiquidationPrice = "-";
                return;
            }

            entryPrice = limitPrice;
        }
        else
        {
            entryPrice = _lastMidPrice.Value;
        }

        var notional = string.Equals(SelectedAmountUnit, "USD", StringComparison.OrdinalIgnoreCase)
            ? amountInput
            : amountInput * entryPrice;

        if (notional <= 0)
        {
            EstimatedCostUsd = "-";
            EstimatedLiquidationPrice = "-";
            return;
        }

        var estimatedCost = notional / leverage;
        var liquidation = _isShortOrderSide
            ? entryPrice * (1m + (1m / leverage))
            : entryPrice * (1m - (1m / leverage));

        if (liquidation < 0)
        {
            liquidation = 0;
        }

        EstimatedCostUsd = NumberText.Trim(estimatedCost);
        EstimatedLiquidationPrice = NumberText.Trim(liquidation);
    }

    private void UpdateAmountUnitOptions(string symbol)
    {
        var nextRelative = ResolveRelativeAmountUnit(symbol);
        var hadRelativeSelected = !string.Equals(SelectedAmountUnit, "USD", StringComparison.OrdinalIgnoreCase);

        if (!string.Equals(_relativeAmountUnit, nextRelative, StringComparison.Ordinal))
        {
            _relativeAmountUnit = nextRelative;
            RaisePropertyChanged(nameof(RelativeAmountUnit));
            RaisePropertyChanged(nameof(OrderAmountUnitOptions));
        }

        if (hadRelativeSelected && !string.Equals(_selectedAmountUnit, _relativeAmountUnit, StringComparison.Ordinal))
        {
            _selectedAmountUnit = _relativeAmountUnit;
            RaisePropertyChanged(nameof(SelectedAmountUnit));
        }

        RecalculateOrderEstimates();
    }

    private static string ResolveRelativeAmountUnit(string symbol)
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

        if (upper.Contains('/'))
        {
            upper = upper.Split('/')[0];
        }

        if (upper.Contains('-'))
        {
            upper = upper.Split('-')[0];
        }

        if (upper.EndsWith("USDT", StringComparison.Ordinal) && upper.Length > 4)
        {
            upper = upper[..^4];
        }
        else if (upper.EndsWith("USDC", StringComparison.Ordinal) && upper.Length > 4)
        {
            upper = upper[..^4];
        }
        else if (upper.EndsWith("USD", StringComparison.Ordinal) && upper.Length > 3)
        {
            upper = upper[..^3];
        }

        return upper switch
        {
            "XBT" => "BTC",
            "" => "BTC",
            _ => upper
        };
    }

    private decimal ParseOrderBookTickSize()
    {
        if (decimal.TryParse(SelectedOrderBookTickSize, NumberStyles.Any, CultureInfo.InvariantCulture, out var tick) && tick > 0)
        {
            return tick;
        }

        return 1m;
    }

    private void AppendRecentTrade(TradeTick tick)
    {
        var priorPrice = RecentTrades.Count > 0 ? RecentTrades[0].Price : (decimal?)null;
        var side = L["Trade_Side_Flat"];
        var priceHex = "#84AFC0";
        var sideHex = "#84AFC0";
        if (priorPrice.HasValue)
        {
            if (tick.Price >= priorPrice.Value)
            {
                side = L["Trade_Side_Buy"];
                priceHex = "#5ED0A9";
                sideHex = "#5ED0A9";
            }
            else
            {
                side = L["Trade_Side_Sell"];
                priceHex = "#E47A8E";
                sideHex = "#E47A8E";
            }
        }

        var row = new RecentTradeRow(
            tick.Timestamp.ToLocalTime(),
            tick.Price,
            tick.Size,
            side,
            priceHex,
            sideHex);

        var next = new List<RecentTradeRow>(RecentTrades.Count + 1) { row };
        next.AddRange(RecentTrades.Take(9));
        RecentTrades = next;
    }

}
