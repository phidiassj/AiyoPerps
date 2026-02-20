using AiyoPerps.Core;
using AiyoPerps.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AiyoPerps.Services.Api;

internal sealed class ApiConnectionSession : IAsyncDisposable
{
    private readonly IPerpVenue _venue;
    private readonly AppLogger _logger;
    private readonly CandleAggregator _aggregator = new();
    private readonly object _sync = new();
    private readonly Dictionary<CandleInterval, IntervalState> _intervals = new();
    private readonly CancellationTokenSource _cts = new();

    private Task? _pumpTask;
    private bool _started;

    public ApiConnectionSession(string connectionId, AccountProfile account, string symbol, CandleInterval interval, IPerpVenue venue, AppLogger logger)
    {
        ConnectionId = connectionId;
        Account = account;
        Symbol = symbol.Trim().ToUpperInvariant();
        DefaultInterval = interval;
        _venue = venue;
        _logger = logger;
        StartedAt = DateTimeOffset.UtcNow;
        StatusMessage = "Created";
    }

    public string ConnectionId { get; }
    public AccountProfile Account { get; }
    public string Symbol { get; }
    public CandleInterval DefaultInterval { get; }
    public IPerpVenue Venue => _venue;
    public DateTimeOffset StartedAt { get; }
    public decimal? LatestPrice { get; private set; }
    public bool IsConnected { get; private set; }
    public string StatusMessage { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_started)
        {
            return;
        }

        _started = true;
        await _venue.ConnectMarketDataAsync([Symbol], cancellationToken);
        IsConnected = true;
        StatusMessage = "Connected";
        _logger.Info("ApiConnection", $"Session connected id={ConnectionId}, account={Account.AccountId}, symbol={Symbol}");

        if (_venue is IHistoricalCandleProvider history)
        {
            await EnsureIntervalLoadedAsync(DefaultInterval, history, cancellationToken);
        }

        _pumpTask = Task.Run(PumpLoopAsync, _cts.Token);
    }

    public async Task EnsureIntervalLoadedAsync(CandleInterval interval, CancellationToken cancellationToken = default)
    {
        if (_intervals.ContainsKey(interval))
        {
            return;
        }

        if (_venue is IHistoricalCandleProvider history)
        {
            await EnsureIntervalLoadedAsync(interval, history, cancellationToken);
            return;
        }

        lock (_sync)
        {
            if (!_intervals.ContainsKey(interval))
            {
                _intervals[interval] = new IntervalState(interval);
            }
        }
    }

    public (long Cursor, decimal? LatestPrice, IReadOnlyList<ApiCandleDto> InitialCandles, IReadOnlyList<ApiCandleDto> DeltaCandles, bool HasDelta) GetMarketData(CandleInterval interval, long? cursor)
    {
        lock (_sync)
        {
            if (!_intervals.TryGetValue(interval, out var state))
            {
                state = new IntervalState(interval);
                _intervals[interval] = state;
            }

            var currentCursor = state.Version;
            if (!cursor.HasValue || cursor.Value <= 0)
            {
                var initial = state.Candles.Values.Select(ToApiCandle).ToList();
                return (currentCursor, LatestPrice, initial, [], false);
            }

            if (cursor.Value >= currentCursor)
            {
                return (currentCursor, LatestPrice, [], [], false);
            }

            var delta = state.Changes
                .Where(x => x.Version > cursor.Value)
                .GroupBy(x => x.Candle.OpenTime)
                .Select(g => g.OrderByDescending(x => x.Version).First().Candle)
                .OrderBy(x => x.OpenTime)
                .Select(ToApiCandle)
                .ToList();

            return (currentCursor, LatestPrice, [], delta, delta.Count > 0);
        }
    }

    public long GetCursor(CandleInterval interval)
    {
        lock (_sync)
        {
            return _intervals.TryGetValue(interval, out var state) ? state.Version : 0;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!_started)
        {
            return;
        }

        _cts.Cancel();
        try
        {
            await _venue.DisconnectMarketDataAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.Warn("ApiConnection", $"Disconnect warning id={ConnectionId}: {ex.Message}");
        }

        if (_pumpTask is not null)
        {
            try
            {
                await _pumpTask;
            }
            catch (Exception ex)
            {
                _logger.Warn("ApiConnection", $"Pump end warning id={ConnectionId}: {ex.Message}");
            }
        }

        IsConnected = false;
        StatusMessage = "Closed";
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        await _venue.DisposeAsync();
        _cts.Dispose();
    }

    public ApiConnectionDto ToDto()
    {
        return new ApiConnectionDto(
            ConnectionId,
            Account.AccountId,
            Account.VenueId,
            Account.Environment,
            Symbol,
            ApiIntervalParser.ToText(DefaultInterval),
            StartedAt,
            LatestPrice,
            GetCursor(DefaultInterval),
            IsConnected,
            StatusMessage);
    }

    private async Task EnsureIntervalLoadedAsync(CandleInterval interval, IHistoricalCandleProvider history, CancellationToken cancellationToken)
    {
        var count = GetInitialCandleCount(interval);
        var candles = await history.GetRecentCandlesAsync(Symbol, interval, count, cancellationToken);
        lock (_sync)
        {
            if (_intervals.ContainsKey(interval))
            {
                return;
            }

            var state = new IntervalState(interval);
            foreach (var candle in candles.OrderBy(x => x.OpenTime))
            {
                state.Upsert(candle, fromHistorical: true);
            }

            _intervals[interval] = state;
        }
    }

    private async Task PumpLoopAsync()
    {
        try
        {
            await foreach (var marketEvent in _venue.MarketEvents(_cts.Token))
            {
                if (marketEvent is not TradeTick tick)
                {
                    continue;
                }

                LatestPrice = tick.Price;

                lock (_sync)
                {
                    foreach (var kv in _intervals)
                    {
                        var state = kv.Value;
                        var update = _aggregator.Aggregate(Account.VenueId, Symbol, state.Interval, tick, state.Current);
                        state.Current = update.Candle;
                        state.Upsert(update.Candle, fromHistorical: false);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
        catch (Exception ex)
        {
            StatusMessage = $"Pump error: {ex.Message}";
            _logger.Error("ApiConnection", $"Pump failed id={ConnectionId}", ex);
        }
    }

    private static ApiCandleDto ToApiCandle(Candle c)
    {
        return new ApiCandleDto(
            c.OpenTime.ToUnixTimeMilliseconds(),
            c.Open,
            c.High,
            c.Low,
            c.Close,
            c.Volume,
            c.IsClosed);
    }

    private static int GetInitialCandleCount(CandleInterval interval)
    {
        var minutes = interval switch
        {
            CandleInterval.M5 => 5,
            CandleInterval.M10 => 10,
            CandleInterval.M15 => 15,
            CandleInterval.M30 => 30,
            CandleInterval.H1 => 60,
            CandleInterval.H2 => 120,
            CandleInterval.H4 => 240,
            CandleInterval.H6 => 360,
            CandleInterval.H12 => 720,
            CandleInterval.D1 => 1440,
            CandleInterval.D7 => 10080,
            CandleInterval.D30 => 43200,
            _ => 5
        };

        var twelveHours = 12 * 60;
        var count = (int)Math.Ceiling(twelveHours / (double)minutes) + 2;
        return Math.Max(8, count);
    }

    private sealed class IntervalState
    {
        public IntervalState(CandleInterval interval)
        {
            Interval = interval;
        }

        public CandleInterval Interval { get; }
        public SortedDictionary<DateTimeOffset, Candle> Candles { get; } = new();
        public List<ChangedCandle> Changes { get; } = [];
        public Candle? Current { get; set; }
        public long Version { get; private set; }

        public void Upsert(Candle candle, bool fromHistorical)
        {
            Candles[candle.OpenTime] = candle;
            while (Candles.Count > 1500)
            {
                var first = Candles.First();
                Candles.Remove(first.Key);
            }

            if (fromHistorical)
            {
                return;
            }

            Version++;
            Changes.Add(new ChangedCandle(Version, candle));
            if (Changes.Count > 4000)
            {
                Changes.RemoveRange(0, Changes.Count - 4000);
            }
        }
    }

    private sealed record ChangedCandle(long Version, Candle Candle);
}
