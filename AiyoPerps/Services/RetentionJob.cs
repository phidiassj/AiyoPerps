using AiyoPerps.Data;
using System;
using System.Linq;

namespace AiyoPerps.Services;

public sealed class RetentionJob
{
    public int MoveOldCandlesToArchive(int retentionDays)
    {
        // MVP skeleton: currently only prunes from main DB boundary and returns candidate count.
        using var db = new AppDbContext();
        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays);
        var candidates = db.Candles.Count(x => x.OpenTime < cutoff && x.IsClosed);
        return candidates;
    }
}
