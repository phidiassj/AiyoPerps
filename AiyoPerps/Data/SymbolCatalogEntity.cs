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

    [MaxLength(128)]
    public string? CanonicalKey { get; set; }

    [MaxLength(32)]
    public string? BaseAsset { get; set; }

    [MaxLength(32)]
    public string? QuoteAsset { get; set; }

    [MaxLength(32)]
    public string? SettleAsset { get; set; }

    [MaxLength(32)]
    public string? ContractType { get; set; }

    [MaxLength(96)]
    public string? DisplaySymbol { get; set; }

    public bool IsActive { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? LastActivatedAt { get; set; }
}
