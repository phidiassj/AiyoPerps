using AiyoPerps.Services;
using AiyoPerps.Services.Api;
using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AiyoPerps;

internal static class HeadlessRuntime
{
    private const int DefaultPort = 5078;

    public static bool IsHeadless(string[] args)
        => args.Any(static arg =>
            string.Equals(arg, "headless", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "--headless", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "/headless", StringComparison.OrdinalIgnoreCase));

    public static async Task RunAsync(string[] args)
    {
        var logger = new AppLogger();
        logger.Info("Headless", "Headless mode bootstrap started");

        var stopSignal = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var shutdownCts = new CancellationTokenSource();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stopSignal.TrySetResult("Console cancel requested");
        };

        var userPreferenceRepository = new UserPreferenceRepository();
        var port = ResolvePort(args, userPreferenceRepository, logger);

        var secretProtector = new AesSecretProtector();
        var accountRepository = new AccountRepository(secretProtector);
        var accountStore = new AccountStore(accountRepository);
        var venueFactory = new VenueFactory(logger);
        var symbolCatalogRepository = new SymbolCatalogRepository();
        var retentionScheduler = new RetentionScheduler(new RetentionJob(), retentionDays: 365);
        var symbolCatalogSyncService = new SymbolCatalogSyncService(symbolCatalogRepository, logger);
        var tradingApiService = new TradingApiService(accountStore, venueFactory, symbolCatalogRepository, logger);
        var localApiServer = new LocalApiServer(
            tradingApiService,
            logger,
            reason =>
            {
                stopSignal.TrySetResult(reason);
                return Task.CompletedTask;
            });

        retentionScheduler.Start();

        _ = Task.Run(async () =>
        {
            try
            {
                logger.Info("Headless", "Symbol catalog sync started (background)");
                await symbolCatalogSyncService.SyncAllAsync();
                logger.Info("Headless", "Symbol catalog sync completed");
            }
            catch (Exception ex)
            {
                logger.Error("Headless", "Symbol catalog sync failed", ex);
            }
        });

        try
        {
            await localApiServer.StartAsync(
                port,
                new LocalApiServerStartOptions
                {
                    BindLocalOnly = false,
                    AllowRemoteOrigins = true
                },
                shutdownCts.Token);

            logger.Info("Headless", $"Headless mode running. HTTP API auto-started on port={port}");
            await stopSignal.Task;
        }
        finally
        {
            shutdownCts.Cancel();
            await DisposeWithTimeoutAsync(localApiServer, tradingApiService, retentionScheduler, logger);
        }
    }

    private static int ResolvePort(string[] args, UserPreferenceRepository preferences, AppLogger logger)
    {
        var argPort = TryReadPort(args);
        if (argPort.HasValue)
        {
            logger.Info("Headless", $"Using CLI HTTP API port={argPort.Value}");
            return argPort.Value;
        }

        var envPort = TryParsePort(Environment.GetEnvironmentVariable("AIYOPERPS_HTTP_PORT"));
        if (envPort.HasValue)
        {
            logger.Info("Headless", $"Using environment HTTP API port={envPort.Value}");
            return envPort.Value;
        }

        var preferencePort = preferences.GetHttpApiPortOrDefault(DefaultPort);
        logger.Info("Headless", $"Using saved/default HTTP API port={preferencePort}");
        return preferencePort;
    }

    private static int? TryReadPort(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.StartsWith("--port=", StringComparison.OrdinalIgnoreCase) ||
                arg.StartsWith("port=", StringComparison.OrdinalIgnoreCase))
            {
                return TryParsePort(arg[(arg.IndexOf('=') + 1)..]);
            }

            if (arg.StartsWith("--http-port=", StringComparison.OrdinalIgnoreCase) ||
                arg.StartsWith("http-port=", StringComparison.OrdinalIgnoreCase))
            {
                return TryParsePort(arg[(arg.IndexOf('=') + 1)..]);
            }

            if ((string.Equals(arg, "--port", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(arg, "--http-port", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < args.Length)
            {
                return TryParsePort(args[i + 1]);
            }
        }

        return null;
    }

    private static int? TryParsePort(string? value)
    {
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) &&
               port is > 0 and <= 65535
            ? port
            : null;
    }

    private static async Task DisposeWithTimeoutAsync(
        LocalApiServer localApiServer,
        TradingApiService tradingApiService,
        RetentionScheduler retentionScheduler,
        AppLogger logger)
    {
        try
        {
            var apiDisposeTask = localApiServer.DisposeAsync().AsTask();
            var tradingDisposeTask = tradingApiService.DisposeAsync().AsTask();
            var allDisposeTask = Task.WhenAll(apiDisposeTask, tradingDisposeTask);
            var completed = await Task.WhenAny(allDisposeTask, Task.Delay(TimeSpan.FromSeconds(5))) == allDisposeTask;
            retentionScheduler.Dispose();

            if (completed)
            {
                logger.Info("Headless", "Headless mode shutdown completed");
                return;
            }

            logger.Warn("Headless", "Headless shutdown timeout after 5s. Forcing process termination.");
            ForceTerminateProcess();
        }
        catch (Exception ex)
        {
            retentionScheduler.Dispose();
            logger.Error("Headless", "Headless shutdown failed", ex);
            ForceTerminateProcess();
        }
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
