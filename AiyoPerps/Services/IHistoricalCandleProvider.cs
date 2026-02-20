using AiyoPerps.Core;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AiyoPerps.Services;

public interface IHistoricalCandleProvider
{
    Task<IReadOnlyList<Candle>> GetRecentCandlesAsync(string symbol, CandleInterval interval, int count, CancellationToken cancellationToken = default);
}
