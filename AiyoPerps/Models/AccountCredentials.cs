using System;

namespace AiyoPerps.Models;

public sealed class AccountCredentials
{
    public Guid AccountId { get; init; }
    public string? ApiKey { get; init; }
    public string? ApiSecret { get; init; }
    public string? AccountAddress { get; init; }
    public string? SubAccountId { get; init; }
    public string? WalletAddress { get; init; }
    public string? PrivateKey { get; init; }
    public string AuthMode { get; init; } = "Both";

    public bool HasApiCredentials =>
        !string.IsNullOrWhiteSpace(ApiKey) &&
        !string.IsNullOrWhiteSpace(ApiSecret);

    public bool HasWalletCredentials =>
        !string.IsNullOrWhiteSpace(WalletAddress) &&
        !string.IsNullOrWhiteSpace(PrivateKey);
}
