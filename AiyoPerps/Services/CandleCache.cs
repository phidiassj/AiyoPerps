using AiyoPerps.Core;
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
