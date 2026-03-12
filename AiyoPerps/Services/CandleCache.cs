using AiyoPerps.Core;
using AiyoPerps.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AiyoPerps.Services;

public sealed class CandleCache(int maxCandlesPerKey = 1500)
{
    private readonly int _maxCandlesPerKey = maxCandlesPerKey;
    private readonly Dictionary<(string VenueId, string Symbol, CandleInterval Interval), SortedDictionary<DateTimeOffset, Candle>> _cache = [];

    public IReadOnlyList<Candle> Get(string venueId, string symbol, CandleInterval interval)
    {
        if (_cache.TryGetValue((venueId, symbol, interval), out var map))
        {
            return map.Values.ToList();
        }

        return [];
    }

    public CandleViewPoint[] GetTailViewPoints(string venueId, string symbol, CandleInterval interval, int maxCount)
    {
        if (maxCount <= 0)
        {
            return [];
        }

        if (!_cache.TryGetValue((venueId, symbol, interval), out var map) || map.Count == 0)
        {
            return [];
        }

        var resultCount = Math.Min(maxCount, map.Count);
        var skip = map.Count - resultCount;
        var points = new CandleViewPoint[resultCount];
        var currentIndex = 0;
        var pointIndex = 0;

        foreach (var candle in map.Values)
        {
            if (currentIndex++ < skip)
            {
                continue;
            }

            points[pointIndex++] = new CandleViewPoint(
                candle.OpenTime,
                candle.Open,
                candle.High,
                candle.Low,
                candle.Close);
        }

        return points;
    }

    public void Upsert(Candle candle)
    {
        var key = (candle.VenueId, candle.Symbol, candle.Interval);
        if (!_cache.TryGetValue(key, out var map))
        {
            map = new SortedDictionary<DateTimeOffset, Candle>();
            _cache[key] = map;
        }

        map[candle.OpenTime] = candle;

        while (map.Count > _maxCandlesPerKey)
        {
            var oldest = map.Keys.First();
            map.Remove(oldest);
        }
    }

    public void Clear(string venueId, string symbol, CandleInterval interval)
    {
        _cache.Remove((venueId, symbol, interval));
    }
}
