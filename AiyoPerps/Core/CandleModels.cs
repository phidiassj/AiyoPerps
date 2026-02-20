using System;

namespace AiyoPerps.Core;

public enum CandleInterval
{
    M5,
    M10,
    M15,
    M30,
    H1,
    H2,
    H4,
    H6,
    H12,
    D1,
    D7,
    D30
}

public sealed record Candle(
    string VenueId,
    string Symbol,
    CandleInterval Interval,
    DateTimeOffset OpenTime,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume,
    bool IsClosed);

public sealed record CandleUpdate(Candle Candle, bool IsClosed);
