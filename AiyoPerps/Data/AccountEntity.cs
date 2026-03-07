using System;
using System.ComponentModel.DataAnnotations;

namespace AiyoPerps.Data;

public sealed class AccountEntity
{
    [Key]
    public Guid AccountId { get; set; }

    [MaxLength(32)]
    public required string VenueId { get; set; }

    [MaxLength(128)]
    public required string DisplayName { get; set; }

    [MaxLength(32)]
    public required string Environment { get; set; }

    [MaxLength(256)]
    public required string Summary { get; set; }

    [MaxLength(16)]
    public string? AuthMode { get; set; }

    [MaxLength(2048)]
    public string? ApiKeyEncrypted { get; set; }

    [MaxLength(2048)]
    public string? ApiSecretEncrypted { get; set; }

    [MaxLength(128)]
    public string? AccountAddress { get; set; }

    [MaxLength(128)]
    public string? SubAccountId { get; set; }

    [MaxLength(128)]
    public string? WalletAddress { get; set; }

    [MaxLength(2048)]
    public string? PrivateKeyEncrypted { get; set; }

    public bool IsEnabled { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastTestedAt { get; set; }
}
