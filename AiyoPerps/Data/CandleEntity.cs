using System;
using System.ComponentModel.DataAnnotations;

namespace AiyoPerps.Data;

public sealed class CandleEntity
{
    [Key]
    public long Id { get; set; }

    [MaxLength(32)]
    public required string VenueId { get; set; }

    [MaxLength(64)]
    public required string Symbol { get; set; }

    [MaxLength(16)]
    public required string Interval { get; set; }

    public DateTimeOffset OpenTime { get; set; }
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }
    public decimal Volume { get; set; }
    public bool IsClosed { get; set; }
}
