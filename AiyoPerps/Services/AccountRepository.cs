using AiyoPerps.Data;
using AiyoPerps.Models;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AiyoPerps.Services;

public sealed class AccountRepository(ISecretProtector protector)
{
    private readonly ISecretProtector _protector = protector;

    public void EnsureDatabase()
    {
        DbSchemaBootstrapper.EnsureSchema();
    }

    public List<AccountProfile> GetAll()
    {
        using var db = new AppDbContext();
        return db.Accounts
            .AsNoTracking()
            .OrderBy(x => x.DisplayName)
            .Select(x => new AccountProfile
            {
                AccountId = x.AccountId,
                VenueId = x.VenueId,
                DisplayName = x.DisplayName,
                Environment = x.Environment,
                Summary = x.Summary,
                IsEnabled = x.IsEnabled,
                HasApiCredentials = !string.IsNullOrWhiteSpace(x.ApiKeyEncrypted) && !string.IsNullOrWhiteSpace(x.ApiSecretEncrypted),
                HasWalletCredentials = !string.IsNullOrWhiteSpace(x.WalletAddress) && !string.IsNullOrWhiteSpace(x.PrivateKeyEncrypted)
            })
            .ToList();
    }

    public AccountProfile? GetById(Guid accountId)
    {
        using var db = new AppDbContext();
        var entity = db.Accounts.AsNoTracking().SingleOrDefault(x => x.AccountId == accountId);
        if (entity is null)
        {
            return null;
        }

        return new AccountProfile
        {
            AccountId = entity.AccountId,
            VenueId = entity.VenueId,
            DisplayName = entity.DisplayName,
            Environment = entity.Environment,
            Summary = entity.Summary,
            IsEnabled = entity.IsEnabled,
            HasApiCredentials = !string.IsNullOrWhiteSpace(entity.ApiKeyEncrypted) && !string.IsNullOrWhiteSpace(entity.ApiSecretEncrypted),
            HasWalletCredentials = !string.IsNullOrWhiteSpace(entity.WalletAddress) && !string.IsNullOrWhiteSpace(entity.PrivateKeyEncrypted)
        };
    }

    public void Add(AccountProfile account, string? apiKey, string? apiSecret, string? accountAddress, string? walletAddress, string? privateKey)
    {
        using var db = new AppDbContext();
        var entity = new AccountEntity
        {
            AccountId = account.AccountId,
            VenueId = account.VenueId,
            DisplayName = account.DisplayName,
            Environment = account.Environment,
            Summary = account.Summary,
            IsEnabled = account.IsEnabled,
            CreatedAt = DateTimeOffset.UtcNow,
            ApiKeyEncrypted = EncryptOrNull(apiKey),
            ApiSecretEncrypted = EncryptOrNull(apiSecret),
            AccountAddress = NormalizeOrNull(accountAddress),
            WalletAddress = NormalizeOrNull(walletAddress),
            PrivateKeyEncrypted = EncryptOrNull(privateKey)
        };

        db.Accounts.Add(entity);
        db.SaveChanges();
    }

    public AccountCredentials GetCredentials(Guid accountId)
    {
        using var db = new AppDbContext();
        var entity = db.Accounts.AsNoTracking().Single(x => x.AccountId == accountId);

        return new AccountCredentials
        {
            AccountId = accountId,
            ApiKey = DecryptOrNull(entity.ApiKeyEncrypted),
            ApiSecret = DecryptOrNull(entity.ApiSecretEncrypted),
            AccountAddress = NormalizeOrNull(entity.AccountAddress),
            WalletAddress = NormalizeOrNull(entity.WalletAddress),
            PrivateKey = DecryptOrNull(entity.PrivateKeyEncrypted)
        };
    }

    public void UpdateEnabled(Guid accountId, bool isEnabled)
    {
        using var db = new AppDbContext();
        var entity = db.Accounts.Single(x => x.AccountId == accountId);
        entity.IsEnabled = isEnabled;
        db.SaveChanges();
    }

    public void UpdateCredentials(Guid accountId, string? apiKey, string? apiSecret, string? accountAddress, string? walletAddress, string? privateKey)
    {
        using var db = new AppDbContext();
        var entity = db.Accounts.Single(x => x.AccountId == accountId);
        entity.ApiKeyEncrypted = EncryptOrNull(apiKey);
        entity.ApiSecretEncrypted = EncryptOrNull(apiSecret);
        entity.AccountAddress = NormalizeOrNull(accountAddress);
        entity.WalletAddress = NormalizeOrNull(walletAddress);
        entity.PrivateKeyEncrypted = EncryptOrNull(privateKey);
        entity.LastTestedAt = DateTimeOffset.UtcNow;
        db.SaveChanges();
    }

    public void UpdateAccount(
        Guid accountId,
        string venueId,
        string displayName,
        string environment,
        string summary,
        string? apiKey,
        string? apiSecret,
        string? accountAddress,
        string? walletAddress,
        string? privateKey,
        bool isEnabled)
    {
        using var db = new AppDbContext();
        var entity = db.Accounts.Single(x => x.AccountId == accountId);
        entity.VenueId = venueId.Trim();
        entity.DisplayName = displayName.Trim();
        entity.Environment = environment.Trim();
        entity.Summary = summary.Trim();
        entity.IsEnabled = isEnabled;
        entity.ApiKeyEncrypted = EncryptOrNull(apiKey);
        entity.ApiSecretEncrypted = EncryptOrNull(apiSecret);
        entity.AccountAddress = NormalizeOrNull(accountAddress);
        entity.WalletAddress = NormalizeOrNull(walletAddress);
        entity.PrivateKeyEncrypted = EncryptOrNull(privateKey);
        entity.LastTestedAt = DateTimeOffset.UtcNow;
        db.SaveChanges();
    }

    public void Delete(Guid accountId)
    {
        using var db = new AppDbContext();
        var entity = db.Accounts.Single(x => x.AccountId == accountId);
        db.Accounts.Remove(entity);
        db.SaveChanges();
    }

    private string? EncryptOrNull(string? plain)
    {
        if (string.IsNullOrWhiteSpace(plain))
        {
            return null;
        }

        return _protector.Protect(plain.Trim());
    }

    private string? DecryptOrNull(string? cipher)
    {
        if (string.IsNullOrWhiteSpace(cipher))
        {
            return null;
        }

        return _protector.Unprotect(cipher);
    }

    private static string? NormalizeOrNull(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
