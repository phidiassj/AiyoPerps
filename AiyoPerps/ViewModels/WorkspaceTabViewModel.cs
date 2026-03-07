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

public sealed partial class WorkspaceTabViewModel : ViewModelBase, IDisposable
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
    private SymbolOptionItem? _selectedSymbolOption;
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
    private readonly Dictionary<string, decimal> _pendingOrderLeverageHints = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _positionClosePriceInputs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _suppressedCanceledOrderIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _closingSymbolsInFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _closeSubmitLock = new();

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
        SymbolOptions = new ObservableCollection<SymbolOptionItem>();

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
    public ObservableCollection<SymbolOptionItem> SymbolOptions { get; }

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

                if (!string.Equals(_selectedSymbolOption?.Value, normalized, StringComparison.Ordinal))
                {
                    _selectedSymbolOption = SymbolOptions.FirstOrDefault(x => string.Equals(x.Value, normalized, StringComparison.OrdinalIgnoreCase))
                        ?? new SymbolOptionItem(normalized, SymbolDisplayText.Format(normalized));
                    RaisePropertyChanged(nameof(SelectedSymbolOption));
                }

                if (Binding is not null)
                {
                    Binding = Binding with { Symbol = normalized };
                    Header = $"{Binding.VenueId}:{SymbolDisplayText.Format(normalized)}";
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

    public SymbolOptionItem? SelectedSymbolOption
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

            var normalized = value is null
                ? null
                : SymbolOptions.FirstOrDefault(x => string.Equals(x.Value, value.Value, StringComparison.OrdinalIgnoreCase)) ?? value;

            if (SetProperty(ref _selectedSymbolOption, normalized))
            {
                if (!string.IsNullOrWhiteSpace(normalized?.Value) &&
                    !string.Equals(Symbol, normalized.Value, StringComparison.Ordinal))
                {
                    Symbol = normalized.Value;
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
                RaisePropertyChanged(nameof(IsMarketOrderSelected));
                RaisePropertyChanged(nameof(IsLimitOrderSelected));
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
                RaisePropertyChanged(nameof(IsLongOrderSelected));
                RaisePropertyChanged(nameof(IsShortOrderSelected));
                RecalculateOrderEstimates();
            }
        }
    }

    public bool IsMarketOrderSelected
    {
        get => !_isLimitOrderType;
        set
        {
            if (value && OrderTypes.Count > 0)
            {
                OrderType = OrderTypes[0];
            }
        }
    }

    public bool IsLimitOrderSelected
    {
        get => _isLimitOrderType;
        set
        {
            if (value && OrderTypes.Count > 1)
            {
                OrderType = OrderTypes[1];
            }
        }
    }

    public bool IsLongOrderSelected
    {
        get => !_isShortOrderSide;
        set
        {
            if (value && OrderSides.Count > 0)
            {
                OrderSide = OrderSides[0];
            }
        }
    }

    public bool IsShortOrderSelected
    {
        get => _isShortOrderSide;
        set
        {
            if (value && OrderSides.Count > 1)
            {
                OrderSide = OrderSides[1];
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

    public string LastMarketEventDisplay => LastMarketEventAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture) ?? "-";

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
            _selectedSymbolOption = SymbolOptions.FirstOrDefault(x => string.Equals(x.Value, connection.Symbol, StringComparison.OrdinalIgnoreCase))
                ?? new SymbolOptionItem(connection.Symbol, SymbolDisplayText.Format(connection.Symbol));
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
        Header = $"{account.VenueId}:{SymbolDisplayText.Format(connection.Symbol)}";
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
        _toastService.ShowWarning(L["Toast_ApiSessionLockedChangeViaApi"]);
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
            RaisePropertyChanged(nameof(IsMarketOrderSelected));
            RaisePropertyChanged(nameof(IsLimitOrderSelected));
        }

        var nextOrderSide = _isShortOrderSide ? orderSides[1] : orderSides[0];
        if (!string.Equals(_orderSide, nextOrderSide, StringComparison.Ordinal))
        {
            _orderSide = nextOrderSide;
            RaisePropertyChanged(nameof(OrderSide));
            RaisePropertyChanged(nameof(IsLongOrderSelected));
            RaisePropertyChanged(nameof(IsShortOrderSelected));
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
                SymbolOptions.Add(new SymbolOptionItem(sym, SymbolDisplayText.Format(sym)));
            }

            var preferred = ResolvePreferredSymbol(account, currentSymbol, symbols);
            if (autoSelectSymbol || !symbols.Any(x => string.Equals(x, currentSymbol, StringComparison.OrdinalIgnoreCase)))
            {
                if (!string.Equals(preferred, currentSymbol, StringComparison.Ordinal))
                {
                    Symbol = preferred;
                }
            }

            var selected = SymbolOptions.FirstOrDefault(x => string.Equals(x.Value, Symbol, StringComparison.OrdinalIgnoreCase))
                ?? SymbolOptions.FirstOrDefault(x => string.Equals(x.Value, preferred, StringComparison.OrdinalIgnoreCase))
                ?? (string.IsNullOrWhiteSpace(preferred) ? null : new SymbolOptionItem(preferred, SymbolDisplayText.Format(preferred)));
            _selectedSymbolOption = selected;
            RaisePropertyChanged(nameof(SelectedSymbolOption));
            RaisePropertyChanged(nameof(Symbol));
            _logger.Info("WorkspaceTab", $"Symbol options loaded venue={account.VenueId}, env={account.Environment}, count={symbols.Count}, selected={selected?.Value}");
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

        if (string.Equals(account.VenueId, "Aster", StringComparison.OrdinalIgnoreCase))
        {
            var asterDefault = symbols.FirstOrDefault(x => string.Equals(x, "BTCUSDT", StringComparison.OrdinalIgnoreCase));
            return asterDefault ?? symbols.FirstOrDefault() ?? "BTCUSDT";
        }

        if (string.Equals(account.VenueId, "GRVT", StringComparison.OrdinalIgnoreCase))
        {
            var grvtDefault = symbols.FirstOrDefault(x => string.Equals(x, "BTC_USDT_Perp", StringComparison.OrdinalIgnoreCase));
            return grvtDefault ?? symbols.FirstOrDefault() ?? "BTC_USDT_Perp";
        }

        return symbols.FirstOrDefault() ?? currentSymbol;
    }

    private async Task ConfirmActivationAsync()
    {
        if (_isApiSessionManaged)
        {
            _toastService.ShowWarning(L["Toast_ApiSessionLocked"]);
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
            Header = $"{SelectedAccount.VenueId}:{SymbolDisplayText.Format(Symbol)}";
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
            _toastService.ShowInfo($"{L["Toast_Connected"]}{_venue.VenueId} {SymbolDisplayText.Format(Symbol)}");
            _marketStreamSymbol = Symbol;
            StartMarketPump();
            StartAccountStatePump();
            _logger.Info("WorkspaceTab", $"Activation completed tabId={TabId}");
        }
        catch (OperationCanceledException ex)
        {
            ConnectionStatus = "Connection timeout";
            IsConfigured = false;
            _toastService.ShowError(L["Toast_ConnectTimeout"]);
            _logger.Error("WorkspaceTab", $"Activation timeout tabId={TabId}", ex);
        }
        catch (Exception ex)
        {
            ConnectionStatus = $"Connection error: {ex.Message}";
            IsConfigured = false;
            _toastService.ShowError($"{L["Toast_ConnectFailed"]}{ex.Message}");
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
                LastOrderResult = $"下單失敗：{configure.Message}";
                _toastService.ShowError(configure.Message);
                _logger.Warn("WorkspaceTab", $"ConfigureLeverage failed tabId={TabId}, symbol={Symbol}, leverage={leverage}, msg={configure.Message}");
                return;
            }

            _logger.Info("WorkspaceTab", $"ConfigureLeverage done tabId={TabId}, symbol={Symbol}, leverage={leverage}");
        }
        catch (Exception ex)
        {
            LastOrderResult = $"下單失敗：{ex.Message}";
            _toastService.ShowError(ex.Message);
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
                    RememberPendingOrderLeverageHint(Symbol, leverage, price, notionalUsd, ack.ClientOrderId);
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

                _toastService.ShowInfo(L["Toast_OrderSuccess"]);
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
                _toastService.ShowError($"{L["Toast_OrderFailed"]}{ack.Message}");
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
            _toastService.ShowError($"{L["Toast_OrderException"]}{ex.Message}");
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
            _toastService.ShowInfo(L["Toast_OrderSuccess"]);
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
            _toastService.ShowError($"{L["Toast_OrderFailed"]}{ex.Message}");
            _ = RefreshAccountStateOnceAsync();
            _logger.Error("WorkspaceTab", $"PlaceOrder(API shared) exception tabId={TabId}", ex);
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
    public string DisplaySymbol => SymbolDisplayText.Format(Symbol);
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
    public string DisplaySymbol => SymbolDisplayText.Format(Symbol);
    public string VenueOrderIdDisplay => string.IsNullOrWhiteSpace(VenueOrderId) ? "-" : VenueOrderId!;
}

public sealed record BalancePanelRow(string Coin, decimal Quantity, decimal UsdValue)
{
    public string QuantityText => NumberText.Trim(decimal.Round(Quantity, 5, MidpointRounding.AwayFromZero), 5, useGrouping: true);
    public string UsdText => NumberText.Trim(decimal.Round(UsdValue, 5, MidpointRounding.AwayFromZero), 5, useGrouping: true);
}

public sealed record SymbolOptionItem(string Value, string Display)
{
    public override string ToString() => Display;
}

public static class SymbolDisplayText
{
    public static string Format(string symbol)
    {
        var normalized = (symbol ?? string.Empty).Trim().ToUpperInvariant();

        var parts = normalized.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 3 && string.Equals(parts[2], "PERP", StringComparison.Ordinal))
        {
            var baseAsset = parts[0];
            var quoteAsset = parts[1];
            if (quoteAsset is "USDT" or "USDC")
            {
                return baseAsset + "USDT";
            }

            return baseAsset + quoteAsset;
        }

        return normalized;
    }
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
