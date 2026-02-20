using AiyoPerps.Core;
using System;

namespace AiyoPerps.Services;

public sealed class CandleAggregator
{
    public CandleUpdate Aggregate(string venueId, string symbol, CandleInterval interval, TradeTick tick, Candle? current)
    {
        var bucketStart = GetBucketStart(tick.Timestamp, interval);
        if (current is null || current.OpenTime != bucketStart)
        {
            return new CandleUpdate(CreateNew(venueId, symbol, interval, bucketStart, tick), false);
        }

        var updated = current with
        {
            High = Math.Max(current.High, tick.Price),
            Low = Math.Min(current.Low, tick.Price),
            Close = tick.Price,
            Volume = current.Volume + tick.Size,
            IsClosed = false
        };

        return new CandleUpdate(updated, false);
    }

    public Candle CloseCurrent(Candle current)
    {
        return current with { IsClosed = true };
    }

    private static Candle CreateNew(string venueId, string symbol, CandleInterval interval, DateTimeOffset bucketStart, TradeTick tick)
    {
        return new Candle(
            venueId,
            symbol,
            interval,
            bucketStart,
            tick.Price,
            tick.Price,
            tick.Price,
            tick.Price,
            tick.Size,
            false);
    }

    private static DateTimeOffset GetBucketStart(DateTimeOffset timestamp, CandleInterval interval)
    {
        var utc = timestamp.ToUniversalTime();
        var unit = interval switch
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

        var ticks = utc.Ticks - (utc.Ticks % unit.Ticks);
        return new DateTimeOffset(ticks, TimeSpan.Zero);
    }
}
