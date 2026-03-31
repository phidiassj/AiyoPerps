using AiyoPerps.Models;
using AiyoPerps.Core;
using AiyoPerps.Services;
using AiyoPerps.Services.Api;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;

namespace AiyoPerps.ViewModels;

public sealed class DashboardTabViewModel : ViewModelBase, IMainTabViewModel, IDisposable
{
    private static readonly string[] DefaultIntervals = ["5m", "10m", "15m", "30m", "1h", "2h", "4h", "6h", "12h", "1d", "7d", "30d"];

    private readonly ObservableCollection<AccountProfile> _accounts;
    private readonly ToastService _toastService;
    private readonly DashboardService _dashboardService;
    private readonly AIAgentExecutionService _aiAgentExecutionService;

    private DashboardMarketRow? _selectedMarketRow;
    private DashboardSymbolOptionDto? _selectedSymbolOption;
    private string _selectedInterval = "5m";
    private bool _showTestnet;
    private bool _isDashboardRunning;
    private bool _isDashboardBusy;
    private bool _isLongOrder = true;
    private bool _isLimitOrder;
    private string _selectedMarginMode = "Cross";
    private IReadOnlyList<string> _marginModeOptions = ["Cross", "Isolated"];
    private double _leverage = 5;
    private string _orderAmount = "1000";
    private string _orderPrice = string.Empty;
    private string _estimatedMargin = "-";
    private string _estimatedLiquidationPrice = "-";
    private bool _isMarketPanelVisible = true;
    private string _leftPanelWidth = "2*";
    private string _rightPanelWidth = "3*";
    private bool _isOrderConfirmationVisible;
    private string _orderConfirmationText = string.Empty;
    private ApiOpenPositionRequest? _pendingOrderRequest;
    private bool _isDisposed;
    private bool _isApplyingSnapshot;
    private bool _isAgentRunBusy;
    private string _agentEnableState = "-";
    private string _agentSelectedType = "-";
    private string _agentWakeInterval = "-";
    private string _agentLastRunStatus = "-";
    private string _agentLastRunTime = "-";
    private AIAgentRunSummaryItem? _selectedAIAgentRun;
    private AIAgentRunDetailViewModel? _selectedAIAgentRunDetail;
    private bool _isOptionsExpanded = true;
    private bool _isAccountsExpanded;

    public DashboardTabViewModel(
        ObservableCollection<AccountProfile> accounts,
        SymbolCatalogRepository symbolCatalogRepository,
        ToastService toastService,
        DashboardService dashboardService,
        AIAgentExecutionService aiAgentExecutionService)
    {
        _accounts = accounts;
        _toastService = toastService;
        _dashboardService = dashboardService;
        _aiAgentExecutionService = aiAgentExecutionService;

        Accounts = [];
        SymbolOptions = [];
        MarketRows = [];
        PositionRows = [];
        PendingOrderRows = [];
        AIAgentRuns = [];

        ToggleDashboardCommand = new RelayCommand(_ => _ = ToggleDashboardAsync(), _ => CanToggleDashboard);
        SubmitOrderCommand = new RelayCommand(_ => BeginSubmitOrder(), _ => IsDashboardRunning && SelectedMarketRow is not null);
        ConfirmOrderCommand = new RelayCommand(_ => _ = ConfirmOrderAsync(), _ => _pendingOrderRequest is not null);
        CancelOrderConfirmationCommand = new RelayCommand(_ => HideOrderConfirmation());
        RunAgentNowCommand = new RelayCommand(_ => _ = RunAgentNowAsync(), _ => CanRunAgentNow);

        _accounts.CollectionChanged += OnAccountsCollectionChanged;
        _dashboardService.SnapshotChanged += OnDashboardSnapshotChanged;
        _aiAgentExecutionService.StateChanged += OnAIAgentStateChanged;

        ApplyDashboardSnapshot(_dashboardService.GetSnapshot());
        RefreshAgentState();
    }

    public string Header => L["Dashboard_Title"];

    public bool IsClosable => false;

    public ObservableCollection<DashboardAccountSelectionItem> Accounts { get; }

    public ObservableCollection<DashboardSymbolOptionDto> SymbolOptions { get; }

    public ObservableCollection<DashboardMarketRow> MarketRows { get; }

    public ObservableCollection<DashboardPositionRow> PositionRows { get; }

    public ObservableCollection<DashboardPendingOrderRow> PendingOrderRows { get; }

    public ObservableCollection<AIAgentRunSummaryItem> AIAgentRuns { get; }

    public ICommand ToggleDashboardCommand { get; }

    public ICommand SubmitOrderCommand { get; }

    public ICommand ConfirmOrderCommand { get; }

    public ICommand CancelOrderConfirmationCommand { get; }

    public ICommand RunAgentNowCommand { get; }

    public string[] IntervalOptions => DefaultIntervals;

    public string SelectedAccountsSummary
    {
        get
        {
            var checkedItems = Accounts.Where(x => !x.IsSelectAll && x.IsChecked).Select(x => x.DisplayText).ToList();
            if (checkedItems.Count == 0)
            {
                return L["Dashboard_NoAccountsSelected"];
            }

            if (checkedItems.Count == 1)
            {
                return checkedItems[0];
            }

            return string.Format(CultureInfo.CurrentCulture, L["Dashboard_AccountsSelectedCount"], checkedItems.Count);
        }
    }

    public bool ShowTestnet
    {
        get => _showTestnet;
        set
        {
            if (SetProperty(ref _showTestnet, value))
            {
                RebuildAccounts();
            }
        }
    }

    public DashboardSymbolOptionDto? SelectedSymbolOption
    {
        get => _selectedSymbolOption;
        set
        {
            if (SetProperty(ref _selectedSymbolOption, value))
            {
                RaisePropertyChanged(nameof(SelectedSymbol));
            }
        }
    }

    public string? SelectedSymbol => SelectedSymbolOption?.Value;

    public string SelectedInterval
    {
        get => _selectedInterval;
        set => SetProperty(ref _selectedInterval, value);
    }

    public bool IsDashboardRunning
    {
        get => _isDashboardRunning;
        private set
        {
            if (SetProperty(ref _isDashboardRunning, value))
            {
                RaisePropertyChanged(nameof(CanEditFilters));
                RaisePropertyChanged(nameof(CanToggleDashboard));
                RaisePropertyChanged(nameof(ToggleDashboardText));
                (ToggleDashboardCommand as RelayCommand)?.NotifyCanExecuteChanged();
                (SubmitOrderCommand as RelayCommand)?.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsDashboardBusy
    {
        get => _isDashboardBusy;
        private set
        {
            if (SetProperty(ref _isDashboardBusy, value))
            {
                RaisePropertyChanged(nameof(CanEditFilters));
                RaisePropertyChanged(nameof(CanToggleDashboard));
                (ToggleDashboardCommand as RelayCommand)?.NotifyCanExecuteChanged();
            }
        }
    }

    public bool CanEditFilters => !IsDashboardRunning && !IsDashboardBusy;

    public bool CanToggleDashboard => !IsDashboardBusy;

    public string ToggleDashboardText => IsDashboardRunning ? L["Dashboard_Stop"] : L["Dashboard_Confirm"];

    public DashboardMarketRow? SelectedMarketRow
    {
        get => _selectedMarketRow;
        set
        {
            if (SetProperty(ref _selectedMarketRow, value))
            {
                if (IsLimitOrder && value is not null)
                {
                    OrderPrice = NumberText.Trim(value.Price, useGrouping: true);
                }

                RaisePropertyChanged(nameof(MaxLeverage));
                RefreshMarginModeSupport();
                RecalculateOrderEstimates();
                (SubmitOrderCommand as RelayCommand)?.NotifyCanExecuteChanged();
            }
        }
    }

    public double Leverage
    {
        get => _leverage;
        set
        {
            var max = Math.Max(1d, MaxLeverage);
            var clamped = Math.Clamp(value, 1d, max);
            if (SetProperty(ref _leverage, clamped))
            {
                RecalculateOrderEstimates();
            }
        }
    }

    public double MaxLeverage => SelectedMarketRow?.MaxLeverage ?? 25d;

    public IReadOnlyList<string> MarginModeOptions
    {
        get => _marginModeOptions;
        private set => SetProperty(ref _marginModeOptions, value);
    }

    public string SelectedMarginMode
    {
        get => _selectedMarginMode;
        set
        {
            var next = CoerceMarginModeSelection(value, MarginModeOptions, CurrentMarginModeVenueId);
            if (SetProperty(ref _selectedMarginMode, next))
            {
                RaisePropertyChanged(nameof(IsCrossMarginModeSelected));
                RaisePropertyChanged(nameof(IsIsolatedMarginModeSelected));
            }
        }
    }

    public bool IsCrossMarginModeSelected
    {
        get => string.Equals(SelectedMarginMode, "Cross", StringComparison.Ordinal);
        set
        {
            if (value)
            {
                SelectedMarginMode = "Cross";
            }
        }
    }

    public bool IsIsolatedMarginModeSelected
    {
        get => string.Equals(SelectedMarginMode, "Isolated", StringComparison.Ordinal);
        set
        {
            if (value && CanUseIsolatedMarginMode)
            {
                SelectedMarginMode = "Isolated";
            }
        }
    }

    public bool CanUseIsolatedMarginMode =>
        MarginModeOptions.Any(x => string.Equals(x, "Isolated", StringComparison.Ordinal)) &&
        IsIsolatedMarginModeEnabled(CurrentMarginModeVenueId);

    public string OrderAmount
    {
        get => _orderAmount;
        set
        {
            if (SetProperty(ref _orderAmount, value))
            {
                RecalculateOrderEstimates();
            }
        }
    }

    public bool IsLongOrder
    {
        get => _isLongOrder;
        set
        {
            if (SetProperty(ref _isLongOrder, value))
            {
                RaisePropertyChanged(nameof(IsShortOrder));
                RecalculateOrderEstimates();
            }
        }
    }

    public bool IsShortOrder
    {
        get => !_isLongOrder;
        set
        {
            if (value)
            {
                IsLongOrder = false;
                RaisePropertyChanged(nameof(IsShortOrder));
            }
        }
    }

    public bool IsLimitOrder
    {
        get => _isLimitOrder;
        set
        {
            if (SetProperty(ref _isLimitOrder, value))
            {
                if (value && SelectedMarketRow is not null && string.IsNullOrWhiteSpace(OrderPrice))
                {
                    OrderPrice = NumberText.Trim(SelectedMarketRow.Price, useGrouping: true);
                }

                RaisePropertyChanged(nameof(IsMarketOrder));
                RecalculateOrderEstimates();
            }
        }
    }

    public bool IsMarketOrder
    {
        get => !IsLimitOrder;
        set
        {
            if (value)
            {
                IsLimitOrder = false;
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

    public string EstimatedMargin
    {
        get => _estimatedMargin;
        private set => SetProperty(ref _estimatedMargin, value);
    }

    public string EstimatedLiquidationPrice
    {
        get => _estimatedLiquidationPrice;
        private set => SetProperty(ref _estimatedLiquidationPrice, value);
    }

    public bool IsMarketPanelVisible
    {
        get => _isMarketPanelVisible;
        private set => SetProperty(ref _isMarketPanelVisible, value);
    }

    public string LeftPanelWidth
    {
        get => _leftPanelWidth;
        private set => SetProperty(ref _leftPanelWidth, value);
    }

    public string RightPanelWidth
    {
        get => _rightPanelWidth;
        private set => SetProperty(ref _rightPanelWidth, value);
    }

    public bool IsOrderConfirmationVisible
    {
        get => _isOrderConfirmationVisible;
        private set => SetProperty(ref _isOrderConfirmationVisible, value);
    }

    public string OrderConfirmationText
    {
        get => _orderConfirmationText;
        private set => SetProperty(ref _orderConfirmationText, value);
    }

    public string AgentEnableState
    {
        get => _agentEnableState;
        private set => SetProperty(ref _agentEnableState, value);
    }

    public string AgentSelectedType
    {
        get => _agentSelectedType;
        private set => SetProperty(ref _agentSelectedType, value);
    }

    public string AgentWakeInterval
    {
        get => _agentWakeInterval;
        private set => SetProperty(ref _agentWakeInterval, value);
    }

    public string AgentLastRunStatus
    {
        get => _agentLastRunStatus;
        private set => SetProperty(ref _agentLastRunStatus, value);
    }

    public string AgentLastRunTime
    {
        get => _agentLastRunTime;
        private set => SetProperty(ref _agentLastRunTime, value);
    }

    public AIAgentRunSummaryItem? SelectedAIAgentRun
    {
        get => _selectedAIAgentRun;
        set
        {
            if (SetProperty(ref _selectedAIAgentRun, value))
            {
                SelectedAIAgentRunDetail = value is null
                    ? null
                    : new AIAgentRunDetailViewModel(value.Record);
            }
        }
    }

    public AIAgentRunDetailViewModel? SelectedAIAgentRunDetail
    {
        get => _selectedAIAgentRunDetail;
        private set
        {
            if (SetProperty(ref _selectedAIAgentRunDetail, value))
            {
                RaisePropertyChanged(nameof(HasSelectedAIAgentRunDetail));
                RaisePropertyChanged(nameof(HasNoSelectedAIAgentRunDetail));
            }
        }
    }

    public bool HasSelectedAIAgentRunDetail => SelectedAIAgentRunDetail is not null;

    public bool HasNoSelectedAIAgentRunDetail => !HasSelectedAIAgentRunDetail;

    public bool IsOptionsExpanded
    {
        get => _isOptionsExpanded;
        set => SetProperty(ref _isOptionsExpanded, value);
    }

    public bool IsAccountsExpanded
    {
        get => _isAccountsExpanded;
        set => SetProperty(ref _isAccountsExpanded, value);
    }

    public bool CanRunAgentNow => !_isAgentRunBusy &&
                                  HasRunnableAgentSettings &&
                                  _aiAgentExecutionService.CanExecuteNow;

    public bool HasRunnableAgentSettings
    {
        get
        {
            var settings = _aiAgentExecutionService.GetSettings();
            return !string.IsNullOrWhiteSpace(settings.CommandTemplate)
                && !string.IsNullOrWhiteSpace(settings.PromptTemplate);
        }
    }

    public void ApplyViewport(double width)
    {
        var showMarketPanel = width >= 1200d;
        IsMarketPanelVisible = showMarketPanel;
        LeftPanelWidth = showMarketPanel ? "2*" : "0";
        RightPanelWidth = showMarketPanel ? "3*" : "*";
    }

    public new void NotifyLocalizationChanged()
    {
        base.NotifyLocalizationChanged();
        RaisePropertyChanged(nameof(Header));
        RaisePropertyChanged(nameof(SelectedAccountsSummary));
        RaisePropertyChanged(nameof(ToggleDashboardText));
        if (SelectedAIAgentRun?.Record is not null)
        {
            SelectedAIAgentRunDetail = new AIAgentRunDetailViewModel(SelectedAIAgentRun.Record);
        }
        RefreshAgentState();
        foreach (var row in PositionRows)
        {
            row.NotifyLocalizationChanged();
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _accounts.CollectionChanged -= OnAccountsCollectionChanged;
        _dashboardService.SnapshotChanged -= OnDashboardSnapshotChanged;
        _aiAgentExecutionService.StateChanged -= OnAIAgentStateChanged;
    }

    private async Task ToggleDashboardAsync()
    {
        if (IsDashboardBusy)
        {
            return;
        }

        IsDashboardBusy = true;
        try
        {
            if (IsDashboardRunning)
            {
                await _dashboardService.StopAsync();
                HideOrderConfirmation();
                return;
            }

            var effectiveAccounts = GetEffectiveAccounts().ToList();
            if (effectiveAccounts.Count == 0)
            {
                _toastService.ShowWarning(L["Dashboard_SelectAtLeastOneAccount"]);
                return;
            }

            if (string.IsNullOrWhiteSpace(SelectedSymbol))
            {
                _toastService.ShowWarning(L["Dashboard_SelectSymbolFirst"]);
                return;
            }

            var configuration = new DashboardConfiguration(
                effectiveAccounts.Select(x => x.AccountId).ToArray(),
                SelectedSymbol,
                SelectedInterval,
                ShowTestnet);
            await _dashboardService.UpdateConfigurationAsync(configuration);
            await _dashboardService.StartAsync();
            IsOptionsExpanded = false;
        }
        catch (Exception ex)
        {
            _toastService.ShowError(ex.Message);
        }
        finally
        {
            IsDashboardBusy = false;
        }
    }

    private void BeginSubmitOrder()
    {
        if (SelectedMarketRow is null)
        {
            _toastService.ShowWarning(L["Dashboard_SelectMarketRowFirst"]);
            return;
        }

        if (string.IsNullOrWhiteSpace(SelectedMarketRow.RawSymbol))
        {
            _toastService.ShowWarning("Selected exchange does not support the current symbol.");
            return;
        }

        var amount = TryParseDecimal(OrderAmount);
        if (!amount.HasValue || amount.Value <= 0)
        {
            _toastService.ShowWarning(L["OrderPanel_Amount_Example"]);
            return;
        }

        decimal? limitPrice = null;
        if (IsLimitOrder)
        {
            limitPrice = TryParseDecimal(OrderPrice);
            if (!limitPrice.HasValue || limitPrice.Value <= 0)
            {
                _toastService.ShowWarning(L["OrderPanel_LimitPrice_Placeholder"]);
                return;
            }
        }

        _pendingOrderRequest = new ApiOpenPositionRequest(
            SelectedMarketRow.AccountId,
            SelectedMarketRow.RawSymbol,
            IsLongOrder ? "long" : "short",
            IsLimitOrder ? "limit" : "market",
            decimal.Round((decimal)Leverage, 2, MidpointRounding.AwayFromZero),
            amount.Value,
            "USD",
            limitPrice,
            SelectedMarginMode);

        var orderType = IsLimitOrder ? L["OrderType_Limit"] : L["OrderType_Market"];
        var side = IsLongOrder ? L["OrderSide_Long"] : L["OrderSide_Short"];
        OrderConfirmationText = string.Format(
            CultureInfo.CurrentCulture,
            L["Dashboard_OrderPreviewToast"],
            SelectedMarketRow.Exchange,
            SelectedMarketRow.Symbol,
            side,
            orderType,
            NumberText.Trim((decimal)Leverage),
            NumberText.Trim(amount.Value, useGrouping: true),
            limitPrice.HasValue ? NumberText.Trim(limitPrice.Value, useGrouping: true) : "-");
        IsOrderConfirmationVisible = true;
        (ConfirmOrderCommand as RelayCommand)?.NotifyCanExecuteChanged();
    }

    private async Task ConfirmOrderAsync()
    {
        if (_pendingOrderRequest is null)
        {
            return;
        }

        try
        {
            await _dashboardService.OpenPositionAsync(_pendingOrderRequest);
            HideOrderConfirmation();
            _toastService.ShowInfo(L["Toast_OrderSuccess"]);
        }
        catch (Exception ex)
        {
            _toastService.ShowError($"{L["Toast_OrderFailed"]}{ex.Message}");
        }
    }

    private void HideOrderConfirmation()
    {
        _pendingOrderRequest = null;
        OrderConfirmationText = string.Empty;
        IsOrderConfirmationVisible = false;
        (ConfirmOrderCommand as RelayCommand)?.NotifyCanExecuteChanged();
    }

    private async Task SubmitClosePositionAsync(DashboardPositionRow row, bool useLimitPrice)
    {
        try
        {
            decimal? limitPrice = null;
            if (useLimitPrice)
            {
                limitPrice = TryParseDecimal(row.CloseLimitPrice);
                if (!limitPrice.HasValue || limitPrice.Value <= 0)
                {
                    _toastService.ShowWarning(L["Toast_ClosePriceInvalid"]);
                    return;
                }
            }

            await _dashboardService.ClosePositionAsync(new ApiClosePositionRequest(
                row.AccountId,
                row.RawSymbol,
                useLimitPrice ? "limit" : "market",
                limitPrice));
            _toastService.ShowInfo(useLimitPrice ? L["Toast_CloseLimitSent"] : L["Toast_CloseMarketSent"]);
        }
        catch (Exception ex)
        {
            _toastService.ShowError($"{L["Toast_CloseFailed"]}{ex.Message}");
        }
    }

    private async Task CancelPendingOrderAsync(DashboardPendingOrderRow row)
    {
        if (string.IsNullOrWhiteSpace(row.OrderId))
        {
            _toastService.ShowWarning(L["Toast_OrderNoCancelableId"]);
            return;
        }

        try
        {
            await _dashboardService.CancelOrderAsync(new ApiCancelOrderRequest(row.AccountId, row.RawSymbol, row.OrderId));
            _toastService.ShowInfo(L["Toast_CancelSuccess"]);
        }
        catch (Exception ex)
        {
            _toastService.ShowError($"{L["Toast_CancelFailed"]}{ex.Message}");
        }
    }

    private void RebuildAccounts()
    {
        var previousSelections = Accounts
            .Where(x => !x.IsSelectAll && x.IsChecked && x.Account is not null)
            .Select(x => x.Account!.AccountId)
            .ToHashSet();

        Accounts.Clear();
        Accounts.Add(new DashboardAccountSelectionItem(null, L["Dashboard_SelectAll"], false, OnAccountItemCheckedChanged));

        foreach (var account in _accounts.Where(ShouldShowAccount))
        {
            Accounts.Add(new DashboardAccountSelectionItem(
                account,
                account.Label,
                previousSelections.Contains(account.AccountId),
                OnAccountItemCheckedChanged));
        }

        UpdateSelectAllState();
        RaisePropertyChanged(nameof(SelectedAccountsSummary));
        RefreshSymbolOptions();
    }

    private bool ShouldShowAccount(AccountProfile account)
    {
        return account.IsEnabled && (ShowTestnet || !string.Equals(account.Environment, "testnet", StringComparison.OrdinalIgnoreCase));
    }

    private void OnAccountItemCheckedChanged(DashboardAccountSelectionItem item)
    {
        if (item.IsSelectAll)
        {
            foreach (var accountItem in Accounts.Where(x => !x.IsSelectAll))
            {
                accountItem.SetIsCheckedSilently(item.IsChecked);
            }

            if (item.IsChecked)
            {
                IsAccountsExpanded = false;
            }
        }

        UpdateSelectAllState();
        RaisePropertyChanged(nameof(SelectedAccountsSummary));
        RefreshSymbolOptions();

        if (GetSelectedAccountsByVenue().Any(x => x.Value.Count > 1))
        {
            _toastService.ShowWarning(L["Dashboard_DuplicateVenueWarning"]);
        }
    }

    private void UpdateSelectAllState()
    {
        var selectable = Accounts.Where(x => !x.IsSelectAll).ToList();
        var allSelected = selectable.Count > 0 && selectable.All(x => x.IsChecked);
        var selectAll = Accounts.FirstOrDefault(x => x.IsSelectAll);
        selectAll?.SetIsCheckedSilently(allSelected);
    }

    private void RefreshSymbolOptions()
    {
        var previous = SelectedSymbolOption?.Value;
        var symbols = _dashboardService.GetAvailableSymbolOptions(new DashboardConfiguration(
            GetEffectiveAccounts().Select(x => x.AccountId).ToArray(),
            previous,
            SelectedInterval,
            ShowTestnet));

        SymbolOptions.Clear();
        foreach (var symbol in symbols)
        {
            SymbolOptions.Add(symbol);
        }

        if (SymbolOptions.Count == 0)
        {
            SelectedSymbolOption = null;
            return;
        }

        SelectedSymbolOption = SymbolOptions.FirstOrDefault(x => string.Equals(x.Value, previous, StringComparison.OrdinalIgnoreCase))
            ?? SymbolOptions[0];
    }

    private Dictionary<string, List<AccountProfile>> GetSelectedAccountsByVenue()
    {
        return Accounts
            .Where(x => !x.IsSelectAll && x.IsChecked && x.Account is not null)
            .Select(x => x.Account!)
            .GroupBy(x => x.VenueId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.ToList(), StringComparer.OrdinalIgnoreCase);
    }

    private IEnumerable<AccountProfile> GetEffectiveAccounts()
    {
        return GetSelectedAccountsByVenue().Values.Select(x => x[0]);
    }

    private void RecalculateOrderEstimates()
    {
        var amount = TryParseDecimal(OrderAmount);
        var referencePrice = IsLimitOrder ? TryParseDecimal(OrderPrice) : SelectedMarketRow?.Price;
        if (!amount.HasValue || amount <= 0 || !referencePrice.HasValue || referencePrice <= 0 || Leverage <= 0)
        {
            EstimatedMargin = "-";
            EstimatedLiquidationPrice = "-";
            return;
        }

        var margin = Math.Round(amount.Value / (decimal)Leverage, 2, MidpointRounding.AwayFromZero);
        var liqFactor = (decimal)Math.Min(0.95d, 0.90d / Math.Max(1d, Leverage));
        var liqPrice = IsLongOrder
            ? referencePrice.Value * (1m - liqFactor)
            : referencePrice.Value * (1m + liqFactor);

        EstimatedMargin = NumberText.Trim(margin, useGrouping: true);
        EstimatedLiquidationPrice = NumberText.Trim(decimal.Round(liqPrice, 2, MidpointRounding.AwayFromZero), useGrouping: true);
    }

    private void ApplyDashboardSnapshot(DashboardSnapshot snapshot)
    {
        _isApplyingSnapshot = true;
        try
        {
            if (ShowTestnet != snapshot.Configuration.ShowTestnet)
            {
                ShowTestnet = snapshot.Configuration.ShowTestnet;
            }
            else if (Accounts.Count == 0)
            {
                RebuildAccounts();
            }

            ApplySelectedAccounts(snapshot.Configuration.SelectedAccountIds);

            var currentSymbols = _dashboardService.GetAvailableSymbolOptions(snapshot.Configuration);
            SymbolOptions.Clear();
            foreach (var symbol in currentSymbols)
            {
                SymbolOptions.Add(symbol);
            }

            SelectedInterval = string.IsNullOrWhiteSpace(snapshot.Configuration.Interval)
                ? "5m"
                : snapshot.Configuration.Interval;

            if (snapshot.Configuration.Symbol is not null)
            {
                SelectedSymbolOption = SymbolOptions.FirstOrDefault(x => string.Equals(x.Value, snapshot.Configuration.Symbol, StringComparison.OrdinalIgnoreCase));
            }
            else if (SymbolOptions.Count > 0 && string.IsNullOrWhiteSpace(SelectedSymbol))
            {
                SelectedSymbolOption = SymbolOptions[0];
            }

            if (SelectedSymbolOption is null && SymbolOptions.Count > 0)
            {
                SelectedSymbolOption = SymbolOptions[0];
            }

            IsDashboardRunning = snapshot.IsRunning;
            ReplaceMarketRows(snapshot.Markets);
            ReplacePositionRows(snapshot.Positions);
            ReplacePendingOrders(snapshot.Orders);
            RefreshMarginModeSupport();
            RecalculateOrderEstimates();
        }
        finally
        {
            _isApplyingSnapshot = false;
        }
    }

    private void ApplySelectedAccounts(IReadOnlyList<Guid> selectedIds)
    {
        var selectedSet = selectedIds.ToHashSet();
        foreach (var item in Accounts.Where(x => !x.IsSelectAll && x.Account is not null))
        {
            item.SetIsCheckedSilently(selectedSet.Contains(item.Account!.AccountId));
        }

        UpdateSelectAllState();
        RaisePropertyChanged(nameof(SelectedAccountsSummary));
    }

    private void ReplaceMarketRows(IReadOnlyList<DashboardMarketDto> markets)
    {
        var selectedAccountId = SelectedMarketRow?.AccountId;
        var orderedMarkets = markets
            .OrderBy(x => x.Exchange, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var existingRows = MarketRows.ToDictionary(BuildMarketRowKey);
        var desiredRows = new List<DashboardMarketRow>(orderedMarkets.Count);
        foreach (var market in orderedMarkets)
        {
            var key = BuildMarketKey(market);
            if (existingRows.Remove(key, out var existingRow))
            {
                existingRow.Update(market);
                desiredRows.Add(existingRow);
                continue;
            }

            desiredRows.Add(new DashboardMarketRow(
                market.AccountId,
                market.Exchange,
                market.Symbol,
                market.RawSymbol,
                market.Price,
                market.Pnl,
                market.Balance,
                market.AvailableBalance,
                market.MaxLeverage));
        }

        SynchronizeRows(MarketRows, desiredRows);

        SelectedMarketRow = MarketRows.FirstOrDefault(x =>
            x.AccountId == selectedAccountId)
            ?? MarketRows.FirstOrDefault();
    }

    private void ReplacePositionRows(IReadOnlyList<DashboardPositionDto> positions)
    {
        var orderedPositions = positions
            .OrderBy(x => x.Exchange, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var existingRows = PositionRows.ToDictionary(BuildPositionRowKey, StringComparer.OrdinalIgnoreCase);
        var desiredRows = new List<DashboardPositionRow>(orderedPositions.Count);
        foreach (var position in orderedPositions)
        {
            var key = BuildPositionKey(position);
            if (existingRows.Remove(key, out var existingRow))
            {
                existingRow.Update(position);
                desiredRows.Add(existingRow);
                continue;
            }

            DashboardPositionRow? row = null;
            row = new DashboardPositionRow(
                position.AccountId,
                position.Exchange,
                position.Symbol,
                position.RawSymbol,
                position.RawSymbol,
                position.Mode,
                position.Side,
                position.Amount,
                position.EntryPrice,
                position.Price,
                position.PnlUsd,
                position.PnlPct,
                new RelayCommand(_ => _ = SubmitClosePositionAsync(row!, useLimitPrice: true)),
                new RelayCommand(_ => _ = SubmitClosePositionAsync(row!, useLimitPrice: false)));
            desiredRows.Add(row);
        }

        SynchronizeRows(PositionRows, desiredRows);
    }

    private void ReplacePendingOrders(IReadOnlyList<DashboardPendingOrderDto> orders)
    {
        var orderedOrders = orders
            .OrderBy(x => x.Exchange, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Symbol, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.OrderId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var existingRows = PendingOrderRows.ToDictionary(BuildPendingOrderRowKey, StringComparer.OrdinalIgnoreCase);
        var desiredRows = new List<DashboardPendingOrderRow>(orderedOrders.Count);
        foreach (var order in orderedOrders)
        {
            var key = BuildPendingOrderKey(order);
            if (existingRows.Remove(key, out var existingRow))
            {
                existingRow.Update(order);
                desiredRows.Add(existingRow);
                continue;
            }

            DashboardPendingOrderRow? row = null;
            row = new DashboardPendingOrderRow(
                order.AccountId,
                order.Exchange,
                order.Symbol,
                order.RawSymbol,
                order.Mode,
                order.Amount,
                order.LimitPrice,
                order.Price,
                order.OrderId,
                new RelayCommand(_ => _ = CancelPendingOrderAsync(row!)));
            desiredRows.Add(row);
        }

        SynchronizeRows(PendingOrderRows, desiredRows);
    }

    private string CurrentMarginModeVenueId => SelectedMarketRow?.Exchange ?? string.Empty;

    private void RefreshMarginModeSupport()
    {
        var venueId = CurrentMarginModeVenueId;
        MarginModeOptions = ResolveMarginModeOptions(venueId);

        var preferredMode = InferCurrentSymbolMarginMode() ?? _selectedMarginMode;
        var next = CoerceMarginModeSelection(preferredMode, MarginModeOptions, venueId);
        if (!string.Equals(_selectedMarginMode, next, StringComparison.Ordinal))
        {
            _selectedMarginMode = next;
            RaisePropertyChanged(nameof(SelectedMarginMode));
            RaisePropertyChanged(nameof(IsCrossMarginModeSelected));
            RaisePropertyChanged(nameof(IsIsolatedMarginModeSelected));
        }

        RaisePropertyChanged(nameof(CanUseIsolatedMarginMode));
    }

    private string? InferCurrentSymbolMarginMode()
    {
        if (SelectedMarketRow is null)
        {
            return null;
        }

        var positionMode = PositionRows
            .FirstOrDefault(x => x.AccountId == SelectedMarketRow.AccountId &&
                                 string.Equals(x.RawSymbol, SelectedMarketRow.RawSymbol, StringComparison.OrdinalIgnoreCase))
            ?.Mode;
        if (!string.IsNullOrWhiteSpace(positionMode))
        {
            return NormalizeMarginMode(positionMode);
        }

        var orderMode = PendingOrderRows
            .FirstOrDefault(x => x.AccountId == SelectedMarketRow.AccountId &&
                                 string.Equals(x.RawSymbol, SelectedMarketRow.RawSymbol, StringComparison.OrdinalIgnoreCase))
            ?.Mode;
        return string.IsNullOrWhiteSpace(orderMode)
            ? null
            : NormalizeMarginMode(orderMode);
    }

    private static string NormalizeMarginMode(string? raw)
    {
        return MarginModeText.ParseOrDefault(raw, MarginMode.Cross) == MarginMode.Isolated
            ? "Isolated"
            : "Cross";
    }

    private static string CoerceMarginModeSelection(string? raw, IReadOnlyList<string> options, string? venueId)
    {
        var normalized = NormalizeMarginMode(raw);
        if (options.Any(x => string.Equals(x, normalized, StringComparison.Ordinal)) &&
            IsMarginModeSelectable(normalized, venueId))
        {
            return normalized;
        }

        return options.FirstOrDefault(x => IsMarginModeSelectable(x, venueId)) ?? "Cross";
    }

    private static IReadOnlyList<string> ResolveMarginModeOptions(string? venueId)
    {
        return (venueId ?? string.Empty).Trim() switch
        {
            "BitMEX" => ["Cross", "Isolated"],
            "Hyperliquid" => ["Cross", "Isolated"],
            "Aster" => ["Cross", "Isolated"],
            "GRVT" => ["Cross", "Isolated"],
            "dYdX" => ["Cross", "Isolated"],
            _ => ["Cross", "Isolated"]
        };
    }

    private static bool IsMarginModeSelectable(string? marginMode, string? venueId)
    {
        var normalized = NormalizeMarginMode(marginMode);
        return !string.Equals(normalized, "Isolated", StringComparison.Ordinal) || IsIsolatedMarginModeEnabled(venueId);
    }

    private static bool IsIsolatedMarginModeEnabled(string? venueId)
    {
        return !string.Equals((venueId ?? string.Empty).Trim(), "dYdX", StringComparison.OrdinalIgnoreCase);
    }

    private static void SynchronizeRows<TRow>(ObservableCollection<TRow> target, IReadOnlyList<TRow> desired)
        where TRow : class
    {
        for (var i = target.Count - 1; i >= desired.Count; i--)
        {
            target.RemoveAt(i);
        }

        for (var i = 0; i < desired.Count; i++)
        {
            var desiredRow = desired[i];
            if (i >= target.Count)
            {
                target.Add(desiredRow);
                continue;
            }

            if (ReferenceEquals(target[i], desiredRow))
            {
                continue;
            }

            var existingIndex = target.IndexOf(desiredRow);
            if (existingIndex >= 0)
            {
                target.Move(existingIndex, i);
                continue;
            }

            target.Insert(i, desiredRow);
        }

        while (target.Count > desired.Count)
        {
            target.RemoveAt(target.Count - 1);
        }
    }

    private static string BuildPositionKey(DashboardPositionDto position)
        => $"{position.AccountId:N}:{position.RawSymbol}";

    private static string BuildPositionRowKey(DashboardPositionRow row)
        => $"{row.AccountId:N}:{row.PositionId}";

    private static string BuildMarketKey(DashboardMarketDto market)
        => market.AccountId.ToString("N");

    private static string BuildMarketRowKey(DashboardMarketRow row)
        => row.AccountId.ToString("N");

    private static string BuildPendingOrderKey(DashboardPendingOrderDto order)
    {
        if (!string.IsNullOrWhiteSpace(order.OrderId))
        {
            return $"{order.AccountId:N}:{order.OrderId}";
        }

        return $"{order.AccountId:N}:{order.RawSymbol}:{order.Mode}:{order.Amount}:{order.LimitPrice}";
    }

    private static string BuildPendingOrderRowKey(DashboardPendingOrderRow row)
    {
        if (!string.IsNullOrWhiteSpace(row.OrderId))
        {
            return $"{row.AccountId:N}:{row.OrderId}";
        }

        return $"{row.AccountId:N}:{row.RawSymbol}:{row.Mode}:{row.Amount}:{row.LimitPrice}";
    }

    private static decimal? TryParseDecimal(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (decimal.TryParse(raw, NumberStyles.Any, CultureInfo.CurrentCulture, out var currentCultureValue))
        {
            return currentCultureValue;
        }

        return decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var invariantValue)
            ? invariantValue
            : null;
    }

    private void OnAccountsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_isApplyingSnapshot)
            {
                RebuildAccounts();
            }
        });
    }

    private void OnDashboardSnapshotChanged(DashboardSnapshot snapshot)
    {
        Dispatcher.UIThread.Post(() => ApplyDashboardSnapshot(snapshot));
    }

    private async Task RunAgentNowAsync()
    {
        if (!CanRunAgentNow)
        {
            return;
        }

        _isAgentRunBusy = true;
        (RunAgentNowCommand as RelayCommand)?.NotifyCanExecuteChanged();
        try
        {
            var result = await _aiAgentExecutionService.RunNowAsync();
            _toastService.ShowInfo(string.Format(CultureInfo.CurrentCulture, L["Agent_RunCompleted"], result.Status));
        }
        catch (Exception ex)
        {
            _toastService.ShowError(ex.Message);
        }
        finally
        {
            _isAgentRunBusy = false;
            RefreshAgentState();
            (RunAgentNowCommand as RelayCommand)?.NotifyCanExecuteChanged();
        }
    }

    public Task DeleteAgentRunAsync(AIAgentRunSummaryItem item)
    {
        if (item is null)
        {
            return Task.CompletedTask;
        }

        _aiAgentExecutionService.DeleteRun(item.RunId);
        _toastService.ShowInfo(L["Agent_DeleteRunSuccess"]);
        return Task.CompletedTask;
    }

    public Task ClearAgentRunsAsync()
    {
        _aiAgentExecutionService.ClearRunHistory();
        _toastService.ShowInfo(L["Agent_ClearHistorySuccess"]);
        return Task.CompletedTask;
    }

    private void RefreshAgentState()
    {
        var selectedRunId = SelectedAIAgentRun?.RunId;
        var settings = _aiAgentExecutionService.GetSettings();
        AgentEnableState = settings.IsEnabled ? L["Agent_Enabled"] : L["Agent_Disabled"];
        AgentSelectedType = AIAgentProfileCatalog.ToDisplayName(settings.AgentType);
        var wakeIntervalText = settings.WakeIntervalMinutes > 0
            ? string.Format(CultureInfo.CurrentCulture, L["Agent_WakeIntervalFormat"], settings.WakeIntervalMinutes)
            : L["Agent_WakeIntervalDisabled"];
        var wakeConditionCount = settings.WakeConditions?.Count(x => x.IsEnabled && !string.IsNullOrWhiteSpace(x.Symbol)) ?? 0;
        AgentWakeInterval = wakeConditionCount > 0
            ? string.Format(CultureInfo.CurrentCulture, L["Agent_WakeIntervalWithConditionsFormat"], wakeIntervalText, wakeConditionCount)
            : wakeIntervalText;

        var lastRun = _aiAgentExecutionService.LastRun;
        AgentLastRunStatus = lastRun?.Status ?? L["Agent_NoRuns"];
        AgentLastRunTime = lastRun is null
            ? "-"
            : lastRun.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);

        var rows = _aiAgentExecutionService.GetRecentRuns()
            .Select(x => new AIAgentRunSummaryItem(x))
            .ToList();
        SynchronizeRows(AIAgentRuns, rows);
        SelectedAIAgentRun = string.IsNullOrWhiteSpace(selectedRunId)
            ? null
            : AIAgentRuns.FirstOrDefault(x => string.Equals(x.RunId, selectedRunId, StringComparison.Ordinal));
        RaisePropertyChanged(nameof(HasRunnableAgentSettings));
        (RunAgentNowCommand as RelayCommand)?.NotifyCanExecuteChanged();
    }

    private void OnAIAgentStateChanged()
    {
        Dispatcher.UIThread.Post(RefreshAgentState);
    }
}

public sealed class DashboardAccountSelectionItem : ViewModelBase
{
    private readonly Action<DashboardAccountSelectionItem> _changed;
    private bool _isChecked;
    private bool _suppressChangedCallback;

    public DashboardAccountSelectionItem(AccountProfile? account, string displayText, bool isChecked, Action<DashboardAccountSelectionItem> changed)
    {
        Account = account;
        DisplayText = displayText;
        _isChecked = isChecked;
        _changed = changed;
    }

    public AccountProfile? Account { get; }

    public bool IsSelectAll => Account is null;

    public string DisplayText { get; }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (SetProperty(ref _isChecked, value) && !_suppressChangedCallback)
            {
                _changed(this);
            }
        }
    }

    public void SetIsCheckedSilently(bool value)
    {
        _suppressChangedCallback = true;
        IsChecked = value;
        _suppressChangedCallback = false;
    }
}

public sealed class DashboardMarketRow : ViewModelBase
{
    private string _exchange;
    private string _symbol;
    private string _rawSymbol;
    private decimal _price;
    private decimal _pnl;
    private decimal _balance;
    private decimal _availableBalance;
    private double _maxLeverage;

    public DashboardMarketRow(Guid accountId, string exchange, string symbol, string rawSymbol, decimal price, decimal pnl, decimal balance, decimal availableBalance, double maxLeverage)
    {
        AccountId = accountId;
        _exchange = exchange;
        _symbol = symbol;
        _rawSymbol = rawSymbol;
        _price = price;
        _pnl = pnl;
        _balance = balance;
        _availableBalance = availableBalance;
        _maxLeverage = maxLeverage;
    }

    public Guid AccountId { get; }

    public string Exchange
    {
        get => _exchange;
        private set => SetProperty(ref _exchange, value);
    }

    public string Symbol
    {
        get => _symbol;
        private set => SetProperty(ref _symbol, value);
    }

    public string RawSymbol
    {
        get => _rawSymbol;
        private set => SetProperty(ref _rawSymbol, value);
    }

    public decimal Price
    {
        get => _price;
        set => SetProperty(ref _price, value);
    }

    public decimal Pnl
    {
        get => _pnl;
        set => SetProperty(ref _pnl, value);
    }

    public decimal Balance
    {
        get => _balance;
        private set => SetProperty(ref _balance, value);
    }

    public decimal AvailableBalance
    {
        get => _availableBalance;
        set => SetProperty(ref _availableBalance, value);
    }

    public double MaxLeverage
    {
        get => _maxLeverage;
        private set => SetProperty(ref _maxLeverage, value);
    }

    public void Update(DashboardMarketDto market)
    {
        Exchange = market.Exchange;
        Symbol = market.Symbol;
        RawSymbol = market.RawSymbol;
        Price = market.Price;
        Pnl = market.Pnl;
        Balance = market.Balance;
        AvailableBalance = market.AvailableBalance;
        MaxLeverage = market.MaxLeverage;
    }
}

public sealed class DashboardPositionRow : ViewModelBase
{
    private string _exchange;
    private string _symbol;
    private decimal _price;
    private decimal _pnlUsd;
    private decimal _pnlPct;
    private string _closeLimitPrice = string.Empty;
    private string _mode;
    private string _side;
    private decimal _amount;
    private decimal _entryPrice;

    public DashboardPositionRow(Guid accountId, string exchange, string symbol, string rawSymbol, string positionId, string mode, string side, decimal amount, decimal entryPrice, decimal price, decimal pnlUsd, decimal pnlPct, ICommand closeLimitCommand, ICommand closeMarketCommand)
    {
        AccountId = accountId;
        _exchange = exchange;
        _symbol = symbol;
        RawSymbol = rawSymbol;
        PositionId = positionId;
        _mode = mode;
        _side = side;
        _amount = amount;
        _entryPrice = entryPrice;
        _price = price;
        _pnlUsd = pnlUsd;
        _pnlPct = pnlPct;
        CloseLimitCommand = closeLimitCommand;
        CloseMarketCommand = closeMarketCommand;
    }

    public Guid AccountId { get; }

    public string Exchange
    {
        get => _exchange;
        private set => SetProperty(ref _exchange, value);
    }

    public string Symbol
    {
        get => _symbol;
        private set => SetProperty(ref _symbol, value);
    }

    public string RawSymbol { get; }

    public string PositionId { get; }

    public string Mode
    {
        get => _mode;
        private set => SetProperty(ref _mode, value);
    }

    public string Side
    {
        get => _side;
        private set => SetProperty(ref _side, value);
    }

    public decimal Amount
    {
        get => _amount;
        private set => SetProperty(ref _amount, value);
    }

    public decimal EntryPrice
    {
        get => _entryPrice;
        private set => SetProperty(ref _entryPrice, value);
    }

    public decimal Price
    {
        get => _price;
        set => SetProperty(ref _price, value);
    }

    public decimal PnlUsd
    {
        get => _pnlUsd;
        set
        {
            if (SetProperty(ref _pnlUsd, value))
            {
                RaisePropertyChanged(nameof(PnlDisplay));
            }
        }
    }

    public decimal PnlPct
    {
        get => _pnlPct;
        set
        {
            if (SetProperty(ref _pnlPct, value))
            {
                RaisePropertyChanged(nameof(PnlDisplay));
            }
        }
    }

    public string PnlDisplay => $"{NumberText.Signed(PnlUsd, 2)}/{NumberText.Signed(PnlPct, 2)}%";

    public string CloseLimitPrice
    {
        get => _closeLimitPrice;
        set => SetProperty(ref _closeLimitPrice, value);
    }

    public ICommand CloseLimitCommand { get; }

    public ICommand CloseMarketCommand { get; }

    public void Update(DashboardPositionDto position)
    {
        Exchange = position.Exchange;
        Symbol = position.Symbol;
        Mode = position.Mode;
        Side = position.Side;
        Amount = position.Amount;
        EntryPrice = position.EntryPrice;
        Price = position.Price;
        PnlUsd = position.PnlUsd;
        PnlPct = position.PnlPct;
    }

    public new void NotifyLocalizationChanged()
    {
        RaisePropertyChanged(nameof(PnlDisplay));
    }
}

public sealed class DashboardPendingOrderRow : ViewModelBase
{
    private string _exchange;
    private string _symbol;
    private string _mode;
    private decimal _amount;
    private decimal _limitPrice;
    private decimal _price;

    public DashboardPendingOrderRow(Guid accountId, string exchange, string symbol, string rawSymbol, string mode, decimal amount, decimal limitPrice, decimal price, string? orderId, ICommand cancelCommand)
    {
        AccountId = accountId;
        _exchange = exchange;
        _symbol = symbol;
        RawSymbol = rawSymbol;
        _mode = mode;
        _amount = amount;
        _limitPrice = limitPrice;
        _price = price;
        OrderId = orderId;
        CancelCommand = cancelCommand;
    }

    public Guid AccountId { get; }

    public string Exchange
    {
        get => _exchange;
        private set => SetProperty(ref _exchange, value);
    }

    public string Symbol
    {
        get => _symbol;
        private set => SetProperty(ref _symbol, value);
    }

    public string RawSymbol { get; }

    public string Mode
    {
        get => _mode;
        private set => SetProperty(ref _mode, value);
    }

    public decimal Amount
    {
        get => _amount;
        private set => SetProperty(ref _amount, value);
    }

    public decimal LimitPrice
    {
        get => _limitPrice;
        private set => SetProperty(ref _limitPrice, value);
    }

    public decimal Price
    {
        get => _price;
        set => SetProperty(ref _price, value);
    }

    public string? OrderId { get; }

    public ICommand CancelCommand { get; }

    public void Update(DashboardPendingOrderDto order)
    {
        Exchange = order.Exchange;
        Symbol = order.Symbol;
        Mode = order.Mode;
        Amount = order.Amount;
        LimitPrice = order.LimitPrice;
        Price = order.Price;
    }
}
