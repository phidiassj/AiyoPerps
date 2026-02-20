using AiyoPerps.Models;
using AiyoPerps.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace AiyoPerps.ViewModels;

public sealed class AccountManagerViewModel : ViewModelBase, System.IDisposable
{
    private readonly AccountStore _accountStore;
    private readonly IVenueFactory _venueFactory;
    private readonly AppLogger _logger;
    private readonly ToastService _toastService;
    private AccountProfile? _selectedAccount;
    private string _newVenueId = "BitMEX";
    private string _newDisplayName = string.Empty;
    private string _newEnvironment = "testnet";
    private string _newSummary = string.Empty;
    private string _newApiKey = string.Empty;
    private string _newApiSecret = string.Empty;
    private string _newAccountAddress = string.Empty;
    private string _newWalletAddress = string.Empty;
    private string _newPrivateKey = string.Empty;
    private string _testConnectionResult = "Not tested";
    private bool _isCreateMode = true;

    public AccountManagerViewModel(AccountStore accountStore, IVenueFactory venueFactory, AppLogger logger, ToastService toastService)
    {
        _accountStore = accountStore;
        _venueFactory = venueFactory;
        _logger = logger;
        _toastService = toastService;
        Accounts = accountStore.Accounts;

        AddCommand = new RelayCommand(
            _ => AddAccount(),
            _ => IsCreateMode && !string.IsNullOrWhiteSpace(NewDisplayName) && !string.IsNullOrWhiteSpace(NewSummary));

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

        SaveCredentialsCommand = new RelayCommand(
            _ => SaveCredentials(),
            _ => IsEditMode && SelectedAccount is not null);

        TestConnectionCommand = new RelayCommand(
            _ => _ = TestConnectionAsync(),
            _ => SelectedAccount is not null);

        BeginAddModeCommand = new RelayCommand(_ => BeginAddMode());

        L.PropertyChanged += OnLocalizationChanged;
        TestConnectionResult = L["AccountManager_NotTested"];
    }

    public ObservableCollection<AccountProfile> Accounts { get; }

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
                    var creds = _accountStore.GetCredentials(value.AccountId);
                    NewApiKey = creds.ApiKey ?? string.Empty;
                    NewApiSecret = creds.ApiSecret ?? string.Empty;
                    NewAccountAddress = creds.AccountAddress ?? string.Empty;
                    NewWalletAddress = creds.WalletAddress ?? string.Empty;
                    NewPrivateKey = creds.PrivateKey ?? string.Empty;
                    TestConnectionResult = $"{value.DisplayName}: {L["AccountManager_NotTested"]}";
                }

                NotifyCommandStates();
                RaisePropertyChanged(nameof(IsEditMode));
                RaisePropertyChanged(nameof(FormModeTitle));
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
        set => SetProperty(ref _newVenueId, value);
    }

    public string NewDisplayName
    {
        get => _newDisplayName;
        set
        {
            if (SetProperty(ref _newDisplayName, value))
            {
                (AddCommand as RelayCommand)?.NotifyCanExecuteChanged();
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
                (AddCommand as RelayCommand)?.NotifyCanExecuteChanged();
            }
        }
    }

    public string NewApiKey
    {
        get => _newApiKey;
        set => SetProperty(ref _newApiKey, value);
    }

    public string NewApiSecret
    {
        get => _newApiSecret;
        set => SetProperty(ref _newApiSecret, value);
    }

    public string TestConnectionResult
    {
        get => _testConnectionResult;
        private set => SetProperty(ref _testConnectionResult, value);
    }

    public string NewWalletAddress
    {
        get => _newWalletAddress;
        set => SetProperty(ref _newWalletAddress, value);
    }

    public string NewAccountAddress
    {
        get => _newAccountAddress;
        set => SetProperty(ref _newAccountAddress, value);
    }

    public string NewPrivateKey
    {
        get => _newPrivateKey;
        set => SetProperty(ref _newPrivateKey, value);
    }

    public string[] VenueOptions { get; } = ["BitMEX", "Hyperliquid"];
    public string[] EnvironmentOptions { get; } = ["testnet", "mainnet"];

    public ICommand AddCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand ToggleEnabledCommand { get; }
    public ICommand SaveCredentialsCommand { get; }
    public ICommand TestConnectionCommand { get; }
    public ICommand BeginAddModeCommand { get; }

    private void AddAccount()
    {
        _accountStore.Add(NewVenueId, NewDisplayName.Trim(), NewEnvironment, NewSummary.Trim(), NewApiKey, NewApiSecret, NewAccountAddress, NewWalletAddress, NewPrivateKey);
        _logger.Info("AccountManager", $"AddAccount venue={NewVenueId}, env={NewEnvironment}, name={NewDisplayName}");
        _toastService.ShowInfo(L["Toast_AccountAdded"]);
        BeginAddMode();
    }

    private void SaveCredentials()
    {
        if (SelectedAccount is null)
        {
            return;
        }

        _accountStore.UpdateCredentials(SelectedAccount, NewApiKey, NewApiSecret, NewAccountAddress, NewWalletAddress, NewPrivateKey);
        TestConnectionResult = $"{SelectedAccount.DisplayName}: {L["AccountManager_CredentialsUpdated"]}";
        _logger.Info("AccountManager", $"UpdateCredentials account={SelectedAccount.DisplayName}, hasApi={!string.IsNullOrWhiteSpace(NewApiKey)}, hasAccountAddr={!string.IsNullOrWhiteSpace(NewAccountAddress)}, hasWallet={!string.IsNullOrWhiteSpace(NewWalletAddress)}");
        _toastService.ShowInfo(L["Toast_CredentialsUpdated"]);
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
            _logger.Info("AccountManager", $"TestConnection start account={SelectedAccount.DisplayName}, venue={SelectedAccount.VenueId}, hasApi={creds.HasApiCredentials}");
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
        catch (System.Exception ex)
        {
            TestConnectionResult = $"{SelectedAccount.DisplayName}: {L["Toast_TestConnectionException"]} ({ex.Message})";
            _logger.Error("AccountManager", $"TestConnection exception account={SelectedAccount.DisplayName}", ex);
            _toastService.ShowError($"{L["Toast_TestConnectionException"]}：{ex.Message}");
        }
    }

    public void Dispose()
    {
        L.PropertyChanged -= OnLocalizationChanged;
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.PropertyName) &&
            !string.Equals(e.PropertyName, "Item[]", System.StringComparison.Ordinal) &&
            !string.Equals(e.PropertyName, "Item", System.StringComparison.Ordinal))
        {
            return;
        }

        NotifyLocalizationChanged();
        RaisePropertyChanged(nameof(FormModeTitle));
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
        NewApiKey = string.Empty;
        NewApiSecret = string.Empty;
        NewAccountAddress = string.Empty;
        NewWalletAddress = string.Empty;
        NewPrivateKey = string.Empty;
        TestConnectionResult = L["AccountManager_NotTested"];
        NotifyCommandStates();
    }

    private void NotifyCommandStates()
    {
        (AddCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (RemoveCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (ToggleEnabledCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (SaveCredentialsCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (TestConnectionCommand as RelayCommand)?.NotifyCanExecuteChanged();
    }
}
