using System;

namespace AiyoPerps.Models;

public sealed class AccountProfile
{
    public Guid AccountId { get; init; } = Guid.NewGuid();
    public required string VenueId { get; init; }
    public required string DisplayName { get; set; }
    public required string Environment { get; set; }
    public required string Summary { get; set; }
    public bool IsEnabled { get; set; } = true;
    public bool HasApiCredentials { get; set; }
    public bool HasWalletCredentials { get; set; }

    public string Label => $"{DisplayName} ({VenueId}/{Environment}) {Summary}{(HasApiCredentials ? " [API]" : string.Empty)}{(HasWalletCredentials ? " [Wallet]" : string.Empty)}";
}
