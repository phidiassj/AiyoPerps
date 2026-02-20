using System;
using System.Threading;

namespace AiyoPerps.Services;

public sealed class RetentionScheduler : IDisposable
{
    private readonly RetentionJob _retentionJob;
    private readonly int _retentionDays;
    private readonly Timer _timer;

    public RetentionScheduler(RetentionJob retentionJob, int retentionDays = 365)
    {
        _retentionJob = retentionJob;
        _retentionDays = retentionDays;
        _timer = new Timer(_ => Execute(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Start()
    {
        Execute();
        _timer.Change(TimeSpan.FromDays(1), TimeSpan.FromDays(1));
    }

    public void Dispose()
    {
        _timer.Dispose();
    }

    private void Execute()
    {
        try
        {
            _retentionJob.MoveOldCandlesToArchive(_retentionDays);
        }
        catch
        {
            // Swallow in MVP skeleton; wire to logger later.
        }
    }
}
