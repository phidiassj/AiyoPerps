using AiyoPerps.Data;
using AiyoPerps.Services;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace AiyoPerps.Test;

public sealed class AIAgentExecutionServiceTests
{
    private static readonly object TestSync = new();

    [Fact]
    public async Task RunNowAsync_PersistsRunRecordAndCapturesStdout()
    {
        using var scope = TestScope.Create();
        var settings = new AIAgentSettings(
            true,
            "custom",
            5,
            "Get-Content \"{{prompt_file}}\"",
            "Agent={{agent_name}}\nNow={{now}}",
            AppContext.BaseDirectory,
            "AIYOPERPS_TEST_ENV=ok",
            30);

        scope.Preferences.SaveAIAgentSettings(settings);
        await using var service = new AIAgentExecutionService(scope.Preferences, scope.RunRepository, scope.HttpApiState, trading: null, scope.Logger);
        service.Start();

        var result = await service.RunNowAsync();

        Assert.Equal(AIAgentExecutionService.StatusSuccess, result.Status);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Agent=Custom", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("Now=", result.Stdout, StringComparison.Ordinal);

        var saved = scope.RunRepository.Find(result.RunId);
        Assert.NotNull(saved);
        Assert.Equal(AIAgentExecutionService.StatusSuccess, saved!.Status);

        var recent = scope.RunRepository.ListRecent(10);
        Assert.Contains(recent, x => x.RunId == result.RunId);
        Assert.Equal(result.RunId, service.LastRun?.RunId);
    }

    [Fact]
    public async Task RunNowAsync_CapturesUtf8ChineseOutput()
    {
        using var scope = TestScope.Create();
        var settings = new AIAgentSettings(
            true,
            "custom",
            5,
            "Write-Output '中文輸出'; [Console]::Error.WriteLine('中文錯誤')",
            "Agent={{agent_name}}",
            AppContext.BaseDirectory,
            string.Empty,
            30);

        await using var service = new AIAgentExecutionService(scope.Preferences, scope.RunRepository, scope.HttpApiState, trading: null, scope.Logger);
        service.SaveSettings(settings);
        service.Start();

        var result = await service.RunNowAsync();

        Assert.Equal(AIAgentExecutionService.StatusSuccess, result.Status);
        Assert.Contains("中文輸出", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("中文錯誤", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunNowAsync_AllowsZeroWakeIntervalForManualExecution()
    {
        using var scope = TestScope.Create();
        var settings = new AIAgentSettings(
            true,
            "custom",
            0,
            "Write-Output 'manual ok'",
            "Prompt",
            AppContext.BaseDirectory,
            string.Empty,
            30);

        await using var service = new AIAgentExecutionService(scope.Preferences, scope.RunRepository, scope.HttpApiState, trading: null, scope.Logger);
        service.SaveSettings(settings);
        service.Start();

        var result = await service.RunNowAsync();

        Assert.Equal(AIAgentExecutionService.StatusSuccess, result.Status);
        Assert.Contains("manual ok", result.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, service.GetSettings().WakeIntervalMinutes);
    }

    [Fact]
    public async Task SaveSettings_PersistsWakeConditions()
    {
        using var scope = TestScope.Create();
        var settings = new AIAgentSettings(
            true,
            "custom",
            0,
            "Write-Output 'ok'",
            "Prompt",
            AppContext.BaseDirectory,
            string.Empty,
            30,
            [
                new AIAgentWakeCondition("price-btc", true, null, "BTC", AIAgentWakeMetric.Price, AIAgentWakeComparison.GreaterThan, 70000m),
                new AIAgentWakeCondition("pnl-eth", true, Guid.NewGuid(), "ETH-USDT", AIAgentWakeMetric.UnrealizedPnlPct, AIAgentWakeComparison.LessThan, -5m)
            ]);

        await using var service = new AIAgentExecutionService(scope.Preferences, scope.RunRepository, scope.HttpApiState, trading: null, scope.Logger);
        service.SaveSettings(settings);

        var actual = service.GetSettings();

        Assert.Equal(2, actual.WakeConditions?.Length);
        Assert.Equal("price-btc", actual.WakeConditions![0].ConditionId);
        Assert.Equal(AIAgentWakeMetric.Price, actual.WakeConditions[0].Metric);
        Assert.Equal(AIAgentWakeComparison.GreaterThan, actual.WakeConditions[0].Comparison);
        Assert.Equal("ETH-USDT", actual.WakeConditions[1].Symbol);
    }

    [Fact]
    public async Task RunNowAsync_LegacyCodexDefaults_AreNormalized()
    {
        using var scope = TestScope.Create();
        var settings = new AIAgentSettings(
            true,
            "codex",
            5,
            AIAgentProfileCatalog.LegacyCodexCommandTemplate,
            "Prompt body",
            AppContext.BaseDirectory,
            string.Empty,
            30);

        await using var service = new AIAgentExecutionService(scope.Preferences, scope.RunRepository, scope.HttpApiState, trading: null, scope.Logger);
        service.SaveSettings(settings);
        service.Start();

        var actual = service.GetSettings();

        Assert.Equal(AIAgentProfileCatalog.DefaultCodexCommandTemplate, actual.CommandTemplate);
        Assert.False(string.Equals(
            AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            actual.WorkingDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(Path.Combine(actual.WorkingDirectory, "AiyoPerps.slnx")) || Directory.Exists(Path.Combine(actual.WorkingDirectory, ".git")));
    }

    [Fact]
    public async Task Start_MarksDanglingRunningRecordsAsFailed()
    {
        using var scope = TestScope.Create();
        scope.RunRepository.Upsert(new AIAgentRunRecord(
            "dangling-run",
            DateTimeOffset.UtcNow.AddMinutes(-2),
            null,
            "custom",
            AIAgentExecutionService.StatusRunning,
            null,
            AppContext.BaseDirectory,
            "Write-Output test",
            "Prompt",
            string.Empty,
            string.Empty));

        using var db = new AppDbContext();
        var before = db.AIAgentRuns.AsNoTracking().Single(x => x.RunId == "dangling-run");
        Assert.Equal(AIAgentExecutionService.StatusRunning, before.Status);

        await using var service = new AIAgentExecutionService(scope.Preferences, scope.RunRepository, scope.HttpApiState, trading: null, scope.Logger);
        service.Start();

        var after = scope.RunRepository.Find("dangling-run");
        Assert.NotNull(after);
        Assert.Equal(AIAgentExecutionService.StatusFailed, after!.Status);
        Assert.NotNull(after.FinishedAt);
        Assert.Contains("interrupted", after.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunNowAsync_InvalidCommand_ReturnsFailedRecord()
    {
        using var scope = TestScope.Create();
        var settings = new AIAgentSettings(
            true,
            "custom",
            5,
            "This-Command-Does-Not-Exist \"{{prompt_file}}\"",
            "Hello from failure path",
            AppContext.BaseDirectory,
            string.Empty,
            30);

        await using var service = new AIAgentExecutionService(scope.Preferences, scope.RunRepository, scope.HttpApiState, trading: null, scope.Logger);
        service.SaveSettings(settings);
        service.Start();

        var result = await service.RunNowAsync();

        Assert.Equal(AIAgentExecutionService.StatusFailed, result.Status);
        Assert.NotNull(result.FinishedAt);
        Assert.Contains("This-Command-Does-Not-Exist", result.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(scope.RunRepository.ListRecent(), x => x.RunId == result.RunId);
    }

    [Fact]
    public async Task RunProcessAsync_CancellationReason_IsRecordedInStderr()
    {
        using var scope = TestScope.Create();
        var settings = new AIAgentSettings(
            true,
            "custom",
            5,
            "Start-Sleep -Seconds 30; Write-Output 'done'",
            "Prompt",
            AppContext.BaseDirectory,
            string.Empty,
            30);

        await using var service = new AIAgentExecutionService(scope.Preferences, scope.RunRepository, scope.HttpApiState, trading: null, scope.Logger);
        var run = new AIAgentRunRecord(
            "cancel-reason-run",
            DateTimeOffset.UtcNow,
            null,
            "custom",
            AIAgentExecutionService.StatusRunning,
            null,
            AppContext.BaseDirectory,
            settings.CommandTemplate,
            settings.PromptTemplate,
            string.Empty,
            string.Empty);
        using var cts = new CancellationTokenSource();
        var promptFile = Path.Combine(Path.GetTempPath(), $"cancel-reason-{Guid.NewGuid():N}.txt");
        File.WriteAllText(promptFile, "Prompt");

        var processTask = InvokeRunProcessAsync(
            service,
            run,
            settings,
            "scheduled",
            promptFile,
            cts.Token,
            () => "settings updated");

        await Task.Delay(150);
        cts.Cancel();
        var result = await processTask;

        Assert.Equal(AIAgentExecutionService.StatusCanceled, result.Status);
        Assert.Contains("settings updated", result.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteRun_RemovesSelectedRecordAndUpdatesLastRun()
    {
        using var scope = TestScope.Create();
        scope.RunRepository.Upsert(new AIAgentRunRecord(
            "run-old",
            DateTimeOffset.UtcNow.AddMinutes(-10),
            DateTimeOffset.UtcNow.AddMinutes(-9),
            "custom",
            AIAgentExecutionService.StatusSuccess,
            0,
            AppContext.BaseDirectory,
            "Write-Output old",
            "Prompt old",
            "old",
            string.Empty));
        scope.RunRepository.Upsert(new AIAgentRunRecord(
            "run-new",
            DateTimeOffset.UtcNow.AddMinutes(-2),
            DateTimeOffset.UtcNow.AddMinutes(-1),
            "custom",
            AIAgentExecutionService.StatusSuccess,
            0,
            AppContext.BaseDirectory,
            "Write-Output new",
            "Prompt new",
            "new",
            string.Empty));

        await using var service = new AIAgentExecutionService(scope.Preferences, scope.RunRepository, scope.HttpApiState, trading: null, scope.Logger);

        service.DeleteRun("run-new");

        Assert.Null(scope.RunRepository.Find("run-new"));
        Assert.NotNull(scope.RunRepository.Find("run-old"));
        Assert.Equal("run-old", service.LastRun?.RunId);
    }

    [Fact]
    public async Task ClearRunHistory_RemovesAllRecordsAndClearsLastRun()
    {
        using var scope = TestScope.Create();
        scope.RunRepository.Upsert(new AIAgentRunRecord(
            "run-clear-a",
            DateTimeOffset.UtcNow.AddMinutes(-4),
            DateTimeOffset.UtcNow.AddMinutes(-3),
            "custom",
            AIAgentExecutionService.StatusSuccess,
            0,
            AppContext.BaseDirectory,
            "Write-Output a",
            "Prompt a",
            "a",
            string.Empty));
        scope.RunRepository.Upsert(new AIAgentRunRecord(
            "run-clear-b",
            DateTimeOffset.UtcNow.AddMinutes(-2),
            DateTimeOffset.UtcNow.AddMinutes(-1),
            "custom",
            AIAgentExecutionService.StatusFailed,
            1,
            AppContext.BaseDirectory,
            "Write-Output b",
            "Prompt b",
            "b",
            "err"));

        await using var service = new AIAgentExecutionService(scope.Preferences, scope.RunRepository, scope.HttpApiState, trading: null, scope.Logger);

        service.ClearRunHistory();

        Assert.Empty(scope.RunRepository.ListRecent());
        Assert.Null(service.LastRun);
    }

    [Fact]
    public async Task RunNowAsync_WaitsUntilHttpApiInitializationCompletes()
    {
        using var scope = TestScope.Create();
        var settings = new AIAgentSettings(
            true,
            "custom",
            5,
            "Write-Output 'ready'",
            "Prompt",
            AppContext.BaseDirectory,
            string.Empty,
            30);

        scope.HttpApiState.MarkInitializing(5078);
        await using var service = new AIAgentExecutionService(scope.Preferences, scope.RunRepository, scope.HttpApiState, trading: null, scope.Logger);
        service.SaveSettings(settings);
        service.Start();

        var runTask = service.RunNowAsync();
        await Task.Delay(150);

        Assert.False(runTask.IsCompleted);
        Assert.True(service.IsBlockedByHttpApiInitialization);

        scope.HttpApiState.MarkReady(5078);
        var result = await runTask;

        Assert.Equal(AIAgentExecutionService.StatusSuccess, result.Status);
        Assert.Contains("ready", result.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunNowAsync_ThrowsWhenAnotherRunIsAlreadyInProgress()
    {
        using var scope = TestScope.Create();
        var settings = new AIAgentSettings(
            true,
            "custom",
            5,
            "Start-Sleep -Seconds 1; Write-Output 'done'",
            "Prompt",
            AppContext.BaseDirectory,
            string.Empty,
            30);

        await using var service = new AIAgentExecutionService(scope.Preferences, scope.RunRepository, scope.HttpApiState, trading: null, scope.Logger);
        service.SaveSettings(settings);
        service.Start();

        var firstRunTask = service.RunNowAsync();
        await Task.Delay(150);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunNowAsync());
        Assert.Contains("already in progress", ex.Message, StringComparison.OrdinalIgnoreCase);

        var firstRun = await firstRunTask;
        Assert.Equal(AIAgentExecutionService.StatusSuccess, firstRun.Status);
        Assert.Single(scope.RunRepository.ListRecent());
    }

    [Fact]
    public async Task ExecuteAsync_ScheduledTriggerSkipsWhenAnotherRunIsAlreadyInProgress()
    {
        using var scope = TestScope.Create();
        var settings = new AIAgentSettings(
            true,
            "custom",
            5,
            "Start-Sleep -Seconds 1; Write-Output 'done'",
            "Prompt",
            AppContext.BaseDirectory,
            string.Empty,
            30);

        await using var service = new AIAgentExecutionService(scope.Preferences, scope.RunRepository, scope.HttpApiState, trading: null, scope.Logger);
        service.SaveSettings(settings);
        service.Start();

        var firstRunTask = service.RunNowAsync();
        await Task.Delay(150);
        var currentRun = service.CurrentRun;
        Assert.NotNull(currentRun);

        var skipped = await InvokeExecuteAsync(service, settings, "scheduled");

        Assert.Equal(currentRun!.RunId, skipped.RunId);
        Assert.Equal(AIAgentExecutionService.StatusRunning, skipped.Status);

        var completed = await firstRunTask;
        Assert.Equal(AIAgentExecutionService.StatusSuccess, completed.Status);
        Assert.Single(scope.RunRepository.ListRecent());
    }

    [Fact]
    public async Task ExecuteAsync_ConditionTriggerSkipsWhenAnotherRunIsAlreadyInProgress()
    {
        using var scope = TestScope.Create();
        var settings = new AIAgentSettings(
            true,
            "custom",
            0,
            "Start-Sleep -Seconds 1; Write-Output 'done'",
            "Prompt",
            AppContext.BaseDirectory,
            string.Empty,
            30,
            [new AIAgentWakeCondition("price-btc", true, null, "BTC", AIAgentWakeMetric.Price, AIAgentWakeComparison.GreaterThan, 70000m)]);

        await using var service = new AIAgentExecutionService(scope.Preferences, scope.RunRepository, scope.HttpApiState, trading: null, scope.Logger);
        service.SaveSettings(settings);
        service.Start();

        var firstRunTask = service.RunNowAsync();
        await Task.Delay(150);
        var currentRun = service.CurrentRun;
        Assert.NotNull(currentRun);

        var skipped = await InvokeExecuteAsync(service, settings, "condition");

        Assert.Equal(currentRun!.RunId, skipped.RunId);
        Assert.Equal(AIAgentExecutionService.StatusRunning, skipped.Status);

        var completed = await firstRunTask;
        Assert.Equal(AIAgentExecutionService.StatusSuccess, completed.Status);
        Assert.Single(scope.RunRepository.ListRecent());
    }

    [Fact]
    public async Task SchedulerLoop_ConditionStillMetAfterRun_TriggersImmediateFollowUpRun()
    {
        using var scope = TestScope.Create();
        var settings = new AIAgentSettings(
            true,
            "custom",
            0,
            "Write-Output 'done'",
            "Prompt",
            AppContext.BaseDirectory,
            string.Empty,
            30,
            [new AIAgentWakeCondition("price-btc", true, null, "BTC", AIAgentWakeMetric.Price, AIAgentWakeComparison.GreaterThan, 70000m)]);
        var evaluationCount = 0;

        static IReadOnlyDictionary<string, bool> CreateStates(bool isMet)
            => new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                ["price-btc"] = isMet
            };

        Task<IReadOnlyDictionary<string, bool>> EvaluateAsync(IReadOnlyList<AIAgentWakeCondition> conditions, CancellationToken cancellationToken)
        {
            var current = Interlocked.Increment(ref evaluationCount);
            return Task.FromResult<IReadOnlyDictionary<string, bool>>(CreateStates(current <= 2));
        }

        await using var service = new AIAgentExecutionService(
            scope.Preferences,
            scope.RunRepository,
            scope.HttpApiState,
            trading: null,
            scope.Logger,
            EvaluateAsync);
        service.SaveSettings(settings);
        service.Start();

        var manualRun = await service.RunNowAsync();

        Assert.Equal(AIAgentExecutionService.StatusSuccess, manualRun.Status);
        await WaitUntilAsync(
            () =>
            {
                var currentRuns = scope.RunRepository.ListRecent(10)
                    .OrderBy(x => x.StartedAt)
                    .ToArray();
                return currentRuns.Length >= 3 &&
                    currentRuns.Take(3).All(x => string.Equals(x.Status, AIAgentExecutionService.StatusSuccess, StringComparison.OrdinalIgnoreCase));
            },
            TimeSpan.FromSeconds(5));

        var runs = scope.RunRepository.ListRecent(10)
            .OrderBy(x => x.StartedAt)
            .ToArray();

        Assert.Equal(3, runs.Length);
        Assert.All(runs, run => Assert.Equal(AIAgentExecutionService.StatusSuccess, run.Status));
    }

    private static Task<AIAgentRunRecord> InvokeExecuteAsync(AIAgentExecutionService service, AIAgentSettings settings, string trigger)
    {
        var method = typeof(AIAgentExecutionService).GetMethod("ExecuteAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var task = method!.Invoke(service, [settings, trigger, CancellationToken.None, null]) as Task<AIAgentRunRecord>;
        Assert.NotNull(task);
        return task!;
    }

    private static Task<AIAgentRunRecord> InvokeRunProcessAsync(
        AIAgentExecutionService service,
        AIAgentRunRecord run,
        AIAgentSettings settings,
        string trigger,
        string promptFile,
        CancellationToken cancellationToken,
        Func<string?> cancellationReasonProvider)
    {
        var method = typeof(AIAgentExecutionService).GetMethod("RunProcessAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var task = method!.Invoke(service, [run, settings, trigger, promptFile, cancellationToken, cancellationReasonProvider]) as Task<AIAgentRunRecord>;
        Assert.NotNull(task);
        return task!;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var startedAt = DateTimeOffset.UtcNow;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow - startedAt > timeout)
            {
                throw new TimeoutException("Condition was not satisfied within the allotted time.");
            }

            await Task.Delay(50);
        }
    }

    private sealed class TestScope : IDisposable
    {
        private TestScope()
        {
            Preferences = new UserPreferenceRepository();
            RunRepository = new AIAgentRunRepository();
            HttpApiState = new HttpApiStateService();
            Logger = new AppLogger();
        }

        public UserPreferenceRepository Preferences { get; }

        public AIAgentRunRepository RunRepository { get; }

        public HttpApiStateService HttpApiState { get; }

        public AppLogger Logger { get; }

        public static TestScope Create()
        {
            lock (TestSync)
            {
                DbSchemaBootstrapper.EnsureSchema();
                using var db = new AppDbContext();
                db.AIAgentRuns.RemoveRange(db.AIAgentRuns);
                var preference = db.UserPreferences.SingleOrDefault(x => x.PreferenceKey == "ai_agent.settings");
                if (preference is not null)
                {
                    db.UserPreferences.Remove(preference);
                }

                db.SaveChanges();
                return new TestScope();
            }
        }

        public void Dispose()
        {
            lock (TestSync)
            {
                using var db = new AppDbContext();
                db.AIAgentRuns.RemoveRange(db.AIAgentRuns);
                var preference = db.UserPreferences.SingleOrDefault(x => x.PreferenceKey == "ai_agent.settings");
                if (preference is not null)
                {
                    db.UserPreferences.Remove(preference);
                }

                db.SaveChanges();
            }
        }
    }
}
