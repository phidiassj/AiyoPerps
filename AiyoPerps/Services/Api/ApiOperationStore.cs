using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace AiyoPerps.Services.Api;

public sealed class ApiOperationStore
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public ApiOperationResult Enqueue(string name, Func<CancellationToken, Task<object?>> work, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid().ToString("N");
        var entry = new Entry(id, name);
        _entries[id] = entry;

        _ = Task.Run(async () =>
        {
            entry.Status = ApiOperationStatus.Running;
            try
            {
                var result = await work(cancellationToken);
                entry.Result = result;
                entry.Status = ApiOperationStatus.Succeeded;
            }
            catch (Exception ex)
            {
                entry.Error = ex.Message;
                entry.Status = ApiOperationStatus.Failed;
            }
            finally
            {
                entry.CompletedAt = DateTimeOffset.UtcNow;
            }
        }, cancellationToken);

        return entry.ToResult();
    }

    public ApiOperationResult? Get(string operationId)
    {
        return _entries.TryGetValue(operationId, out var entry)
            ? entry.ToResult()
            : null;
    }

    private sealed class Entry
    {
        public Entry(string operationId, string name)
        {
            OperationId = operationId;
            Name = name;
            CreatedAt = DateTimeOffset.UtcNow;
            Status = ApiOperationStatus.Pending;
        }

        public string OperationId { get; }
        public string Name { get; }
        public ApiOperationStatus Status { get; set; }
        public DateTimeOffset CreatedAt { get; }
        public DateTimeOffset? CompletedAt { get; set; }
        public object? Result { get; set; }
        public string? Error { get; set; }

        public ApiOperationResult ToResult()
            => new(
                OperationId,
                Name,
                Status,
                CreatedAt,
                CompletedAt,
                Result,
                Error);
    }
}
