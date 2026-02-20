using AiyoPerps.Services;
using AiyoPerps.Services.Api;
using Avalonia.Threading;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Input;

namespace AiyoPerps.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase, System.IDisposable
{
    private readonly AccountStore _accountStore;
    private readonly IVenueFactory _venueFactory;
    private readonly CandleRepository _candleRepository;
    private readonly SymbolCatalogRepository _symbolCatalogRepository;
    private readonly AppLogger _logger;
    private readonly ToastService _toastService;
    private readonly UserPreferenceRepository _userPreferenceRepository;
    private readonly LocalApiServer _localApiServer;
    private readonly TradingApiService _tradingApiService;
    private WorkspaceTabViewModel? _selectedTab;
    private LanguageOption? _selectedLanguage;
    private bool _suppressLanguageSave;
    private bool _suppressHttpApiToggle;
    private string _httpApiPort = "5078";
    private bool _isHttpApiEnabled;
    private string _httpApiStatus = "HTTP API: OFF";

    public MainWindowViewModel(AccountStore accountStore, IVenueFactory venueFactory, CandleRepository candleRepository, SymbolCatalogRepository symbolCatalogRepository, AppLogger logger, ToastService toastService, UserPreferenceRepository userPreferenceRepository, LocalApiServer localApiServer, TradingApiService tradingApiService)
    {
        _accountStore = accountStore;
        _venueFactory = venueFactory;
        _candleRepository = candleRepository;
        _symbolCatalogRepository = symbolCatalogRepository;
        _logger = logger;
        _toastService = toastService;
        _userPreferenceRepository = userPreferenceRepository;
        _localApiServer = localApiServer;
        _tradingApiService = tradingApiService;

        AddTabCommand = new RelayCommand(_ => AddTab());
        CloseTabCommand = new RelayCommand(
            parameter =>
            {
                if (parameter is WorkspaceTabViewModel tab)
                {
                    CloseTab(tab);
                }
            });
        L.PropertyChanged += OnLocalizationChanged;
        _suppressLanguageSave = true;
        SelectedLanguage = LanguageOptions.FirstOrDefault(x => x.Code == L.CurrentLanguageCode)
            ?? LanguageOptions.FirstOrDefault(x => x.Code == "en")
            ?? LanguageOptions[0];
        _suppressLanguageSave = false;

        _httpApiPort = _userPreferenceRepository.GetHttpApiPortOrDefault(5078).ToString();
        _suppressHttpApiToggle = true;
        IsHttpApiEnabled = _userPreferenceRepository.GetHttpApiEnabledOrDefault(false);
        _suppressHttpApiToggle = false;

        if (IsHttpApiEnabled)
        {
            _ = SetHttpApiEnabledAsync(true);
        }

        _tradingApiService.ConnectionOpened += OnApiConnectionOpened;
        _tradingApiService.ConnectionClosed += OnApiConnectionClosed;
        AddTab();
    }

    public ObservableCollection<WorkspaceTabViewModel> Tabs { get; } = [];

    public WorkspaceTabViewModel? SelectedTab
    {
        get => _selectedTab;
        set => SetProperty(ref _selectedTab, value);
    }

    public ICommand AddTabCommand { get; }
    public ICommand CloseTabCommand { get; }

    public LanguageOption[] LanguageOptions { get; } =
    [
        new("zh-TW", "繁體中文"),
        new("en", "English")
    ];

    public LanguageOption? SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (SetProperty(ref _selectedLanguage, value) && value is not null)
            {
                L.SetLanguage(value.Code);
                if (!_suppressLanguageSave)
                {
                    _userPreferenceRepository.SaveLanguageCode(value.Code);
                }
                _logger.Info("MainWindow", $"Language switched code={value.Code}");
            }
        }
    }

    public string HttpApiPort
    {
        get => _httpApiPort;
        set
        {
            if (SetProperty(ref _httpApiPort, value) &&
                int.TryParse(value, out var port) &&
                port is > 0 and <= 65535)
            {
                _userPreferenceRepository.SaveHttpApiPort(port);
            }
        }
    }

    public bool IsHttpApiEnabled
    {
        get => _isHttpApiEnabled;
        set
        {
            if (SetProperty(ref _isHttpApiEnabled, value) && !_suppressHttpApiToggle)
            {
                RaisePropertyChanged(nameof(CanEditHttpApiPort));
                _ = SetHttpApiEnabledAsync(value);
            }
        }
    }

    public bool CanEditHttpApiPort => !IsHttpApiEnabled;

    public string HttpApiStatus
    {
        get => _httpApiStatus;
        private set => SetProperty(ref _httpApiStatus, value);
    }

    public void AddTab()
    {
        _logger.Info("MainWindow", "AddTab requested");
        var tab = new WorkspaceTabViewModel(_accountStore, _accountStore.Accounts, _venueFactory, _candleRepository, _symbolCatalogRepository, _logger, _toastService, _userPreferenceRepository, _tradingApiService);
        Tabs.Add(tab);
        SelectedTab = tab;
    }

    public void CloseTab(WorkspaceTabViewModel tab)
    {
        _ = CloseTabAsync(tab, initiatedByApi: false);
    }

    private async System.Threading.Tasks.Task CloseTabAsync(WorkspaceTabViewModel tab, bool initiatedByApi)
    {
        _logger.Info("MainWindow", $"CloseTab requested tabId={tab.TabId}");

        if (!initiatedByApi && tab.IsApiSessionManaged && tab.TryGetApiSessionIdentity(out var accountId, out var symbol))
        {
            try
            {
                await _tradingApiService.CloseConnectionAsync(accountId, symbol);
            }
            catch (Exception ex)
            {
                _logger.Warn("MainWindow", $"CloseTab sync-close API session warning tabId={tab.TabId}: {ex.Message}");
            }
        }

        _ = tab.DisposeAsync();
        Tabs.Remove(tab);

        if (SelectedTab == tab)
        {
            SelectedTab = Tabs.LastOrDefault();
        }

        if (Tabs.Count == 0)
        {
            AddTab();
        }
    }

    public void Dispose()
    {
        L.PropertyChanged -= OnLocalizationChanged;
        _tradingApiService.ConnectionOpened -= OnApiConnectionOpened;
        _tradingApiService.ConnectionClosed -= OnApiConnectionClosed;
        foreach (var tab in Tabs.ToList())
        {
            _ = tab.DisposeAsync();
        }
    }

    private void OnApiConnectionOpened(ApiConnectionDto dto)
    {
        Dispatcher.UIThread.Post(() => _ = EnsureTabForApiConnectionAsync(dto));
    }

    private void OnApiConnectionClosed(Guid accountId, string symbol)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var tabs = Tabs.Where(x => x.MatchesBinding(accountId, symbol)).ToList();
            if (tabs.Count == 0)
            {
                return;
            }

            foreach (var tab in tabs)
            {
                _ = CloseTabAsync(tab, initiatedByApi: true);
            }
        });
    }

    private async System.Threading.Tasks.Task EnsureTabForApiConnectionAsync(ApiConnectionDto dto)
    {
        var existing = Tabs.FirstOrDefault(x => x.MatchesBinding(dto.AccountId, dto.Symbol));
        if (existing is not null)
        {
            await existing.AttachApiSessionAsync(dto);
            SelectedTab = existing;
            return;
        }

        var tab = new WorkspaceTabViewModel(_accountStore, _accountStore.Accounts, _venueFactory, _candleRepository, _symbolCatalogRepository, _logger, _toastService, _userPreferenceRepository, _tradingApiService);
        Tabs.Add(tab);
        SelectedTab = tab;
        await tab.AttachApiSessionAsync(dto);
    }

    private async System.Threading.Tasks.Task SetHttpApiEnabledAsync(bool enabled)
    {
        if (enabled)
        {
            if (!int.TryParse(HttpApiPort, out var port) || port is <= 0 or > 65535)
            {
                _toastService.ShowError("HTTP API port must be 1..65535.");
                _suppressHttpApiToggle = true;
                IsHttpApiEnabled = false;
                _suppressHttpApiToggle = false;
                RaisePropertyChanged(nameof(CanEditHttpApiPort));
                return;
            }

            try
            {
                await _localApiServer.StartAsync(port);
                HttpApiStatus = $"HTTP API: ON ({port})";
                _userPreferenceRepository.SaveHttpApiPort(port);
                _userPreferenceRepository.SaveHttpApiEnabled(true);
                _logger.Info("MainWindow", $"HTTP API enabled port={port}");
            }
            catch (System.Exception ex)
            {
                _suppressHttpApiToggle = true;
                IsHttpApiEnabled = false;
                _suppressHttpApiToggle = false;
                RaisePropertyChanged(nameof(CanEditHttpApiPort));
                HttpApiStatus = "HTTP API: ERROR";
                _userPreferenceRepository.SaveHttpApiEnabled(false);
                _toastService.ShowError($"HTTP API start failed: {ex.Message}");
                _logger.Error("MainWindow", "HTTP API start failed", ex);
            }

            return;
        }

        try
        {
            await _localApiServer.StopAsync();
            HttpApiStatus = "HTTP API: OFF";
            _userPreferenceRepository.SaveHttpApiEnabled(false);
            _logger.Info("MainWindow", "HTTP API disabled");
        }
        catch (System.Exception ex)
        {
            HttpApiStatus = "HTTP API: ERROR";
            _toastService.ShowError($"HTTP API stop failed: {ex.Message}");
            _logger.Error("MainWindow", "HTTP API stop failed", ex);
        }
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
        foreach (var tab in Tabs)
        {
            tab.NotifyLocalizationChanged();
        }
    }
}

public sealed record LanguageOption(string Code, string DisplayName);
