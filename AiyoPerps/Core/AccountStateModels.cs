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
    decimal RealizedPnlUsd,
    MarginMode MarginMode = MarginMode.Unknown);

public sealed record VenueOpenOrder(
    string Symbol,
    decimal NotionalUsd,
    decimal Leverage,
    decimal? LimitPrice,
    string Status,
    string? OrderId,
    MarginMode MarginMode = MarginMode.Unknown);

public sealed record VenueBalance(
    string Asset,
    decimal Quantity,
    decimal UsdValue);

public sealed record VenueAccountSnapshot(
    DateTimeOffset Timestamp,
    IReadOnlyList<VenuePosition> Positions,
    IReadOnlyList<VenueOpenOrder> OpenOrders,
    IReadOnlyList<VenueBalance> Balances);

[Flags]
public enum AccountSnapshotSections
{
    None = 0,
    Positions = 1,
    Orders = 2,
    Balances = 4,
    All = Positions | Orders | Balances
}

public interface IAccountStateProvider
{
    Task<VenueAccountSnapshot> GetAccountSnapshotAsync(AccountSnapshotSections sections, CancellationToken cancellationToken = default);

    Task<VenueAccountSnapshot> GetAccountSnapshotAsync(CancellationToken cancellationToken = default)
    {
        return GetAccountSnapshotAsync(AccountSnapshotSections.All, cancellationToken);
    }
}
