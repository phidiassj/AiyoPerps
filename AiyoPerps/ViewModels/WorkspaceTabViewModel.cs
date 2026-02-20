using AiyoPerps.Core;
using AiyoPerps.Models;
using AiyoPerps.Services;
using AiyoPerps.Services.Api;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows.Input;

namespace AiyoPerps.ViewModels;

public sealed class WorkspaceTabViewModel : ViewModelBase, IDisposable
{
    private readonly ViewportService _viewportService = new(new OrderBookAutoHidePolicy());
    private readonly AccountStore _accountStore;
    private readonly IVenueFactory _venueFactory;
    private readonly CandleAggregator _candleAggregator = new();
    private readonly CandleCache _candleCache = new();
    private readonly CandleRepository _candleRepository;
    private readonly SymbolCatalogRepository _symbolCatalogRepository;
    private readonly AppLogger _logger;
    private readonly ToastService _toastService;
    private readonly UserPreferenceRepository _userPreferenceRepository;
    private readonly TradingApiService? _tradingApiService;
    private readonly object _candleLock = new();
    private readonly SemaphoreSlim _settingsReloadGate = new(1, 1);
    private readonly Channel<Candle> _candlePersistChannel = Channel.CreateUnbounded<Candle>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });
    private readonly HashSet<string> _storageLoadAttempted = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();

    private IPerpVenue? _venue;
    private Task? _marketPumpTask;
    private Task? _accountStatePumpTask;
    private Task? _candlePersistTask;
    private CancellationTokenSource? _marketPumpCts;
    private CancellationTokenSource? _accountStatePumpCts;
    private CancellationTokenSource? _apiSessionPumpCts;
    private Candle? _currentCandle;
    private int _disposeStarted;
    private bool _suppressSymbolReload;
    private string? _marketStreamSymbol;
    private bool _isApiSessionManaged;
    private bool _isApplyingApiSession;
    private Guid? _apiSessionAccountId;
    private long? _apiSessionCursor;
    private Task? _apiSessionPumpTask;
    private DateTimeOffset _lastSharedLockToastAt;

    private AccountProfile? _selectedAccount;
    private bool _isConfigured;
    private bool _isOrderBookVisible = true;
    private string _symbol = "BTCUSDT";
    private string? _selectedSymbolOption = "BTCUSDT";
    private string _selectedInterval = "5m";
    private string _connectionStatus = "Disconnected";
    private DateTimeOffset? _lastMarketEventAt;
    private string _candleStatus = "尚無 K 線資料";
    private string? _hoverCandleStatus;
    private string _orderType = "市價單";
    private string _orderSide = "做多";
    private bool _isLimitOrderType;
    private bool _isShortOrderSide;
    private string _orderLeverage = "5";
    private string _orderQuantity = "1";
    private string _relativeAmountUnit = "BTC";
    private string _selectedAmountUnit = "USD";
    private string _orderPrice = string.Empty;
    private string _lastOrderResult = "尚未下單";
    private string _orderBookSummary = "尚無委託簿資料";
    private string _spreadText = "Spread -";
    private string _selectedOrderBookTickSize = "1";
    private bool _isRecentTradesEnabledByViewport = true;
    private decimal? _lastMidPrice;
    private string _estimatedCostUsd = "-";
    private string _estimatedLiquidationPrice = "-";
    private int _selectedOrderPanelTabIndex;
    private IReadOnlyList<OrderBookLevelRow> _askLevels = Array.Empty<OrderBookLevelRow>();
    private IReadOnlyList<OrderBookLevelRow> _bidLevels = Array.Empty<OrderBookLevelRow>();
    private IReadOnlyList<RecentTradeRow> _recentTrades = Array.Empty<RecentTradeRow>();
    private IReadOnlyList<CandleViewPoint> _candleSeries = Array.Empty<CandleViewPoint>();
    private readonly ObservableCollection<PositionPanelRow> _activePositions = [];
    private IReadOnlyList<PendingOrderPanelRow> _pendingOrders = Array.Empty<PendingOrderPanelRow>();
    private IReadOnlyList<BalancePanelRow> _balances = Array.Empty<BalancePanelRow>();
    private IReadOnlyList<PendingOrderPanelRow> _remotePendingOrders = Array.Empty<PendingOrderPanelRow>();
    private IReadOnlyList<string> _orderTypes = Array.Empty<string>();
    private IReadOnlyList<string> _orderSides = Array.Empty<string>();
    private readonly Dictionary<string, PositionState> _positionStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PendingOrderState> _pendingOrderStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _positionClosePriceInputs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _suppressedCanceledOrderIds = new(StringComparer.OrdinalIgnoreCase);

    public WorkspaceTabViewModel(AccountStore accountStore, ObservableCollection<AccountProfile> sharedAccounts, IVenueFactory venueFactory, CandleRepository candleRepository, SymbolCatalogRepository symbolCatalogRepository, AppLogger logger, ToastService toastService, UserPreferenceRepository userPreferenceRepository, TradingApiService? tradingApiService = null)
    {
        _accountStore = accountStore;
        _venueFactory = venueFactory;
        _candleRepository = candleRepository;
        _symbolCatalogRepository = symbolCatalogRepository;
        _logger = logger;
        _toastService = toastService;
        _userPreferenceRepository = userPreferenceRepository;
        _tradingApiService = tradingApiService;

        TabId = Guid.NewGuid();
        Header = "新分頁";
        AvailableAccounts = sharedAccounts;
        SymbolOptions = new ObservableCollection<string>();

        var savedLeverage = _userPreferenceRepository.GetOrderLeverageOrDefault(_orderLeverage);
        if (!string.IsNullOrWhiteSpace(savedLeverage))
        {
            _orderLeverage = savedLeverage;
        }

        var savedQuantity = _userPreferenceRepository.GetOrderQuantityOrDefault(_orderQuantity);
        if (!string.IsNullOrWhiteSpace(savedQuantity))
        {
            _orderQuantity = savedQuantity;
        }

        _logger.Info("WorkspaceTab", $"Order input restored leverage={_orderLeverage}, quantity={_orderQuantity}");

        ConfirmActivationCommand = new RelayCommand(
            _ => _ = ConfirmActivationAsync(),
            _ => SelectedAccount is not null && !IsConfigured);

        SubmitOrderCommand = new RelayCommand(
            _ => _ = SubmitOrderAsync(),
            _ => IsConfigured);

        ClosePositionLimitCommand = new RelayCommand(
            parameter => _ = SubmitClosePositionAsync(parameter as PositionPanelRow, useLimitPrice: true),
            _ => IsConfigured);

        ClosePositionMarketCommand = new RelayCommand(
            parameter => _ = SubmitClosePositionAsync(parameter as PositionPanelRow, useLimitPrice: false),
            _ => IsConfigured);

        CancelPendingOrderCommand = new RelayCommand(
            parameter => _ = CancelPendingOrderAsync(parameter as PendingOrderPanelRow),
            _ => IsConfigured);

        RefreshDataCommand = new RelayCommand(
            _ => _ = RefreshRecentDataAsync(),
            _ => IsConfigured);

        L.PropertyChanged += OnLocalizationChanged;
        RefreshLocalizedOrderOptions();
        UpdateAmountUnitOptions(_symbol);
        _candlePersistTask = Task.Run(() => PersistCandlesLoopAsync(_cts.Token), _cts.Token);
    }

    public Guid TabId { get; }
    public string Header { get; private set; }
    public WorkspaceBinding? Binding { get; private set; }
    public ObservableCollection<AccountProfile> AvailableAccounts { get; }
    public ObservableCollection<string> SymbolOptions { get; }

    public ICommand ConfirmActivationCommand { get; }
    public ICommand SubmitOrderCommand { get; }
    public ICommand ClosePositionLimitCommand { get; }
    public ICommand ClosePositionMarketCommand { get; }
    public ICommand CancelPendingOrderCommand { get; }
    public ICommand RefreshDataCommand { get; }

    public AccountProfile? SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            if (_isApiSessionManaged && !_isApplyingApiSession)
            {
                NotifySharedSessionLocked();
                RaisePropertyChanged(nameof(SelectedAccount));
                return;
            }

            if (SetProperty(ref _selectedAccount, value))
            {
                LoadSymbolOptions(value, autoSelectSymbol: true);

                (ConfirmActivationCommand as RelayCommand)?.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsConfigured
    {
        get => _isConfigured;
        private set
        {
            if (SetProperty(ref _isConfigured, value))
            {
                RaisePropertyChanged(nameof(IsUnconfigured));
                (SubmitOrderCommand as RelayCommand)?.NotifyCanExecuteChanged();
                (ClosePositionLimitCommand as RelayCommand)?.NotifyCanExecuteChanged();
                (ClosePositionMarketCommand as RelayCommand)?.NotifyCanExecuteChanged();
                (CancelPendingOrderCommand as RelayCommand)?.NotifyCanExecuteChanged();
                (RefreshDataCommand as RelayCommand)?.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsUnconfigured => !IsConfigured;

    public bool IsOrderBookVisible
    {
        get => _isOrderBookVisible;
        private set
        {
            if (SetProperty(ref _isOrderBookVisible, value))
            {
                RaisePropertyChanged(nameof(IsRecentTradesPanelVisible));
            }
        }
    }

    public string Symbol
    {
        get => _symbol;
        set
        {
            if (_isApiSessionManaged && !_isApplyingApiSession)
            {
                NotifySharedSessionLocked();
                RaisePropertyChanged(nameof(Symbol));
                return;
            }

            var normalized = (value ?? string.Empty).Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return;
            }

            if (SetProperty(ref _symbol, normalized))
            {
                UpdateAmountUnitOptions(normalized);

                if (!string.Equals(_selectedSymbolOption, normalized, StringComparison.Ordinal))
                {
                    _selectedSymbolOption = normalized;
                    RaisePropertyChanged(nameof(SelectedSymbolOption));
                }

                if (Binding is not null)
                {
                    Binding = Binding with { Symbol = normalized };
                    Header = $"{Binding.VenueId}:{normalized}";
                    RaisePropertyChanged(nameof(Header));
                }

                HoverCandleStatus = null;
                RecentTrades = Array.Empty<RecentTradeRow>();
                RefreshCurrentCandleFromCache();
                if (IsConfigured && !_suppressSymbolReload)
                {
                    _ = ReloadForMarketSettingChangeAsync("symbol");
                }
            }
        }
    }

    public string? SelectedSymbolOption
    {
        get => _selectedSymbolOption;
        set
        {
            if (_isApiSessionManaged && !_isApplyingApiSession)
            {
                NotifySharedSessionLocked();
                RaisePropertyChanged(nameof(SelectedSymbolOption));
                return;
            }

            var normalized = string.IsNullOrWhiteSpace(value)
                ? null
                : value.Trim().ToUpperInvariant();

            if (SetProperty(ref _selectedSymbolOption, normalized))
            {
                if (!string.IsNullOrWhiteSpace(normalized) &&
                    !string.Equals(Symbol, normalized, StringComparison.Ordinal))
                {
                    Symbol = normalized;
                }
            }
        }
    }

    public string SelectedInterval
    {
        get => _selectedInterval;
        set
        {
            if (_isApiSessionManaged && !_isApplyingApiSession)
            {
                NotifySharedSessionLocked();
                RaisePropertyChanged(nameof(SelectedInterval));
                return;
            }

            if (SetProperty(ref _selectedInterval, value))
            {
                HoverCandleStatus = null;
                RefreshCurrentCandleFromCache();
                if (IsConfigured)
                {
                    _ = ReloadForMarketSettingChangeAsync("interval");
                }
            }
        }
    }

    public string[] Intervals { get; } = ["5m", "10m", "15m", "30m", "1h", "2h", "4h", "6h", "12h", "1d", "7d", "30d"];

    public IReadOnlyList<string> OrderTypes
    {
        get => _orderTypes;
        private set => SetProperty(ref _orderTypes, value);
    }

    public IReadOnlyList<string> OrderSides
    {
        get => _orderSides;
        private set => SetProperty(ref _orderSides, value);
    }

    public string[] OrderBookTickSizeOptions { get; } = ["1", "10", "100"];

    public string OrderType
    {
        get => _orderType;
        set
        {
            if (SetProperty(ref _orderType, value))
            {
                _isLimitOrderType = IsLimitOrderText(value);
                RaisePropertyChanged(nameof(IsLimitOrder));
                RecalculateOrderEstimates();
            }
        }
    }

    public string OrderSide
    {
        get => _orderSide;
        set
        {
            if (SetProperty(ref _orderSide, value))
            {
                _isShortOrderSide = IsShortSideText(value);
                RecalculateOrderEstimates();
            }
        }
    }

    public string OrderQuantity
    {
        get => _orderQuantity;
        set
        {
            if (SetProperty(ref _orderQuantity, value))
            {
                PersistOrderQuantity(value);
                RecalculateOrderEstimates();
            }
        }
    }

    public string RelativeAmountUnit => _relativeAmountUnit;

    public string[] OrderAmountUnitOptions => ["USD", _relativeAmountUnit];

    public string SelectedAmountUnit
    {
        get => _selectedAmountUnit;
        set
        {
            var next = string.IsNullOrWhiteSpace(value) ? "USD" : value.Trim().ToUpperInvariant();
            if (SetProperty(ref _selectedAmountUnit, next))
            {
                RecalculateOrderEstimates();
            }
        }
    }

    public string OrderLeverage
    {
        get => _orderLeverage;
        set
        {
            if (SetProperty(ref _orderLeverage, value))
            {
                PersistOrderLeverage(value);
                RecalculateOrderEstimates();
            }
        }
    }

    public string OrderPrice
    {
        get => _orderPrice;
        set
        {
            if (SetProperty(ref _orderPrice, value))
            {
                RecalculateOrderEstimates();
            }
        }
    }

    public string LastOrderResult
    {
        get => _lastOrderResult;
        private set => SetProperty(ref _lastOrderResult, value);
    }

    public string ConnectionStatus
    {
        get => _connectionStatus;
        private set => SetProperty(ref _connectionStatus, value);
    }

    public DateTimeOffset? LastMarketEventAt
    {
        get => _lastMarketEventAt;
        private set
        {
            if (SetProperty(ref _lastMarketEventAt, value))
            {
                RaisePropertyChanged(nameof(LastMarketEventDisplay));
            }
        }
    }

    public string CandleStatus
    {
        get => _candleStatus;
        private set
        {
            if (SetProperty(ref _candleStatus, value))
            {
                RaisePropertyChanged(nameof(CandleStatusDisplay));
            }
        }
    }

    public string? HoverCandleStatus
    {
        get => _hoverCandleStatus;
        set
        {
            if (SetProperty(ref _hoverCandleStatus, value))
            {
                RaisePropertyChanged(nameof(CandleStatusDisplay));
            }
        }
    }

    public string OrderBookSummary
    {
        get => _orderBookSummary;
        private set => SetProperty(ref _orderBookSummary, value);
    }

    public string SpreadText
    {
        get => _spreadText;
        private set => SetProperty(ref _spreadText, value);
    }

    public string EstimatedCostUsd
    {
        get => _estimatedCostUsd;
        private set => SetProperty(ref _estimatedCostUsd, value);
    }

    public string EstimatedLiquidationPrice
    {
        get => _estimatedLiquidationPrice;
        private set => SetProperty(ref _estimatedLiquidationPrice, value);
    }

    public int SelectedOrderPanelTabIndex
    {
        get => _selectedOrderPanelTabIndex;
        set => SetProperty(ref _selectedOrderPanelTabIndex, value);
    }

    public string SelectedOrderBookTickSize
    {
        get => _selectedOrderBookTickSize;
        set
        {
            if (SetProperty(ref _selectedOrderBookTickSize, value))
            {
                if (_lastMidPrice.HasValue)
                {
                    UpdateOrderBookSnapshot(_lastMidPrice.Value);
                }
            }
        }
    }

    public bool IsRecentTradesEnabledByViewport
    {
        get => _isRecentTradesEnabledByViewport;
        private set
        {
            if (SetProperty(ref _isRecentTradesEnabledByViewport, value))
            {
                RaisePropertyChanged(nameof(IsRecentTradesPanelVisible));
            }
        }
    }

    public IReadOnlyList<OrderBookLevelRow> AskLevels
    {
        get => _askLevels;
        private set => SetProperty(ref _askLevels, value);
    }

    public IReadOnlyList<OrderBookLevelRow> BidLevels
    {
        get => _bidLevels;
        private set => SetProperty(ref _bidLevels, value);
    }

    public IReadOnlyList<RecentTradeRow> RecentTrades
    {
        get => _recentTrades;
        private set
        {
            if (SetProperty(ref _recentTrades, value))
            {
                RaisePropertyChanged(nameof(IsRecentTradesPanelVisible));
            }
        }
    }

    public IReadOnlyList<CandleViewPoint> CandleSeries
    {
        get => _candleSeries;
        private set => SetProperty(ref _candleSeries, value);
    }

    public IReadOnlyList<PositionPanelRow> ActivePositions
    {
        get => _activePositions;
    }

    public IReadOnlyList<PendingOrderPanelRow> PendingOrders
    {
        get => _pendingOrders;
        private set
        {
            if (SetProperty(ref _pendingOrders, value))
            {
                RaisePropertyChanged(nameof(HasPendingOrders));
                RaisePropertyChanged(nameof(HasNoPendingOrders));
            }
        }
    }

    public IReadOnlyList<BalancePanelRow> Balances
    {
        get => _balances;
        private set
        {
            if (SetProperty(ref _balances, value))
            {
                RaisePropertyChanged(nameof(HasBalances));
                RaisePropertyChanged(nameof(HasNoBalances));
            }
        }
    }

    public string CandleStatusDisplay => string.IsNullOrWhiteSpace(HoverCandleStatus) ? CandleStatus : HoverCandleStatus!;
    public bool IsApiSessionManaged => _isApiSessionManaged;
    public bool CanEditMarketSessionSettings => !_isApiSessionManaged;
    public bool IsLimitOrder => _isLimitOrderType;
    public bool IsRecentTradesPanelVisible => IsOrderBookVisible && IsRecentTradesEnabledByViewport && RecentTrades.Count > 0;
    public bool HasActivePositions => ActivePositions.Count > 0;
    public bool HasNoActivePositions => !HasActivePositions;
    public bool HasPendingOrders => PendingOrders.Count > 0;
    public bool HasNoPendingOrders => !HasPendingOrders;
    public bool HasBalances => Balances.Count > 0;
    public bool HasNoBalances => !HasBalances;

    public string LastMarketEventDisplay => LastMarketEventAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-";

    public bool MatchesBinding(Guid accountId, string symbol)
    {
        if (Binding is null)
        {
            return false;
        }

        return Binding.AccountId == accountId &&
               string.Equals(Binding.Symbol, symbol?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public bool TryGetApiSessionIdentity(out Guid accountId, out string symbol)
    {
        accountId = _apiSessionAccountId ?? Guid.Empty;
        symbol = Symbol;
        return _isApiSessionManaged && _apiSessionAccountId.HasValue && !string.IsNullOrWhiteSpace(Symbol);
    }

    public async Task AttachApiSessionAsync(ApiConnectionDto connection)
    {
        if (_tradingApiService is null)
        {
            throw new InvalidOperationException("TradingApiService is unavailable.");
        }

        var account = AvailableAccounts.FirstOrDefault(x => x.AccountId == connection.AccountId);
        if (account is null)
        {
            throw new InvalidOperationException($"Account not found for API session: {connection.AccountId}");
        }

        await StopMarketPumpAsync();
        await StopAccountStatePumpAsync();
        await StopApiSessionPumpAsync();

        if (_venue is not null)
        {
            try
            {
                await _venue.DisconnectMarketDataAsync(CancellationToken.None);
                await _venue.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.Warn("WorkspaceTab", $"AttachApiSession dispose old venue warning tabId={TabId}: {ex.Message}");
            }
            finally
            {
                _venue = null;
            }
        }

        _isApiSessionManaged = true;
        _apiSessionAccountId = connection.AccountId;
        _apiSessionCursor = null;
        _marketStreamSymbol = connection.Symbol;
        _suppressSymbolReload = true;
        _isApplyingApiSession = true;
        try
        {
            SelectedAccount = account;
            _symbol = connection.Symbol;
            _selectedSymbolOption = connection.Symbol;
            _selectedInterval = string.IsNullOrWhiteSpace(connection.Interval) ? "5m" : connection.Interval;
            RaisePropertyChanged(nameof(Symbol));
            RaisePropertyChanged(nameof(SelectedSymbolOption));
            RaisePropertyChanged(nameof(SelectedInterval));
        }
        finally
        {
            _suppressSymbolReload = false;
            _isApplyingApiSession = false;
        }

        Binding = new WorkspaceBinding(account.VenueId, account.AccountId, connection.Symbol);
        Header = $"{account.VenueId}:{connection.Symbol}";
        RaisePropertyChanged(nameof(Header));
        IsConfigured = true;
        RaisePropertyChanged(nameof(CanEditMarketSessionSettings));
        ConnectionStatus = $"Connected ({connection.VenueId} shared)";
        _symbolCatalogRepository.MarkActivated(account.VenueId, account.Environment, connection.Symbol);
        LoadSymbolOptions(account, autoSelectSymbol: false);
        RefreshCurrentCandleFromCache();
        StartApiSessionPump();
        _logger.Info("WorkspaceTab", $"Attached API shared session tabId={TabId}, account={connection.AccountId}, symbol={connection.Symbol}");
    }

    private void PersistOrderLeverage(string raw)
    {
        if (!decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
        {
            return;
        }

        var normalized = NumberText.Trim(parsed, 4);
        _userPreferenceRepository.SaveOrderLeverage(normalized);
    }

    private void PersistOrderQuantity(string raw)
    {
        if (!decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
        {
            return;
        }

        var normalized = NumberText.Trim(parsed, 8);
        _userPreferenceRepository.SaveOrderQuantity(normalized);
    }

    public void ApplyViewport(double width, double height)
    {
        IsOrderBookVisible = _viewportService.IsOrderBookVisible(width);
        IsRecentTradesEnabledByViewport = height >= 860;
    }

    private void OnLocalizationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.PropertyName) &&
            !string.Equals(e.PropertyName, "Item[]", StringComparison.Ordinal) &&
            !string.Equals(e.PropertyName, "Item", StringComparison.Ordinal))
        {
            return;
        }

        NotifyLocalizationChanged();
        RefreshLocalizedOrderOptions();
    }

    private void NotifySharedSessionLocked()
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastSharedLockToastAt < TimeSpan.FromSeconds(1.5))
        {
            return;
        }

        _lastSharedLockToastAt = now;
        _toastService.ShowWarning("此分頁由 API 共享連線管理，請透過 API 變更。");
    }

    private void RefreshLocalizedOrderOptions()
    {
        var orderTypes = new[]
        {
            L["OrderType_Market"],
            L["OrderType_Limit"]
        };

        var orderSides = new[]
        {
            L["OrderSide_Long"],
            L["OrderSide_Short"]
        };

        OrderTypes = orderTypes;
        OrderSides = orderSides;

        var nextOrderType = _isLimitOrderType ? orderTypes[1] : orderTypes[0];
        if (!string.Equals(_orderType, nextOrderType, StringComparison.Ordinal))
        {
            _orderType = nextOrderType;
            RaisePropertyChanged(nameof(OrderType));
        }

        var nextOrderSide = _isShortOrderSide ? orderSides[1] : orderSides[0];
        if (!string.Equals(_orderSide, nextOrderSide, StringComparison.Ordinal))
        {
            _orderSide = nextOrderSide;
            RaisePropertyChanged(nameof(OrderSide));
        }
    }

    private bool IsLimitOrderText(string value)
    {
        return OrderTypes.Count > 1 && string.Equals(value, OrderTypes[1], StringComparison.Ordinal);
    }

    private bool IsShortSideText(string value)
    {
        return OrderSides.Count > 1 && string.Equals(value, OrderSides[1], StringComparison.Ordinal);
    }

    private void LoadSymbolOptions(AccountProfile? account, bool autoSelectSymbol)
    {
        var currentSymbol = Symbol;
        _suppressSymbolReload = true;
        try
        {
            SymbolOptions.Clear();
            if (account is null)
            {
                SelectedSymbolOption = null;
                return;
            }

            var symbols = _symbolCatalogRepository.GetActiveSymbols(account.VenueId, account.Environment).ToList();
            if (symbols.Count == 0)
            {
                var fallback = ResolvePreferredSymbol(account, currentSymbol, symbols);
                if (!string.IsNullOrWhiteSpace(fallback))
                {
                    symbols.Add(fallback);
                }
            }

            foreach (var sym in symbols)
            {
                SymbolOptions.Add(sym);
            }

            var preferred = ResolvePreferredSymbol(account, currentSymbol, symbols);
            if (autoSelectSymbol || !symbols.Any(x => string.Equals(x, currentSymbol, StringComparison.OrdinalIgnoreCase)))
            {
                if (!string.Equals(preferred, currentSymbol, StringComparison.Ordinal))
                {
                    Symbol = preferred;
                }
            }

            var selected = symbols.FirstOrDefault(x => string.Equals(x, Symbol, StringComparison.OrdinalIgnoreCase))
                ?? preferred;
            _selectedSymbolOption = selected;
            RaisePropertyChanged(nameof(SelectedSymbolOption));
            RaisePropertyChanged(nameof(Symbol));
            _logger.Info("WorkspaceTab", $"Symbol options loaded venue={account.VenueId}, env={account.Environment}, count={symbols.Count}, selected={selected}");
        }
        finally
        {
            _suppressSymbolReload = false;
        }
    }

    private static string ResolvePreferredSymbol(AccountProfile account, string currentSymbol, IReadOnlyList<string> symbols)
    {
        if (symbols.Any(x => string.Equals(x, currentSymbol, StringComparison.OrdinalIgnoreCase)))
        {
            return currentSymbol;
        }

        if (string.Equals(account.VenueId, "BitMEX", StringComparison.OrdinalIgnoreCase))
        {
            var bitmexDefault = symbols.FirstOrDefault(x => string.Equals(x, "XBTUSD", StringComparison.OrdinalIgnoreCase));
            return bitmexDefault ?? symbols.FirstOrDefault() ?? "XBTUSD";
        }

        if (string.Equals(account.VenueId, "Hyperliquid", StringComparison.OrdinalIgnoreCase))
        {
            var hlDefault = symbols.FirstOrDefault(x => string.Equals(x, "BTC", StringComparison.OrdinalIgnoreCase));
            return hlDefault ?? symbols.FirstOrDefault() ?? "BTC";
        }

        return symbols.FirstOrDefault() ?? currentSymbol;
    }

    private async Task ConfirmActivationAsync()
    {
        if (_isApiSessionManaged)
        {
            _toastService.ShowWarning("此分頁由 API 共享連線管理。");
            return;
        }

        if (SelectedAccount is null || IsConfigured)
        {
            return;
        }

        try
        {
            _logger.Info("WorkspaceTab", $"Activation start tabId={TabId}, account={SelectedAccount.DisplayName}, venue={SelectedAccount.VenueId}, symbol={Symbol}");

            Binding = new WorkspaceBinding(SelectedAccount.VenueId, SelectedAccount.AccountId, Symbol);
            Header = $"{SelectedAccount.VenueId}:{Symbol}";
            RaisePropertyChanged(nameof(Header));

            IsConfigured = true;
            ConnectionStatus = "Connecting...";

            var credentials = _accountStore.GetCredentials(SelectedAccount.AccountId);
            _logger.Info("WorkspaceTab", $"Credentials loaded tabId={TabId}, hasApi={credentials.HasApiCredentials}");
            _venue = _venueFactory.Create(SelectedAccount, credentials);

            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            connectCts.CancelAfter(TimeSpan.FromSeconds(15));
            await _venue.ConnectMarketDataAsync([Symbol], connectCts.Token);
            _logger.Info("WorkspaceTab", $"MarketData connected tabId={TabId}");

            SeedCandleCacheFromStorage();
            await TryLoadVenueHistoricalCandlesAsync(connectCts.Token);
            RefreshCurrentCandleFromCache();
            _symbolCatalogRepository.MarkActivated(SelectedAccount.VenueId, SelectedAccount.Environment, Symbol);
            LoadSymbolOptions(SelectedAccount, autoSelectSymbol: false);
            ConnectionStatus = $"Connected ({_venue.VenueId})";
            _toastService.ShowInfo($"已連線：{_venue.VenueId} {Symbol}");
            _marketStreamSymbol = Symbol;
            StartMarketPump();
            StartAccountStatePump();
            _logger.Info("WorkspaceTab", $"Activation completed tabId={TabId}");
        }
        catch (OperationCanceledException ex)
        {
            ConnectionStatus = "Connection timeout";
            IsConfigured = false;
            _toastService.ShowError("連線逾時，請檢查網路或 API 設定");
            _logger.Error("WorkspaceTab", $"Activation timeout tabId={TabId}", ex);
        }
        catch (Exception ex)
        {
            ConnectionStatus = $"Connection error: {ex.Message}";
            IsConfigured = false;
            _toastService.ShowError($"連線失敗：{ex.Message}");
            _logger.Error("WorkspaceTab", $"Activation failed tabId={TabId}", ex);
        }
    }

    private async Task PumpMarketEventsAsync(CancellationToken cancellationToken)
    {
        if (_venue is null || Binding is null)
        {
            return;
        }

        try
        {
            await foreach (var marketEvent in _venue.MarketEvents(cancellationToken))
            {
                if (marketEvent is TradeTick tick)
                {
                    var streamSymbol = _marketStreamSymbol;
                    if (string.IsNullOrWhiteSpace(streamSymbol))
                    {
                        continue;
                    }

                    HandleTradeTick(Binding.VenueId, streamSymbol, tick);
                }

                Dispatcher.UIThread.Post(() =>
                {
                    LastMarketEventAt = marketEvent.Timestamp;
                    ConnectionStatus = $"Connected ({_venue.VenueId})";
                });
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path.
            _logger.Info("WorkspaceTab", $"Market pump canceled tabId={TabId}");
        }
        catch (Exception ex)
        {
            _logger.Error("WorkspaceTab", $"Market pump error tabId={TabId}", ex);
            Dispatcher.UIThread.Post(() =>
            {
                ConnectionStatus = $"Error: {ex.Message}";
            });
        }
    }

    private async Task SubmitOrderAsync()
    {
        if (_isApiSessionManaged && _tradingApiService is not null && _apiSessionAccountId.HasValue)
        {
            await SubmitOrderViaApiSessionAsync(_apiSessionAccountId.Value);
            return;
        }

        if (!IsConfigured || _venue is null || Binding is null)
        {
            return;
        }

        if (!decimal.TryParse(OrderQuantity, NumberStyles.Any, CultureInfo.InvariantCulture, out var qty) || qty <= 0)
        {
            LastOrderResult = "下單失敗：Qty 格式錯誤";
            return;
        }

        if (!decimal.TryParse(OrderLeverage, NumberStyles.Any, CultureInfo.InvariantCulture, out var leverage) || leverage <= 0)
        {
            LastOrderResult = "下單失敗：槓桿格式錯誤";
            return;
        }

        try
        {
            _logger.Info("WorkspaceTab", $"ConfigureLeverage start tabId={TabId}, symbol={Symbol}, leverage={leverage}");
            var configure = await _venue.ConfigureLeverageAsync(Symbol, leverage, _cts.Token);
            if (!configure.IsSuccess)
            {
                LastOrderResult = $"下單失敗：槓桿設定失敗：{configure.Message}";
                _toastService.ShowError($"槓桿設定失敗：{configure.Message}");
                _logger.Warn("WorkspaceTab", $"ConfigureLeverage failed tabId={TabId}, symbol={Symbol}, leverage={leverage}, msg={configure.Message}");
                return;
            }

            _logger.Info("WorkspaceTab", $"ConfigureLeverage done tabId={TabId}, symbol={Symbol}, leverage={leverage}");
        }
        catch (Exception ex)
        {
            LastOrderResult = $"下單失敗：槓桿設定例外：{ex.Message}";
            _toastService.ShowError($"槓桿設定例外：{ex.Message}");
            _logger.Error("WorkspaceTab", $"ConfigureLeverage exception tabId={TabId}, symbol={Symbol}, leverage={leverage}", ex);
            return;
        }

        decimal? price = null;
        if (IsLimitOrder)
        {
            if (!decimal.TryParse(OrderPrice, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedPrice) || parsedPrice <= 0)
            {
                LastOrderResult = "下單失敗：Limit Price 格式錯誤";
                return;
            }

            price = parsedPrice;
        }

        var entryPrice = price ?? _lastMidPrice ?? 0m;
        if (entryPrice <= 0)
        {
            LastOrderResult = "下單失敗：尚無可用價格";
            return;
        }

        var notionalUsd = string.Equals(SelectedAmountUnit, "USD", StringComparison.OrdinalIgnoreCase)
            ? qty
            : qty * entryPrice;
        if (notionalUsd <= 0)
        {
            LastOrderResult = "下單失敗：金額格式錯誤";
            return;
        }

        var baseQuantity = string.Equals(SelectedAmountUnit, "USD", StringComparison.OrdinalIgnoreCase)
            ? (entryPrice > 0 ? notionalUsd / entryPrice : 0m)
            : qty;
        if (baseQuantity <= 0)
        {
            LastOrderResult = "下單失敗：數量換算錯誤";
            return;
        }

        var localOrderId = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Guid.NewGuid():N}"[..22];
        UpsertPendingOrder(new PendingOrderState(
            localOrderId,
            Symbol,
            notionalUsd,
            leverage,
            price,
            "送單中",
            null));

        try
        {
            _logger.Info("WorkspaceTab", $"PlaceOrder start tabId={TabId}, symbol={Symbol}, side={OrderSide}, type={OrderType}, amountInput={OrderQuantity}, unit={SelectedAmountUnit}, baseQty={baseQuantity}, notionalUsd={notionalUsd}, price={OrderPrice}");
            var venueSide = _isShortOrderSide ? "Sell" : "Buy";
            var ack = await _venue.PlaceOrderAsync(Symbol, venueSide, baseQuantity, price, _cts.Token);
            LastOrderResult = ack.Success
                ? $"下單成功：{ack.ClientOrderId}"
                : $"下單失敗：{ack.Message ?? "unknown"}";
            if (ack.Success)
            {
                if (IsLimitOrder)
                {
                    UpsertPendingOrder(new PendingOrderState(
                        localOrderId,
                        Symbol,
                        notionalUsd,
                        leverage,
                        price,
                        "已送出待同步",
                        ack.ClientOrderId));
                }
                else
                {
                    RemovePendingOrder(localOrderId);
                }

                _toastService.ShowInfo("下單成功");
            }
            else
            {
                UpsertPendingOrder(new PendingOrderState(
                    localOrderId,
                    Symbol,
                    notionalUsd,
                    leverage,
                    price,
                    $"送出失敗：{ack.Message ?? "unknown"}",
                    ack.ClientOrderId));
                _toastService.ShowError($"下單失敗：{ack.Message}");
            }

            RemovePendingOrder(localOrderId, onlyIfStatusMatches: "送單中");
            _ = RefreshAccountStateOnceAsync();
            _logger.Info("WorkspaceTab", $"PlaceOrder done tabId={TabId}, success={ack.Success}, msg={ack.Message}");
        }
        catch (Exception ex)
        {
            LastOrderResult = $"下單例外：{ex.Message}";
            UpsertPendingOrder(new PendingOrderState(
                localOrderId,
                Symbol,
                notionalUsd,
                leverage,
                price,
                $"送單例外：{ex.Message}",
                null));
            RemovePendingOrder(localOrderId, onlyIfStatusMatches: "送單中");
            _ = RefreshAccountStateOnceAsync();
            _toastService.ShowError($"下單例外：{ex.Message}");
            _logger.Error("WorkspaceTab", $"PlaceOrder exception tabId={TabId}", ex);
        }
    }

    private async Task SubmitOrderViaApiSessionAsync(Guid accountId)
    {
        if (!decimal.TryParse(OrderQuantity, NumberStyles.Any, CultureInfo.InvariantCulture, out var qty) || qty <= 0)
        {
            LastOrderResult = "下單失敗：Qty 格式錯誤";
            return;
        }

        if (!decimal.TryParse(OrderLeverage, NumberStyles.Any, CultureInfo.InvariantCulture, out var leverage) || leverage <= 0)
        {
            LastOrderResult = "下單失敗：槓桿格式錯誤";
            return;
        }

        decimal? price = null;
        if (IsLimitOrder)
        {
            if (!decimal.TryParse(OrderPrice, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedPrice) || parsedPrice <= 0)
            {
                LastOrderResult = "下單失敗：Limit Price 格式錯誤";
                return;
            }

            price = parsedPrice;
        }

        var localOrderId = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Guid.NewGuid():N}"[..22];
        UpsertPendingOrder(new PendingOrderState(
            localOrderId,
            Symbol,
            qty,
            leverage,
            price,
            "送單中",
            null));

        try
        {
            var side = _isShortOrderSide ? "short" : "long";
            var orderType = IsLimitOrder ? "limit" : "market";
            var unit = string.IsNullOrWhiteSpace(SelectedAmountUnit) ? "USD" : SelectedAmountUnit;
            var result = await _tradingApiService!.OpenPositionAsync(
                new ApiOpenPositionRequest(
                    accountId,
                    Symbol,
                    side,
                    orderType,
                    leverage,
                    qty,
                    unit,
                    price),
                _cts.Token);

            LastOrderResult = "下單成功";
            RemovePendingOrder(localOrderId);
            _toastService.ShowInfo("下單成功");
            _ = RefreshAccountStateOnceAsync();
            _logger.Info("WorkspaceTab", $"PlaceOrder(API shared) done tabId={TabId}, symbol={Symbol}, side={side}, type={orderType}, result={result}");
        }
        catch (Exception ex)
        {
            LastOrderResult = $"下單失敗：{ex.Message}";
            UpsertPendingOrder(new PendingOrderState(
                localOrderId,
                Symbol,
                qty,
                leverage,
                price,
                $"送出失敗：{ex.Message}",
                null));
            RemovePendingOrder(localOrderId, onlyIfStatusMatches: "送單中");
            _toastService.ShowError($"下單失敗：{ex.Message}");
            _ = RefreshAccountStateOnceAsync();
            _logger.Error("WorkspaceTab", $"PlaceOrder(API shared) exception tabId={TabId}", ex);
        }
    }

    private void HandleTradeTick(string venueId, string symbol, TradeTick tick)
    {
        // Only real trades should mutate candles; synthetic mid-price updates are used for book snapshot only.
        Candle? updated = null;

        if (tick.Size > 0)
        {
            lock (_candleLock)
            {
                var interval = ParseInterval(SelectedInterval);
                if (_currentCandle is not null && (_currentCandle.Symbol != symbol || _currentCandle.Interval != interval))
                {
                    _currentCandle = null;
                }

                var update = _candleAggregator.Aggregate(venueId, symbol, interval, tick, _currentCandle);
                updated = update.Candle;
                _currentCandle = updated;
                _candleCache.Upsert(updated);
            }

            _candlePersistChannel.Writer.TryWrite(updated);
            Dispatcher.UIThread.Post(() => CandleStatus = FormatCandleStatus(updated));
        }

        Dispatcher.UIThread.Post(() =>
        {
            UpdateOrderBookSnapshot(tick.Price);
            UpdatePositionMarks(symbol, tick.Price);
            if (tick.Size > 0)
            {
                AppendRecentTrade(tick);
            }
        });

        if (updated is not null)
        {
            Dispatcher.UIThread.Post(UpdateCandleSeriesFromCache);
        }
    }

    private void SeedCandleCacheFromStorage()
    {
        if (Binding is null)
        {
            return;
        }

        var interval = ParseInterval(SelectedInterval);
        var recent = _candleRepository.LoadRecent(Binding.VenueId, Symbol, interval, 300);
        lock (_candleLock)
        {
            foreach (var candle in recent)
            {
                _candleCache.Upsert(candle);
            }

            _currentCandle = recent.LastOrDefault();
        }

        MarkStorageLoaded(Binding.VenueId, Symbol, interval);
        _logger.Info("WorkspaceTab", $"Seed from storage tabId={TabId}, symbol={Symbol}, interval={interval}, count={recent.Count}");
    }

    private async Task TryLoadVenueHistoricalCandlesAsync(CancellationToken cancellationToken)
    {
        if (Binding is null || _venue is not IHistoricalCandleProvider provider)
        {
            return;
        }

        try
        {
            var interval = ParseInterval(SelectedInterval);
            var (requiredCount, since) = CalculateBackfillRequirement(interval, 12.0);
            var dbCount = _candleRepository.CountSince(Binding.VenueId, Symbol, interval, since);
            var inMemoryCount = _candleCache.Get(Binding.VenueId, Symbol, interval)
                .Count(x => x.OpenTime >= since);

            _logger.Info("WorkspaceTab", $"Backfill check tabId={TabId}, symbol={Symbol}, interval={interval}, required={requiredCount}, db={dbCount}, mem={inMemoryCount}");
            if (dbCount >= requiredCount || inMemoryCount >= requiredCount)
            {
                return;
            }

            var fetchCount = Math.Max(120, requiredCount + 40);
            _logger.Info("WorkspaceTab", $"Load venue historical candles start tabId={TabId}, symbol={Symbol}, interval={interval}, fetchCount={fetchCount}");
            var recent = await provider.GetRecentCandlesAsync(Symbol, interval, fetchCount, cancellationToken);
            lock (_candleLock)
            {
                foreach (var candle in recent)
                {
                    _candleCache.Upsert(candle);
                    _candleRepository.Upsert(candle);
                }

                _currentCandle = recent.LastOrDefault() ?? _currentCandle;
            }
            MarkStorageLoaded(Binding.VenueId, Symbol, interval);

            _logger.Info("WorkspaceTab", $"Load venue historical candles done tabId={TabId}, count={recent.Count}");
        }
        catch (Exception ex)
        {
            _logger.Error("WorkspaceTab", $"Load venue historical candles failed tabId={TabId}", ex);
        }
    }

    private async Task RefreshRecentDataAsync()
    {
        if (!IsConfigured || Binding is null || _venue is not IHistoricalCandleProvider provider)
        {
            return;
        }

        await _settingsReloadGate.WaitAsync();
        try
        {
            var interval = ParseInterval(SelectedInterval);
            var (requiredCount, since) = CalculateBackfillRequirement(interval, 12.0);
            var fetchCount = Math.Max(120, requiredCount + 40);

            _logger.Info("WorkspaceTab", $"Manual refresh start tabId={TabId}, symbol={Symbol}, interval={interval}, since={since:O}, fetchCount={fetchCount}");

            var deleted = _candleRepository.DeleteSince(Binding.VenueId, Symbol, interval, since);
            _logger.Info("WorkspaceTab", $"Manual refresh deleted old candles tabId={TabId}, deleted={deleted}");

            lock (_candleLock)
            {
                _candleCache.Clear(Binding.VenueId, Symbol, interval);
                _currentCandle = null;
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            cts.CancelAfter(TimeSpan.FromSeconds(20));

            var recent = await provider.GetRecentCandlesAsync(Symbol, interval, fetchCount, cts.Token);
            lock (_candleLock)
            {
                foreach (var candle in recent)
                {
                    if (candle.OpenTime < since)
                    {
                        continue;
                    }

                    _candleCache.Upsert(candle);
                    _candleRepository.Upsert(candle);
                }
            }

            RefreshCurrentCandleFromCache();
            _toastService.ShowInfo("資料重整完成（近 12 小時）");
            _logger.Info("WorkspaceTab", $"Manual refresh completed tabId={TabId}, loaded={recent.Count}");
        }
        catch (Exception ex)
        {
            _toastService.ShowError($"資料重整失敗：{ex.Message}");
            _logger.Error("WorkspaceTab", $"Manual refresh failed tabId={TabId}", ex);
        }
        finally
        {
            _settingsReloadGate.Release();
        }
    }

    private async Task ReloadForMarketSettingChangeAsync(string reason)
    {
        if (_venue is null || Binding is null)
        {
            return;
        }

        await _settingsReloadGate.WaitAsync();
        try
        {
            _logger.Info("WorkspaceTab", $"Reload start tabId={TabId}, reason={reason}, symbol={Symbol}, interval={SelectedInterval}");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            cts.CancelAfter(TimeSpan.FromSeconds(15));

            if (reason == "symbol")
            {
                await ReconnectForSymbolChangeAsync(Symbol, cts.Token);
            }

            lock (_candleLock)
            {
                _currentCandle = null;
            }

            await TryLoadVenueHistoricalCandlesAsync(cts.Token);
            RefreshCurrentCandleFromCache();

            if (reason == "symbol" && SelectedAccount is not null)
            {
                _symbolCatalogRepository.MarkActivated(SelectedAccount.VenueId, SelectedAccount.Environment, Symbol);
                LoadSymbolOptions(SelectedAccount, autoSelectSymbol: false);
            }

            _logger.Info("WorkspaceTab", $"Reload completed tabId={TabId}, reason={reason}");
        }
        catch (Exception ex)
        {
            _logger.Error("WorkspaceTab", $"Reload failed tabId={TabId}, reason={reason}", ex);
            _toastService.ShowError($"資料重載失敗：{ex.Message}");
        }
        finally
        {
            _settingsReloadGate.Release();
        }
    }

    private static double IntervalToHours(CandleInterval interval)
    {
        return interval switch
        {
            CandleInterval.M5 => 5.0 / 60.0,
            CandleInterval.M10 => 10.0 / 60.0,
            CandleInterval.M15 => 15.0 / 60.0,
            CandleInterval.M30 => 30.0 / 60.0,
            CandleInterval.H1 => 1,
            CandleInterval.H2 => 2,
            CandleInterval.H4 => 4,
            CandleInterval.H6 => 6,
            CandleInterval.H12 => 12,
            CandleInterval.D1 => 24,
            CandleInterval.D7 => 24 * 7,
            CandleInterval.D30 => 24 * 30,
            _ => 1
        };
    }

    private static (int RequiredCount, DateTimeOffset Since) CalculateBackfillRequirement(CandleInterval interval, double requiredHours)
    {
        var intervalHours = Math.Max(1.0 / 60.0, IntervalToHours(interval));
        var requiredCount = Math.Max(1, (int)Math.Ceiling(requiredHours / intervalHours));
        var since = DateTimeOffset.UtcNow.AddHours(-requiredHours);
        return (requiredCount, since);
    }

    private void RefreshCurrentCandleFromCache()
    {
        if (Binding is null)
        {
            CandleStatus = "尚無 K 線資料";
            return;
        }

        Candle? last;
        lock (_candleLock)
        {
            var interval = ParseInterval(SelectedInterval);
            var inMemory = _candleCache.Get(Binding.VenueId, Symbol, interval);

            if (inMemory.Count == 0 && ShouldLoadFromStorage(Binding.VenueId, Symbol, interval))
            {
                var fromStorage = _candleRepository.LoadRecent(Binding.VenueId, Symbol, interval, 300);
                foreach (var candle in fromStorage)
                {
                    _candleCache.Upsert(candle);
                }

                last = fromStorage.LastOrDefault();
                MarkStorageLoaded(Binding.VenueId, Symbol, interval);
                _logger.Info("WorkspaceTab", $"Load from storage for view tabId={TabId}, symbol={Symbol}, interval={interval}, count={fromStorage.Count}");
            }
            else
            {
                last = inMemory.LastOrDefault();
            }

            _currentCandle = last;
        }

        CandleStatus = last is null ? "尚無 K 線資料" : FormatCandleStatus(last);
        UpdateCandleSeriesFromCache();
    }

    private static CandleInterval ParseInterval(string interval)
    {
        return interval switch
        {
            "5m" => CandleInterval.M5,
            "10m" => CandleInterval.M10,
            "15m" => CandleInterval.M15,
            "30m" => CandleInterval.M30,
            "1h" => CandleInterval.H1,
            "2h" => CandleInterval.H2,
            "4h" => CandleInterval.H4,
            "6h" => CandleInterval.H6,
            "12h" => CandleInterval.H12,
            "1d" => CandleInterval.D1,
            "7d" => CandleInterval.D7,
            "30d" => CandleInterval.D30,
            _ => CandleInterval.M5
        };
    }

    private static string FormatCandleStatus(Candle candle)
    {
        return $"{candle.Interval}   O:{NumberText.Trim(candle.Open)}   H:{NumberText.Trim(candle.High)}   L:{NumberText.Trim(candle.Low)}   C:{NumberText.Trim(candle.Close)}   V:{NumberText.Trim(candle.Volume)}";
    }

    private static string BuildOrderBookSummary(decimal mid)
    {
        var ask1 = mid + 1;
        var ask2 = mid + 2;
        var ask3 = mid + 3;
        var bid1 = mid - 1;
        var bid2 = mid - 2;
        var bid3 = mid - 3;

        return
            $"ASK {NumberText.Trim(ask3)} x {NumberText.Trim(2.00m)}\n" +
            $"ASK {NumberText.Trim(ask2)} x {NumberText.Trim(1.50m)}\n" +
            $"ASK {NumberText.Trim(ask1)} x {NumberText.Trim(1.00m)}\n" +
            $"MID {NumberText.Trim(mid)}\n" +
            $"BID {NumberText.Trim(bid1)} x {NumberText.Trim(1.20m)}\n" +
            $"BID {NumberText.Trim(bid2)} x {NumberText.Trim(1.80m)}\n" +
            $"BID {NumberText.Trim(bid3)} x {NumberText.Trim(2.40m)}";
    }

    private void UpdateOrderBookSnapshot(decimal mid)
    {
        OrderBookSummary = BuildOrderBookSummary(mid);
        _lastMidPrice = mid;
        var snapshot = BuildOrderBookLevels(mid, ParseOrderBookTickSize());
        AskLevels = snapshot.Asks;
        BidLevels = snapshot.Bids;
        SpreadText = snapshot.SpreadText;
        RecalculateOrderEstimates();
    }

    private static (IReadOnlyList<OrderBookLevelRow> Asks, IReadOnlyList<OrderBookLevelRow> Bids, string SpreadText) BuildOrderBookLevels(decimal mid, decimal tickSize)
    {
        if (mid <= 0)
        {
            return (Array.Empty<OrderBookLevelRow>(), Array.Empty<OrderBookLevelRow>(), "Spread -");
        }

        const int levels = 8;
        var asks = new List<OrderBookLevelRow>(levels);
        var bids = new List<OrderBookLevelRow>(levels);

        var askTotal = 0m;
        var bidTotal = 0m;
        var bestAsk = decimal.Ceiling(mid / tickSize) * tickSize;
        var bestBid = decimal.Floor(mid / tickSize) * tickSize;
        if (bestAsk <= bestBid)
        {
            bestAsk = bestBid + tickSize;
        }

        for (var i = 0; i < levels; i++)
        {
            var step = i + 1;
            var askPx = bestAsk + (i * tickSize);
            var bidPx = bestBid - (i * tickSize);
            var askSize = Math.Round(0.8m + (step * 0.34m), 2);
            var bidSize = Math.Round(0.85m + (step * 0.32m), 2);
            askTotal += askSize;
            bidTotal += bidSize;

            asks.Add(new OrderBookLevelRow(askPx, askSize, askTotal, IsAsk: true));
            bids.Add(new OrderBookLevelRow(bidPx, bidSize, bidTotal, IsAsk: false));
        }

        var spread = asks[0].Price - bids[0].Price;
        var spreadPct = mid == 0 ? 0 : (spread / mid) * 100m;
        var spreadText = $"Spread {NumberText.Trim(spread)} ({NumberText.Trim(spreadPct)}%)";
        asks.Reverse();
        return (asks, bids, spreadText);
    }

    private void RecalculateOrderEstimates()
    {
        if (!_lastMidPrice.HasValue || _lastMidPrice.Value <= 0)
        {
            EstimatedCostUsd = "-";
            EstimatedLiquidationPrice = "-";
            return;
        }

        if (!decimal.TryParse(OrderQuantity, NumberStyles.Any, CultureInfo.InvariantCulture, out var amountInput) || amountInput <= 0)
        {
            EstimatedCostUsd = "-";
            EstimatedLiquidationPrice = "-";
            return;
        }

        if (!decimal.TryParse(OrderLeverage, NumberStyles.Any, CultureInfo.InvariantCulture, out var leverage) || leverage <= 0)
        {
            EstimatedCostUsd = "-";
            EstimatedLiquidationPrice = "-";
            return;
        }

        decimal entryPrice;
        if (IsLimitOrder)
        {
            if (!decimal.TryParse(OrderPrice, NumberStyles.Any, CultureInfo.InvariantCulture, out var limitPrice) || limitPrice <= 0)
            {
                EstimatedCostUsd = "-";
                EstimatedLiquidationPrice = "-";
                return;
            }

            entryPrice = limitPrice;
        }
        else
        {
            entryPrice = _lastMidPrice.Value;
        }

        var notional = string.Equals(SelectedAmountUnit, "USD", StringComparison.OrdinalIgnoreCase)
            ? amountInput
            : amountInput * entryPrice;

        if (notional <= 0)
        {
            EstimatedCostUsd = "-";
            EstimatedLiquidationPrice = "-";
            return;
        }

        var estimatedCost = notional / leverage;
        var liquidation = _isShortOrderSide
            ? entryPrice * (1m + (1m / leverage))
            : entryPrice * (1m - (1m / leverage));

        if (liquidation < 0)
        {
            liquidation = 0;
        }

        EstimatedCostUsd = NumberText.Trim(estimatedCost);
        EstimatedLiquidationPrice = NumberText.Trim(liquidation);
    }

    private void UpdateAmountUnitOptions(string symbol)
    {
        var nextRelative = ResolveRelativeAmountUnit(symbol);
        var hadRelativeSelected = !string.Equals(SelectedAmountUnit, "USD", StringComparison.OrdinalIgnoreCase);

        if (!string.Equals(_relativeAmountUnit, nextRelative, StringComparison.Ordinal))
        {
            _relativeAmountUnit = nextRelative;
            RaisePropertyChanged(nameof(RelativeAmountUnit));
            RaisePropertyChanged(nameof(OrderAmountUnitOptions));
        }

        if (hadRelativeSelected && !string.Equals(_selectedAmountUnit, _relativeAmountUnit, StringComparison.Ordinal))
        {
            _selectedAmountUnit = _relativeAmountUnit;
            RaisePropertyChanged(nameof(SelectedAmountUnit));
        }

        RecalculateOrderEstimates();
    }

    private static string ResolveRelativeAmountUnit(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return "BTC";
        }

        var upper = symbol.Trim().ToUpperInvariant();
        if (upper.Contains(':'))
        {
            upper = upper[(upper.LastIndexOf(':') + 1)..];
        }

        if (upper.Contains('/'))
        {
            upper = upper.Split('/')[0];
        }

        if (upper.Contains('-'))
        {
            upper = upper.Split('-')[0];
        }

        if (upper.EndsWith("USDT", StringComparison.Ordinal) && upper.Length > 4)
        {
            upper = upper[..^4];
        }
        else if (upper.EndsWith("USDC", StringComparison.Ordinal) && upper.Length > 4)
        {
            upper = upper[..^4];
        }
        else if (upper.EndsWith("USD", StringComparison.Ordinal) && upper.Length > 3)
        {
            upper = upper[..^3];
        }

        return upper switch
        {
            "XBT" => "BTC",
            "" => "BTC",
            _ => upper
        };
    }

    private decimal ParseOrderBookTickSize()
    {
        if (decimal.TryParse(SelectedOrderBookTickSize, NumberStyles.Any, CultureInfo.InvariantCulture, out var tick) && tick > 0)
        {
            return tick;
        }

        return 1m;
    }

    private void AppendRecentTrade(TradeTick tick)
    {
        var priorPrice = RecentTrades.Count > 0 ? RecentTrades[0].Price : (decimal?)null;
        var side = L["Trade_Side_Flat"];
        var priceHex = "#84AFC0";
        var sideHex = "#84AFC0";
        if (priorPrice.HasValue)
        {
            if (tick.Price >= priorPrice.Value)
            {
                side = L["Trade_Side_Buy"];
                priceHex = "#5ED0A9";
                sideHex = "#5ED0A9";
            }
            else
            {
                side = L["Trade_Side_Sell"];
                priceHex = "#E47A8E";
                sideHex = "#E47A8E";
            }
        }

        var row = new RecentTradeRow(
            tick.Timestamp.ToLocalTime(),
            tick.Price,
            tick.Size,
            side,
            priceHex,
            sideHex);

        var next = new List<RecentTradeRow>(RecentTrades.Count + 1) { row };
        next.AddRange(RecentTrades.Take(9));
        RecentTrades = next;
    }

    private void ApplyAccountSnapshot(VenueAccountSnapshot snapshot)
    {
        _positionStates.Clear();
        foreach (var p in snapshot.Positions)
        {
            _positionStates[p.Symbol] = new PositionState(
                p.Symbol,
                p.Quantity < 0 ? "Short" : "Long",
                p.NotionalUsd,
                p.Leverage,
                p.EntryPrice,
                p.MarkPrice,
                p.UnrealizedPnlUsd,
                p.RealizedPnlUsd,
                p.Quantity);
        }

        RebuildPositionRows();

        CleanupSuppressedCanceledOrderIds();

        _remotePendingOrders = snapshot.OpenOrders
            .Where(x => !IsSuppressedCanceledOrderId(x.OrderId))
            .Where(x => !IsFailedOrderStatus(x.Status))
            .Select(x => new PendingOrderPanelRow(
                x.Symbol,
                $"{NumberText.Trim(x.NotionalUsd, useGrouping: true)} USD",
                FormatLeverageText(x.Leverage),
                x.LimitPrice.HasValue ? NumberText.Trim(x.LimitPrice.Value) : "-",
                x.Status,
                x.OrderId,
                null,
                true))
            .ToList();

        CleanupSyncedPendingOrders();
        CleanupTransientPendingOrders();
        RebuildPendingOrderRows();

        var balances = snapshot.Balances
            .GroupBy(x => x.Asset, StringComparer.OrdinalIgnoreCase)
            .Select(g => new VenueBalance(
                g.Key.ToUpperInvariant(),
                g.Sum(x => x.Quantity),
                g.Sum(x => x.UsdValue)))
            .Where(x => x.Quantity != 0m)
            .OrderBy(x => IsStableDisplayAsset(x.Asset) ? 0 : 1)
            .ThenBy(x => x.Asset, StringComparer.OrdinalIgnoreCase)
            .Select(x => new BalancePanelRow(
                x.Asset.ToUpperInvariant(),
                x.Quantity,
                x.UsdValue))
            .ToList();
        Balances = balances;
    }

    private static bool IsStableDisplayAsset(string asset)
    {
        if (string.IsNullOrWhiteSpace(asset))
        {
            return false;
        }

        var upper = asset.Trim().ToUpperInvariant();
        return upper.StartsWith("USD", StringComparison.Ordinal) ||
               upper.StartsWith("USDT", StringComparison.Ordinal) ||
               upper.StartsWith("USDC", StringComparison.Ordinal);
    }

    private void CleanupTransientPendingOrders()
    {
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-5);
        var toRemove = _pendingOrderStates
            .Where(x =>
                IsPendingOrderStatusForPanel(x.Value.Status) &&
                x.Value.CreatedAt < cutoff)
            .Select(x => x.Key)
            .ToList();

        foreach (var id in toRemove)
        {
            _pendingOrderStates.Remove(id);
        }

        if (toRemove.Count > 0)
        {
            _logger.Info("WorkspaceTab", $"Pending order cleanup removed synced-local rows tabId={TabId}, removed={toRemove.Count}");
        }
    }

    private void CleanupSyncedPendingOrders()
    {
        var remoteIds = new HashSet<string>(
            _remotePendingOrders
                .Select(x => x.VenueOrderId)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x!),
            StringComparer.OrdinalIgnoreCase);

        var toRemove = _pendingOrderStates
            .Where(x =>
                !string.IsNullOrWhiteSpace(x.Value.VenueOrderId) &&
                !remoteIds.Contains(x.Value.VenueOrderId!) &&
                x.Value.Status.Contains("待同步", StringComparison.Ordinal))
            .Select(x => x.Key)
            .ToList();

        foreach (var id in toRemove)
        {
            _pendingOrderStates.Remove(id);
        }
    }

    private static string FormatLeverageText(decimal leverage)
    {
        return leverage > 0
            ? $"{NumberText.Trim(leverage)}x"
            : "-";
    }

    private void UpsertPosition(PositionState state)
    {
        _positionStates[state.Symbol] = state;
        RebuildPositionRows();
        _logger.Info("WorkspaceTab", $"Position upsert symbol={state.Symbol}, side={state.Side}, notional={state.NotionalUsd:F2}, leverage={state.Leverage:F2}");
    }

    private void UpdatePositionMarks(string symbol, decimal markPrice)
    {
        if (markPrice <= 0)
        {
            return;
        }

        if (!_positionStates.TryGetValue(symbol, out var existing))
        {
            return;
        }

        if (Math.Abs(existing.MarkPrice - markPrice) < 0.0000001m)
        {
            return;
        }

        _positionStates[symbol] = existing with { MarkPrice = markPrice };
        var next = _positionStates[symbol];
        var pct = ComputeUnrealizedPnlPct(next);
        var unrealizedUsd = next.NotionalUsd > 0
            ? next.NotionalUsd * (pct / 100m)
            : next.UnrealizedPnlUsd;
        _positionStates[symbol] = next with { UnrealizedPnlUsd = unrealizedUsd };
        RebuildPositionRows();
    }

    private void RebuildPositionRows()
    {
        var sortedStates = _positionStates.Values
            .OrderBy(x => x.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var existingRows = _activePositions.ToDictionary(x => x.Symbol, StringComparer.OrdinalIgnoreCase);
        var desiredSymbols = new HashSet<string>(sortedStates.Select(x => x.Symbol), StringComparer.OrdinalIgnoreCase);
        var removedSymbols = _positionClosePriceInputs.Keys
            .Where(x => !desiredSymbols.Contains(x))
            .ToList();
        foreach (var symbol in removedSymbols)
        {
            _positionClosePriceInputs.Remove(symbol);
        }

        for (var i = _activePositions.Count - 1; i >= 0; i--)
        {
            if (!desiredSymbols.Contains(_activePositions[i].Symbol))
            {
                _activePositions.RemoveAt(i);
            }
        }

        for (var i = 0; i < sortedStates.Count; i++)
        {
            var state = sortedStates[i];
            var closePrice = _positionClosePriceInputs.TryGetValue(state.Symbol, out var existingClosePrice) && !string.IsNullOrWhiteSpace(existingClosePrice)
                ? existingClosePrice
                : NumberText.Trim(state.EntryPrice > 0 ? state.EntryPrice : state.MarkPrice);
            if (!_positionClosePriceInputs.ContainsKey(state.Symbol))
            {
                _positionClosePriceInputs[state.Symbol] = closePrice;
            }

            if (!existingRows.TryGetValue(state.Symbol, out var row))
            {
                row = new PositionPanelRow(state.Symbol, closePrice);
                row.PropertyChanged += (_, args) =>
                {
                    if (string.Equals(args.PropertyName, nameof(PositionPanelRow.ClosePrice), StringComparison.Ordinal))
                    {
                        _positionClosePriceInputs[row.Symbol] = row.ClosePrice;
                    }
                };
                _activePositions.Insert(i, row);
            }
            else
            {
                var currentIndex = _activePositions.IndexOf(row);
                if (currentIndex != i && currentIndex >= 0)
                {
                    _activePositions.Move(currentIndex, i);
                }
            }

            row.ApplyState(
                contractAmount: $"{NumberText.Trim(state.NotionalUsd, useGrouping: true)} USD",
                leverage: FormatLeverageText(state.Leverage),
                entryPrice: state.EntryPrice,
                markPrice: state.MarkPrice,
                unrealizedPnlPct: ComputeUnrealizedPnlPct(state),
                unrealizedPnlUsd: state.UnrealizedPnlUsd,
                realizedPnlUsd: state.RealizedPnlUsd);
        }

        RaisePropertyChanged(nameof(HasActivePositions));
        RaisePropertyChanged(nameof(HasNoActivePositions));
    }

    private void UpsertPendingOrder(PendingOrderState order)
    {
        _pendingOrderStates[order.LocalId] = order;
        RebuildPendingOrderRows();
    }

    private void RemovePendingOrder(string localId, string? onlyIfStatusMatches = null)
    {
        if (!_pendingOrderStates.TryGetValue(localId, out var existing))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(onlyIfStatusMatches) &&
            !string.Equals(existing.Status, onlyIfStatusMatches, StringComparison.Ordinal))
        {
            return;
        }

        _pendingOrderStates.Remove(localId);
        RebuildPendingOrderRows();
    }

    private void RebuildPendingOrderRows()
    {
        var localRows = _pendingOrderStates.Values
            .Where(x => IsPendingOrderStatusForPanel(x.Status))
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new PendingOrderPanelRow(
                x.Symbol,
                $"{NumberText.Trim(x.NotionalUsd, useGrouping: true)} USD",
                FormatLeverageText(x.Leverage),
                x.LimitPrice.HasValue ? NumberText.Trim(x.LimitPrice.Value) : "-",
                x.Status,
                x.VenueOrderId,
                x.LocalId,
                false))
            .ToList();

        var remoteOrderIds = new HashSet<string>(
            _remotePendingOrders
                .Select(x => x.VenueOrderId)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x!),
            StringComparer.OrdinalIgnoreCase);
        var merged = new List<PendingOrderPanelRow>(localRows.Count + _remotePendingOrders.Count);
        merged.AddRange(localRows.Where(x =>
            string.IsNullOrWhiteSpace(x.VenueOrderId) ||
            !remoteOrderIds.Contains(x.VenueOrderId)));
        merged.AddRange(_remotePendingOrders.Where(x => !IsFailedOrderStatus(x.Status)));
        PendingOrders = merged;
    }

    private static bool IsPendingOrderStatusForPanel(string status)
    {
        return string.Equals(status, "送單中", StringComparison.Ordinal) ||
               string.Equals(status, "已送出待同步", StringComparison.Ordinal) ||
               string.Equals(status, "平倉送單中", StringComparison.Ordinal) ||
               string.Equals(status, "平倉已送出待同步", StringComparison.Ordinal);
    }

    private static bool IsFailedOrderStatus(string status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return false;
        }

        var s = status.Trim().ToLowerInvariant();
        return s.Contains("fail", StringComparison.Ordinal) ||
               s.Contains("error", StringComparison.Ordinal) ||
               s.Contains("reject", StringComparison.Ordinal) ||
               s.Contains("cancel", StringComparison.Ordinal) ||
               s.Contains("失敗", StringComparison.Ordinal) ||
               s.Contains("例外", StringComparison.Ordinal);
    }

    private bool IsSuppressedCanceledOrderId(string? orderId)
    {
        if (string.IsNullOrWhiteSpace(orderId))
        {
            return false;
        }

        return _suppressedCanceledOrderIds.ContainsKey(orderId);
    }

    private void SuppressCanceledOrderId(string? orderId)
    {
        if (string.IsNullOrWhiteSpace(orderId))
        {
            return;
        }

        _suppressedCanceledOrderIds[orderId] = DateTimeOffset.UtcNow;
    }

    private void CleanupSuppressedCanceledOrderIds()
    {
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-10);
        var expired = _suppressedCanceledOrderIds
            .Where(x => x.Value < cutoff)
            .Select(x => x.Key)
            .ToList();
        foreach (var id in expired)
        {
            _suppressedCanceledOrderIds.Remove(id);
        }
    }

    private static decimal ComputeUnrealizedPnlPct(PositionState state)
    {
        if (state.EntryPrice <= 0)
        {
            return 0;
        }

        var raw = string.Equals(state.Side, "Short", StringComparison.Ordinal)
            ? ((state.EntryPrice - state.MarkPrice) / state.EntryPrice) * 100m
            : ((state.MarkPrice - state.EntryPrice) / state.EntryPrice) * 100m;

        return raw;
    }

    private async Task SubmitClosePositionAsync(PositionPanelRow? row, bool useLimitPrice)
    {
        if (_isApiSessionManaged && _tradingApiService is not null && _apiSessionAccountId.HasValue)
        {
            await SubmitClosePositionViaApiSessionAsync(row, useLimitPrice, _apiSessionAccountId.Value);
            return;
        }

        if (row is null || !IsConfigured || _venue is null)
        {
            return;
        }

        if (!_positionStates.TryGetValue(row.Symbol, out var state))
        {
            _toastService.ShowError("找不到持倉資料");
            return;
        }

        var marketReferencePrice = state.MarkPrice > 0 ? state.MarkPrice : (_lastMidPrice ?? 0m);
        if (marketReferencePrice <= 0)
        {
            _toastService.ShowError("平倉失敗：尚無可用價格");
            return;
        }

        decimal? price = null;
        if (useLimitPrice)
        {
            var rawPrice = row.ClosePrice?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(rawPrice))
            {
                rawPrice = NumberText.Trim(marketReferencePrice);
                row.ClosePrice = rawPrice;
            }

            if (!decimal.TryParse(rawPrice, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedPrice) || parsedPrice <= 0)
            {
                _toastService.ShowError("平倉價格格式錯誤");
                return;
            }

            price = parsedPrice;
            _positionClosePriceInputs[row.Symbol] = rawPrice;
        }

        var notionalForDisplay = state.NotionalUsd > 0
            ? Math.Abs(state.NotionalUsd)
            : Math.Abs(state.Quantity) * marketReferencePrice;
        if (notionalForDisplay <= 0)
        {
            notionalForDisplay = marketReferencePrice;
        }

        var baseQuantity = state.NotionalUsd > 0
            ? Math.Abs(state.NotionalUsd) / marketReferencePrice
            : Math.Abs(state.Quantity);
        if (baseQuantity <= 0 && Math.Abs(state.Quantity) <= 0)
        {
            _toastService.ShowError("平倉失敗：部位數量為 0");
            return;
        }

        var localOrderId = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Guid.NewGuid():N}"[..22];
        var side = string.Equals(state.Side, "Short", StringComparison.Ordinal) ? "Buy" : "Sell";
        var sendingStatus = useLimitPrice ? "平倉送單中" : "平倉送單中";
        var syncedStatus = useLimitPrice ? "平倉已送出待同步" : "平倉已送出待同步";

        UpsertPendingOrder(new PendingOrderState(
            localOrderId,
            row.Symbol,
            notionalForDisplay,
            state.Leverage,
            price,
            sendingStatus,
            null));

        try
        {
            var closeQty = Math.Abs(state.Quantity);
            if (closeQty <= 0)
            {
                _toastService.ShowError("平倉失敗：無法取得持倉數量");
                RemovePendingOrder(localOrderId);
                return;
            }

            _logger.Info("WorkspaceTab", $"ClosePosition start tabId={TabId}, symbol={row.Symbol}, side={side}, closeQty={closeQty}, rawPosQty={state.Quantity}, notionalUsd={notionalForDisplay}, useLimit={useLimitPrice}, px={price}");
            var ack = await _venue.PlaceCloseOrderAsync(row.Symbol, side, closeQty, price, _cts.Token);
            if (ack.Success)
            {
                if (useLimitPrice)
                {
                    UpsertPendingOrder(new PendingOrderState(
                        localOrderId,
                        row.Symbol,
                        notionalForDisplay,
                        state.Leverage,
                        price,
                        syncedStatus,
                        ack.ClientOrderId));
                }
                else
                {
                    RemovePendingOrder(localOrderId);
                }

                _toastService.ShowInfo(useLimitPrice ? "平倉限價單已送出" : "平倉市價單已送出");
            }
            else
            {
                RemovePendingOrder(localOrderId);
                _toastService.ShowError($"平倉失敗：{ack.Message}");
            }

            RemovePendingOrder(localOrderId, onlyIfStatusMatches: sendingStatus);
            _ = RefreshAccountStateOnceAsync();
            _logger.Info("WorkspaceTab", $"ClosePosition done tabId={TabId}, symbol={row.Symbol}, success={ack.Success}, msg={ack.Message}");
        }
        catch (Exception ex)
        {
            RemovePendingOrder(localOrderId);
            _ = RefreshAccountStateOnceAsync();
            _toastService.ShowError($"平倉例外：{ex.Message}");
            _logger.Error("WorkspaceTab", $"ClosePosition exception tabId={TabId}, symbol={row.Symbol}", ex);
        }
    }

    private async Task SubmitClosePositionViaApiSessionAsync(PositionPanelRow? row, bool useLimitPrice, Guid accountId)
    {
        if (row is null || !_positionStates.TryGetValue(row.Symbol, out var state))
        {
            return;
        }

        decimal? price = null;
        if (useLimitPrice)
        {
            var rawPrice = row.ClosePrice?.Trim() ?? string.Empty;
            if (!decimal.TryParse(rawPrice, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedPrice) || parsedPrice <= 0)
            {
                _toastService.ShowError("平倉價格格式錯誤");
                return;
            }

            price = parsedPrice;
            _positionClosePriceInputs[row.Symbol] = rawPrice;
        }

        var localOrderId = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Guid.NewGuid():N}"[..22];
        UpsertPendingOrder(new PendingOrderState(
            localOrderId,
            row.Symbol,
            state.NotionalUsd > 0 ? Math.Abs(state.NotionalUsd) : 0m,
            state.Leverage,
            price,
            "平倉送單中",
            null));

        try
        {
            var orderType = useLimitPrice ? "limit" : "market";
            var result = await _tradingApiService!.ClosePositionAsync(
                new ApiClosePositionRequest(
                    accountId,
                    row.Symbol,
                    orderType,
                    price),
                _cts.Token);

            RemovePendingOrder(localOrderId);
            _toastService.ShowInfo(useLimitPrice ? "平倉限價單已送出" : "平倉市價單已送出");
            _ = RefreshAccountStateOnceAsync();
            _logger.Info("WorkspaceTab", $"ClosePosition(API shared) done tabId={TabId}, symbol={row.Symbol}, useLimit={useLimitPrice}, result={result}");
        }
        catch (Exception ex)
        {
            RemovePendingOrder(localOrderId);
            _toastService.ShowError($"平倉失敗：{ex.Message}");
            _ = RefreshAccountStateOnceAsync();
            _logger.Error("WorkspaceTab", $"ClosePosition(API shared) exception tabId={TabId}, symbol={row.Symbol}", ex);
        }
    }

    private async Task CancelPendingOrderAsync(PendingOrderPanelRow? row)
    {
        if (_isApiSessionManaged && _tradingApiService is not null && _apiSessionAccountId.HasValue)
        {
            await CancelPendingOrderViaApiSessionAsync(row, _apiSessionAccountId.Value);
            return;
        }

        if (row is null || _venue is null || !IsConfigured)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(row.VenueOrderId))
        {
            if (!string.IsNullOrWhiteSpace(row.LocalOrderId))
            {
                _logger.Info("WorkspaceTab", $"CancelPending local-only remove tabId={TabId}, symbol={row.Symbol}, localId={row.LocalOrderId}");
                RemovePendingOrder(row.LocalOrderId);
                _toastService.ShowInfo("本地待同步訂單已移除");
            }
            else
            {
                _toastService.ShowWarning("此訂單目前無可取消識別碼");
            }

            return;
        }

        try
        {
            _logger.Info("WorkspaceTab", $"CancelPending start tabId={TabId}, symbol={row.Symbol}, orderId={row.VenueOrderId}");
            var ack = await _venue.CancelOrderAsync(row.Symbol, row.VenueOrderId, _cts.Token);
            if (ack.Success)
            {
                SuppressCanceledOrderId(row.VenueOrderId);
                _remotePendingOrders = _remotePendingOrders
                    .Where(x => !string.Equals(x.VenueOrderId, row.VenueOrderId, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                RebuildPendingOrderRows();

                if (!string.IsNullOrWhiteSpace(row.LocalOrderId))
                {
                    RemovePendingOrder(row.LocalOrderId);
                }

                _toastService.ShowInfo("訂單取消成功");
                _ = RefreshAccountStateOnceAsync();
            }
            else
            {
                if ((ack.Message ?? string.Empty).Contains("invalid", StringComparison.OrdinalIgnoreCase))
                {
                    SuppressCanceledOrderId(row.VenueOrderId);
                    _remotePendingOrders = _remotePendingOrders
                        .Where(x => !string.Equals(x.VenueOrderId, row.VenueOrderId, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    RebuildPendingOrderRows();
                    _ = RefreshAccountStateOnceAsync();
                }

                _toastService.ShowError($"取消失敗：{ack.Message ?? "unknown"}");
            }

            _logger.Info("WorkspaceTab", $"CancelPending done tabId={TabId}, symbol={row.Symbol}, orderId={row.VenueOrderId}, success={ack.Success}, msg={ack.Message}");
        }
        catch (Exception ex)
        {
            _toastService.ShowError($"取消例外：{ex.Message}");
            _logger.Error("WorkspaceTab", $"CancelPending exception tabId={TabId}, symbol={row.Symbol}, orderId={row.VenueOrderId}", ex);
        }
    }

    private async Task CancelPendingOrderViaApiSessionAsync(PendingOrderPanelRow? row, Guid accountId)
    {
        if (row is null || !IsConfigured)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(row.VenueOrderId))
        {
            if (!string.IsNullOrWhiteSpace(row.LocalOrderId))
            {
                _logger.Info("WorkspaceTab", $"CancelPending(API shared) local remove tabId={TabId}, symbol={row.Symbol}, localId={row.LocalOrderId}");
                RemovePendingOrder(row.LocalOrderId);
                _toastService.ShowInfo("本地待同步訂單已移除");
            }
            else
            {
                _toastService.ShowWarning("此訂單目前無可取消識別碼");
            }

            return;
        }

        try
        {
            await _tradingApiService!.CancelOrderAsync(new ApiCancelOrderRequest(accountId, row.Symbol, row.VenueOrderId), _cts.Token);
            SuppressCanceledOrderId(row.VenueOrderId);
            _remotePendingOrders = _remotePendingOrders
                .Where(x => !string.Equals(x.VenueOrderId, row.VenueOrderId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            RebuildPendingOrderRows();

            if (!string.IsNullOrWhiteSpace(row.LocalOrderId))
            {
                RemovePendingOrder(row.LocalOrderId);
            }

            _toastService.ShowInfo("訂單取消成功");
            _ = RefreshAccountStateOnceAsync();
        }
        catch (Exception ex)
        {
            if ((ex.Message ?? string.Empty).Contains("invalid", StringComparison.OrdinalIgnoreCase))
            {
                SuppressCanceledOrderId(row.VenueOrderId);
                _remotePendingOrders = _remotePendingOrders
                    .Where(x => !string.Equals(x.VenueOrderId, row.VenueOrderId, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                RebuildPendingOrderRows();
                _ = RefreshAccountStateOnceAsync();
            }

            _toastService.ShowError($"取消失敗：{ex.Message}");
            _logger.Error("WorkspaceTab", $"CancelPending(API shared) exception tabId={TabId}, symbol={row.Symbol}, orderId={row.VenueOrderId}", ex);
        }
    }

    private void UpdateCandleSeriesFromCache()
    {
        if (Binding is null)
        {
            CandleSeries = Array.Empty<CandleViewPoint>();
            return;
        }

        IReadOnlyList<CandleViewPoint> points;
        lock (_candleLock)
        {
            var interval = ParseInterval(SelectedInterval);
            points = _candleCache.Get(Binding.VenueId, Symbol, interval)
                .TakeLast(600)
                .Select(x => new CandleViewPoint(x.OpenTime, x.Open, x.High, x.Low, x.Close))
                .ToList();
        }

        CandleSeries = points;
    }

    public void Dispose()
    {
        _ = DisposeAsync();
    }

    public async Task DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) == 1)
        {
            return;
        }

        _logger.Info("WorkspaceTab", $"Dispose start tabId={TabId}");
        L.PropertyChanged -= OnLocalizationChanged;
        _cts.Cancel();
        await StopMarketPumpAsync();
        await StopAccountStatePumpAsync();
        await StopApiSessionPumpAsync();
        _candlePersistChannel.Writer.TryComplete();

        if (_candlePersistTask is not null)
        {
            try
            {
                await _candlePersistTask.WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (Exception ex)
            {
                _logger.Warn("WorkspaceTab", $"Dispose candle persist wait warning tabId={TabId}: {ex.Message}");
            }
        }

        if (_venue is not null)
        {
            try
            {
                await _venue.DisconnectMarketDataAsync(CancellationToken.None);
                await _venue.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.Warn("WorkspaceTab", $"Dispose venue warning tabId={TabId}: {ex.Message}");
            }
        }

        _cts.Dispose();
        _settingsReloadGate.Dispose();
        _isApiSessionManaged = false;
        _apiSessionAccountId = null;
        _apiSessionCursor = null;
        RaisePropertyChanged(nameof(CanEditMarketSessionSettings));
        ConnectionStatus = "Disposed";
        _logger.Info("WorkspaceTab", $"Dispose done tabId={TabId}");
    }

    private void StartMarketPump()
    {
        _ = StopApiSessionPumpAsync();
        _marketPumpCts?.Cancel();
        _marketPumpCts?.Dispose();
        _marketPumpCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _marketPumpTask = PumpMarketEventsAsync(_marketPumpCts.Token);
        _logger.Info("WorkspaceTab", $"Market pump started tabId={TabId}, symbol={_marketStreamSymbol}");
    }

    private void StartAccountStatePump()
    {
        _ = StopApiSessionPumpAsync();
        _accountStatePumpCts?.Cancel();
        _accountStatePumpCts?.Dispose();
        _accountStatePumpTask = null;

        if (_venue is not IAccountStateProvider provider)
        {
            _logger.Info("WorkspaceTab", $"Account state provider unavailable tabId={TabId}, venue={_venue?.VenueId}");
            Dispatcher.UIThread.Post(() =>
                ApplyAccountSnapshot(new VenueAccountSnapshot(DateTimeOffset.UtcNow, [], [], [])));
            return;
        }

        _accountStatePumpCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _accountStatePumpTask = PumpAccountStateAsync(provider, _accountStatePumpCts.Token);
        _logger.Info("WorkspaceTab", $"Account state pump started tabId={TabId}, venue={_venue?.VenueId}");
    }

    private void StartApiSessionPump()
    {
        _apiSessionPumpCts?.Cancel();
        _apiSessionPumpCts?.Dispose();
        _apiSessionPumpCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _apiSessionPumpTask = PumpApiSessionAsync(_apiSessionPumpCts.Token);
        _logger.Info("WorkspaceTab", $"API session pump started tabId={TabId}, symbol={Symbol}");
    }

    private async Task StopMarketPumpAsync()
    {
        if (_marketPumpCts is null)
        {
            return;
        }

        _marketPumpCts.Cancel();
        if (_marketPumpTask is not null)
        {
            try
            {
                await _marketPumpTask.WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (Exception ex)
            {
                _logger.Warn("WorkspaceTab", $"Stop market pump warning tabId={TabId}: {ex.Message}");
            }
        }

        _marketPumpCts.Dispose();
        _marketPumpCts = null;
        _marketPumpTask = null;
        _logger.Info("WorkspaceTab", $"Market pump stopped tabId={TabId}");
    }

    private async Task StopAccountStatePumpAsync()
    {
        if (_accountStatePumpCts is null)
        {
            return;
        }

        _accountStatePumpCts.Cancel();
        if (_accountStatePumpTask is not null)
        {
            try
            {
                await _accountStatePumpTask.WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (Exception ex)
            {
                _logger.Warn("WorkspaceTab", $"Stop account state pump warning tabId={TabId}: {ex.Message}");
            }
        }

        _accountStatePumpCts.Dispose();
        _accountStatePumpCts = null;
        _accountStatePumpTask = null;
        _logger.Info("WorkspaceTab", $"Account state pump stopped tabId={TabId}");
    }

    private async Task StopApiSessionPumpAsync()
    {
        if (_apiSessionPumpCts is null)
        {
            return;
        }

        _apiSessionPumpCts.Cancel();
        if (_apiSessionPumpTask is not null)
        {
            try
            {
                await _apiSessionPumpTask.WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (Exception ex)
            {
                _logger.Warn("WorkspaceTab", $"Stop API session pump warning tabId={TabId}: {ex.Message}");
            }
        }

        _apiSessionPumpCts.Dispose();
        _apiSessionPumpCts = null;
        _apiSessionPumpTask = null;
        _logger.Info("WorkspaceTab", $"API session pump stopped tabId={TabId}");
    }

    private async Task PumpAccountStateAsync(IAccountStateProvider provider, CancellationToken cancellationToken)
    {
        var loop = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var snapshot = await provider.GetAccountSnapshotAsync(cancellationToken);
                Dispatcher.UIThread.Post(() => ApplyAccountSnapshot(snapshot));
                if (loop % 10 == 0)
                {
                    _logger.Info("WorkspaceTab", $"Account snapshot tabId={TabId}, positions={snapshot.Positions.Count}, orders={snapshot.OpenOrders.Count}, balances={snapshot.Balances.Count}");
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Error("WorkspaceTab", $"Account snapshot loop failed tabId={TabId}", ex);
            }

            loop++;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(4), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RefreshAccountStateOnceAsync()
    {
        if (_isApiSessionManaged && _tradingApiService is not null && _apiSessionAccountId.HasValue)
        {
            try
            {
                var snapshot = await FetchApiSnapshotAsync(_apiSessionAccountId.Value, Symbol, _cts.Token);
                Dispatcher.UIThread.Post(() => ApplyAccountSnapshot(snapshot));
                _logger.Info("WorkspaceTab", $"Account snapshot on-demand (API shared) tabId={TabId}, positions={snapshot.Positions.Count}, orders={snapshot.OpenOrders.Count}, balances={snapshot.Balances.Count}");
            }
            catch (Exception ex)
            {
                _logger.Warn("WorkspaceTab", $"Account snapshot on-demand (API shared) failed tabId={TabId}: {ex.Message}");
            }

            return;
        }

        if (_venue is not IAccountStateProvider provider)
        {
            return;
        }

        try
        {
            var snapshot = await provider.GetAccountSnapshotAsync(_cts.Token);
            Dispatcher.UIThread.Post(() => ApplyAccountSnapshot(snapshot));
            _logger.Info("WorkspaceTab", $"Account snapshot on-demand tabId={TabId}, positions={snapshot.Positions.Count}, orders={snapshot.OpenOrders.Count}, balances={snapshot.Balances.Count}");
        }
        catch (Exception ex)
        {
            _logger.Warn("WorkspaceTab", $"Account snapshot on-demand failed tabId={TabId}: {ex.Message}");
        }
    }

    private async Task PersistCandlesLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var candle in _candlePersistChannel.Reader.ReadAllAsync(cancellationToken))
            {
                _candleRepository.Upsert(candle);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.Info("WorkspaceTab", $"Candle persist canceled tabId={TabId}");
        }
        catch (Exception ex)
        {
            _logger.Error("WorkspaceTab", $"Candle persist failed tabId={TabId}", ex);
        }
    }

    private bool ShouldLoadFromStorage(string venueId, string symbol, CandleInterval interval)
    {
        return !_storageLoadAttempted.Contains(BuildStorageKey(venueId, symbol, interval));
    }

    private void MarkStorageLoaded(string venueId, string symbol, CandleInterval interval)
    {
        _storageLoadAttempted.Add(BuildStorageKey(venueId, symbol, interval));
    }

    private static string BuildStorageKey(string venueId, string symbol, CandleInterval interval)
    {
        return $"{venueId}|{symbol}|{interval}";
    }

    private async Task ReconnectForSymbolChangeAsync(string targetSymbol, CancellationToken cancellationToken)
    {
        if (SelectedAccount is null || _venue is null)
        {
            return;
        }

        _logger.Info("WorkspaceTab", $"Symbol reconnect start tabId={TabId}, target={targetSymbol}");
        await StopMarketPumpAsync();
        await StopAccountStatePumpAsync();

        var oldVenue = _venue;
        var previousStreamSymbol = _marketStreamSymbol;
        var credentials = _accountStore.GetCredentials(SelectedAccount.AccountId);
        var newVenue = _venueFactory.Create(SelectedAccount, credentials);
        try
        {
            await newVenue.ConnectMarketDataAsync([targetSymbol], cancellationToken);
            _venue = newVenue;
            _marketStreamSymbol = targetSymbol;
            StartMarketPump();
            StartAccountStatePump();

            await oldVenue.DisconnectMarketDataAsync(cancellationToken);
            await oldVenue.DisposeAsync();
            _logger.Info("WorkspaceTab", $"Symbol reconnect done tabId={TabId}, target={targetSymbol}");
        }
        catch
        {
            try
            {
                await newVenue.DisconnectMarketDataAsync(CancellationToken.None);
                await newVenue.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.Warn("WorkspaceTab", $"Symbol reconnect cleanup warning tabId={TabId}: {ex.Message}");
            }

            _venue = oldVenue;
            _marketStreamSymbol = previousStreamSymbol;
            StartMarketPump();
            StartAccountStatePump();
            throw;
        }
    }

    private async Task PumpApiSessionAsync(CancellationToken cancellationToken)
    {
        if (!_isApiSessionManaged || _tradingApiService is null || !_apiSessionAccountId.HasValue || Binding is null)
        {
            return;
        }

        var accountId = _apiSessionAccountId.Value;
        var symbol = Symbol;
        var interval = SelectedInterval;
        var loop = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var market = await _tradingApiService.GetMarketDataAsync(accountId, symbol, interval, _apiSessionCursor, cancellationToken);
                ApplyApiMarketData(Binding.VenueId, symbol, interval, market);

                if (loop % 4 == 0)
                {
                    var snapshot = await FetchApiSnapshotAsync(accountId, symbol, cancellationToken);
                    Dispatcher.UIThread.Post(() => ApplyAccountSnapshot(snapshot));
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Error("WorkspaceTab", $"API session pump failed tabId={TabId}, symbol={symbol}", ex);
                Dispatcher.UIThread.Post(() => ConnectionStatus = $"Error: {ex.Message}");
            }

            loop++;
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(700), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void ApplyApiMarketData(string venueId, string symbol, string intervalText, ApiMarketDataResponse market)
    {
        var interval = ParseInterval(intervalText);
        var incoming = market.InitialCandles.Count > 0 ? market.InitialCandles : market.DeltaCandles;

        Candle? latest = null;
        lock (_candleLock)
        {
            foreach (var c in incoming)
            {
                var candle = new Candle(
                    venueId,
                    symbol,
                    interval,
                    DateTimeOffset.FromUnixTimeMilliseconds(c.OpenTimeMs),
                    c.Open,
                    c.High,
                    c.Low,
                    c.Close,
                    c.Volume,
                    c.IsClosed);
                latest = candle;
                _candleCache.Upsert(candle);
                _candleRepository.Upsert(candle);
            }

            if (latest is not null)
            {
                _currentCandle = latest;
            }
        }

        _apiSessionCursor = market.Cursor;
        Dispatcher.UIThread.Post(() =>
        {
            LastMarketEventAt = DateTimeOffset.UtcNow;
            ConnectionStatus = $"Connected ({Binding?.VenueId} shared)";
            if (market.LatestPrice.HasValue && market.LatestPrice.Value > 0)
            {
                UpdateOrderBookSnapshot(market.LatestPrice.Value);
                UpdatePositionMarks(symbol, market.LatestPrice.Value);
            }

            CandleStatus = _currentCandle is null ? "尚無 K 線資料" : FormatCandleStatus(_currentCandle);
            UpdateCandleSeriesFromCache();
        });
    }

    private async Task<VenueAccountSnapshot> FetchApiSnapshotAsync(Guid accountId, string symbol, CancellationToken cancellationToken)
    {
        if (_tradingApiService is null)
        {
            return new VenueAccountSnapshot(DateTimeOffset.UtcNow, [], [], []);
        }

        var positions = await _tradingApiService.ListPositionsAsync(accountId, symbol, cancellationToken);
        var orders = await _tradingApiService.ListOpenOrdersAsync(accountId, symbol, cancellationToken);
        var balances = await _tradingApiService.ListBalancesAsync(accountId, null, cancellationToken);

        return new VenueAccountSnapshot(
            DateTimeOffset.UtcNow,
            positions.Select(x => new VenuePosition(
                x.Symbol,
                x.Quantity,
                x.NotionalUsd,
                x.Leverage,
                x.EntryPrice,
                x.MarkPrice,
                x.UnrealizedPnlPct,
                x.UnrealizedPnlUsd,
                x.RealizedPnlUsd)).ToList(),
            orders.Select(x => new VenueOpenOrder(
                x.Symbol,
                x.NotionalUsd,
                x.Leverage,
                x.LimitPrice,
                x.Status,
                x.OrderId)).ToList(),
            balances.Select(x => new VenueBalance(
                x.Asset,
                x.Quantity,
                x.UsdValue)).ToList());
    }
}

internal static class NumberText
{
    public static string Trim(decimal value, int maxDecimals = 8, bool useGrouping = false)
    {
        var decimals = Math.Max(0, maxDecimals);
        var rounded = decimal.Round(value, decimals, MidpointRounding.AwayFromZero);
        string format;
        if (decimals == 0)
        {
            format = useGrouping ? "#,0" : "0";
        }
        else
        {
            var tail = new string('#', decimals);
            format = useGrouping ? $"#,0.{tail}" : $"0.{tail}";
        }

        var text = rounded.ToString(format, CultureInfo.InvariantCulture);
        return text == "-0" ? "0" : text;
    }

    public static string Signed(decimal value, int maxDecimals = 8)
    {
        if (value > 0)
        {
            return "+" + Trim(value, maxDecimals);
        }

        if (value < 0)
        {
            return "-" + Trim(Math.Abs(value), maxDecimals);
        }

        return "0";
    }
}

public sealed record OrderBookLevelRow(decimal Price, decimal Size, decimal Total, bool IsAsk)
{
    public string PriceText => NumberText.Trim(Price);
    public string SizeText => NumberText.Trim(Size, useGrouping: true);
    public string TotalText => NumberText.Trim(Total, useGrouping: true);
    public string PriceHex => IsAsk ? "#E47A8E" : "#5ED0A9";
    public string TotalHex => IsAsk ? "#CB6078" : "#48B991";
    public string RowBackgroundHex => IsAsk ? "#25151D" : "#13261F";
}

public sealed record RecentTradeRow(DateTimeOffset TradeTime, decimal Price, decimal Size, string Side, string PriceHex, string SideHex)
{
    public string TimeText => TradeTime.ToString("HH:mm:ss");
    public string PriceText => NumberText.Trim(Price);
    public string SizeText => NumberText.Trim(Size, useGrouping: true);
}

public sealed class PositionPanelRow : ViewModelBase
{
    private string _contractAmount = string.Empty;
    private string _leverage = string.Empty;
    private decimal _entryPrice;
    private decimal _markPrice;
    private decimal _unrealizedPnlPct;
    private decimal _unrealizedPnlUsd;
    private decimal _realizedPnlUsd;
    private string _closePrice;

    public PositionPanelRow(string symbol, string closePrice)
    {
        Symbol = symbol;
        _closePrice = closePrice;
    }

    public string Symbol { get; }
    public string ContractAmount
    {
        get => _contractAmount;
        private set => SetProperty(ref _contractAmount, value);
    }

    public string Leverage
    {
        get => _leverage;
        private set => SetProperty(ref _leverage, value);
    }

    public decimal EntryPrice
    {
        get => _entryPrice;
        private set
        {
            if (SetProperty(ref _entryPrice, value))
            {
                RaisePropertyChanged(nameof(EntryPriceText));
            }
        }
    }

    public decimal MarkPrice
    {
        get => _markPrice;
        private set
        {
            if (SetProperty(ref _markPrice, value))
            {
                RaisePropertyChanged(nameof(MarkPriceText));
            }
        }
    }

    public decimal UnrealizedPnlPct
    {
        get => _unrealizedPnlPct;
        private set
        {
            if (SetProperty(ref _unrealizedPnlPct, value))
            {
                RaisePropertyChanged(nameof(UnrealizedPnlPctText));
                RaisePropertyChanged(nameof(UnrealizedPnlHex));
            }
        }
    }

    public decimal UnrealizedPnlUsd
    {
        get => _unrealizedPnlUsd;
        private set
        {
            if (SetProperty(ref _unrealizedPnlUsd, value))
            {
                RaisePropertyChanged(nameof(UnrealizedPnlUsdText));
                RaisePropertyChanged(nameof(UnrealizedPnlUsdHex));
            }
        }
    }

    public decimal RealizedPnlUsd
    {
        get => _realizedPnlUsd;
        private set
        {
            if (SetProperty(ref _realizedPnlUsd, value))
            {
                RaisePropertyChanged(nameof(RealizedPnlUsdText));
                RaisePropertyChanged(nameof(RealizedPnlHex));
            }
        }
    }

    public string ClosePrice
    {
        get => _closePrice;
        set => SetProperty(ref _closePrice, value);
    }

    public string EntryPriceText => NumberText.Trim(EntryPrice);
    public string MarkPriceText => NumberText.Trim(MarkPrice);
    public string UnrealizedPnlPctText => $"{NumberText.Signed(UnrealizedPnlPct, 2)}%";
    public string UnrealizedPnlUsdText => NumberText.Signed(UnrealizedPnlUsd, 4);
    public string RealizedPnlUsdText => NumberText.Signed(RealizedPnlUsd);
    public string UnrealizedPnlHex => UnrealizedPnlPct >= 0 ? "#5ED0A9" : "#E47A8E";
    public string UnrealizedPnlUsdHex => UnrealizedPnlUsd >= 0 ? "#5ED0A9" : "#E47A8E";
    public string RealizedPnlHex => RealizedPnlUsd >= 0 ? "#5ED0A9" : "#E47A8E";

    public void ApplyState(
        string contractAmount,
        string leverage,
        decimal entryPrice,
        decimal markPrice,
        decimal unrealizedPnlPct,
        decimal unrealizedPnlUsd,
        decimal realizedPnlUsd)
    {
        ContractAmount = contractAmount;
        Leverage = leverage;
        EntryPrice = entryPrice;
        MarkPrice = markPrice;
        UnrealizedPnlPct = unrealizedPnlPct;
        UnrealizedPnlUsd = unrealizedPnlUsd;
        RealizedPnlUsd = realizedPnlUsd;
    }
}

public sealed record PendingOrderPanelRow(
    string Symbol,
    string ContractAmount,
    string Leverage,
    string LimitPrice,
    string Status,
    string? VenueOrderId,
    string? LocalOrderId,
    bool IsExchangeOrder)
{
    public string VenueOrderIdDisplay => string.IsNullOrWhiteSpace(VenueOrderId) ? "-" : VenueOrderId!;
}

public sealed record BalancePanelRow(string Coin, decimal Quantity, decimal UsdValue)
{
    public string QuantityText => NumberText.Trim(decimal.Round(Quantity, 5, MidpointRounding.AwayFromZero), 5, useGrouping: true);
    public string UsdText => NumberText.Trim(decimal.Round(UsdValue, 5, MidpointRounding.AwayFromZero), 5, useGrouping: true);
}

internal sealed record PositionState(
    string Symbol,
    string Side,
    decimal NotionalUsd,
    decimal Leverage,
    decimal EntryPrice,
    decimal MarkPrice,
    decimal UnrealizedPnlUsd,
    decimal RealizedPnlUsd,
    decimal Quantity);

internal sealed record PendingOrderState(
    string LocalId,
    string Symbol,
    decimal NotionalUsd,
    decimal Leverage,
    decimal? LimitPrice,
    string Status,
    string? VenueOrderId)
{
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
}
