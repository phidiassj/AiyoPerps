using AiyoPerps.Core;
using AiyoPerps.Data;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AiyoPerps.Services;

public sealed class CandleRepository
{
    public IReadOnlyList<Candle> LoadRecent(string venueId, string symbol, CandleInterval interval, int count)
    {
        DbSchemaBootstrapper.EnsureSchema();
        var intervalText = IntervalToText(interval);

        using var db = new AppDbContext();
        return db.Candles
            .AsNoTracking()
            .Where(x => x.VenueId == venueId && x.Symbol == symbol && x.Interval == intervalText)
            .AsEnumerable()
            .OrderByDescending(x => x.OpenTime)
            .Take(count)
            .OrderBy(x => x.OpenTime)
            .Select(ToDomain)
            .ToList();
    }

    public int CountSince(string venueId, string symbol, CandleInterval interval, DateTimeOffset sinceUtc)
    {
        DbSchemaBootstrapper.EnsureSchema();
        var intervalText = IntervalToText(interval);

        using var db = new AppDbContext();
        return db.Candles
            .AsNoTracking()
            .Where(x => x.VenueId == venueId && x.Symbol == symbol && x.Interval == intervalText)
            .AsEnumerable()
            .Count(x => x.OpenTime >= sinceUtc);
    }

    public int DeleteSince(string venueId, string symbol, CandleInterval interval, DateTimeOffset sinceUtc)
    {
        DbSchemaBootstrapper.EnsureSchema();
        var intervalText = IntervalToText(interval);

        using var db = new AppDbContext();
        var toDelete = db.Candles
            .Where(x => x.VenueId == venueId && x.Symbol == symbol && x.Interval == intervalText)
            .AsEnumerable()
            .Where(x => x.OpenTime >= sinceUtc)
            .ToList();

        if (toDelete.Count == 0)
        {
            return 0;
        }

        db.Candles.RemoveRange(toDelete);
        db.SaveChanges();
        return toDelete.Count;
    }

    public void Upsert(Candle candle)
    {
        DbSchemaBootstrapper.EnsureSchema();
        using var db = new AppDbContext();
        var intervalText = IntervalToText(candle.Interval);

        var existing = db.Candles.SingleOrDefault(x =>
            x.VenueId == candle.VenueId &&
            x.Symbol == candle.Symbol &&
            x.Interval == intervalText &&
            x.OpenTime == candle.OpenTime);

        if (existing is null)
        {
            db.Candles.Add(new CandleEntity
            {
                VenueId = candle.VenueId,
                Symbol = candle.Symbol,
                Interval = intervalText,
                OpenTime = candle.OpenTime,
                Open = candle.Open,
                High = candle.High,
                Low = candle.Low,
                Close = candle.Close,
                Volume = candle.Volume,
                IsClosed = candle.IsClosed
            });
        }
        else
        {
            existing.Open = candle.Open;
            existing.High = candle.High;
            existing.Low = candle.Low;
            existing.Close = candle.Close;
            existing.Volume = candle.Volume;
            existing.IsClosed = candle.IsClosed;
        }

        db.SaveChanges();
    }

    private static string IntervalToText(CandleInterval interval)
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

    private static Candle ToDomain(CandleEntity entity)
    {
        return new Candle(
            entity.VenueId,
            entity.Symbol,
            TextToInterval(entity.Interval),
            entity.OpenTime,
            entity.Open,
            entity.High,
            entity.Low,
            entity.Close,
            entity.Volume,
            entity.IsClosed);
    }

    private static CandleInterval TextToInterval(string interval)
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
}
