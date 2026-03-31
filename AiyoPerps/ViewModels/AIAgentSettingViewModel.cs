using AiyoPerps.Services;
using AiyoPerps.Services.Api;
using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;

namespace AiyoPerps.ViewModels;

public sealed class AIAgentSettingViewModel : ViewModelBase
{
    private readonly AIAgentExecutionService _service;
    private readonly TradingApiService _trading;
    private readonly DashboardService _dashboardService;
    private readonly ToastService _toastService;
    private readonly AppLogger _logger;

    private bool _isEnabled;
    private AIAgentProfileOption? _selectedAgent;
    private int _wakeIntervalMinutes;
    private string _commandTemplate = string.Empty;
    private string _promptTemplate = string.Empty;
    private string _workingDirectory = string.Empty;
    private string _environmentVariables = string.Empty;
    private int _timeoutSeconds;
    private bool _isBusy;
    private string _statusMessage = string.Empty;

    public AIAgentSettingViewModel(AIAgentExecutionService service, TradingApiService trading, DashboardService dashboardService, ToastService toastService, AppLogger logger)
    {
        _service = service;
        _trading = trading;
        _dashboardService = dashboardService;
        _toastService = toastService;
        _logger = logger;

        AgentOptions =
        [
            new AIAgentProfileOption("codex", "Codex"),
            new AIAgentProfileOption("claude-code", "Claude Code"),
            new AIAgentProfileOption("gemini-cli", "Gemini CLI"),
            new AIAgentProfileOption("custom", "Custom")
        ];

        WakeMetricOptions =
        [
            new NamedOption(AIAgentWakeMetric.Price, () => L["Agent_WakeMetricPrice"]),
            new NamedOption(AIAgentWakeMetric.UnrealizedPnlPct, () => L["Agent_WakeMetricUnrealizedPnlPct"])
        ];

        WakeComparisonOptions =
        [
            new NamedOption(AIAgentWakeComparison.GreaterThan, () => L["Agent_WakeComparisonGreaterThan"]),
            new NamedOption(AIAgentWakeComparison.LessThan, () => L["Agent_WakeComparisonLessThan"])
        ];

        WakeConditions = [];
        RefreshAccountOptions();

        SaveCommand = new RelayCommand(_ => Save(), _ => CanSave);
        ResetTemplateCommand = new RelayCommand(_ => ResetTemplates(), _ => SelectedAgent is not null && !IsBusy);
        TestRunCommand = new RelayCommand(_ => _ = TestRunAsync(), _ => CanSave && !IsBusy);
        AddWakeConditionCommand = new RelayCommand(_ => AddWakeCondition(), _ => !IsBusy);
        DeleteWakeConditionCommand = new RelayCommand(DeleteWakeCondition, parameter => parameter is AIAgentWakeConditionItemViewModel && !IsBusy);

        Load(_service.GetSettings());
    }

    public AIAgentProfileOption[] AgentOptions { get; }

    public NamedOption[] WakeMetricOptions { get; }

    public NamedOption[] WakeComparisonOptions { get; }

    public ObservableCollection<AIAgentAccountOption> WakeAccountOptions { get; } = [];

    public ObservableCollection<AIAgentWakeConditionItemViewModel> WakeConditions { get; }

    public ICommand SaveCommand { get; }

    public ICommand ResetTemplateCommand { get; }

    public ICommand TestRunCommand { get; }

    public ICommand AddWakeConditionCommand { get; }

    public ICommand DeleteWakeConditionCommand { get; }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetAndNotify(ref _isEnabled, value);
    }

    public AIAgentProfileOption? SelectedAgent
    {
        get => _selectedAgent;
        set
        {
            if (!SetProperty(ref _selectedAgent, value))
            {
                return;
            }

            ApplyAgentDefaults();
            NotifyCommandStates();
        }
    }

    public int WakeIntervalMinutes
    {
        get => _wakeIntervalMinutes;
        set => SetAndNotify(ref _wakeIntervalMinutes, value);
    }

    public string CommandTemplate
    {
        get => _commandTemplate;
        set => SetAndNotify(ref _commandTemplate, value);
    }

    public string PromptTemplate
    {
        get => _promptTemplate;
        set => SetAndNotify(ref _promptTemplate, value);
    }

    public string WorkingDirectory
    {
        get => _workingDirectory;
        set => SetAndNotify(ref _workingDirectory, value);
    }

    public string EnvironmentVariables
    {
        get => _environmentVariables;
        set => SetAndNotify(ref _environmentVariables, value);
    }

    public int TimeoutSeconds
    {
        get => _timeoutSeconds;
        set => SetAndNotify(ref _timeoutSeconds, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaisePropertyChanged(nameof(CanSave));
                NotifyCommandStates();
            }
        }
    }

    public bool CanSave =>
        !IsBusy &&
        SelectedAgent is not null &&
        WakeIntervalMinutes >= 0 &&
        TimeoutSeconds >= 10 &&
        !string.IsNullOrWhiteSpace(CommandTemplate) &&
        !string.IsNullOrWhiteSpace(PromptTemplate) &&
        !string.IsNullOrWhiteSpace(WorkingDirectory) &&
        WakeConditions.All(x => x.IsValid);

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string McpInstallCommand => "npx -y @phidiassj/aiyoperps-mcp-installer";

    public void SetWorkingDirectory(string path)
    {
        WorkingDirectory = path;
    }

    public new void NotifyLocalizationChanged()
    {
        base.NotifyLocalizationChanged();
        foreach (var row in WakeConditions)
        {
            row.NotifyLocalizationChanged();
        }

        RaisePropertyChanged(nameof(WakeMetricOptions));
        RaisePropertyChanged(nameof(WakeComparisonOptions));
        RaisePropertyChanged(nameof(WakeAccountOptions));
    }

    private void Load(AIAgentSettings settings)
    {
        _isEnabled = settings.IsEnabled;
        _selectedAgent = AgentOptions.FirstOrDefault(x => x.AgentType == settings.AgentType) ?? AgentOptions[0];
        _wakeIntervalMinutes = settings.WakeIntervalMinutes;
        _commandTemplate = settings.CommandTemplate;
        _promptTemplate = settings.PromptTemplate;
        _workingDirectory = settings.WorkingDirectory;
        _environmentVariables = settings.EnvironmentVariables;
        _timeoutSeconds = settings.TimeoutSeconds;
        WakeConditions.Clear();
        foreach (var condition in settings.WakeConditions ?? [])
        {
            WakeConditions.Add(CreateConditionItem(condition));
        }

        RaisePropertyChanged(nameof(IsEnabled));
        RaisePropertyChanged(nameof(SelectedAgent));
        RaisePropertyChanged(nameof(WakeIntervalMinutes));
        RaisePropertyChanged(nameof(CommandTemplate));
        RaisePropertyChanged(nameof(PromptTemplate));
        RaisePropertyChanged(nameof(WorkingDirectory));
        RaisePropertyChanged(nameof(EnvironmentVariables));
        RaisePropertyChanged(nameof(TimeoutSeconds));
        RaisePropertyChanged(nameof(CanSave));
        NotifyCommandStates();
    }

    private void ApplyAgentDefaults()
    {
        if (SelectedAgent is null)
        {
            return;
        }

        var defaults = AIAgentProfileCatalog.CreateDefault(SelectedAgent.AgentType);
        if (string.IsNullOrWhiteSpace(CommandTemplate) || AgentOptions.Any(x => x.AgentType != SelectedAgent.AgentType && string.Equals(CommandTemplate, AIAgentProfileCatalog.CreateDefault(x.AgentType).CommandTemplate, StringComparison.Ordinal)))
        {
            CommandTemplate = defaults.CommandTemplate;
        }

        if (string.IsNullOrWhiteSpace(PromptTemplate) || AgentOptions.Any(x => x.AgentType != SelectedAgent.AgentType && string.Equals(PromptTemplate, AIAgentProfileCatalog.CreateDefault(x.AgentType).PromptTemplate, StringComparison.Ordinal)))
        {
            PromptTemplate = defaults.PromptTemplate;
        }

        if (string.IsNullOrWhiteSpace(WorkingDirectory))
        {
            WorkingDirectory = defaults.WorkingDirectory;
        }
    }

    private void ResetTemplates()
    {
        if (SelectedAgent is null)
        {
            return;
        }

        var defaults = AIAgentProfileCatalog.CreateDefault(SelectedAgent.AgentType);
        CommandTemplate = defaults.CommandTemplate;
        PromptTemplate = defaults.PromptTemplate;
        WorkingDirectory = defaults.WorkingDirectory;
        TimeoutSeconds = defaults.TimeoutSeconds;
        WakeIntervalMinutes = defaults.WakeIntervalMinutes;
        StatusMessage = string.Empty;
    }

    private void AddWakeCondition()
    {
        var defaultAccount = WakeAccountOptions.FirstOrDefault();
        WakeConditions.Add(CreateConditionItem(AIAgentWakeCondition.CreateDefault() with
        {
            AccountId = defaultAccount?.AccountId
        }));
        RaisePropertyChanged(nameof(CanSave));
    }

    private void DeleteWakeCondition(object? parameter)
    {
        if (parameter is not AIAgentWakeConditionItemViewModel item)
        {
            return;
        }

        WakeConditions.Remove(item);
        RaisePropertyChanged(nameof(CanSave));
    }

    private void Save()
    {
        try
        {
            _service.SaveSettings(BuildSettings());
            StatusMessage = L["Agent_SettingsSaved"];
            _toastService.ShowInfo(L["Agent_SettingsSaved"]);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            _toastService.ShowError(ex.Message);
            _logger.Error("AIAgentSettings", "Save settings failed", ex);
        }
    }

    private async Task TestRunAsync()
    {
        try
        {
            IsBusy = true;
            StatusMessage = L["Agent_TestRunStarted"];
            var result = await _service.TestRunAsync(BuildSettings());
            StatusMessage = string.Format(L["Agent_TestRunCompleted"], result.Status);
            _toastService.ShowInfo(StatusMessage);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            _toastService.ShowError(ex.Message);
            _logger.Error("AIAgentSettings", "Test run failed", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private AIAgentSettings BuildSettings()
    {
        return new AIAgentSettings(
            IsEnabled,
            SelectedAgent?.AgentType ?? "custom",
            WakeIntervalMinutes,
            CommandTemplate,
            PromptTemplate,
            WorkingDirectory,
            EnvironmentVariables,
            TimeoutSeconds,
            WakeConditions.Select(x => x.ToModel()).ToArray());
    }

    private void SetAndNotify<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (SetProperty(ref field, value, propertyName))
        {
            RaisePropertyChanged(nameof(CanSave));
            NotifyCommandStates();
        }
    }

    private void NotifyCommandStates()
    {
        (SaveCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (ResetTemplateCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (TestRunCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (AddWakeConditionCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (DeleteWakeConditionCommand as RelayCommand)?.NotifyCanExecuteChanged();
    }

    private void RefreshAccountOptions()
    {
        WakeAccountOptions.Clear();
        WakeAccountOptions.Add(new AIAgentAccountOption(null, () => L["Agent_WakeConditionAllAccounts"]));
        foreach (var account in _trading.ListAccounts().Where(x => x.IsEnabled).OrderBy(x => x.DisplayName, StringComparer.CurrentCulture))
        {
            WakeAccountOptions.Add(new AIAgentAccountOption(
                account.AccountId,
                () => $"{account.DisplayName} ({account.VenueId})"));
        }
    }

    internal IReadOnlyList<DashboardSymbolOptionDto> GetSymbolOptions(Guid? accountId)
    {
        var selectedAccountIds = accountId is Guid singleAccountId
            ? [singleAccountId]
            : _trading.ListAccounts()
                .Where(x => x.IsEnabled)
                .Select(x => x.AccountId)
                .ToArray();

        return _dashboardService.GetAvailableSymbolOptions(new DashboardConfiguration(
            selectedAccountIds,
            null,
            "5m",
            true));
    }

    private AIAgentWakeConditionItemViewModel CreateConditionItem(AIAgentWakeCondition condition)
    {
        var item = new AIAgentWakeConditionItemViewModel(this, condition);
        item.PropertyChanged += (_, _) =>
        {
            RaisePropertyChanged(nameof(CanSave));
            NotifyCommandStates();
        };

        return item;
    }
}

public sealed class AIAgentWakeConditionItemViewModel : ViewModelBase
{
    private readonly AIAgentSettingViewModel _owner;
    private readonly string _conditionId;
    private bool _isEnabled;
    private AIAgentAccountOption? _selectedAccount;
    private DashboardSymbolOptionDto? _selectedSymbolOption;
    private NamedOption? _selectedMetric;
    private NamedOption? _selectedComparison;
    private decimal _threshold;

    public AIAgentWakeConditionItemViewModel(AIAgentSettingViewModel owner, AIAgentWakeCondition condition)
    {
        _owner = owner;
        _conditionId = string.IsNullOrWhiteSpace(condition.ConditionId) ? Guid.NewGuid().ToString("N") : condition.ConditionId;
        _isEnabled = condition.IsEnabled;
        _threshold = condition.Threshold;
        DeleteCommand = new RelayCommand(_ => _owner.DeleteWakeConditionCommand.Execute(this));
        _selectedMetric = owner.WakeMetricOptions.FirstOrDefault(x => string.Equals(x.Value, AIAgentWakeMetric.Normalize(condition.Metric), StringComparison.Ordinal))
            ?? owner.WakeMetricOptions[0];
        _selectedComparison = owner.WakeComparisonOptions.FirstOrDefault(x => string.Equals(x.Value, AIAgentWakeComparison.Normalize(condition.Comparison), StringComparison.Ordinal))
            ?? owner.WakeComparisonOptions[0];
        _selectedAccount = owner.WakeAccountOptions.FirstOrDefault(x => x.AccountId == condition.AccountId)
            ?? owner.WakeAccountOptions.FirstOrDefault();
        RefreshSymbolOptions(condition.Symbol);
    }

    public ObservableCollection<AIAgentAccountOption> AccountOptions => _owner.WakeAccountOptions;

    public ObservableCollection<DashboardSymbolOptionDto> SymbolOptions { get; } = [];

    public NamedOption[] MetricOptions => _owner.WakeMetricOptions;

    public NamedOption[] ComparisonOptions => _owner.WakeComparisonOptions;

    public ICommand DeleteCommand { get; }

    public string DeleteLabel => L["Agent_Delete"];

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    public AIAgentAccountOption? SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            if (!SetProperty(ref _selectedAccount, value))
            {
                return;
            }

            RefreshSymbolOptions(SelectedSymbol);
        }
    }

    public DashboardSymbolOptionDto? SelectedSymbolOption
    {
        get => _selectedSymbolOption;
        set
        {
            if (SetProperty(ref _selectedSymbolOption, value))
            {
                RaisePropertyChanged(nameof(IsValid));
                RaisePropertyChanged(nameof(SelectedSymbol));
            }
        }
    }

    public string SelectedSymbol => SelectedSymbolOption?.Value ?? string.Empty;

    public NamedOption? SelectedMetric
    {
        get => _selectedMetric;
        set => SetProperty(ref _selectedMetric, value);
    }

    public NamedOption? SelectedComparison
    {
        get => _selectedComparison;
        set => SetProperty(ref _selectedComparison, value);
    }

    public decimal Threshold
    {
        get => _threshold;
        set => SetProperty(ref _threshold, value);
    }

    public bool IsValid => !string.IsNullOrWhiteSpace(SelectedSymbol) && SelectedMetric is not null && SelectedComparison is not null;

    public string Symbol => SelectedSymbol;

    public AIAgentWakeCondition ToModel()
    {
        return new AIAgentWakeCondition(
            _conditionId,
            IsEnabled,
            SelectedAccount?.AccountId,
            SelectedSymbol.Trim(),
            SelectedMetric?.Value ?? AIAgentWakeMetric.Price,
            SelectedComparison?.Value ?? AIAgentWakeComparison.GreaterThan,
            Threshold);
    }

    private void RefreshSymbolOptions(string? currentSymbol)
    {
        var options = _owner.GetSymbolOptions(SelectedAccount?.AccountId);
        SymbolOptions.Clear();
        foreach (var option in options)
        {
            SymbolOptions.Add(option);
        }

        var normalizedCurrent = currentSymbol?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(normalizedCurrent) &&
            !SymbolOptions.Any(x => string.Equals(x.Value, normalizedCurrent, StringComparison.OrdinalIgnoreCase)))
        {
            SymbolOptions.Insert(0, new DashboardSymbolOptionDto(normalizedCurrent, normalizedCurrent));
        }

        SelectedSymbolOption = SymbolOptions.FirstOrDefault(x => string.Equals(x.Value, normalizedCurrent, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed record NamedOption(string Value, Func<string> DisplayNameFactory)
{
    public string DisplayName => DisplayNameFactory();
}

public sealed record AIAgentAccountOption(Guid? AccountId, Func<string> DisplayNameFactory)
{
    public string DisplayName => DisplayNameFactory();
}
