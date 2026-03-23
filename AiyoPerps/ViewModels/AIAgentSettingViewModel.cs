using AiyoPerps.Services;
using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;

namespace AiyoPerps.ViewModels;

public sealed class AIAgentSettingViewModel : ViewModelBase
{
    private readonly AIAgentExecutionService _service;
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

    public AIAgentSettingViewModel(AIAgentExecutionService service, ToastService toastService, AppLogger logger)
    {
        _service = service;
        _toastService = toastService;
        _logger = logger;

        AgentOptions =
        [
            new AIAgentProfileOption("codex", "Codex"),
            new AIAgentProfileOption("claude-code", "Claude Code"),
            new AIAgentProfileOption("gemini-cli", "Gemini CLI"),
            new AIAgentProfileOption("custom", "Custom")
        ];

        SaveCommand = new RelayCommand(_ => Save(), _ => CanSave);
        ResetTemplateCommand = new RelayCommand(_ => ResetTemplates(), _ => SelectedAgent is not null && !IsBusy);
        TestRunCommand = new RelayCommand(_ => _ = TestRunAsync(), _ => CanSave && !IsBusy);

        Load(_service.GetSettings());
    }

    public AIAgentProfileOption[] AgentOptions { get; }

    public ICommand SaveCommand { get; }

    public ICommand ResetTemplateCommand { get; }

    public ICommand TestRunCommand { get; }

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
        WakeIntervalMinutes > 0 &&
        TimeoutSeconds >= 10 &&
        !string.IsNullOrWhiteSpace(CommandTemplate) &&
        !string.IsNullOrWhiteSpace(PromptTemplate) &&
        !string.IsNullOrWhiteSpace(WorkingDirectory);

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
            TimeoutSeconds);
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
    }
}
