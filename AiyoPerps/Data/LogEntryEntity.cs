using System;
using System.ComponentModel.DataAnnotations;

namespace AiyoPerps.Data;

public sealed class LogEntryEntity
{
    [Key]
    public long Id { get; set; }

    public DateTimeOffset Timestamp { get; set; }

    [MaxLength(16)]
    public required string Level { get; set; }

    [MaxLength(64)]
    public required string Source { get; set; }

    [MaxLength(512)]
    public required string Message { get; set; }

    public string? Exception { get; set; }
}
