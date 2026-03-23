using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AiyoPerps.Services;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AiyoPerps.Services.Api;

public sealed class LocalApiServer : IAsyncDisposable
{
    private static readonly JsonSerializerOptions McpRequestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly JsonSerializerOptions McpArgumentsJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly TradingApiService _trading;
    private readonly DashboardService _dashboard;
    private readonly ApiOperationStore _operations = new();
    private readonly AppLogger _logger;
    private readonly Func<string, Task>? _requestShutdown;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private WebApplication? _app;
    private LocalApiServerStartOptions _startOptions = new();
    private IReadOnlyList<Ipv4Subnet> _allowedWslSubnets = [];
    private HashSet<string> _localIpv4Hosts = new(StringComparer.OrdinalIgnoreCase);

    public LocalApiServer(TradingApiService trading, DashboardService dashboard, AppLogger logger, Func<string, Task>? requestShutdown = null)
    {
        _trading = trading;
        _dashboard = dashboard;
        _logger = logger;
        _requestShutdown = requestShutdown;
    }

    public bool IsRunning => _app is not null;
    public int Port { get; private set; }

    public async Task StartAsync(
        int port,
        LocalApiServerStartOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (port is <= 0 or > 65535)
        {
            throw new ApiBadRequestException("Port must be within 1..65535.");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_app is not null && Port == port)
            {
                return;
            }

            if (_app is not null)
            {
                await StopInternalAsync();
            }

            _startOptions = options ?? new LocalApiServerStartOptions();
            RefreshLocalNetworkAllowlist();

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(LocalApiServer).Assembly.FullName,
                ContentRootPath = AppContext.BaseDirectory
            });
            builder.Services.AddOpenApi();

            builder.WebHost.UseKestrel();
            if (_startOptions.BindLocalOnly)
            {
                builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
                builder.WebHost.UseUrls($"http://localhost:{port}");
                builder.WebHost.UseUrls($"http://winhost:{port}");
            }
            else
            {
                builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
            }

            var app = builder.Build();
            MapEndpoints(app);
            await app.StartAsync(cancellationToken);
            _app = app;
            Port = port;

            _logger.Info("Api", $"HTTP API started ({_startOptions.BindLocalOnlyLabel}) port={port}");
            if (_allowedWslSubnets.Count > 0)
            {
                _logger.Info("Api", $"WSL subnets allowed: {string.Join(", ", _allowedWslSubnets.Select(x => x.DisplayText))}");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await StopInternalAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _gate.Dispose();
    }

    private async Task StopInternalAsync()
    {
        if (_app is null)
        {
            return;
        }

        try
        {
            await _app.StopAsync();
        }
        catch (Exception ex)
        {
            _logger.Warn("Api", $"HTTP API stop warning: {ex.Message}");
        }
        finally
        {
            try
            {
                await _app.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.Warn("Api", $"HTTP API dispose warning: {ex.Message}");
            }
        }

        _app = null;
        _logger.Info("Api", "HTTP API stopped");
    }

    private void MapEndpoints(WebApplication app)
    {
        app.MapOpenApi("/openapi/v1.json");
        app.MapGet("/scalar", () => Results.Content(BuildScalarHtml(), "text/html"));

        app.Use(async (context, next) =>
        {
            var traceApiRequest = ShouldTraceApiRequest(context.Request.Path);
            var requestSummary = traceApiRequest ? BuildRequestSummary(context) : string.Empty;
            var stopwatch = traceApiRequest ? Stopwatch.StartNew() : null;

            if (traceApiRequest)
            {
                _logger.Info("Api", $"HTTP request start {requestSummary}");
            }

            if (!IsAllowedRequest(context))
            {
                _logger.Warn("Api", $"HTTP request forbidden host {BuildRequestSummary(context)}");
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { error = "Forbidden host." });
                return;
            }

            var origin = context.Request.Headers.Origin.ToString();
            if (!string.IsNullOrWhiteSpace(origin))
            {
                if (!TryNormalizeAllowedOrigin(origin, out var normalizedOrigin))
                {
                    _logger.Warn("Api", $"HTTP request forbidden origin {BuildRequestSummary(context)}");
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsJsonAsync(new { error = "Forbidden origin." });
                    return;
                }

                context.Response.Headers["Access-Control-Allow-Origin"] = normalizedOrigin;
                context.Response.Headers["Vary"] = "Origin";
                context.Response.Headers["Access-Control-Allow-Methods"] = "GET,POST,PUT,DELETE,OPTIONS";
                context.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type,Authorization,X-Api-Token";

                if (HttpMethods.IsOptions(context.Request.Method))
                {
                    context.Response.StatusCode = StatusCodes.Status204NoContent;
                    if (traceApiRequest && stopwatch is not null)
                    {
                        stopwatch.Stop();
                        _logger.Info("Api", $"HTTP request done {requestSummary}, status={context.Response.StatusCode}, elapsedMs={stopwatch.ElapsedMilliseconds}");
                    }
                    return;
                }
            }

            await next();

            if (traceApiRequest && stopwatch is not null)
            {
                stopwatch.Stop();
                _logger.Info("Api", $"HTTP request done {requestSummary}, status={context.Response.StatusCode}, elapsedMs={stopwatch.ElapsedMilliseconds}");
            }
        });

        app.MapGet("/api/v1/health", () => Results.Ok(new
        {
            status = "ok",
            server = "AiyoPerps Local API",
            port = Port,
            running = IsRunning,
            utcNow = DateTimeOffset.UtcNow,
            bindScope = _startOptions.BindLocalOnlyLabel,
            allowedWslSubnets = _allowedWslSubnets.Select(x => x.DisplayText).ToArray()
        }));

        app.MapGet("/api/v1/dashboard/status", () => Results.Ok(BuildDashboardStatusPayload()));

        app.MapGet("/api/v1/dashboard/options", () => Results.Ok(BuildDashboardOptionsPayload()));

        app.MapGet("/api/v1/dashboard/config", () => Results.Ok(_dashboard.GetConfiguration()));

        app.MapPut("/api/v1/dashboard/config", async (ApiDashboardConfigurationRequest request, CancellationToken ct) =>
        {
            var snapshot = await _dashboard.UpdateConfigurationAsync(ToDashboardConfiguration(request), ct);
            return Results.Ok(snapshot);
        });

        app.MapGet("/api/v1/dashboard/snapshot", () => Results.Ok(_dashboard.GetSnapshot()));

        app.MapPost("/api/v1/dashboard/start", () =>
            QueueOperation("dashboard-start", async ct => await _dashboard.StartAsync(ct)));

        app.MapPost("/api/v1/dashboard/stop", () =>
            QueueOperation("dashboard-stop", async ct => await _dashboard.StopAsync(ct)));

        app.MapPost("/api/v1/dashboard/refresh", () =>
            QueueOperation("dashboard-refresh", async ct => await _dashboard.RefreshAsync(ct)));

        app.MapPost("/api/v1/dashboard/open-position", (ApiOpenPositionRequest request) =>
            QueueOperation("dashboard-open-position", async ct => await _dashboard.OpenPositionAsync(request, ct)));

        app.MapPost("/api/v1/dashboard/close-position", (ApiClosePositionRequest request) =>
            QueueOperation("dashboard-close-position", async ct => await _dashboard.ClosePositionAsync(request, ct)));

        app.MapPost("/api/v1/dashboard/cancel-order", (ApiCancelOrderRequest request) =>
            QueueOperation("dashboard-cancel-order", async ct => await _dashboard.CancelOrderAsync(request, ct)));

        app.MapPost("/api/v1/app/shutdown", () =>
        {
            if (_requestShutdown is null)
            {
                return Results.StatusCode(StatusCodes.Status501NotImplemented);
            }

            _ = Task.Run(async () =>
            {
                await Task.Delay(250);
                try
                {
                    await _requestShutdown("Requested by API endpoint /api/v1/app/shutdown");
                }
                catch (Exception ex)
                {
                    _logger.Error("Api", "Shutdown request callback failed", ex);
                }
            });

            return Results.Ok(new { accepted = true, message = "Application shutdown requested." });
        });

        app.MapGet("/api/v1/accounts", () => Results.Ok(_trading.ListAccounts()));

        app.MapGet("/api/v1/accounts/{accountId:guid}", (Guid accountId) =>
            Safe(() => _trading.GetAccount(accountId)));

        app.MapPost("/api/v1/accounts", (ApiAccountUpsertRequest request) =>
            Safe(() => _trading.CreateAccount(request)));

        app.MapPut("/api/v1/accounts/{accountId:guid}", (Guid accountId, ApiAccountUpsertRequest request) =>
            Safe(() => _trading.UpdateAccount(accountId, request)));

        app.MapDelete("/api/v1/accounts/{accountId:guid}", (Guid accountId) =>
            QueueOperation("delete-account", async ct =>
            {
                await _trading.DeleteAccountAsync(accountId);
                return new { accountId, deleted = true };
            }));

        app.MapGet("/api/v1/accounts/{accountId:guid}/symbols", (Guid accountId) =>
            Safe(() => _trading.ListSymbols(accountId)));

        app.MapGet("/api/v1/symbols", (Guid accountId) =>
            Safe(() => _trading.ListSymbols(accountId)));

        app.MapGet("/api/v1/connections", () => Results.Ok(_trading.ListConnections()));

        app.MapPost("/api/v1/connections/open", (ApiConnectionOpenRequest request) =>
            QueueOperation("open-connection", async ct => await _trading.OpenConnectionAsync(request.AccountId, request.Symbol, request.Interval, ct)));

        app.MapPost("/api/v1/connections/close", (ApiConnectionCloseRequest request) =>
            QueueOperation("close-connection", async ct =>
            {
                var closed = await _trading.CloseConnectionAsync(request.AccountId, request.Symbol);
                return new { request.AccountId, request.Symbol, closed };
            }));

        app.MapGet("/api/v1/market-data", async (Guid accountId, string symbol, string? interval, long? cursor, CancellationToken ct) =>
        {
            try
            {
                var response = await _trading.GetMarketDataAsync(accountId, symbol, interval ?? "5m", cursor, ct);
                return Results.Ok(response);
            }
            catch (Exception ex)
            {
                return MapError(ex);
            }
        });

        app.MapGet("/api/v1/market/snapshot", async (Guid accountId, string symbol, string? interval, long? cursor, CancellationToken ct) =>
        {
            try
            {
                var response = await _trading.GetMarketDataAsync(accountId, symbol, interval ?? "5m", cursor, ct);
                return Results.Ok(response);
            }
            catch (Exception ex)
            {
                return MapError(ex);
            }
        });

        app.MapGet("/api/v1/positions", async (Guid accountId, string? symbol, CancellationToken ct) =>
        {
            try
            {
                var response = await _trading.ListPositionsAsync(accountId, symbol, ct);
                return Results.Ok(response);
            }
            catch (Exception ex)
            {
                return MapError(ex);
            }
        });

        app.MapGet("/api/v1/orders", async (Guid accountId, string? symbol, CancellationToken ct) =>
        {
            try
            {
                var response = await _trading.ListOpenOrdersAsync(accountId, symbol, ct);
                return Results.Ok(response);
            }
            catch (Exception ex)
            {
                return MapError(ex);
            }
        });

        app.MapGet("/api/v1/balances", async (Guid accountId, string? symbol, CancellationToken ct) =>
        {
            try
            {
                var response = await _trading.ListBalancesAsync(accountId, symbol, ct);
                return Results.Ok(response);
            }
            catch (Exception ex)
            {
                return MapError(ex);
            }
        });

        app.MapPost("/api/v1/trading/open-position", (ApiOpenPositionRequest request) =>
            QueueOperation("open-position", async ct => await _trading.OpenPositionAsync(request, ct)));

        app.MapPost("/api/v1/trading/close-position", (ApiClosePositionRequest request) =>
            QueueOperation("close-position", async ct => await _trading.ClosePositionAsync(request, ct)));

        app.MapPost("/api/v1/trading/cancel-order", (ApiCancelOrderRequest request) =>
            QueueOperation("cancel-order", async ct => await _trading.CancelOrderAsync(request, ct)));

        app.MapPost("/api/v1/stress/run", (ApiStressRunRequest request) =>
            QueueOperation("stress-run", async ct => await _trading.RunStressAsync(request, ct)));

        app.MapGet("/api/v1/operations/{operationId}", (string operationId) =>
        {
            var op = _operations.Get(operationId);
            if (op is null)
            {
                return Results.NotFound(new { error = "Operation not found.", operationId });
            }

            return Results.Ok(op);
        });

        app.MapGet("/api/v1/mcp/tools", () => Results.Ok(BuildMcpTools()));

        app.MapPost("/mcp", async (HttpContext context) =>
        {
            try
            {
                var request = await JsonSerializer.DeserializeAsync<McpRpcRequest>(context.Request.Body, McpRequestJsonOptions, context.RequestAborted);
                if (request is null)
                {
                    _logger.Warn("Api", $"MCP invalid request body {BuildRequestSummary(context)}");
                    return Results.BadRequest(new { jsonrpc = "2.0", error = new { code = -32600, message = "Invalid JSON-RPC request." }, id = (object?)null });
                }

                _logger.Info("Api", $"MCP request received method={request.Method ?? "(null)"}, id={FormatRpcIdForLog(request.Id)}, {BuildRequestSummary(context)}");
                var response = await HandleMcpAsync(request, context.RequestAborted);
                _logger.Info("Api", $"MCP response ready method={request.Method ?? "(null)"}, id={FormatRpcIdForLog(request.Id)}");
                return Results.Json(response);
            }
            catch (Exception ex)
            {
                _logger.Error("Api", $"MCP request failed {BuildRequestSummary(context)}", ex);
                return Results.BadRequest(new { jsonrpc = "2.0", error = new { code = -32000, message = ex.Message }, id = (object?)null });
            }
        });
    }

    private IResult QueueOperation(string name, Func<CancellationToken, Task<object?>> work)
    {
        var op = _operations.Enqueue(name, work);
        return Results.Ok(new
        {
            operationId = op.OperationId,
            status = op.Status.ToString(),
            createdAt = op.CreatedAt,
            statusUrl = $"/api/v1/operations/{op.OperationId}"
        });
    }

    private static IResult Safe<T>(Func<T> run)
    {
        try
        {
            return Results.Ok(run());
        }
        catch (Exception ex)
        {
            return MapError(ex);
        }
    }

    private static IResult MapError(Exception ex)
    {
        return ex switch
        {
            ApiBadRequestException => Results.BadRequest(new { error = ex.Message }),
            ApiNotFoundException => Results.NotFound(new { error = ex.Message }),
            ApiConflictException => Results.Conflict(new { error = ex.Message }),
            _ => Results.Problem(ex.Message)
        };
    }

    private async Task<object> HandleMcpAsync(McpRpcRequest request, CancellationToken cancellationToken)
    {
        var rpcId = NormalizeRpcId(request.Id);

        object MakeError(int code, string message)
            => new { jsonrpc = "2.0", error = new { code, message }, id = rpcId };

        object MakeResult(object result)
            => new { jsonrpc = "2.0", result, id = rpcId };

        var method = request.Method?.Trim() ?? string.Empty;
        if (string.Equals(method, "initialize", StringComparison.OrdinalIgnoreCase))
        {
            var negotiatedProtocolVersion = "2025-01-01";
            if (request.Params.ValueKind == JsonValueKind.Object &&
                request.Params.TryGetProperty("protocolVersion", out var protocolVersionElement))
            {
                var requestedProtocolVersion = protocolVersionElement.GetString();
                if (!string.IsNullOrWhiteSpace(requestedProtocolVersion))
                {
                    negotiatedProtocolVersion = requestedProtocolVersion.Trim();
                }
            }

            _logger.Info("Api", $"MCP initialize handled id={FormatRpcIdForLog(request.Id)}");
            return MakeResult(new
            {
                protocolVersion = negotiatedProtocolVersion,
                serverInfo = new { name = "AiyoPerps MCP", version = "1.0.0" },
                capabilities = new { tools = new { } }
            });
        }

        if (string.Equals(method, "ping", StringComparison.OrdinalIgnoreCase))
        {
            _logger.Info("Api", $"MCP ping handled id={FormatRpcIdForLog(request.Id)}");
            return MakeResult(new { ok = true, utcNow = DateTimeOffset.UtcNow });
        }

        if (string.Equals(method, "tools/list", StringComparison.OrdinalIgnoreCase))
        {
            _logger.Info("Api", $"MCP tools/list handled id={FormatRpcIdForLog(request.Id)}");
            return MakeResult(new { tools = BuildMcpTools() });
        }

        if (!string.Equals(method, "tools/call", StringComparison.OrdinalIgnoreCase))
        {
            return MakeError(-32601, $"Unsupported method: {method}");
        }

        if (!request.Params.TryGetProperty("name", out var nameElement))
        {
            return MakeError(-32602, "Missing params.name");
        }

        var toolName = nameElement.GetString() ?? string.Empty;
        var args = request.Params.TryGetProperty("arguments", out var argsElement)
            ? argsElement
            : default;

        try
        {
            var payload = await ExecuteMcpToolAsync(toolName, args, cancellationToken);
            var structuredContent = new
            {
                success = true,
                result = NormalizeStructuredContent(payload)
            };
            return MakeResult(new
            {
                content = new[]
                {
                    new
                    {
                        type = "text",
                        text = JsonSerializer.Serialize(structuredContent)
                    }
                },
                structuredContent
            });
        }
        catch (Exception ex)
        {
            return MakeError(-32000, ex.Message);
        }
    }

    private async Task<object> ExecuteMcpToolAsync(string toolName, JsonElement args, CancellationToken cancellationToken)
    {
        var normalizedToolName = NormalizeMcpToolName(toolName);
        return normalizedToolName switch
        {
            "accounts_list" => _trading.ListAccounts(),
            "accounts_get" => _trading.GetAccount(ReadGuid(args, "accountId")),
            "accounts_create" => _trading.CreateAccount(Read<ApiAccountUpsertRequest>(args)),
            "accounts_update" => _trading.UpdateAccount(ReadGuid(args, "accountId"), Read<ApiAccountUpsertRequest>(args)),
            "accounts_delete" => QueueMcpOperation("delete-account", async ct =>
            {
                await _trading.DeleteAccountAsync(ReadGuid(args, "accountId"));
                return new { deleted = true };
            }),
            "symbols_list" => _trading.ListSymbols(ReadGuid(args, "accountId")),
            "connections_list" => _trading.ListConnections(),
            "connections_open" => QueueMcpOperation("open-connection", async ct =>
            {
                var req = Read<ApiConnectionOpenRequest>(args);
                return await _trading.OpenConnectionAsync(req.AccountId, req.Symbol, req.Interval, ct, notifyLifecycleEvents: false);
            }),
            "connections_close" => QueueMcpOperation("close-connection", async ct =>
            {
                var req = Read<ApiConnectionCloseRequest>(args);
                var closed = await _trading.CloseConnectionAsync(req.AccountId, req.Symbol, ct, notifyLifecycleEvents: false);
                return new { req.AccountId, req.Symbol, closed };
            }),
            "dashboard_status_get" => BuildDashboardStatusPayload(),
            "dashboard_options_get" => BuildDashboardOptionsPayload(),
            "dashboard_config_get" => _dashboard.GetConfiguration(),
            "dashboard_config_set" => await _dashboard.UpdateConfigurationAsync(ToDashboardConfiguration(Read<ApiDashboardConfigurationRequest>(args)), cancellationToken),
            "dashboard_snapshot_get" => _dashboard.GetSnapshot(),
            "dashboard_start" => QueueMcpOperation("dashboard-start", async ct => await _dashboard.StartAsync(ct)),
            "dashboard_stop" => QueueMcpOperation("dashboard-stop", async ct => await _dashboard.StopAsync(ct)),
            "dashboard_refresh" => QueueMcpOperation("dashboard-refresh", async ct => await _dashboard.RefreshAsync(ct)),
            "dashboard_positions_open" => QueueMcpOperation("dashboard-open-position", async ct => await _dashboard.OpenPositionAsync(Read<ApiOpenPositionRequest>(args), ct)),
            "dashboard_positions_close" => QueueMcpOperation("dashboard-close-position", async ct => await _dashboard.ClosePositionAsync(Read<ApiClosePositionRequest>(args), ct)),
            "dashboard_orders_cancel" => QueueMcpOperation("dashboard-cancel-order", async ct => await _dashboard.CancelOrderAsync(Read<ApiCancelOrderRequest>(args), ct)),
            "market_snapshot" => await _trading.GetMarketDataAsync(
                ReadGuid(args, "accountId"),
                ReadString(args, "symbol"),
                ReadString(args, "interval", "5m"),
                ReadNullableLong(args, "cursor"),
                cancellationToken,
                notifyLifecycleEvents: false),
            "market_data_get" => await _trading.GetMarketDataAsync(
                ReadGuid(args, "accountId"),
                ReadString(args, "symbol"),
                ReadString(args, "interval", "5m"),
                ReadNullableLong(args, "cursor"),
                cancellationToken,
                notifyLifecycleEvents: false),
            "positions_list" => await _trading.ListPositionsAsync(
                ReadGuid(args, "accountId"),
                ReadOptionalString(args, "symbol"),
                cancellationToken,
                notifyLifecycleEvents: false),
            "orders_list" => await _trading.ListOpenOrdersAsync(
                ReadGuid(args, "accountId"),
                ReadOptionalString(args, "symbol"),
                cancellationToken,
                notifyLifecycleEvents: false),
            "balances_list" => await _trading.ListBalancesAsync(
                ReadGuid(args, "accountId"),
                ReadOptionalString(args, "symbol"),
                cancellationToken,
                notifyLifecycleEvents: false),
            "positions_open" => QueueMcpOperation("open-position", async ct => await _trading.OpenPositionAsync(Read<ApiOpenPositionRequest>(args), ct, notifyLifecycleEvents: false)),
            "positions_close" => QueueMcpOperation("close-position", async ct => await _trading.ClosePositionAsync(Read<ApiClosePositionRequest>(args), ct, notifyLifecycleEvents: false)),
            "orders_cancel" => QueueMcpOperation("cancel-order", async ct => await _trading.CancelOrderAsync(Read<ApiCancelOrderRequest>(args), ct, notifyLifecycleEvents: false)),
            "stress_run" => QueueMcpOperation("stress-run", async ct => await _trading.RunStressAsync(Read<ApiStressRunRequest>(args), ct)),
            "app_shutdown" => RequestShutdownFromTool(),
            "operations_get" => _operations.Get(ReadString(args, "operationId")) ?? throw new ApiNotFoundException("Operation not found."),
            _ => throw new ApiBadRequestException($"Unknown tool: {toolName}")
        };
    }

    private object QueueMcpOperation(string name, Func<CancellationToken, Task<object?>> work)
    {
        var op = _operations.Enqueue(name, work);
        return new
        {
            operationId = op.OperationId,
            status = op.Status.ToString(),
            createdAt = op.CreatedAt,
            statusUrl = $"/api/v1/operations/{op.OperationId}"
        };
    }

    private object RequestShutdownFromTool()
    {
        if (_requestShutdown is null)
        {
            throw new ApiConflictException("Application shutdown endpoint is not configured.");
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(250);
            await _requestShutdown("Requested by MCP tool app.shutdown");
        });

        return new
        {
            accepted = true,
            message = "Application shutdown requested."
        };
    }

    private object BuildDashboardStatusPayload()
    {
        var configuration = _dashboard.GetConfiguration();
        var snapshot = _dashboard.GetSnapshot();
        return new
        {
            isRunning = snapshot.IsRunning,
            configuration,
            updatedAt = snapshot.UpdatedAt,
            counts = new
            {
                markets = snapshot.Markets.Count,
                positions = snapshot.Positions.Count,
                orders = snapshot.Orders.Count
            }
        };
    }

    private object BuildDashboardOptionsPayload()
    {
        var configuration = _dashboard.GetConfiguration();
        return new
        {
            configuration,
            accounts = _dashboard.GetSelectableAccounts(configuration.ShowTestnet),
            symbols = _dashboard.GetAvailableSymbolOptions(configuration)
        };
    }

    private static DashboardConfiguration ToDashboardConfiguration(ApiDashboardConfigurationRequest request)
    {
        return new DashboardConfiguration(
            request.SelectedAccountIds ?? [],
            request.Symbol,
            request.Interval ?? "5m",
            request.ShowTestnet);
    }

    private static object[] BuildMcpTools()
    {
        return
        [
            Tool("accounts_list", "List all configured accounts.", ObjectSchema()),
            Tool("accounts_get", "Get one account by accountId.", ObjectSchema(
                new Dictionary<string, object?>
                {
                    ["accountId"] = StringSchema("Account identifier (GUID).", format: "uuid")
                },
                "accountId")),
            Tool("accounts_create", "Create a new account profile with credentials.", ObjectSchema(
                new Dictionary<string, object?>
                {
                    ["venueId"] = StringSchema("Venue name, for example BitMEX, Hyperliquid, Aster, GRVT, or dYdX.", allowedValues: ["BitMEX", "Hyperliquid", "Aster", "GRVT", "dYdX"]),
                    ["displayName"] = StringSchema("User-facing account display name."),
                    ["environment"] = StringSchema("Environment name, for example mainnet or testnet.", allowedValues: ["mainnet", "testnet"]),
                    ["summary"] = StringSchema("Short summary shown in the UI."),
                    ["authMode"] = StringSchema("Authentication mode.", allowedValues: ["ApiKey", "Wallet", "Both"]),
                    ["apiKey"] = StringSchema("Optional API key."),
                    ["apiSecret"] = StringSchema("Optional API secret."),
                    ["accountAddress"] = StringSchema("Optional public account address."),
                    ["subAccountId"] = StringSchema("Optional sub account identifier (required by some venues such as GRVT)."),
                    ["walletAddress"] = StringSchema("Optional wallet address."),
                    ["privateKey"] = StringSchema("Optional private key."),
                    ["isEnabled"] = BooleanSchema("Whether the account is enabled.")
                },
                "venueId", "displayName", "environment", "summary")),
            Tool("accounts_update", "Update account profile and credentials by accountId.", ObjectSchema(
                new Dictionary<string, object?>
                {
                    ["accountId"] = StringSchema("Account identifier (GUID).", format: "uuid"),
                    ["venueId"] = StringSchema("Venue name, for example BitMEX, Hyperliquid, Aster, GRVT, or dYdX.", allowedValues: ["BitMEX", "Hyperliquid", "Aster", "GRVT", "dYdX"]),
                    ["displayName"] = StringSchema("User-facing account display name."),
                    ["environment"] = StringSchema("Environment name, for example mainnet or testnet.", allowedValues: ["mainnet", "testnet"]),
                    ["summary"] = StringSchema("Short summary shown in the UI."),
                    ["authMode"] = StringSchema("Authentication mode.", allowedValues: ["ApiKey", "Wallet", "Both"]),
                    ["apiKey"] = StringSchema("Optional API key."),
                    ["apiSecret"] = StringSchema("Optional API secret."),
                    ["accountAddress"] = StringSchema("Optional public account address."),
                    ["subAccountId"] = StringSchema("Optional sub account identifier (required by some venues such as GRVT)."),
                    ["walletAddress"] = StringSchema("Optional wallet address."),
                    ["privateKey"] = StringSchema("Optional private key."),
                    ["isEnabled"] = BooleanSchema("Whether the account is enabled.")
                },
                "accountId", "venueId", "displayName", "environment", "summary")),
            Tool("accounts_delete", "Delete account by accountId (async operation).", ObjectSchema(
                new Dictionary<string, object?>
                {
                    ["accountId"] = StringSchema("Account identifier (GUID).", format: "uuid")
                },
                "accountId")),
            Tool("symbols_list", "List tradable symbols for one account.", ObjectSchema(
                new Dictionary<string, object?>
                {
                    ["accountId"] = StringSchema("Account identifier (GUID).", format: "uuid")
                },
                "accountId")),
            Tool("connections_list", "List active market-data connections.", ObjectSchema()),
            Tool("connections_open", "Open connection by accountId + symbol (async operation).", ObjectSchema(
                new Dictionary<string, object?>
                {
                    ["accountId"] = StringSchema("Account identifier (GUID).", format: "uuid"),
                    ["symbol"] = StringSchema("Trading symbol, for example BTC."),
                    ["interval"] = StringSchema("Candle interval, for example 5m.")
                },
                "accountId", "symbol", "interval")),
            Tool("connections_close", "Close connection by accountId + symbol (async operation).", ObjectSchema(
                new Dictionary<string, object?>
                {
                    ["accountId"] = StringSchema("Account identifier (GUID).", format: "uuid"),
                    ["symbol"] = StringSchema("Trading symbol, for example BTC.")
                },
                "accountId", "symbol")),
            Tool("dashboard_status_get", "Get dashboard runtime status and counters.", ObjectSchema()),
            Tool("dashboard_options_get", "Get dashboard-selectable accounts and symbols for the current configuration.", ObjectSchema()),
            Tool("dashboard_config_get", "Get the current dashboard configuration.", ObjectSchema()),
            Tool("dashboard_config_set", "Update the dashboard configuration.", ObjectSchema(
                new Dictionary<string, object?>
                {
                    ["selectedAccountIds"] = new Dictionary<string, object?>
                    {
                        ["type"] = "array",
                        ["description"] = "Selected dashboard account ids.",
                        ["items"] = StringSchema("Account identifier (GUID).", format: "uuid")
                    },
                    ["symbol"] = StringSchema("Dashboard symbol."),
                    ["interval"] = StringSchema("Dashboard interval, defaults to 5m."),
                    ["showTestnet"] = BooleanSchema("Whether dashboard options include testnet accounts.")
                },
                "selectedAccountIds", "interval", "showTestnet")),
            Tool("dashboard_snapshot_get", "Get the latest dashboard snapshot.", ObjectSchema()),
            Tool("dashboard_start", "Start the dashboard runtime with the current configuration.", ObjectSchema()),
            Tool("dashboard_stop", "Stop the dashboard runtime and clear runtime data.", ObjectSchema()),
            Tool("dashboard_refresh", "Refresh the dashboard snapshot immediately.", ObjectSchema()),
            Tool("dashboard_positions_open", "Open a dashboard position on the selected account row (async order operation).", ObjectSchema(
                new Dictionary<string, object?>
                {
                    ["accountId"] = StringSchema("Account identifier (GUID).", format: "uuid"),
                    ["symbol"] = StringSchema("Trading symbol."),
                    ["side"] = StringSchema("buy, sell, long, or short.", allowedValues: ["buy", "sell", "long", "short"]),
                    ["orderType"] = StringSchema("market or limit.", allowedValues: ["market", "limit"]),
                    ["leverage"] = NumberSchema("Requested leverage."),
                    ["marginMode"] = StringSchema("cross or isolated.", allowedValues: ["cross", "isolated"]),
                    ["amount"] = NumberSchema("Input amount."),
                    ["amountUnit"] = StringSchema("Amount unit, for example USD."),
                    ["limitPrice"] = NumberSchema("Required for limit orders.")
                },
                "accountId", "symbol", "side", "orderType", "leverage", "amount", "amountUnit")),
            Tool("dashboard_positions_close", "Close a dashboard position by positionId (async order operation).", ObjectSchema(
                new Dictionary<string, object?>
                {
                    ["accountId"] = StringSchema("Account identifier (GUID).", format: "uuid"),
                    ["positionId"] = StringSchema("Position identifier."),
                    ["orderType"] = StringSchema("market or limit.", allowedValues: ["market", "limit"]),
                    ["limitPrice"] = NumberSchema("Required for limit orders.")
                },
                "accountId", "positionId", "orderType")),
            Tool("dashboard_orders_cancel", "Cancel a dashboard order by orderId (async operation).", ObjectSchema(
                new Dictionary<string, object?>
                {
                    ["accountId"] = StringSchema("Account identifier (GUID).", format: "uuid"),
                    ["symbol"] = StringSchema("Trading symbol."),
                    ["orderId"] = StringSchema("Order identifier.")
                },
                "accountId", "symbol", "orderId")),
            Tool("market_snapshot", "Get initial snapshot or cursor-based candle delta.", ObjectSchema(
                new Dictionary<string, object?>
                {
                    ["accountId"] = StringSchema("Account identifier (GUID).", format: "uuid"),
                    ["symbol"] = StringSchema("Trading symbol, for example BTC."),
                    ["interval"] = StringSchema("Candle interval, defaults to 5m."),
                    ["cursor"] = IntegerSchema("Optional cursor from a previous response.")
                },
                "accountId", "symbol")),
            Tool("market_data_get", "Get initial or delta candle data by cursor.", ObjectSchema(
                new Dictionary<string, object?>
                {
                    ["accountId"] = StringSchema("Account identifier (GUID).", format: "uuid"),
                    ["symbol"] = StringSchema("Trading symbol, for example BTC."),
                    ["interval"] = StringSchema("Candle interval, defaults to 5m."),
                    ["cursor"] = IntegerSchema("Optional cursor from a previous response.")
                },
                "accountId", "symbol")),
            Tool("positions_list", "List active positions.", ObjectSchema(
                new Dictionary<string, object?>
                {
                    ["accountId"] = StringSchema("Account identifier (GUID).", format: "uuid"),
                    ["symbol"] = StringSchema("Optional symbol filter.")
                },
                "accountId")),
            Tool("orders_list", "List open orders (exchange open orders).", ObjectSchema(
                new Dictionary<string, object?>
                {
                    ["accountId"] = StringSchema("Account identifier (GUID).", format: "uuid"),
                    ["symbol"] = StringSchema("Optional symbol filter.")
                },
                "accountId")),
            Tool("balances_list", "List balances.", ObjectSchema(
                new Dictionary<string, object?>
                {
                    ["accountId"] = StringSchema("Account identifier (GUID).", format: "uuid"),
                    ["symbol"] = StringSchema("Optional symbol filter.")
                },
                "accountId")),
            Tool("positions_open", "Open position (async order operation).", ObjectSchema(
                new Dictionary<string, object?>
                {
                    ["accountId"] = StringSchema("Account identifier (GUID).", format: "uuid"),
                    ["symbol"] = StringSchema("Trading symbol."),
                    ["side"] = StringSchema("buy, sell, long, or short.", allowedValues: ["buy", "sell", "long", "short"]),
                    ["orderType"] = StringSchema("market or limit.", allowedValues: ["market", "limit"]),
                    ["leverage"] = NumberSchema("Requested leverage."),
                    ["marginMode"] = StringSchema("cross or isolated.", allowedValues: ["cross", "isolated"]),
                    ["amount"] = NumberSchema("Input amount."),
                    ["amountUnit"] = StringSchema("Amount unit, for example USD."),
                    ["limitPrice"] = NumberSchema("Required for limit orders.")
                },
                "accountId", "symbol", "side", "orderType", "leverage", "amount", "amountUnit")),
            Tool("positions_close", "Close position by positionId (async order operation).", ObjectSchema(
                new Dictionary<string, object?>
                {
                    ["accountId"] = StringSchema("Account identifier (GUID).", format: "uuid"),
                    ["positionId"] = StringSchema("Position identifier."),
                    ["orderType"] = StringSchema("market or limit.", allowedValues: ["market", "limit"]),
                    ["limitPrice"] = NumberSchema("Required for limit orders.")
                },
                "accountId", "positionId", "orderType")),
            Tool("orders_cancel", "Cancel order by orderId (async operation).", ObjectSchema(
                new Dictionary<string, object?>
                {
                    ["accountId"] = StringSchema("Account identifier (GUID).", format: "uuid"),
                    ["symbol"] = StringSchema("Trading symbol."),
                    ["orderId"] = StringSchema("Order identifier.")
                },
                "accountId", "symbol", "orderId")),
            Tool("stress_run", "Run server-side market snapshot stress test.", ObjectSchema(
                new Dictionary<string, object?>
                {
                    ["accountId"] = StringSchema("Account identifier (GUID).", format: "uuid"),
                    ["symbol"] = StringSchema("Trading symbol."),
                    ["interval"] = StringSchema("Optional candle interval."),
                    ["concurrency"] = IntegerSchema("Optional concurrent worker count."),
                    ["iterations"] = IntegerSchema("Optional iteration count.")
                },
                "accountId", "symbol")),
            Tool("app_shutdown", "Request graceful app shutdown and resource release.", ObjectSchema()),
            Tool("operations_get", "Query async operation status by operationId.", ObjectSchema(
                new Dictionary<string, object?>
                {
                    ["operationId"] = StringSchema("Operation identifier.")
                },
                "operationId"))
        ];
    }

    private static string NormalizeMcpToolName(string toolName)
    {
        return (toolName ?? string.Empty)
            .Trim()
            .Replace('.', '_');
    }

    private static object NormalizeStructuredContent(object? payload)
    {
        if (payload is null)
        {
            return new Dictionary<string, object?>();
        }

        if (payload is string ||
            payload is bool ||
            payload is byte ||
            payload is sbyte ||
            payload is short ||
            payload is ushort ||
            payload is int ||
            payload is uint ||
            payload is long ||
            payload is ulong ||
            payload is float ||
            payload is double ||
            payload is decimal ||
            payload is Guid ||
            payload is DateTime ||
            payload is DateTimeOffset)
        {
            return new Dictionary<string, object?> { ["value"] = payload };
        }

        if (payload is JsonElement jsonElement)
        {
            return jsonElement.ValueKind == JsonValueKind.Object
                ? payload
                : new Dictionary<string, object?> { ["value"] = payload };
        }

        if (payload is IEnumerable && payload is not IDictionary)
        {
            return new Dictionary<string, object?> { ["value"] = payload };
        }

        return payload;
    }

    private static object Tool(string name, string description, object inputSchema)
    {
        return new
        {
            name,
            description,
            inputSchema
        };
    }

    private static object ObjectSchema(
        IDictionary<string, object?>? properties = null,
        params string[] required)
    {
        return new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = properties ?? new Dictionary<string, object?>(),
            ["required"] = required ?? [],
            ["additionalProperties"] = true
        };
    }

    private static object StringSchema(
        string description,
        string? format = null,
        IReadOnlyList<string>? allowedValues = null)
    {
        var schema = new Dictionary<string, object?>
        {
            ["type"] = "string",
            ["description"] = description
        };

        if (!string.IsNullOrWhiteSpace(format))
        {
            schema["format"] = format;
        }

        if (allowedValues is not null && allowedValues.Count > 0)
        {
            schema["enum"] = allowedValues;
        }

        return schema;
    }

    private static object NumberSchema(string description)
        => new Dictionary<string, object?>
        {
            ["type"] = "number",
            ["description"] = description
        };

    private static object IntegerSchema(string description)
        => new Dictionary<string, object?>
        {
            ["type"] = "integer",
            ["description"] = description
        };

    private static object BooleanSchema(string description)
        => new Dictionary<string, object?>
        {
            ["type"] = "boolean",
            ["description"] = description
        };

    private static T Read<T>(JsonElement element)
    {
        if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw new ApiBadRequestException("arguments is required.");
        }

        var obj = element.Deserialize<T>(McpArgumentsJsonOptions);
        if (obj is null)
        {
            throw new ApiBadRequestException($"arguments cannot be parsed as {typeof(T).Name}");
        }

        return obj;
    }

    private static Guid ReadGuid(JsonElement element, string propertyName)
    {
        var text = ReadString(element, propertyName);
        if (!Guid.TryParse(text, out var value))
        {
            throw new ApiBadRequestException($"Invalid Guid: {propertyName}");
        }

        return value;
    }

    private static string ReadString(JsonElement element, string propertyName, string? defaultValue = null)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            if (defaultValue is not null)
            {
                return defaultValue;
            }

            throw new ApiBadRequestException($"Missing property: {propertyName}");
        }

        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            if (defaultValue is not null)
            {
                return defaultValue;
            }

            throw new ApiBadRequestException($"Property is required: {propertyName}");
        }

        return text;
    }

    private static long? ReadNullableLong(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var n) => n,
            JsonValueKind.String when long.TryParse(value.GetString(), out var s) => s,
            _ => throw new ApiBadRequestException($"Invalid long value: {propertyName}")
        };
    }

    private static string? ReadOptionalString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static object? NormalizeRpcId(JsonElement id)
    {
        return id.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => id.GetString(),
            JsonValueKind.Number when id.TryGetInt64(out var n) => n,
            JsonValueKind.Number when id.TryGetDecimal(out var d) => d,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => id.GetRawText()
        };
    }

    private bool IsAllowedRequest(HttpContext context)
    {
        if (!_startOptions.BindLocalOnly)
        {
            return true;
        }

        if (!IsAllowedRemoteIp(context.Connection.RemoteIpAddress))
        {
            return false;
        }

        return IsAllowedRequestHost(context.Request.Host.Host);
    }

    private bool IsAllowedRequestHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var h = host.Trim().ToLowerInvariant();
        if (h is "localhost" or "127.0.0.1" or "::1" or "[::1]" or "winhost")
        {
            return true;
        }

        return IPAddress.TryParse(h, out var hostIp) &&
               hostIp.AddressFamily == AddressFamily.InterNetwork &&
               _localIpv4Hosts.Contains(hostIp.ToString());
    }

    private static bool ShouldTraceApiRequest(PathString path)
    {
        return path.StartsWithSegments("/mcp", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/api/v1/health", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildRequestSummary(HttpContext context)
    {
        var remoteIp = context.Connection.RemoteIpAddress?.ToString() ?? "(null)";
        var host = context.Request.Host.HasValue ? context.Request.Host.Value : "(null)";
        var origin = context.Request.Headers.Origin.ToString();
        var userAgent = context.Request.Headers.UserAgent.ToString();
        return $"method={context.Request.Method}, path={context.Request.Path}, host={host}, remoteIp={remoteIp}, origin={TrimForLog(origin, 96)}, ua={TrimForLog(userAgent, 96)}";
    }

    private static string FormatRpcIdForLog(object? id)
    {
        if (id is null)
        {
            return "(null)";
        }

        return TrimForLog(id.ToString() ?? "(null)", 64);
    }

    private static string TrimForLog(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "-";
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength
            ? trimmed
            : $"{trimmed[..maxLength]}...";
    }

    private bool TryNormalizeAllowedOrigin(string origin, out string normalized)
    {
        normalized = string.Empty;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!_startOptions.AllowRemoteOrigins)
        {
            if (!IsAllowedRequestHost(uri.Host))
            {
                return false;
            }
        }
        else if (string.IsNullOrWhiteSpace(uri.Host))
        {
            return false;
        }

        normalized = uri.GetLeftPart(UriPartial.Authority);
        return true;
    }

    private bool IsAllowedRemoteIp(IPAddress? remoteIp)
    {
        if (remoteIp is null)
        {
            return false;
        }

        if (IPAddress.IsLoopback(remoteIp))
        {
            return true;
        }

        if (remoteIp.IsIPv4MappedToIPv6)
        {
            remoteIp = remoteIp.MapToIPv4();
        }

        if (remoteIp.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        return _allowedWslSubnets.Any(subnet => subnet.Contains(remoteIp));
    }

    private void RefreshLocalNetworkAllowlist()
    {
        _allowedWslSubnets = DetectWslSubnets();
        _localIpv4Hosts = DetectLocalIpv4Hosts();
    }

    private static IReadOnlyList<Ipv4Subnet> DetectWslSubnets()
    {
        var results = new List<Ipv4Subnet>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            var name = nic.Name ?? string.Empty;
            var description = nic.Description ?? string.Empty;
            var isWslAdapter =
                name.Contains("WSL", StringComparison.OrdinalIgnoreCase) ||
                description.Contains("WSL", StringComparison.OrdinalIgnoreCase);

            if (!isWslAdapter)
            {
                continue;
            }

            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork ||
                    unicast.IPv4Mask is null)
                {
                    continue;
                }

                results.Add(new Ipv4Subnet(unicast.Address, unicast.IPv4Mask));
            }
        }

        return results
            .Distinct()
            .ToArray();
    }

    private static HashSet<string> DetectLocalIpv4Hosts()
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    values.Add(unicast.Address.ToString());
                }
            }
        }

        return values;
    }

    private static string BuildScalarHtml()
    {
        return """
<!doctype html>
<html>
  <head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1" />
    <title>AiyoPerps API Reference</title>
  </head>
  <body>
    <script id="api-reference" data-url="/openapi/v1.json"></script>
    <script src="https://cdn.jsdelivr.net/npm/@scalar/api-reference"></script>
  </body>
</html>
""";
    }

    private sealed class McpRpcRequest
    {
        public string Jsonrpc { get; set; } = "2.0";
        public string Method { get; set; } = string.Empty;
        public JsonElement Params { get; set; }
        public JsonElement Id { get; set; }
    }

    private readonly record struct Ipv4Subnet(uint Network, uint Mask)
    {
        public Ipv4Subnet(IPAddress address, IPAddress mask)
            : this(
                ToUint(address) & ToUint(mask),
                ToUint(mask))
        {
        }

        public string DisplayText => $"{ToDisplayIp(Network)}/{PrefixLength(Mask)}";

        public bool Contains(IPAddress address)
            => (ToUint(address) & Mask) == Network;

        private static uint ToUint(IPAddress address)
        {
            var bytes = address.GetAddressBytes();
            if (bytes.Length != 4)
            {
                throw new ArgumentException("IPv4 address required.", nameof(address));
            }

            return ((uint)bytes[0] << 24) |
                   ((uint)bytes[1] << 16) |
                   ((uint)bytes[2] << 8) |
                   bytes[3];
        }

        private static string ToDisplayIp(uint value)
            => new IPAddress(
            [
                (byte)(value >> 24),
                (byte)(value >> 16),
                (byte)(value >> 8),
                (byte)value
            ]).ToString();

        private static int PrefixLength(uint mask)
        {
            var count = 0;
            while (mask != 0)
            {
                count += (int)(mask & 1);
                mask >>= 1;
            }

            return count;
        }
    }
}

public sealed class LocalApiServerStartOptions
{
    public bool BindLocalOnly { get; init; } = true;
    public bool AllowRemoteOrigins { get; init; }

    public string BindLocalOnlyLabel => BindLocalOnly ? "localhost-only (+winhost)" : "all interfaces";
}
