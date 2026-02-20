using AiyoPerps.Models;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace AiyoPerps.Services;

public sealed class AccountStore
{
    private readonly AccountRepository _repository;
    private readonly ObservableCollection<AccountProfile> _accounts = [];

    public AccountStore(AccountRepository repository)
    {
        _repository = repository;
        _repository.EnsureDatabase();

        var existing = _repository.GetAll();
        if (existing.Count == 0)
        {
            Add("BitMEX", "BitMEX Testnet", "testnet", "api_****_xxxx", null, null, null, null, null);
            Add("Hyperliquid", "HL Wallet", "mainnet", "0x12ab...89ef", null, null, null, null, null);
        }
        else
        {
            foreach (var account in existing)
            {
                _accounts.Add(account);
            }
        }
    }

    public ObservableCollection<AccountProfile> Accounts => _accounts;

    public IReadOnlyList<AccountProfile> Snapshot()
    {
        return _repository.GetAll();
    }

    public AccountProfile? Find(Guid accountId)
    {
        return _repository.GetById(accountId);
    }

    public void Add(string venueId, string name, string environment, string summary, string? apiKey, string? apiSecret, string? accountAddress, string? walletAddress, string? privateKey)
    {
        var account = new AccountProfile
        {
            VenueId = venueId,
            DisplayName = name,
            Environment = environment,
            Summary = summary,
            IsEnabled = true,
            HasApiCredentials = !string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrWhiteSpace(apiSecret),
            HasWalletCredentials = !string.IsNullOrWhiteSpace(walletAddress) && !string.IsNullOrWhiteSpace(privateKey)
        };

        _repository.Add(account, apiKey, apiSecret, accountAddress, walletAddress, privateKey);
        Reload();
    }

    public AccountCredentials GetCredentials(Guid accountId)
    {
        return _repository.GetCredentials(accountId);
    }

    public void UpdateCredentials(AccountProfile account, string? apiKey, string? apiSecret, string? accountAddress, string? walletAddress, string? privateKey)
    {
        _repository.UpdateCredentials(account.AccountId, apiKey, apiSecret, accountAddress, walletAddress, privateKey);
        Reload();
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
        _repository.UpdateAccount(
            accountId,
            venueId,
            displayName,
            environment,
            summary,
            apiKey,
            apiSecret,
            accountAddress,
            walletAddress,
            privateKey,
            isEnabled);
        Reload();
    }

    public void Remove(AccountProfile account)
    {
        _repository.Delete(account.AccountId);
        Reload();
    }

    public void Remove(Guid accountId)
    {
        _repository.Delete(accountId);
        Reload();
    }

    public void ToggleEnabled(AccountProfile account)
    {
        account.IsEnabled = !account.IsEnabled;
        _repository.UpdateEnabled(account.AccountId, account.IsEnabled);
        Reload();
    }

    public void Reload()
    {
        var current = _repository.GetAll();
        void Apply()
        {
            _accounts.Clear();
            foreach (var item in current)
            {
                _accounts.Add(item);
            }
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            Apply();
            return;
        }

        Dispatcher.UIThread.Post(Apply);
    }
}
