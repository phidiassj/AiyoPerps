using System;
using System.ComponentModel.DataAnnotations;

namespace AiyoPerps.Data;

public sealed class SymbolCatalogEntity
{
    [Key]
    public long Id { get; set; }

    [MaxLength(32)]
    public required string VenueId { get; set; }

    [MaxLength(16)]
    public required string Environment { get; set; }

    [MaxLength(64)]
    public required string Symbol { get; set; }

    public bool IsActive { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? LastActivatedAt { get; set; }
}
