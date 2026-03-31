using AiyoPerps.Services.Api;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AiyoPerps.Services;

public sealed class AIAgentExecutionService : IAsyncDisposable
{
    public const string StatusRunning = "Running";
    public const string StatusSuccess = "Success";
    public const string StatusFailed = "Failed";
    public const string StatusTimeout = "Timeout";
    public const string StatusCanceled = "Canceled";

    private static readonly TimeSpan DefaultConditionPollInterval = TimeSpan.FromSeconds(15);
    private static readonly IReadOnlyDictionary<string, bool> EmptyConditionState = new Dictionary<string, bool>();

    private readonly UserPreferenceRepository _preferences;
    private readonly AIAgentRunRepository _runRepository;
    private readonly HttpApiStateService _httpApiStateService;
    private readonly TradingApiService? _trading;
    private readonly AppLogger _logger;
    private readonly Func<IReadOnlyList<AIAgentWakeCondition>, CancellationToken, Task<IReadOnlyDictionary<string, bool>>> _wakeConditionEvaluator;
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly SemaphoreSlim _schedulerSignal = new(0, int.MaxValue);
    private readonly object _sync = new();

    private CancellationTokenSource? _schedulerCts;
    private Task? _schedulerTask;
    private bool _started;
    private bool _disposed;
    private bool _pendingConditionRun;
    private bool _pendingPostRunConditionCheck;
    private AIAgentSettings _settings;
    private AIAgentRunRecord? _currentRun;
    private AIAgentRunRecord? _lastRun;

    public AIAgentExecutionService(
        UserPreferenceRepository preferences,
        AIAgentRunRepository runRepository,
        HttpApiStateService httpApiStateService,
        TradingApiService? trading,
        AppLogger logger,
        Func<IReadOnlyList<AIAgentWakeCondition>, CancellationToken, Task<IReadOnlyDictionary<string, bool>>>? wakeConditionEvaluator = null)
    {
        _preferences = preferences;
        _runRepository = runRepository;
        _httpApiStateService = httpApiStateService;
        _trading = trading;
        _logger = logger;
        _settings = NormalizeSettings(_preferences.GetAIAgentSettingsOrDefault());
        _lastRun = _runRepository.ListRecent(1).FirstOrDefault();
        _wakeConditionEvaluator = wakeConditionEvaluator ?? EvaluateWakeConditionsCoreAsync;
        _httpApiStateService.StateChanged += OnHttpApiStateChanged;
    }

    public event Action? StateChanged;

    public bool IsRunning => _currentRun is not null && string.Equals(_currentRun.Status, StatusRunning, StringComparison.OrdinalIgnoreCase);

    public bool IsBlockedByHttpApiInitialization => _httpApiStateService.IsInitializing;

    public bool CanExecuteNow => !IsRunning && !IsBlockedByHttpApiInitialization;

    public AIAgentRunRecord? CurrentRun => _currentRun;

    public AIAgentRunRecord? LastRun => _lastRun;

    public AIAgentSettings GetSettings() => _settings;

    public IReadOnlyList<AIAgentRunRecord> GetRecentRuns(int count = 200) => _runRepository.ListRecent(count);

    public AIAgentRunRecord? GetRun(string runId) => _runRepository.Find(runId);

    public void DeleteRun(string runId)
    {
        ThrowIfDisposed();
        _runRepository.Delete(runId);
        _lastRun = _runRepository.ListRecent(1).FirstOrDefault();
        RaiseStateChanged();
    }

    public void ClearRunHistory()
    {
        ThrowIfDisposed();
        _runRepository.Clear();
        _lastRun = null;
        RaiseStateChanged();
    }

    public void Start()
    {
        lock (_sync)
        {
            if (_started)
            {
                return;
            }

            _started = true;
            _pendingConditionRun = false;
            _pendingPostRunConditionCheck = false;
            _runRepository.MarkDanglingRunsAsFailed("A previous AI agent run was interrupted before completion.");
            _lastRun = _runRepository.ListRecent(1).FirstOrDefault();
            RestartSchedulerUnsafe();
        }

        RaiseStateChanged();
    }

    public void SaveSettings(AIAgentSettings settings)
    {
        ThrowIfDisposed();
        var normalized = NormalizeSettings(settings);
        _preferences.SaveAIAgentSettings(normalized);
        _settings = normalized;
        lock (_sync)
        {
            _pendingConditionRun = false;
            _pendingPostRunConditionCheck = false;
            if (_started)
            {
                RestartSchedulerUnsafe();
            }
        }

        RaiseStateChanged();
    }

    public Task<AIAgentRunRecord> RunNowAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return ExecuteAsync(_settings, "manual", cancellationToken);
    }

    public Task<AIAgentRunRecord> TestRunAsync(AIAgentSettings settings, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return ExecuteAsync(settings, "test", cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancellationTokenSource? schedulerCts;
        Task? schedulerTask;
        lock (_sync)
        {
            schedulerCts = _schedulerCts;
            schedulerTask = _schedulerTask;
            _schedulerCts = null;
            _schedulerTask = null;
        }

        if (schedulerCts is not null)
        {
            schedulerCts.Cancel();
            schedulerCts.Dispose();
        }

        if (schedulerTask is not null)
        {
            try
            {
                await schedulerTask;
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }

        _httpApiStateService.StateChanged -= OnHttpApiStateChanged;
        _runGate.Dispose();
        _schedulerSignal.Dispose();
    }

    private void RestartSchedulerUnsafe()
    {
        _schedulerCts?.Cancel();
        _schedulerCts?.Dispose();
        _schedulerTask = null;
        _schedulerCts = null;

        if (!_settings.IsEnabled || !CanRun(_settings) || !HasAutoWakeSource(_settings))
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _schedulerCts = cts;
        _schedulerTask = Task.Run(() => SchedulerLoopAsync(cts.Token), cts.Token);
    }

    private async Task SchedulerLoopAsync(CancellationToken cancellationToken)
    {
        var lastConditionStates = EmptyConditionState;
        var nextIntervalDueAt = GetNextIntervalDueAt(_settings, DateTimeOffset.UtcNow);
        var wasRunning = IsRunning;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var delay = GetSchedulerDelay(_settings, nextIntervalDueAt);
                await WaitForSchedulerSignalAsync(delay, cancellationToken);

                var settings = _settings;
                var isRunning = IsRunning;
                var justCompletedRun = ConsumePostRunConditionCheckPending(isRunning) || (wasRunning && !isRunning);
                if (justCompletedRun)
                {
                    nextIntervalDueAt = GetNextIntervalDueAt(settings, DateTimeOffset.UtcNow);
                }

                wasRunning = isRunning;

                var triggeredCondition = false;
                var enabledConditions = GetEnabledWakeConditions(settings);
                if (enabledConditions.Count > 0)
                {
                    var currentStates = await _wakeConditionEvaluator(enabledConditions, cancellationToken);
                    triggeredCondition = TryDetectConditionEdge(enabledConditions, lastConditionStates, currentStates);
                    var anyConditionMet = AnyConditionMet(currentStates);
                    lastConditionStates = currentStates;

                    if (triggeredCondition && isRunning)
                    {
                        lock (_sync)
                        {
                            _pendingConditionRun = true;
                        }

                        triggeredCondition = false;
                    }

                    if (!isRunning && justCompletedRun && anyConditionMet)
                    {
                        triggeredCondition = true;
                    }
                }
                else
                {
                    lastConditionStates = EmptyConditionState;
                }

                if (!isRunning)
                {
                    lock (_sync)
                    {
                        if (_pendingConditionRun)
                        {
                            _pendingConditionRun = false;
                            triggeredCondition = true;
                        }
                    }
                }

                if (triggeredCondition)
                {
                    try
                    {
                        await ExecuteAsync(settings, "condition", cancellationToken);
                        nextIntervalDueAt = GetNextIntervalDueAt(_settings, DateTimeOffset.UtcNow);
                        wasRunning = IsRunning;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.Error("AIAgent", "Condition-triggered run failed", ex);
                    }

                    continue;
                }

                if (!IsIntervalEnabled(settings) || isRunning || nextIntervalDueAt is null || DateTimeOffset.UtcNow < nextIntervalDueAt.Value)
                {
                    continue;
                }

                try
                {
                    await ExecuteAsync(settings, "scheduled", cancellationToken);
                    nextIntervalDueAt = GetNextIntervalDueAt(_settings, DateTimeOffset.UtcNow);
                    wasRunning = IsRunning;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.Error("AIAgent", "Scheduled run failed", ex);
                    nextIntervalDueAt = GetNextIntervalDueAt(_settings, DateTimeOffset.UtcNow);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown or settings update.
        }
    }

    private static TimeSpan GetSchedulerDelay(AIAgentSettings settings, DateTimeOffset? nextIntervalDueAt)
    {
        var delay = DefaultConditionPollInterval;
        if (IsIntervalEnabled(settings) && nextIntervalDueAt is not null)
        {
            var untilInterval = nextIntervalDueAt.Value - DateTimeOffset.UtcNow;
            if (untilInterval <= TimeSpan.Zero)
            {
                return TimeSpan.FromSeconds(1);
            }

            if (untilInterval < delay)
            {
                delay = untilInterval;
            }
        }

        if (delay < TimeSpan.FromSeconds(1))
        {
            return TimeSpan.FromSeconds(1);
        }

        return delay;
    }

    private static bool IsIntervalEnabled(AIAgentSettings settings) => settings.WakeIntervalMinutes > 0;

    private static DateTimeOffset? GetNextIntervalDueAt(AIAgentSettings settings, DateTimeOffset fromTime)
        => IsIntervalEnabled(settings) ? fromTime.AddMinutes(settings.WakeIntervalMinutes) : null;

    private static IReadOnlyList<AIAgentWakeCondition> GetEnabledWakeConditions(AIAgentSettings settings)
        => settings.WakeConditions?
            .Where(x => x.IsEnabled && !string.IsNullOrWhiteSpace(x.Symbol))
            .ToArray()
           ?? [];

    private static bool HasAutoWakeSource(AIAgentSettings settings)
        => IsIntervalEnabled(settings) || GetEnabledWakeConditions(settings).Count > 0;

    private static bool TryDetectConditionEdge(
        IReadOnlyList<AIAgentWakeCondition> conditions,
        IReadOnlyDictionary<string, bool> previousStates,
        IReadOnlyDictionary<string, bool> currentStates)
    {
        foreach (var condition in conditions)
        {
            var conditionId = condition.ConditionId;
            var isMet = currentStates.TryGetValue(conditionId, out var currentMet) && currentMet;
            var wasMet = previousStates.TryGetValue(conditionId, out var previousMet) && previousMet;
            if (isMet && !wasMet)
            {
                return true;
            }
        }

        return false;
    }

    private static bool AnyConditionMet(IReadOnlyDictionary<string, bool> states)
        => states.Values.Any(x => x);

    private async Task<AIAgentRunRecord> ExecuteAsync(AIAgentSettings settings, string trigger, CancellationToken cancellationToken)
    {
        var normalizedSettings = NormalizeSettings(settings);
        ValidateSettings(normalizedSettings);
        await WaitForHttpApiInitializationAsync(trigger, cancellationToken);

        if (!await TryEnterRunGateAsync(trigger, cancellationToken))
        {
            return GetSkippedRunFallback();
        }

        try
        {
            var runId = Guid.NewGuid().ToString("N");
            var startedAt = DateTimeOffset.UtcNow;
            var workingDirectory = ResolveWorkingDirectory(normalizedSettings);
            var renderedPrompt = RenderPrompt(normalizedSettings);
            var promptFile = WritePromptFile(runId, renderedPrompt);
            var renderedCommand = RenderCommand(normalizedSettings.CommandTemplate, normalizedSettings, promptFile, startedAt);
            var runningRecord = new AIAgentRunRecord(
                runId,
                startedAt,
                null,
                normalizedSettings.AgentType,
                StatusRunning,
                null,
                workingDirectory,
                renderedCommand,
                renderedPrompt,
                string.Empty,
                string.Empty);

            _currentRun = runningRecord;
            _runRepository.Upsert(runningRecord);
            RaiseStateChanged();

            var completedRecord = await RunProcessAsync(runningRecord, normalizedSettings, trigger, promptFile, cancellationToken);
            _lastRun = completedRecord;
            _currentRun = null;
            _runRepository.Upsert(completedRecord);
            lock (_sync)
            {
                _pendingPostRunConditionCheck = true;
            }
            RaiseStateChanged();
            SignalScheduler();
            return completedRecord;
        }
        finally
        {
            _runGate.Release();
        }
    }

    private async Task<bool> TryEnterRunGateAsync(string trigger, CancellationToken cancellationToken)
    {
        if (await _runGate.WaitAsync(0, cancellationToken))
        {
            return true;
        }

        if (string.Equals(trigger, "scheduled", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trigger, "condition", StringComparison.OrdinalIgnoreCase))
        {
            _logger.Info("AIAgent", $"Auto run skipped because another AI agent run is already in progress. trigger={trigger}");
            return false;
        }

        throw new InvalidOperationException("An AI agent run is already in progress.");
    }

    private AIAgentRunRecord GetSkippedRunFallback()
    {
        return _currentRun
            ?? _lastRun
            ?? throw new InvalidOperationException("No AI agent run state is available for a skipped execution.");
    }

    private async Task<AIAgentRunRecord> RunProcessAsync(
        AIAgentRunRecord runningRecord,
        AIAgentSettings settings,
        string trigger,
        string promptFile,
        CancellationToken cancellationToken)
    {
        Process? process = null;
        string stdout = string.Empty;
        string stderr = string.Empty;
        int? exitCode = null;
        var status = StatusFailed;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                WorkingDirectory = runningRecord.WorkingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(PreparePowerShellCommand(runningRecord.RenderedCommand));
            ApplyEnvironmentVariables(startInfo, settings.EnvironmentVariables, runningRecord.WorkingDirectory);

            process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            if (!process.Start())
            {
                throw new InvalidOperationException("Failed to start AI agent process.");
            }

            _logger.Info("AIAgent", $"Run started trigger={trigger}, agent={settings.AgentType}, runId={runningRecord.RunId}");

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(10, settings.TimeoutSeconds)));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(linkedCts.Token);
                stdout = await stdoutTask;
                stderr = await stderrTask;
                exitCode = process.ExitCode;
                status = process.ExitCode == 0 ? StatusSuccess : StatusFailed;
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                status = StatusTimeout;
                stderr = await ReadAndKillProcessAsync(process, stderrTask, "The AI agent process timed out.");
                stdout = await SafeReadAsync(stdoutTask);
            }
            catch (OperationCanceledException)
            {
                status = StatusCanceled;
                stderr = await ReadAndKillProcessAsync(process, stderrTask, "The AI agent process was canceled.");
                stdout = await SafeReadAsync(stdoutTask);
            }
        }
        catch (Exception ex)
        {
            stderr = string.IsNullOrWhiteSpace(stderr) ? ex.Message : $"{stderr}{Environment.NewLine}{ex.Message}";
            status = StatusFailed;
            _logger.Error("AIAgent", $"Run failed runId={runningRecord.RunId}", ex);
        }
        finally
        {
            try
            {
                if (File.Exists(promptFile))
                {
                    File.Delete(promptFile);
                }
            }
            catch (Exception ex)
            {
                _logger.Warn("AIAgent", $"Prompt file cleanup warning runId={runningRecord.RunId}: {ex.Message}");
            }

            process?.Dispose();
        }

        var finishedAt = DateTimeOffset.UtcNow;
        return runningRecord with
        {
            FinishedAt = finishedAt,
            Status = status,
            ExitCode = exitCode,
            Stdout = stdout,
            Stderr = stderr
        };
    }

    private async Task<IReadOnlyDictionary<string, bool>> EvaluateWakeConditionsCoreAsync(
        IReadOnlyList<AIAgentWakeCondition> conditions,
        CancellationToken cancellationToken)
    {
        if (_trading is null || conditions.Count == 0)
        {
            return EmptyConditionState;
        }

        var enabledAccounts = _trading.ListAccounts()
            .Where(x => x.IsEnabled)
            .ToArray();
        if (enabledAccounts.Length == 0)
        {
            return EmptyConditionState;
        }

        var requiredAccountIds = new HashSet<Guid>(conditions.Where(x => x.AccountId is not null).Select(x => x.AccountId!.Value));
        var conditionTargetsAllAccounts = conditions.Any(x => x.AccountId is null);
        var targetAccounts = enabledAccounts
            .Where(x => conditionTargetsAllAccounts || requiredAccountIds.Contains(x.AccountId))
            .ToArray();

        var positionsByAccount = new Dictionary<Guid, IReadOnlyList<ApiPositionDto>>();
        foreach (var account in targetAccounts)
        {
            positionsByAccount[account.AccountId] = await _trading.ListPositionsAsync(
                account.AccountId,
                symbol: null,
                cancellationToken,
                notifyLifecycleEvents: false);
        }

        var states = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var condition in conditions)
        {
            var accounts = condition.AccountId is Guid accountId
                ? targetAccounts.Where(x => x.AccountId == accountId)
                : targetAccounts;

            var isMet = false;
            foreach (var account in accounts)
            {
                if (!positionsByAccount.TryGetValue(account.AccountId, out var positions))
                {
                    continue;
                }

                if (positions.Any(position => PositionMatchesCondition(account, position, condition)))
                {
                    isMet = true;
                    break;
                }
            }

            states[condition.ConditionId] = isMet;
        }

        return states;
    }

    private static bool PositionMatchesCondition(ApiAccountDto account, ApiPositionDto position, AIAgentWakeCondition condition)
    {
        if (!MatchesConditionSymbol(account.VenueId, position.Symbol, condition.Symbol))
        {
            return false;
        }

        var metric = AIAgentWakeMetric.Normalize(condition.Metric);
        var comparison = AIAgentWakeComparison.Normalize(condition.Comparison);
        var value = metric switch
        {
            AIAgentWakeMetric.UnrealizedPnlPct => position.UnrealizedPnlPct,
            _ => position.MarkPrice
        };

        return comparison == AIAgentWakeComparison.LessThan
            ? value < condition.Threshold
            : value > condition.Threshold;
    }

    private static bool MatchesConditionSymbol(string venueId, string positionSymbol, string targetSymbol)
    {
        var normalizedTarget = NormalizeConditionSymbol(targetSymbol);
        if (string.IsNullOrWhiteSpace(normalizedTarget))
        {
            return false;
        }

        var normalizedRaw = NormalizeConditionSymbol(positionSymbol);
        if (string.Equals(normalizedTarget, normalizedRaw, StringComparison.Ordinal))
        {
            return true;
        }

        var formatted = NormalizeConditionSymbol(SymbolCanonicalizer.Format(venueId, positionSymbol));
        return string.Equals(normalizedTarget, formatted, StringComparison.Ordinal);
    }

    private static string NormalizeConditionSymbol(string? value)
        => (value ?? string.Empty).Trim().Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();

    private async Task WaitForSchedulerSignalAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        var delayTask = Task.Delay(delay, cancellationToken);
        var signalTask = _schedulerSignal.WaitAsync(cancellationToken);
        var completedTask = await Task.WhenAny(delayTask, signalTask);
        await completedTask;
    }

    private void SignalScheduler()
    {
        try
        {
            _schedulerSignal.Release();
        }
        catch (ObjectDisposedException)
        {
            // Ignore shutdown races.
        }
    }

    private bool ConsumePostRunConditionCheckPending(bool isRunning)
    {
        if (isRunning)
        {
            return false;
        }

        lock (_sync)
        {
            if (!_pendingPostRunConditionCheck)
            {
                return false;
            }

            _pendingPostRunConditionCheck = false;
            return true;
        }
    }

    private static async Task<string> ReadAndKillProcessAsync(Process process, Task<string> stderrTask, string fallbackMessage)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Ignore cleanup errors.
        }

        var stderr = await SafeReadAsync(stderrTask);
        return string.IsNullOrWhiteSpace(stderr) ? fallbackMessage : stderr;
    }

    private static async Task<string> SafeReadAsync(Task<string> task)
    {
        try
        {
            return await task;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void ApplyEnvironmentVariables(ProcessStartInfo startInfo, string raw, string workingDirectory)
    {
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            startInfo.Environment["AIYOPERPS_WORKDIR"] = workingDirectory;
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        foreach (var line in raw
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            startInfo.Environment[key] = value;
        }
    }

    private static bool CanRun(AIAgentSettings settings)
    {
        return !string.IsNullOrWhiteSpace(settings.CommandTemplate)
            && !string.IsNullOrWhiteSpace(settings.PromptTemplate);
    }

    private static void ValidateSettings(AIAgentSettings settings)
    {
        if (!CanRun(settings))
        {
            throw new InvalidOperationException("AI Agent settings are incomplete.");
        }

        if (settings.TimeoutSeconds < 10)
        {
            throw new InvalidOperationException("Timeout must be at least 10 seconds.");
        }

        var workingDirectory = ResolveWorkingDirectory(settings);
        if (!Directory.Exists(workingDirectory))
        {
            throw new DirectoryNotFoundException($"Working directory not found: {workingDirectory}");
        }
    }

    private static AIAgentSettings NormalizeSettings(AIAgentSettings settings)
    {
        var normalizedAgentType = AIAgentProfileCatalog.Normalize(settings.AgentType);
        var defaultSettings = AIAgentProfileCatalog.CreateDefault(normalizedAgentType);
        var normalizedWorkingDirectory = NormalizeWorkingDirectory(settings.WorkingDirectory);
        var normalizedCommandTemplate = NormalizeCommandTemplate(normalizedAgentType, settings.CommandTemplate);
        var normalizedConditions = (settings.WakeConditions ?? [])
            .Select(NormalizeWakeCondition)
            .Where(x => !string.IsNullOrWhiteSpace(x.Symbol))
            .ToArray();

        return settings with
        {
            AgentType = normalizedAgentType,
            WakeIntervalMinutes = settings.WakeIntervalMinutes < 0 ? defaultSettings.WakeIntervalMinutes : settings.WakeIntervalMinutes,
            CommandTemplate = normalizedCommandTemplate,
            PromptTemplate = settings.PromptTemplate?.Trim() ?? string.Empty,
            WorkingDirectory = normalizedWorkingDirectory,
            EnvironmentVariables = settings.EnvironmentVariables ?? string.Empty,
            TimeoutSeconds = settings.TimeoutSeconds < 10 ? defaultSettings.TimeoutSeconds : settings.TimeoutSeconds,
            WakeConditions = normalizedConditions
        };
    }

    private static AIAgentWakeCondition NormalizeWakeCondition(AIAgentWakeCondition condition)
    {
        var conditionId = string.IsNullOrWhiteSpace(condition.ConditionId)
            ? Guid.NewGuid().ToString("N")
            : condition.ConditionId.Trim();

        return condition with
        {
            ConditionId = conditionId,
            Symbol = condition.Symbol?.Trim() ?? string.Empty,
            Metric = AIAgentWakeMetric.Normalize(condition.Metric),
            Comparison = AIAgentWakeComparison.Normalize(condition.Comparison)
        };
    }

    private static string ResolveWorkingDirectory(AIAgentSettings settings)
    {
        return NormalizeWorkingDirectory(settings.WorkingDirectory);
    }

    private static string RenderPrompt(AIAgentSettings settings)
    {
        var now = DateTimeOffset.Now;
        return settings.PromptTemplate
            .Replace("{{now}}", now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{{agent_name}}", AIAgentProfileCatalog.ToDisplayName(settings.AgentType), StringComparison.Ordinal);
    }

    private static string RenderCommand(string commandTemplate, AIAgentSettings settings, string promptFile, DateTimeOffset startedAt)
    {
        return commandTemplate
            .Replace("{{prompt_file}}", promptFile, StringComparison.Ordinal)
            .Replace("{{working_directory}}", ResolveWorkingDirectory(settings), StringComparison.Ordinal)
            .Replace("{{timestamp}}", startedAt.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    private static string WritePromptFile(string runId, string prompt)
    {
        var directory = Path.Combine(Path.GetTempPath(), "AiyoPerps", "agent-prompts");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{runId}.txt");
        File.WriteAllText(path, prompt, Encoding.UTF8);
        return path;
    }

    private static string PreparePowerShellCommand(string command)
    {
        const string utf8Preamble = "$ErrorActionPreference='Stop';[Console]::InputEncoding=[System.Text.UTF8Encoding]::new($false);[Console]::OutputEncoding=[System.Text.UTF8Encoding]::new($false);$OutputEncoding=[System.Text.UTF8Encoding]::new($false);";
        return $"{utf8Preamble} try {{ & {{ {command} }} }} catch {{ [Console]::Error.WriteLine(($_ | Out-String)); exit 1 }}";
    }

    private static string NormalizeWorkingDirectory(string? workingDirectory)
    {
        var normalizedBaseDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedWorkingDirectory = workingDirectory?.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(normalizedWorkingDirectory) ||
            string.Equals(normalizedWorkingDirectory, normalizedBaseDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return AIAgentProfileCatalog.ResolveDefaultWorkingDirectory();
        }

        return workingDirectory!.Trim();
    }

    private static string NormalizeCommandTemplate(string normalizedAgentType, string? commandTemplate)
    {
        var normalized = commandTemplate?.Trim() ?? string.Empty;
        if (string.Equals(normalizedAgentType, "codex", StringComparison.Ordinal) &&
            string.Equals(normalized, AIAgentProfileCatalog.LegacyCodexCommandTemplate, StringComparison.Ordinal))
        {
            return AIAgentProfileCatalog.DefaultCodexCommandTemplate;
        }

        return normalized;
    }

    private void RaiseStateChanged()
    {
        try
        {
            StateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.Warn("AIAgent", $"StateChanged event warning: {ex.Message}");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private async Task WaitForHttpApiInitializationAsync(string trigger, CancellationToken cancellationToken)
    {
        if (!_httpApiStateService.IsInitializing)
        {
            return;
        }

        _logger.Info("AIAgent", $"Run delayed until HTTP API becomes ready trigger={trigger}");
        await _httpApiStateService.WaitForReadyOrInitializationExitAsync(cancellationToken);
    }

    private void OnHttpApiStateChanged()
    {
        RaiseStateChanged();
        SignalScheduler();
    }
}
