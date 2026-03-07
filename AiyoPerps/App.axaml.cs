using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.Markup.Xaml;
using AiyoPerps.Services;
using AiyoPerps.Services.Api;
using AiyoPerps.ViewModels;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace AiyoPerps;

public partial class App : Application
{
    public static ToastService ToastService { get; } = new();
    public static AppLogger Logger { get; } = new();
    public static LocalizationService Localization { get; } = new();
    public static UserPreferenceRepository UserPreferenceRepository { get; } = new();
    private static readonly ISecretProtector SecretProtector = new AesSecretProtector();
    private static readonly AccountRepository AccountRepository = new(SecretProtector);
    private static readonly object ShutdownSync = new();
    private static Task<bool>? ShutdownCleanupTask;
    private static int RetentionSchedulerDisposed;

    public static AccountStore AccountStore { get; } = new(AccountRepository);
    public static IVenueFactory VenueFactory { get; } = new VenueFactory(Logger);
    public static CandleRepository CandleRepository { get; } = new();
    public static SymbolCatalogRepository SymbolCatalogRepository { get; } = new();
    public static SymbolCatalogSyncService SymbolCatalogSyncService { get; } = new(SymbolCatalogRepository, Logger);
    public static WorkspaceLayoutRepository WorkspaceLayoutRepository { get; } = new();
    public static RetentionScheduler RetentionScheduler { get; } = new(new RetentionJob(), retentionDays: 365);
    public static TradingApiService TradingApiService { get; } = new(AccountStore, VenueFactory, SymbolCatalogRepository, Logger);
    public static LocalApiServer LocalApiServer { get; } = new(TradingApiService, Logger, RequestShutdownAsync);

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        RetentionScheduler.Start();
        Logger.Info("App", "Application framework initialization started");
        var preferredLanguage = UserPreferenceRepository.GetLanguageCodeOrDefault("en");
        Localization.SetLanguage(preferredLanguage);
        Logger.Info("App", $"Preferred language applied code={Localization.CurrentLanguageCode}");
        _ = Task.Run(async () =>
        {
            try
            {
                Logger.Info("App", "Symbol catalog sync started (background)");
                await SymbolCatalogSyncService.SyncAllAsync();
                Logger.Info("App", "Symbol catalog sync completed");
            }
            catch (Exception ex)
            {
                Logger.Error("App", "Symbol catalog sync failed", ex);
            }
        });

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(AccountStore, VenueFactory, CandleRepository, SymbolCatalogRepository, Logger, ToastService, UserPreferenceRepository, LocalApiServer, TradingApiService)
            };

            desktop.Exit += (_, _) =>
            {
                Logger.Info("App", "Application exit");
                var completedInTime = false;
                try
                {
                    completedInTime = RunShutdownCleanupAsync().GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    Logger.Warn("App", $"Shutdown wait warning: {ex.Message}");
                }

                if (!completedInTime)
                {
                    Logger.Warn("App", "Shutdown timeout after 5s. Forcing process termination.");
                    ForceTerminateProcess();
                }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    public static Task<bool> RunShutdownCleanupAsync()
    {
        lock (ShutdownSync)
        {
            ShutdownCleanupTask ??= RunShutdownCleanupCoreAsync();
            return ShutdownCleanupTask;
        }
    }

    private static async Task<bool> RunShutdownCleanupCoreAsync()
    {
        Logger.Info("App", "Shutdown cleanup started");
        try
        {
            var apiDisposeTask = LocalApiServer.DisposeAsync().AsTask();
            var tradingDisposeTask = TradingApiService.DisposeAsync().AsTask();
            var allDisposeTask = Task.WhenAll(apiDisposeTask, tradingDisposeTask);
            var completedInTime = await Task.WhenAny(allDisposeTask, Task.Delay(TimeSpan.FromSeconds(5))) == allDisposeTask;

            if (completedInTime)
            {
                try
                {
                    await allDisposeTask;
                }
                catch (Exception ex)
                {
                    Logger.Warn("App", $"Shutdown cleanup warning: {ex.Message}");
                }

                Logger.Info("App", "Shutdown cleanup completed");
                return true;
            }

            Logger.Warn("App", "Shutdown cleanup timeout after 5s.");
            return false;
        }
        catch (Exception ex)
        {
            Logger.Error("App", "Shutdown cleanup failed", ex);
            return false;
        }
        finally
        {
            DisposeRetentionSchedulerOnce();
        }
    }

    private static Task RequestShutdownAsync(string reason)
    {
        Logger.Info("App", $"Shutdown requested. reason={reason}");
        if (Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return Task.CompletedTask;
        }

        if (desktop.MainWindow is MainWindow mainWindow)
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                return mainWindow.BeginShutdownAsync(reason);
            }

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    await mainWindow.BeginShutdownAsync(reason);
                    tcs.SetResult();
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });
            return tcs.Task;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            desktop.Shutdown();
            return Task.CompletedTask;
        }

        Dispatcher.UIThread.Post(() => desktop.Shutdown());
        return Task.CompletedTask;
    }

    private static void DisposeRetentionSchedulerOnce()
    {
        if (Interlocked.Exchange(ref RetentionSchedulerDisposed, 1) == 1)
        {
            return;
        }

        RetentionScheduler.Dispose();
    }

    private static void ForceTerminateProcess()
    {
        try
        {
            Process.GetCurrentProcess().Kill(true);
        }
        catch
        {
            Environment.Exit(-1);
        }
    }
}
