using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AiyoPerps.Core;

public sealed record VenuePosition(
    string Symbol,
    decimal Quantity,
    decimal NotionalUsd,
    decimal Leverage,
    decimal EntryPrice,
    decimal MarkPrice,
    decimal UnrealizedPnlPct,
    decimal UnrealizedPnlUsd,
    decimal RealizedPnlUsd);

public sealed record VenueOpenOrder(
    string Symbol,
    decimal NotionalUsd,
    decimal Leverage,
    decimal? LimitPrice,
    string Status,
    string? OrderId);

public sealed record VenueBalance(
    string Asset,
    decimal Quantity,
    decimal UsdValue);

public sealed record VenueAccountSnapshot(
    DateTimeOffset Timestamp,
    IReadOnlyList<VenuePosition> Positions,
    IReadOnlyList<VenueOpenOrder> OpenOrders,
    IReadOnlyList<VenueBalance> Balances);

public interface IAccountStateProvider
{
    Task<VenueAccountSnapshot> GetAccountSnapshotAsync(CancellationToken cancellationToken = default);
}
