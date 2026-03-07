using AiyoPerps.Core;
using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AiyoPerps.Services.Api;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ApiOperationStatus
{
    Pending,
    Running,
    Succeeded,
    Failed
}

public sealed record ApiOperationResult(
    string OperationId,
    string Name,
    ApiOperationStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    object? Result,
    string? Error);

public sealed record ApiAccountDto(
    Guid AccountId,
    string VenueId,
    string DisplayName,
    string Environment,
    string Summary,
    string AuthMode,
    string? SubAccountId,
    bool IsEnabled,
    bool HasApiCredentials,
    bool HasWalletCredentials);

public sealed record ApiAccountUpsertRequest(
    string VenueId,
    string DisplayName,
    string Environment,
    string Summary,
    string? AuthMode,
    string? ApiKey,
    string? ApiSecret,
    string? AccountAddress,
    string? SubAccountId,
    string? WalletAddress,
    string? PrivateKey,
    bool? IsEnabled);

public sealed record ApiConnectionOpenRequest(Guid AccountId, string Symbol, string Interval);
public sealed record ApiConnectionCloseRequest(Guid AccountId, string Symbol);

public sealed record ApiConnectionDto(
    string ConnectionId,
    Guid AccountId,
    string VenueId,
    string Environment,
    string Symbol,
    string Interval,
    DateTimeOffset StartedAt,
    decimal? LatestPrice,
    long Cursor,
    bool IsConnected,
    string StatusMessage);

public sealed record ApiCandleDto(
    long OpenTimeMs,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume,
    bool IsClosed);

public sealed record ApiMarketDataResponse(
    Guid AccountId,
    string Symbol,
    string Interval,
    long Cursor,
    decimal? LatestPrice,
    IReadOnlyList<ApiCandleDto> InitialCandles,
    IReadOnlyList<ApiCandleDto> DeltaCandles,
    bool HasDelta);

public sealed record ApiOpenPositionRequest(
    Guid AccountId,
    string Symbol,
    string Side,
    string OrderType,
    decimal Leverage,
    decimal Amount,
    string AmountUnit,
    decimal? LimitPrice);

public sealed record ApiClosePositionRequest(
    Guid AccountId,
    string PositionId,
    string OrderType,
    decimal? LimitPrice);

public sealed record ApiCancelOrderRequest(Guid AccountId, string Symbol, string OrderId);

public sealed record ApiPositionDto(
    string PositionId,
    string Symbol,
    decimal Quantity,
    decimal NotionalUsd,
    decimal Leverage,
    decimal EntryPrice,
    decimal MarkPrice,
    decimal UnrealizedPnlPct,
    decimal UnrealizedPnlUsd,
    decimal RealizedPnlUsd);

public sealed record ApiOpenOrderDto(
    string Symbol,
    decimal NotionalUsd,
    decimal Leverage,
    decimal? LimitPrice,
    string Status,
    string? OrderId);

public sealed record ApiBalanceDto(string Asset, decimal Quantity, decimal UsdValue);

public sealed record ApiStressRunRequest(
    Guid AccountId,
    string Symbol,
    string? Interval,
    int? Concurrency,
    int? Iterations);

public static class ApiIntervalParser
{
    public static CandleInterval ParseOrDefault(string? text)
    {
        var s = (text ?? string.Empty).Trim().ToLowerInvariant();
        return s switch
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

    public static string ToText(CandleInterval interval)
    {
        return interval switch
        {
            CandleInterval.M5 => "5m",
            CandleInterval.M10 => "10m",
            CandleInterval.M15 => "15m",
            CandleInterval.M30 => "30m",
            CandleInterval.H1 => "1h",
            CandleInterval.H2 => "2h",
            CandleInterval.H4 => "4h",
            CandleInterval.H6 => "6h",
            CandleInterval.H12 => "12h",
            CandleInterval.D1 => "1d",
            CandleInterval.D7 => "7d",
            CandleInterval.D30 => "30d",
            _ => "5m"
        };
    }
}
