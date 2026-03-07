using AiyoPerps.Core;
using AiyoPerps.Models;
using AiyoPerps.Services.Api;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace AiyoPerps.ViewModels;

public sealed partial class WorkspaceTabViewModel
{
    private void ApplyAccountSnapshot(VenueAccountSnapshot snapshot)
    {
        var previousStates = new Dictionary<string, PositionState>(_positionStates, StringComparer.OrdinalIgnoreCase);
        _positionStates.Clear();
        foreach (var p in snapshot.Positions)
        {
            var markPrice = p.MarkPrice;
            if (markPrice <= 0 && previousStates.TryGetValue(p.Symbol, out var previous) && previous.MarkPrice > 0)
            {
                markPrice = previous.MarkPrice;
                _logger.Warn("WorkspaceTab", $"Account snapshot markPrice fallback symbol={p.Symbol}, incoming={p.MarkPrice}, fallback={markPrice}");
            }

            _positionStates[p.Symbol] = new PositionState(
                p.Symbol,
                p.Quantity < 0 ? "Short" : "Long",
                p.NotionalUsd,
                p.Leverage,
                p.EntryPrice,
                markPrice,
                p.UnrealizedPnlUsd,
                p.RealizedPnlUsd,
                p.Quantity);
        }

        RebuildPositionRows();

        CleanupSuppressedCanceledOrderIds();

        _remotePendingOrders = snapshot.OpenOrders
            .Where(x => !IsSuppressedCanceledOrderId(x.OrderId))
            .Where(x => !IsFailedOrderStatus(x.Status))
            .Select(x => new PendingOrderPanelRow(
                x.Symbol,
                $"{NumberText.Trim(x.NotionalUsd, useGrouping: true)} USD",
                FormatLeverageText(ResolvePendingOrderLeverage(x)),
                x.LimitPrice.HasValue ? NumberText.Trim(x.LimitPrice.Value) : "-",
                x.Status,
                x.OrderId,
                null,
                true))
            .ToList();

        CleanupSyncedPendingOrders();
        CleanupTransientPendingOrders();
        RebuildPendingOrderRows();

        var balances = snapshot.Balances
            .GroupBy(x => x.Asset, StringComparer.OrdinalIgnoreCase)
            .Select(g => new VenueBalance(
                g.Key.ToUpperInvariant(),
                g.Sum(x => x.Quantity),
                g.Sum(x => x.UsdValue)))
            .Where(x => x.Quantity != 0m)
            .OrderBy(x => IsStableDisplayAsset(x.Asset) ? 0 : 1)
            .ThenBy(x => x.Asset, StringComparer.OrdinalIgnoreCase)
            .Select(x => new BalancePanelRow(
                x.Asset.ToUpperInvariant(),
                x.Quantity,
                x.UsdValue))
            .ToList();
        Balances = balances;
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

    private void CleanupTransientPendingOrders()
    {
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-5);
        var toRemove = _pendingOrderStates
            .Where(x =>
                IsPendingOrderStatusForPanel(x.Value.Status) &&
                x.Value.CreatedAt < cutoff)
            .Select(x => x.Key)
            .ToList();

        foreach (var id in toRemove)
        {
            _pendingOrderStates.Remove(id);
        }

        if (toRemove.Count > 0)
        {
            _logger.Info("WorkspaceTab", $"Pending order cleanup removed synced-local rows tabId={TabId}, removed={toRemove.Count}");
        }
    }

    private void CleanupSyncedPendingOrders()
    {
        var remoteIds = new HashSet<string>(
            _remotePendingOrders
                .Select(x => x.VenueOrderId)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x!),
            StringComparer.OrdinalIgnoreCase);

        var toRemove = _pendingOrderStates
            .Where(x =>
                !string.IsNullOrWhiteSpace(x.Value.VenueOrderId) &&
                !remoteIds.Contains(x.Value.VenueOrderId!) &&
                x.Value.Status.Contains("待同步", StringComparison.Ordinal))
            .Select(x => x.Key)
            .ToList();

        foreach (var id in toRemove)
        {
            _pendingOrderStates.Remove(id);
        }
    }

    private static string FormatLeverageText(decimal leverage)
    {
        return leverage > 0
            ? $"{NumberText.Trim(leverage)}x"
            : "-";
    }

    private decimal ResolvePendingOrderLeverage(VenueOpenOrder order)
    {
        if (order.Leverage > 0)
        {
            return order.Leverage;
        }

        if (!string.IsNullOrWhiteSpace(order.OrderId))
        {
            var matchByVenueId = _pendingOrderStates.Values
                .FirstOrDefault(x => string.Equals(x.VenueOrderId, order.OrderId, StringComparison.OrdinalIgnoreCase));
            if (matchByVenueId is not null && matchByVenueId.Leverage > 0)
            {
                return matchByVenueId.Leverage;
            }
        }

        var matchBySymbolAndPrice = _pendingOrderStates.Values
            .Where(x => string.Equals(x.Symbol, order.Symbol, StringComparison.OrdinalIgnoreCase))
            .Where(x => !string.IsNullOrWhiteSpace(x.Status) && IsPendingOrderStatusForPanel(x.Status))
            .FirstOrDefault(x =>
                (x.LimitPrice is null && order.LimitPrice is null) ||
                (x.LimitPrice.HasValue && order.LimitPrice.HasValue && Math.Abs(x.LimitPrice.Value - order.LimitPrice.Value) < 0.0000001m));

        if (matchBySymbolAndPrice?.Leverage > 0)
        {
            var hintKey = BuildPendingOrderLeverageHintKey(order.Symbol, order.LimitPrice, order.NotionalUsd, order.OrderId, includeOrderId: true);
            var hintKeyNoId = BuildPendingOrderLeverageHintKey(order.Symbol, order.LimitPrice, order.NotionalUsd, order.OrderId, includeOrderId: false);
            _pendingOrderLeverageHints[hintKey] = matchBySymbolAndPrice.Leverage;
            _pendingOrderLeverageHints[hintKeyNoId] = matchBySymbolAndPrice.Leverage;
            return matchBySymbolAndPrice.Leverage;
        }

        var fallbackKey = BuildPendingOrderLeverageHintKey(order.Symbol, order.LimitPrice, order.NotionalUsd, order.OrderId, includeOrderId: true);
        if (_pendingOrderLeverageHints.TryGetValue(fallbackKey, out var hinted))
        {
            return hinted;
        }

        var fallbackKeyNoId = BuildPendingOrderLeverageHintKey(order.Symbol, order.LimitPrice, order.NotionalUsd, order.OrderId, includeOrderId: false);
        return _pendingOrderLeverageHints.TryGetValue(fallbackKeyNoId, out hinted)
            ? hinted
            : 0m;
    }

    private void RememberPendingOrderLeverageHint(string symbol, decimal leverage, decimal? limitPrice, decimal notionalUsd, string? orderId)
    {
        if (leverage <= 0)
        {
            return;
        }

        var key = BuildPendingOrderLeverageHintKey(symbol, limitPrice, notionalUsd, orderId, includeOrderId: true);
        var keyNoId = BuildPendingOrderLeverageHintKey(symbol, limitPrice, notionalUsd, orderId, includeOrderId: false);
        _pendingOrderLeverageHints[key] = leverage;
        _pendingOrderLeverageHints[keyNoId] = leverage;
    }

    private static string BuildPendingOrderLeverageHintKey(string symbol, decimal? limitPrice, decimal notionalUsd, string? orderId, bool includeOrderId)
    {
        var normalizedSymbol = (symbol ?? string.Empty).Trim().ToUpperInvariant();
        var priceText = limitPrice.HasValue ? decimal.Round(limitPrice.Value, 8, MidpointRounding.AwayFromZero).ToString("0.########", CultureInfo.InvariantCulture) : "MKT";
        var notionalText = decimal.Round(notionalUsd, 4, MidpointRounding.AwayFromZero).ToString("0.####", CultureInfo.InvariantCulture);
        var idText = includeOrderId ? (string.IsNullOrWhiteSpace(orderId) ? "-" : orderId.Trim()) : "*";
        return $"{normalizedSymbol}|{priceText}|{notionalText}|{idText}";
    }

    private void UpdatePositionMarks(string symbol, decimal markPrice)
    {
        if (markPrice <= 0)
        {
            return;
        }

        if (!_positionStates.TryGetValue(symbol, out var existing))
        {
            return;
        }

        if (Math.Abs(existing.MarkPrice - markPrice) < 0.0000001m)
        {
            return;
        }

        _positionStates[symbol] = existing with { MarkPrice = markPrice };
        var next = _positionStates[symbol];
        var pct = ComputeUnrealizedPnlPct(next);
        var unrealizedUsd = next.NotionalUsd > 0
            ? next.NotionalUsd * (pct / 100m)
            : next.UnrealizedPnlUsd;
        _positionStates[symbol] = next with { UnrealizedPnlUsd = unrealizedUsd };
        RebuildPositionRows();
    }

    private void RebuildPositionRows()
    {
        var sortedStates = _positionStates.Values
            .OrderBy(x => x.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var existingRows = _activePositions.ToDictionary(x => x.Symbol, StringComparer.OrdinalIgnoreCase);
        var desiredSymbols = new HashSet<string>(sortedStates.Select(x => x.Symbol), StringComparer.OrdinalIgnoreCase);
        var removedSymbols = _positionClosePriceInputs.Keys
            .Where(x => !desiredSymbols.Contains(x))
            .ToList();
        foreach (var symbol in removedSymbols)
        {
            _positionClosePriceInputs.Remove(symbol);
        }

        for (var i = _activePositions.Count - 1; i >= 0; i--)
        {
            if (!desiredSymbols.Contains(_activePositions[i].Symbol))
            {
                _activePositions.RemoveAt(i);
            }
        }

        for (var i = 0; i < sortedStates.Count; i++)
        {
            var state = sortedStates[i];
            var closePrice = _positionClosePriceInputs.TryGetValue(state.Symbol, out var existingClosePrice) && !string.IsNullOrWhiteSpace(existingClosePrice)
                ? existingClosePrice
                : NumberText.Trim(state.EntryPrice > 0 ? state.EntryPrice : state.MarkPrice);
            if (!_positionClosePriceInputs.ContainsKey(state.Symbol))
            {
                _positionClosePriceInputs[state.Symbol] = closePrice;
            }

            if (!existingRows.TryGetValue(state.Symbol, out var row))
            {
                row = new PositionPanelRow(state.Symbol, closePrice);
                row.PropertyChanged += (_, args) =>
                {
                    if (string.Equals(args.PropertyName, nameof(PositionPanelRow.ClosePrice), StringComparison.Ordinal))
                    {
                        _positionClosePriceInputs[row.Symbol] = row.ClosePrice;
                    }
                };
                _activePositions.Insert(i, row);
            }
            else
            {
                var currentIndex = _activePositions.IndexOf(row);
                if (currentIndex != i && currentIndex >= 0)
                {
                    _activePositions.Move(currentIndex, i);
                }
            }

            row.ApplyState(
                contractAmount: $"{NumberText.Trim(state.NotionalUsd, useGrouping: true)} USD",
                leverage: FormatLeverageText(state.Leverage),
                entryPrice: state.EntryPrice,
                markPrice: state.MarkPrice,
                unrealizedPnlPct: ComputeUnrealizedPnlPct(state),
                unrealizedPnlUsd: state.UnrealizedPnlUsd,
                realizedPnlUsd: state.RealizedPnlUsd);
        }

        RaisePropertyChanged(nameof(HasActivePositions));
        RaisePropertyChanged(nameof(HasNoActivePositions));
    }

    private void UpsertPendingOrder(PendingOrderState order)
    {
        _pendingOrderStates[order.LocalId] = order;
        RebuildPendingOrderRows();
    }

    private void RemovePendingOrder(string localId, string? onlyIfStatusMatches = null)
    {
        if (!_pendingOrderStates.TryGetValue(localId, out var existing))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(onlyIfStatusMatches) &&
            !string.Equals(existing.Status, onlyIfStatusMatches, StringComparison.Ordinal))
        {
            return;
        }

        _pendingOrderStates.Remove(localId);
        RebuildPendingOrderRows();
    }

    private void RebuildPendingOrderRows()
    {
        var localRows = _pendingOrderStates.Values
            .Where(x => IsPendingOrderStatusForPanel(x.Status))
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new PendingOrderPanelRow(
                x.Symbol,
                $"{NumberText.Trim(x.NotionalUsd, useGrouping: true)} USD",
                FormatLeverageText(x.Leverage),
                x.LimitPrice.HasValue ? NumberText.Trim(x.LimitPrice.Value) : "-",
                x.Status,
                x.VenueOrderId,
                x.LocalId,
                false))
            .ToList();

        var remoteOrderIds = new HashSet<string>(
            _remotePendingOrders
                .Select(x => x.VenueOrderId)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x!),
            StringComparer.OrdinalIgnoreCase);
        var merged = new List<PendingOrderPanelRow>(localRows.Count + _remotePendingOrders.Count);
        merged.AddRange(localRows.Where(x =>
            string.IsNullOrWhiteSpace(x.VenueOrderId) ||
            !remoteOrderIds.Contains(x.VenueOrderId)));
        merged.AddRange(_remotePendingOrders.Where(x => !IsFailedOrderStatus(x.Status)));
        PendingOrders = merged;
    }

    private static bool IsPendingOrderStatusForPanel(string status)
    {
        return string.Equals(status, "送單中", StringComparison.Ordinal) ||
               string.Equals(status, "已送出待同步", StringComparison.Ordinal) ||
               string.Equals(status, "平倉送單中", StringComparison.Ordinal) ||
               string.Equals(status, "平倉已送出待同步", StringComparison.Ordinal);
    }

    private static bool IsFailedOrderStatus(string status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return false;
        }

        var s = status.Trim().ToLowerInvariant();
        return s.Contains("fail", StringComparison.Ordinal) ||
               s.Contains("error", StringComparison.Ordinal) ||
               s.Contains("reject", StringComparison.Ordinal) ||
               s.Contains("cancel", StringComparison.Ordinal) ||
               s.Contains("失敗", StringComparison.Ordinal) ||
               s.Contains("例外", StringComparison.Ordinal);
    }

    private bool TryBeginCloseSubmit(string symbol)
    {
        lock (_closeSubmitLock)
        {
            return _closingSymbolsInFlight.Add(symbol);
        }
    }

    private void EndCloseSubmit(string symbol)
    {
        lock (_closeSubmitLock)
        {
            _closingSymbolsInFlight.Remove(symbol);
        }
    }

    private bool IsSuppressedCanceledOrderId(string? orderId)
    {
        if (string.IsNullOrWhiteSpace(orderId))
        {
            return false;
        }

        return _suppressedCanceledOrderIds.ContainsKey(orderId);
    }

    private void SuppressCanceledOrderId(string? orderId)
    {
        if (string.IsNullOrWhiteSpace(orderId))
        {
            return;
        }

        _suppressedCanceledOrderIds[orderId] = DateTimeOffset.UtcNow;
    }

    private void CleanupSuppressedCanceledOrderIds()
    {
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-10);
        var expired = _suppressedCanceledOrderIds
            .Where(x => x.Value < cutoff)
            .Select(x => x.Key)
            .ToList();
        foreach (var id in expired)
        {
            _suppressedCanceledOrderIds.Remove(id);
        }
    }

    private static decimal ComputeUnrealizedPnlPct(PositionState state)
    {
        if (state.EntryPrice <= 0 || state.MarkPrice <= 0)
        {
            return 0;
        }

        var raw = string.Equals(state.Side, "Short", StringComparison.Ordinal)
            ? ((state.EntryPrice - state.MarkPrice) / state.EntryPrice) * 100m
            : ((state.MarkPrice - state.EntryPrice) / state.EntryPrice) * 100m;

        return raw;
    }

    private async Task SubmitClosePositionAsync(PositionPanelRow? row, bool useLimitPrice)
    {
        if (_isApiSessionManaged && _tradingApiService is not null && _apiSessionAccountId.HasValue)
        {
            await SubmitClosePositionViaApiSessionAsync(row, useLimitPrice, _apiSessionAccountId.Value);
            return;
        }

        if (row is null || !IsConfigured || _venue is null)
        {
            return;
        }

        if (!TryBeginCloseSubmit(row.Symbol))
        {
            _toastService.ShowWarning(L["Toast_CloseInProgress"]);
            _logger.Warn("WorkspaceTab", $"ClosePosition skipped duplicate submit tabId={TabId}, symbol={row.Symbol}, useLimit={useLimitPrice}");
            return;
        }

        try
        {
            if (!_positionStates.TryGetValue(row.Symbol, out var state))
            {
                _toastService.ShowError(L["Toast_PositionNotFound"]);
                return;
            }

            var marketReferencePrice = state.MarkPrice > 0 ? state.MarkPrice : (_lastMidPrice ?? 0m);
            if (marketReferencePrice <= 0)
            {
                _toastService.ShowError(L["Toast_CloseNoPrice"]);
                return;
            }

            decimal? price = null;
            if (useLimitPrice)
            {
                var rawPrice = row.ClosePrice?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(rawPrice))
                {
                    rawPrice = NumberText.Trim(marketReferencePrice);
                    row.ClosePrice = rawPrice;
                }

                if (!decimal.TryParse(rawPrice, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedPrice) || parsedPrice <= 0)
                {
                    _toastService.ShowError(L["Toast_ClosePriceInvalid"]);
                    return;
                }

                price = parsedPrice;
                _positionClosePriceInputs[row.Symbol] = rawPrice;
            }

            var notionalForDisplay = state.NotionalUsd > 0
                ? Math.Abs(state.NotionalUsd)
                : Math.Abs(state.Quantity) * marketReferencePrice;
            if (notionalForDisplay <= 0)
            {
                notionalForDisplay = marketReferencePrice;
            }

            var baseQuantity = state.NotionalUsd > 0
                ? Math.Abs(state.NotionalUsd) / marketReferencePrice
                : Math.Abs(state.Quantity);
            if (baseQuantity <= 0 && Math.Abs(state.Quantity) <= 0)
            {
                _toastService.ShowError(L["Toast_CloseQtyZero"]);
                return;
            }

            var localOrderId = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Guid.NewGuid():N}"[..22];
            var side = string.Equals(state.Side, "Short", StringComparison.Ordinal) ? "Buy" : "Sell";
            var sendingStatus = useLimitPrice ? "平倉送單中" : "平倉送單中";
            var syncedStatus = useLimitPrice ? "平倉已送出待同步" : "平倉已送出待同步";

            UpsertPendingOrder(new PendingOrderState(
                localOrderId,
                row.Symbol,
                notionalForDisplay,
                state.Leverage,
                price,
                sendingStatus,
                null));

            try
            {
                var closeQty = Math.Abs(state.Quantity);
                if (closeQty <= 0)
                {
                    _toastService.ShowError(L["Toast_CloseQtyUnavailable"]);
                    RemovePendingOrder(localOrderId);
                    return;
                }

                _logger.Info("WorkspaceTab", $"ClosePosition start tabId={TabId}, symbol={row.Symbol}, side={side}, closeQty={closeQty}, rawPosQty={state.Quantity}, notionalUsd={notionalForDisplay}, useLimit={useLimitPrice}, px={price}");
                var ack = await _venue.PlaceCloseOrderAsync(row.Symbol, side, closeQty, price, _cts.Token);
                if (ack.Success)
                {
                    if (useLimitPrice)
                    {
                        RememberPendingOrderLeverageHint(row.Symbol, state.Leverage, price, notionalForDisplay, ack.ClientOrderId);
                        UpsertPendingOrder(new PendingOrderState(
                            localOrderId,
                            row.Symbol,
                            notionalForDisplay,
                            state.Leverage,
                            price,
                            syncedStatus,
                            ack.ClientOrderId));
                    }
                    else
                    {
                        RemovePendingOrder(localOrderId);
                    }

                    _toastService.ShowInfo(useLimitPrice ? L["Toast_CloseLimitSent"] : L["Toast_CloseMarketSent"]);
                }
                else
                {
                    RemovePendingOrder(localOrderId);
                    _toastService.ShowError($"{L["Toast_CloseFailed"]}{ack.Message}");
                }

                RemovePendingOrder(localOrderId, onlyIfStatusMatches: sendingStatus);
                _ = RefreshAccountStateOnceAsync();
                _logger.Info("WorkspaceTab", $"ClosePosition done tabId={TabId}, symbol={row.Symbol}, success={ack.Success}, msg={ack.Message}");
            }
            catch (Exception ex)
            {
                RemovePendingOrder(localOrderId);
                _ = RefreshAccountStateOnceAsync();
                _toastService.ShowError($"{L["Toast_CloseException"]}{ex.Message}");
                _logger.Error("WorkspaceTab", $"ClosePosition exception tabId={TabId}, symbol={row.Symbol}", ex);
            }
        }
        finally
        {
            EndCloseSubmit(row.Symbol);
        }
    }

    private async Task SubmitClosePositionViaApiSessionAsync(PositionPanelRow? row, bool useLimitPrice, Guid accountId)
    {
        if (row is null || !_positionStates.TryGetValue(row.Symbol, out var state))
        {
            return;
        }

        if (!TryBeginCloseSubmit(row.Symbol))
        {
            _toastService.ShowWarning(L["Toast_CloseInProgress"]);
            _logger.Warn("WorkspaceTab", $"ClosePosition(API shared) skipped duplicate submit tabId={TabId}, symbol={row.Symbol}, useLimit={useLimitPrice}");
            return;
        }

        try
        {
            decimal? price = null;
            if (useLimitPrice)
            {
                var rawPrice = row.ClosePrice?.Trim() ?? string.Empty;
                if (!decimal.TryParse(rawPrice, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedPrice) || parsedPrice <= 0)
                {
                    _toastService.ShowError(L["Toast_ClosePriceInvalid"]);
                    return;
                }

                price = parsedPrice;
                _positionClosePriceInputs[row.Symbol] = rawPrice;
            }

            var localOrderId = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Guid.NewGuid():N}"[..22];
            UpsertPendingOrder(new PendingOrderState(
                localOrderId,
                row.Symbol,
                state.NotionalUsd > 0 ? Math.Abs(state.NotionalUsd) : 0m,
                state.Leverage,
                price,
                "平倉送單中",
                null));

            try
            {
                var orderType = useLimitPrice ? "limit" : "market";
                var result = await _tradingApiService!.ClosePositionAsync(
                    new ApiClosePositionRequest(
                        accountId,
                        row.Symbol,
                        orderType,
                        price),
                    _cts.Token);

                RemovePendingOrder(localOrderId);
                _toastService.ShowInfo(useLimitPrice ? L["Toast_CloseLimitSent"] : L["Toast_CloseMarketSent"]);
                _ = RefreshAccountStateOnceAsync();
                _logger.Info("WorkspaceTab", $"ClosePosition(API shared) done tabId={TabId}, symbol={row.Symbol}, useLimit={useLimitPrice}, result={result}");
            }
            catch (Exception ex)
            {
                RemovePendingOrder(localOrderId);
                _toastService.ShowError($"{L["Toast_CloseFailed"]}{ex.Message}");
                _ = RefreshAccountStateOnceAsync();
                _logger.Error("WorkspaceTab", $"ClosePosition(API shared) exception tabId={TabId}, symbol={row.Symbol}", ex);
            }
        }
        finally
        {
            EndCloseSubmit(row.Symbol);
        }
    }

    private async Task CancelPendingOrderAsync(PendingOrderPanelRow? row)
    {
        if (_isApiSessionManaged && _tradingApiService is not null && _apiSessionAccountId.HasValue)
        {
            await CancelPendingOrderViaApiSessionAsync(row, _apiSessionAccountId.Value);
            return;
        }

        if (row is null || _venue is null || !IsConfigured)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(row.VenueOrderId))
        {
            if (!string.IsNullOrWhiteSpace(row.LocalOrderId))
            {
                _logger.Info("WorkspaceTab", $"CancelPending local-only remove tabId={TabId}, symbol={row.Symbol}, localId={row.LocalOrderId}");
                RemovePendingOrder(row.LocalOrderId);
                _toastService.ShowInfo(L["Toast_LocalPendingRemoved"]);
            }
            else
            {
                _toastService.ShowWarning(L["Toast_OrderNoCancelableId"]);
            }

            return;
        }

        try
        {
            _logger.Info("WorkspaceTab", $"CancelPending start tabId={TabId}, symbol={row.Symbol}, orderId={row.VenueOrderId}");
            var ack = await _venue.CancelOrderAsync(row.Symbol, row.VenueOrderId, _cts.Token);
            if (ack.Success)
            {
                SuppressCanceledOrderId(row.VenueOrderId);
                _remotePendingOrders = _remotePendingOrders
                    .Where(x => !string.Equals(x.VenueOrderId, row.VenueOrderId, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                RebuildPendingOrderRows();

                if (!string.IsNullOrWhiteSpace(row.LocalOrderId))
                {
                    RemovePendingOrder(row.LocalOrderId);
                }

                _toastService.ShowInfo(L["Toast_CancelSuccess"]);
                _ = RefreshAccountStateOnceAsync();
            }
            else
            {
                if ((ack.Message ?? string.Empty).Contains("invalid", StringComparison.OrdinalIgnoreCase))
                {
                    SuppressCanceledOrderId(row.VenueOrderId);
                    _remotePendingOrders = _remotePendingOrders
                        .Where(x => !string.Equals(x.VenueOrderId, row.VenueOrderId, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    RebuildPendingOrderRows();
                    _ = RefreshAccountStateOnceAsync();
                }

                _toastService.ShowError($"{L["Toast_CancelFailed"]}{ack.Message ?? "unknown"}");
            }

            _logger.Info("WorkspaceTab", $"CancelPending done tabId={TabId}, symbol={row.Symbol}, orderId={row.VenueOrderId}, success={ack.Success}, msg={ack.Message}");
        }
        catch (Exception ex)
        {
            _toastService.ShowError($"{L["Toast_CancelException"]}{ex.Message}");
            _logger.Error("WorkspaceTab", $"CancelPending exception tabId={TabId}, symbol={row.Symbol}, orderId={row.VenueOrderId}", ex);
        }
    }

    private async Task CancelPendingOrderViaApiSessionAsync(PendingOrderPanelRow? row, Guid accountId)
    {
        if (row is null || !IsConfigured)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(row.VenueOrderId))
        {
            if (!string.IsNullOrWhiteSpace(row.LocalOrderId))
            {
                _logger.Info("WorkspaceTab", $"CancelPending(API shared) local remove tabId={TabId}, symbol={row.Symbol}, localId={row.LocalOrderId}");
                RemovePendingOrder(row.LocalOrderId);
                _toastService.ShowInfo(L["Toast_LocalPendingRemoved"]);
            }
            else
            {
                _toastService.ShowWarning(L["Toast_OrderNoCancelableId"]);
            }

            return;
        }

        try
        {
            await _tradingApiService!.CancelOrderAsync(new ApiCancelOrderRequest(accountId, row.Symbol, row.VenueOrderId), _cts.Token);
            SuppressCanceledOrderId(row.VenueOrderId);
            _remotePendingOrders = _remotePendingOrders
                .Where(x => !string.Equals(x.VenueOrderId, row.VenueOrderId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            RebuildPendingOrderRows();

            if (!string.IsNullOrWhiteSpace(row.LocalOrderId))
            {
                RemovePendingOrder(row.LocalOrderId);
            }

            _toastService.ShowInfo(L["Toast_CancelSuccess"]);
            _ = RefreshAccountStateOnceAsync();
        }
        catch (Exception ex)
        {
            if ((ex.Message ?? string.Empty).Contains("invalid", StringComparison.OrdinalIgnoreCase))
            {
                SuppressCanceledOrderId(row.VenueOrderId);
                _remotePendingOrders = _remotePendingOrders
                    .Where(x => !string.Equals(x.VenueOrderId, row.VenueOrderId, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                RebuildPendingOrderRows();
                _ = RefreshAccountStateOnceAsync();
            }

            _toastService.ShowError($"{L["Toast_CancelFailed"]}{ex.Message}");
            _logger.Error("WorkspaceTab", $"CancelPending(API shared) exception tabId={TabId}, symbol={row.Symbol}, orderId={row.VenueOrderId}", ex);
        }
    }

}
