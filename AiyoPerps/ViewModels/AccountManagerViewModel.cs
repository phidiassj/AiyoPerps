using AiyoPerps.Models;
using AiyoPerps.Services;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace AiyoPerps.ViewModels;

public sealed class AccountManagerViewModel : ViewModelBase, IDisposable
{
    public sealed class AuthModeOption
    {
        public AuthModeOption(string value)
        {
            Value = value;
            Label = value;
        }

        public string Value { get; }
        public string Label { get; }
    }

    private readonly AccountStore _accountStore;
    private readonly IVenueFactory _venueFactory;
    private readonly AppLogger _logger;
    private readonly ToastService _toastService;
    private readonly ObservableCollection<AuthModeOption> _authModeOptions = [];
    private AccountProfile? _selectedAccount;
    private AuthModeOption? _selectedAuthModeOption;
    private string _newVenueId = "BitMEX";
    private string _newDisplayName = string.Empty;
    private string _newEnvironment = "testnet";
    private string _newSummary = string.Empty;
    private string _newAuthMode = "ApiKey";
    private string _newApiKey = string.Empty;
    private string _newApiSecret = string.Empty;
    private string _newAccountAddress = string.Empty;
    private string _newSubAccountId = string.Empty;
    private string _newWalletAddress = string.Empty;
    private string _newPrivateKey = string.Empty;
    private string _testConnectionResult = string.Empty;
    private bool _isCreateMode = true;

    public AccountManagerViewModel(AccountStore accountStore, IVenueFactory venueFactory, AppLogger logger, ToastService toastService)
    {
        _accountStore = accountStore;
        _venueFactory = venueFactory;
        _logger = logger;
        _toastService = toastService;
        Accounts = accountStore.Accounts;

        AddCommand = new RelayCommand(_ => AddAccount(), _ => IsCreateMode && CanSubmit());
        RemoveCommand = new RelayCommand(
            _ =>
            {
                if (SelectedAccount is not null)
                {
                    _accountStore.Remove(SelectedAccount);
                }
            },
            _ => SelectedAccount is not null);

        ToggleEnabledCommand = new RelayCommand(
            _ =>
            {
                if (SelectedAccount is not null)
                {
                    _accountStore.ToggleEnabled(SelectedAccount);
                    RaisePropertyChanged(nameof(Accounts));
                }
            },
            _ => SelectedAccount is not null);

        SaveCredentialsCommand = new RelayCommand(_ => SaveCredentials(), _ => IsEditMode && SelectedAccount is not null && CanSubmit());
        TestConnectionCommand = new RelayCommand(_ => _ = TestConnectionAsync(), _ => SelectedAccount is not null);
        BeginAddModeCommand = new RelayCommand(_ => BeginAddMode());

        RefreshAuthModeOptions(_newVenueId);
        L.PropertyChanged += OnLocalizationChanged;
    }

    public ObservableCollection<AccountProfile> Accounts { get; }
    public ObservableCollection<AuthModeOption> AuthModeOptions => _authModeOptions;

    public AccountProfile? SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            if (SetProperty(ref _selectedAccount, value))
            {
                if (value is not null)
                {
                    IsCreateMode = false;
                    NewVenueId = value.VenueId;
                    NewDisplayName = value.DisplayName;
                    NewEnvironment = value.Environment;
                    NewSummary = value.Summary;
                    NewAuthMode = string.IsNullOrWhiteSpace(value.AuthMode) ? GetDefaultAuthMode(value.VenueId) : value.AuthMode;
                    NewSubAccountId = value.SubAccountId ?? string.Empty;

                    var creds = _accountStore.GetCredentials(value.AccountId);
                    NewApiKey = creds.ApiKey ?? string.Empty;
                    NewApiSecret = creds.ApiSecret ?? string.Empty;
                    NewAccountAddress = creds.AccountAddress ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(NewSubAccountId))
                    {
                        NewSubAccountId = creds.SubAccountId ?? string.Empty;
                    }

                    NewWalletAddress = creds.WalletAddress ?? string.Empty;
                    NewPrivateKey = creds.PrivateKey ?? string.Empty;
                    TestConnectionResult = $"{value.DisplayName}: {L["AccountManager_NotTested"]}";
                }

                NotifyCommandStates();
                RaisePropertyChanged(nameof(IsEditMode));
                RaisePropertyChanged(nameof(FormModeTitle));
                RaisePropertyChanged(nameof(FormStatusMessage));
                RaisePropertyChanged(nameof(HasFormStatusMessage));
            }
        }
    }

    public AuthModeOption? SelectedAuthModeOption
    {
        get => _selectedAuthModeOption;
        set
        {
            if (SetProperty(ref _selectedAuthModeOption, value) && value is not null)
            {
                if (!string.Equals(_newAuthMode, value.Value, StringComparison.OrdinalIgnoreCase))
                {
                    _newAuthMode = value.Value;
                    RaisePropertyChanged(nameof(NewAuthMode));
                    RaisePropertyChanged(nameof(IsApiAuthSelected));
                    RaisePropertyChanged(nameof(IsWalletAuthSelected));
                    NotifyCommandStates();
                }
            }
        }
    }

    public bool IsCreateMode
    {
        get => _isCreateMode;
        private set
        {
            if (SetProperty(ref _isCreateMode, value))
            {
                RaisePropertyChanged(nameof(IsEditMode));
                RaisePropertyChanged(nameof(FormModeTitle));
                RaisePropertyChanged(nameof(FormStatusMessage));
                RaisePropertyChanged(nameof(HasFormStatusMessage));
                NotifyCommandStates();
            }
        }
    }

    public bool IsEditMode => !IsCreateMode && SelectedAccount is not null;

    public string FormModeTitle => IsCreateMode
        ? L["AccountManager_AddAccount"]
        : $"{L["AccountManager_UpdateCreds"]} - {SelectedAccount?.DisplayName}";

    public string NewVenueId
    {
        get => _newVenueId;
        set
        {
            if (SetProperty(ref _newVenueId, value))
            {
                RefreshAuthModeOptions(value);
                NewAuthMode = _newAuthMode;
                RaisePropertyChanged(nameof(CanEditAuthMode));
                RaisePropertyChanged(nameof(IsApiAuthSelected));
                RaisePropertyChanged(nameof(IsWalletAuthSelected));
                RaisePropertyChanged(nameof(IsAccountAddressVisible));
                RaisePropertyChanged(nameof(IsAccountAddressRequired));
                RaisePropertyChanged(nameof(IsSubAccountIdRequired));
                RaisePropertyChanged(nameof(VenueCapabilityText));
                NotifyCommandStates();
            }
        }
    }

    public string NewDisplayName
    {
        get => _newDisplayName;
        set
        {
            if (SetProperty(ref _newDisplayName, value))
            {
                NotifyCommandStates();
            }
        }
    }

    public string NewEnvironment
    {
        get => _newEnvironment;
        set => SetProperty(ref _newEnvironment, value);
    }

    public string NewSummary
    {
        get => _newSummary;
        set
        {
            if (SetProperty(ref _newSummary, value))
            {
                NotifyCommandStates();
            }
        }
    }

    public string NewAuthMode
    {
        get => _newAuthMode;
        set
        {
            var normalized = NormalizeAuthMode(value, NewVenueId);
            if (SetProperty(ref _newAuthMode, normalized))
            {
                SyncSelectedAuthModeOption();
                RaisePropertyChanged(nameof(IsApiAuthSelected));
                RaisePropertyChanged(nameof(IsWalletAuthSelected));
                NotifyCommandStates();
            }
        }
    }

    public string NewApiKey
    {
        get => _newApiKey;
        set
        {
            if (SetProperty(ref _newApiKey, value))
            {
                NotifyCommandStates();
            }
        }
    }

    public string NewApiSecret
    {
        get => _newApiSecret;
        set
        {
            if (SetProperty(ref _newApiSecret, value))
            {
                NotifyCommandStates();
            }
        }
    }

    public string TestConnectionResult
    {
        get => _testConnectionResult;
        private set
        {
            if (SetProperty(ref _testConnectionResult, value))
            {
                RaisePropertyChanged(nameof(FormStatusMessage));
                RaisePropertyChanged(nameof(HasFormStatusMessage));
            }
        }
    }

    public string NewWalletAddress
    {
        get => _newWalletAddress;
        set
        {
            if (SetProperty(ref _newWalletAddress, value))
            {
                NotifyCommandStates();
            }
        }
    }

    public string NewAccountAddress
    {
        get => _newAccountAddress;
        set
        {
            if (SetProperty(ref _newAccountAddress, value))
            {
                NotifyCommandStates();
            }
        }
    }

    public string NewSubAccountId
    {
        get => _newSubAccountId;
        set
        {
            if (SetProperty(ref _newSubAccountId, value))
            {
                NotifyCommandStates();
            }
        }
    }

    public string NewPrivateKey
    {
        get => _newPrivateKey;
        set
        {
            if (SetProperty(ref _newPrivateKey, value))
            {
                NotifyCommandStates();
            }
        }
    }

    public string[] VenueOptions { get; } = ["BitMEX", "Hyperliquid", "Aster", "GRVT"];
    public string[] EnvironmentOptions { get; } = ["testnet", "mainnet"];
    public bool CanEditAuthMode => AuthModeOptions.Count > 1;
    public bool IsApiAuthSelected => SupportsApiCredentials(NewVenueId) &&
                                     (string.Equals(NewAuthMode, "ApiKey", StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(NewAuthMode, "Both", StringComparison.OrdinalIgnoreCase));
    public bool IsWalletAuthSelected => SupportsWalletCredentials(NewVenueId) &&
                                        (string.Equals(NewAuthMode, "Wallet", StringComparison.OrdinalIgnoreCase) ||
                                         string.Equals(NewAuthMode, "Both", StringComparison.OrdinalIgnoreCase));
    public bool IsAccountAddressVisible => UsesAccountAddress(NewVenueId);
    public bool IsAccountAddressRequired => RequiresAccountAddress(NewVenueId);
    public bool IsSubAccountIdRequired => string.Equals(NewVenueId, "GRVT", StringComparison.OrdinalIgnoreCase);
    public string VenueCapabilityText => NewVenueId switch
    {
        "BitMEX" => L["AccountManager_VenueCapability_BitMEX"],
        "Hyperliquid" => L["AccountManager_VenueCapability_Hyperliquid"],
        "Aster" => L["AccountManager_VenueCapability_Aster"],
        "GRVT" => L["AccountManager_VenueCapability_GRVT"],
        _ => string.Empty
    };
    public string FormStatusMessage => IsCreateMode ? L["AccountManager_TestAvailableAfterSave"] : TestConnectionResult;
    public bool HasFormStatusMessage => !string.IsNullOrWhiteSpace(FormStatusMessage);

    public ICommand AddCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand ToggleEnabledCommand { get; }
    public ICommand SaveCredentialsCommand { get; }
    public ICommand TestConnectionCommand { get; }
    public ICommand BeginAddModeCommand { get; }

    private void AddAccount()
    {
        _accountStore.Add(
            NewVenueId,
            NewDisplayName.Trim(),
            NewEnvironment,
            NewSummary.Trim(),
            NewAuthMode,
            NewApiKey,
            NewApiSecret,
            NewAccountAddress,
            NewSubAccountId,
            NewWalletAddress,
            NewPrivateKey);

        _logger.Info("AccountManager", $"AddAccount venue={NewVenueId}, env={NewEnvironment}, name={NewDisplayName}, authMode={NewAuthMode}");
        _toastService.ShowInfo(L["Toast_AccountAdded"]);
        ShowVenueCredentialHintIfNeeded();
        BeginAddMode();
    }

    private void SaveCredentials()
    {
        if (SelectedAccount is null)
        {
            return;
        }

        var accountId = SelectedAccount.AccountId;
        var displayName = SelectedAccount.DisplayName;
        var venueId = SelectedAccount.VenueId;

        _accountStore.UpdateCredentials(
            SelectedAccount,
            NewApiKey,
            NewApiSecret,
            NewAccountAddress,
            NewSubAccountId,
            NewWalletAddress,
            NewPrivateKey,
            NewAuthMode);

        SelectedAccount = Accounts.FirstOrDefault(x => x.AccountId == accountId);
        TestConnectionResult = $"{displayName}: {L["AccountManager_CredentialsUpdated"]}";
        _logger.Info("AccountManager", $"UpdateCredentials account={displayName}, venue={venueId}, authMode={NewAuthMode}, hasApi={!string.IsNullOrWhiteSpace(NewApiKey)}, hasWallet={!string.IsNullOrWhiteSpace(NewWalletAddress)}");
        _toastService.ShowInfo(L["Toast_CredentialsUpdated"]);
        ShowVenueCredentialHintIfNeeded();
    }

    private async Task TestConnectionAsync()
    {
        if (SelectedAccount is null)
        {
            return;
        }

        try
        {
            var creds = _accountStore.GetCredentials(SelectedAccount.AccountId);
            _logger.Info("AccountManager", $"TestConnection start account={SelectedAccount.DisplayName}, venue={SelectedAccount.VenueId}, hasApi={creds.HasApiCredentials}, hasWallet={creds.HasWalletCredentials}, authMode={creds.AuthMode}");
            var venue = _venueFactory.Create(SelectedAccount, creds);
            var result = await venue.ValidateConnectionAsync(CancellationToken.None);
            await venue.DisposeAsync();
            TestConnectionResult = $"{SelectedAccount.DisplayName}: {result.Message}";
            _logger.Info("AccountManager", $"TestConnection done account={SelectedAccount.DisplayName}, success={result.IsSuccess}, message={result.Message}");
            if (result.IsSuccess)
            {
                _toastService.ShowInfo(L["Toast_TestConnectionSuccess"]);
            }
            else
            {
                _toastService.ShowError($"{L["Toast_TestConnectionFailed"]}{result.Message}");
            }
        }
        catch (Exception ex)
        {
            TestConnectionResult = $"{SelectedAccount.DisplayName}: {L["Toast_TestConnectionException"]} ({ex.Message})";
            _logger.Error("AccountManager", $"TestConnection exception account={SelectedAccount.DisplayName}", ex);
            _toastService.ShowError($"{L["Toast_TestConnectionException"]}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        L.PropertyChanged -= OnLocalizationChanged;
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.PropertyName) &&
            !string.Equals(e.PropertyName, "Item[]", StringComparison.Ordinal) &&
            !string.Equals(e.PropertyName, "Item", StringComparison.Ordinal))
        {
            return;
        }

        NotifyLocalizationChanged();
        RaisePropertyChanged(nameof(FormModeTitle));
        RaisePropertyChanged(nameof(VenueCapabilityText));
        RaisePropertyChanged(nameof(FormStatusMessage));
        RaisePropertyChanged(nameof(HasFormStatusMessage));
    }

    private void BeginAddMode()
    {
        _selectedAccount = null;
        RaisePropertyChanged(nameof(SelectedAccount));
        IsCreateMode = true;
        NewVenueId = "BitMEX";
        NewEnvironment = "testnet";
        NewDisplayName = string.Empty;
        NewSummary = string.Empty;
        NewAuthMode = GetDefaultAuthMode(NewVenueId);
        NewApiKey = string.Empty;
        NewApiSecret = string.Empty;
        NewAccountAddress = string.Empty;
        NewSubAccountId = string.Empty;
        NewWalletAddress = string.Empty;
        NewPrivateKey = string.Empty;
        TestConnectionResult = string.Empty;
        RaisePropertyChanged(nameof(FormStatusMessage));
        RaisePropertyChanged(nameof(HasFormStatusMessage));
        NotifyCommandStates();
    }

    private bool CanSubmit()
    {
        if (string.IsNullOrWhiteSpace(NewDisplayName) || string.IsNullOrWhiteSpace(NewSummary))
        {
            return false;
        }

        if (IsSubAccountIdRequired && string.IsNullOrWhiteSpace(NewSubAccountId))
        {
            return false;
        }

        if (IsAccountAddressRequired && string.IsNullOrWhiteSpace(NewAccountAddress))
        {
            return false;
        }

        if (IsApiAuthSelected && (string.IsNullOrWhiteSpace(NewApiKey) || string.IsNullOrWhiteSpace(NewApiSecret)))
        {
            return false;
        }

        if (IsWalletAuthSelected && (string.IsNullOrWhiteSpace(NewWalletAddress) || string.IsNullOrWhiteSpace(NewPrivateKey)))
        {
            return false;
        }

        return true;
    }

    private void NotifyCommandStates()
    {
        (AddCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (RemoveCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (ToggleEnabledCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (SaveCredentialsCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (TestConnectionCommand as RelayCommand)?.NotifyCanExecuteChanged();
    }

    private void ShowVenueCredentialHintIfNeeded()
    {
        if (string.Equals(NewVenueId, "GRVT", StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrWhiteSpace(NewWalletAddress) || string.IsNullOrWhiteSpace(NewPrivateKey)))
        {
            _toastService.ShowWarning(L["AccountManager_GrvtWalletRequired"]);
        }
    }

    private void RefreshAuthModeOptions(string? venueId)
    {
        _authModeOptions.Clear();
        foreach (var option in GetAuthModeOptions(venueId))
        {
            _authModeOptions.Add(new AuthModeOption(option));
        }

        SyncSelectedAuthModeOption();
        RaisePropertyChanged(nameof(AuthModeOptions));
    }

    private void SyncSelectedAuthModeOption()
    {
        var next = _authModeOptions.FirstOrDefault(x => string.Equals(x.Value, _newAuthMode, StringComparison.OrdinalIgnoreCase))
            ?? _authModeOptions.FirstOrDefault();

        if (!ReferenceEquals(_selectedAuthModeOption, next))
        {
            _selectedAuthModeOption = next;
            RaisePropertyChanged(nameof(SelectedAuthModeOption));
        }

        if (next is not null && !string.Equals(_newAuthMode, next.Value, StringComparison.OrdinalIgnoreCase))
        {
            _newAuthMode = next.Value;
            RaisePropertyChanged(nameof(NewAuthMode));
        }
    }

    private static string[] GetAuthModeOptions(string? venueId)
    {
        return (venueId ?? string.Empty).Trim() switch
        {
            "BitMEX" => ["ApiKey"],
            "Hyperliquid" => ["Wallet"],
            "Aster" => ["Wallet"],
            "GRVT" => ["ApiKey", "Wallet", "Both"],
            _ => ["ApiKey", "Wallet", "Both"]
        };
    }

    private static bool SupportsApiCredentials(string? venueId)
    {
        return GetAuthModeOptions(venueId).Contains("ApiKey", StringComparer.OrdinalIgnoreCase);
    }

    private static bool SupportsWalletCredentials(string? venueId)
    {
        return GetAuthModeOptions(venueId).Contains("Wallet", StringComparer.OrdinalIgnoreCase);
    }

    private static bool UsesAccountAddress(string? venueId)
    {
        return string.Equals(venueId, "Hyperliquid", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(venueId, "Aster", StringComparison.OrdinalIgnoreCase);
    }

    private static bool RequiresAccountAddress(string? venueId)
    {
        return string.Equals(venueId, "Aster", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetDefaultAuthMode(string? venueId)
    {
        return GetAuthModeOptions(venueId) switch
        {
            var options when options.Contains("Both", StringComparer.OrdinalIgnoreCase) => "Both",
            var options when options.Contains("ApiKey", StringComparer.OrdinalIgnoreCase) => "ApiKey",
            var options when options.Contains("Wallet", StringComparer.OrdinalIgnoreCase) => "Wallet",
            _ => "Both"
        };
    }

    private static string NormalizeAuthMode(string? mode, string? venueId)
    {
        if (string.Equals(mode, "ApiKey", StringComparison.OrdinalIgnoreCase) &&
            SupportsApiCredentials(venueId))
        {
            return "ApiKey";
        }

        if (string.Equals(mode, "Wallet", StringComparison.OrdinalIgnoreCase) &&
            SupportsWalletCredentials(venueId))
        {
            return "Wallet";
        }

        if (string.Equals(mode, "Both", StringComparison.OrdinalIgnoreCase) &&
            GetAuthModeOptions(venueId).Contains("Both", StringComparer.OrdinalIgnoreCase))
        {
            return "Both";
        }

        return GetDefaultAuthMode(venueId);
    }
}
