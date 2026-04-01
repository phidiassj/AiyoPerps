using AiyoPerps.Services;
using AiyoPerps.ViewModels;
using System;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Xunit;

namespace AiyoPerps.Test;

public sealed class DashboardTabViewModelTests
{
    [Fact]
    public void ReplaceMarketRows_ReusesExistingRow()
    {
        var viewModel = CreateSubject();

        InvokePrivate(viewModel, "ReplaceMarketRows",
        new[]
        {
            new DashboardMarketDto(
                Guid.NewGuid(),
                "FakeVm",
                "Fake Account",
                "BTC",
                "BTCUSDT",
                68100m,
                10m,
                2000m,
                1500m,
                25d)
        });

        var existing = Assert.Single(viewModel.MarketRows);

        InvokePrivate(viewModel, "ReplaceMarketRows",
        new[]
        {
            new DashboardMarketDto(
                existing.AccountId,
                "FakeVm",
                "Fake Account",
                "BTC",
                "BTCUSDT",
                68250m,
                18m,
                2100m,
                1550m,
                30d)
        });

        var updated = Assert.Single(viewModel.MarketRows);
        Assert.Same(existing, updated);
        Assert.Equal(68250m, updated.Price);
        Assert.Equal(18m, updated.Pnl);
        Assert.Equal(2100m, updated.Balance);
        Assert.Equal(1550m, updated.AvailableBalance);
        Assert.Equal(30d, updated.MaxLeverage);
    }

    [Fact]
    public void ReplacePositionRows_ReusesExistingRowAndPreservesLimitInput()
    {
        var viewModel = CreateSubject();

        InvokePrivate(viewModel, "ReplacePositionRows",
        new[]
        {
            new DashboardPositionDto(
                Guid.NewGuid(),
                "FakeVm",
                "BTC",
                "BTCUSDT",
                "Cross",
                1000m,
                68000m,
                68100m,
                10m,
                1.5m,
                "Long")
        });

        var existing = Assert.Single(viewModel.PositionRows);
        existing.CloseLimitPrice = "70000";

        InvokePrivate(viewModel, "ReplacePositionRows",
        new[]
        {
            new DashboardPositionDto(
                existing.AccountId,
                "FakeVm",
                "BTC",
                "BTCUSDT",
                "Cross",
                1200m,
                68010m,
                68250m,
                18m,
                2.2m,
                "Long")
        });

        var updated = Assert.Single(viewModel.PositionRows);
        Assert.Same(existing, updated);
        Assert.Equal("70000", updated.CloseLimitPrice);
        Assert.Equal(1200m, updated.Amount);
        Assert.Equal(68250m, updated.Price);
        Assert.Equal(18m, updated.PnlUsd);
    }

    [Fact]
    public void ReplacePendingOrders_ReusesExistingRow()
    {
        var viewModel = CreateSubject();

        InvokePrivate(viewModel, "ReplacePendingOrders",
        new[]
        {
            new DashboardPendingOrderDto(
                Guid.NewGuid(),
                "FakeVm",
                "BTC",
                "BTCUSDT",
                "Cross",
                500m,
                69000m,
                68100m,
                "order-1")
        });

        var existing = Assert.Single(viewModel.PendingOrderRows);

        InvokePrivate(viewModel, "ReplacePendingOrders",
        new[]
        {
            new DashboardPendingOrderDto(
                existing.AccountId,
                "FakeVm",
                "BTC",
                "BTCUSDT",
                "Cross",
                650m,
                69100m,
                68200m,
                "order-1")
        });

        var updated = Assert.Single(viewModel.PendingOrderRows);
        Assert.Same(existing, updated);
        Assert.Equal(650m, updated.Amount);
        Assert.Equal(69100m, updated.LimitPrice);
        Assert.Equal(68200m, updated.Price);
    }

    [Fact]
    public void ReplacePendingOrders_SkipsRecentlySuppressedCanceledOrders()
    {
        var viewModel = CreateSubject();
        InvokePrivate(viewModel, "SuppressCanceledOrderId", "order-1");

        InvokePrivate(viewModel, "ReplacePendingOrders",
        new[]
        {
            new DashboardPendingOrderDto(
                Guid.NewGuid(),
                "FakeVm",
                "BTC",
                "BTCUSDT",
                "Cross",
                500m,
                69000m,
                68100m,
                "order-1")
        });

        Assert.Empty(viewModel.PendingOrderRows);
    }

    [Fact]
    public void RefreshMarginModeSupport_UsesCurrentSymbolPositionMode()
    {
        var viewModel = CreateSubject();
        var accountId = Guid.NewGuid();

        viewModel.MarketRows.Add(new DashboardMarketRow(
            accountId,
            "BitMEX",
            "BTC",
            "XBTUSDT",
            68100m,
            0m,
            1000m,
            900m,
            50d));
        viewModel.PositionRows.Add(new DashboardPositionRow(
            accountId,
            "BitMEX",
            "BTC",
            "XBTUSDT",
            "XBTUSDT",
            "Isolated",
            "Long",
            100m,
            68000m,
            68100m,
            5m,
            1m,
            new RelayCommand(_ => { }),
            new RelayCommand(_ => { })));

        SetField(viewModel, "_selectedMarketRow", viewModel.MarketRows[0]);
        SetField(viewModel, "_selectedMarginMode", "Cross");

        InvokePrivate(viewModel, "RefreshMarginModeSupport");

        Assert.Equal("Isolated", viewModel.SelectedMarginMode);
        Assert.True(viewModel.IsIsolatedMarginModeSelected);
    }

    [Fact]
    public void SelectedAIAgentRun_UpdatesDetailViewModel()
    {
        var viewModel = CreateSubject();
        var record = new AIAgentRunRecord(
            "run-1",
            DateTimeOffset.UtcNow.AddMinutes(-2),
            DateTimeOffset.UtcNow,
            "gpt-5.4",
            "Succeeded",
            0,
            @"E:\work\AiyoPerps",
            "codex run",
            "prompt body",
            "stdout body",
            "stderr body");
        var item = new AIAgentRunSummaryItem(record);

        viewModel.SelectedAIAgentRun = item;

        Assert.Same(item, viewModel.SelectedAIAgentRun);
        Assert.NotNull(viewModel.SelectedAIAgentRunDetail);
        Assert.Equal(record.RunId, viewModel.SelectedAIAgentRunDetail!.Record.RunId);
        Assert.True(viewModel.HasSelectedAIAgentRunDetail);
    }

    [Fact]
    public async Task CanRunAgentNow_IsFalseWhileHttpApiInitializing()
    {
        using var scope = DashboardAgentScope.Create();
        var settings = new AIAgentSettings(
            true,
            "custom",
            5,
            "Write-Output 'ok'",
            "Prompt",
            AppContext.BaseDirectory,
            string.Empty,
            30);

        scope.Preferences.SaveAIAgentSettings(settings);
        scope.HttpApiState.MarkInitializing(5078);
        await using var service = new AIAgentExecutionService(scope.Preferences, scope.RunRepository, scope.HttpApiState, trading: null, scope.Logger);

        var viewModel = CreateSubject();
        SetField(viewModel, "_aiAgentExecutionService", service);

        Assert.False(viewModel.CanRunAgentNow);

        scope.HttpApiState.MarkReady(5078);

        Assert.True(viewModel.CanRunAgentNow);
    }

    [Fact]
    public async Task CanRunAgentNow_IsFalseWhileAgentIsRunning()
    {
        using var scope = DashboardAgentScope.Create();
        var settings = new AIAgentSettings(
            true,
            "custom",
            5,
            "Start-Sleep -Seconds 1; Write-Output 'ok'",
            "Prompt",
            AppContext.BaseDirectory,
            string.Empty,
            30);

        scope.Preferences.SaveAIAgentSettings(settings);
        await using var service = new AIAgentExecutionService(scope.Preferences, scope.RunRepository, scope.HttpApiState, trading: null, scope.Logger);
        service.Start();

        var viewModel = CreateSubject();
        SetField(viewModel, "_aiAgentExecutionService", service);

        var runTask = service.RunNowAsync();
        await Task.Delay(150);

        Assert.False(viewModel.CanRunAgentNow);

        await runTask;

        Assert.True(viewModel.CanRunAgentNow);
    }

    private static DashboardTabViewModel CreateSubject()
    {
        var viewModel = (DashboardTabViewModel)RuntimeHelpers.GetUninitializedObject(typeof(DashboardTabViewModel));
        SetBackingField(viewModel, "<AIAgentRuns>k__BackingField", new ObservableCollection<AIAgentRunSummaryItem>());
        SetBackingField(viewModel, "<MarketRows>k__BackingField", new ObservableCollection<DashboardMarketRow>());
        SetBackingField(viewModel, "<PositionRows>k__BackingField", new ObservableCollection<DashboardPositionRow>());
        SetBackingField(viewModel, "<PendingOrderRows>k__BackingField", new ObservableCollection<DashboardPendingOrderRow>());
        SetField(viewModel, "_marginModeOptions", new[] { "Cross", "Isolated" });
        SetField(viewModel, "_selectedMarginMode", "Cross");
        return viewModel;
    }

    private sealed class DashboardAgentScope : IDisposable
    {
        private DashboardAgentScope()
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

        public static DashboardAgentScope Create()
        {
            return new DashboardAgentScope();
        }

        public void Dispose()
        {
        }
    }

    private static void InvokePrivate(object instance, string methodName, object argument)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(instance, [argument]);
    }

    private static void InvokePrivate(object instance, string methodName)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(instance, null);
    }

    private static void SetBackingField(object instance, string fieldName, object value)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(instance, value);
    }

    private static void SetField(object instance, string fieldName, object value)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(instance, value);
    }
}
