using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
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

    private readonly TradingApiService _trading;
    private readonly ApiOperationStore _operations = new();
    private readonly AppLogger _logger;
    private readonly Func<string, Task>? _requestShutdown;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private WebApplication? _app;
    private LocalApiServerStartOptions _startOptions = new();

    public LocalApiServer(TradingApiService trading, AppLogger logger, Func<string, Task>? requestShutdown = null)
    {
        _trading = trading;
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
            if (!IsAllowedRequestHost(context.Request.Host.Host))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { error = "Forbidden host." });
                return;
            }

            var origin = context.Request.Headers.Origin.ToString();
            if (!string.IsNullOrWhiteSpace(origin))
            {
                if (!TryNormalizeAllowedOrigin(origin, out var normalizedOrigin))
                {
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
                    return;
                }
            }

            await next();
        });

        app.MapGet("/api/v1/health", () => Results.Ok(new
        {
            status = "ok",
            server = "AiyoPerps Local API",
            port = Port,
            running = IsRunning,
            utcNow = DateTimeOffset.UtcNow,
            bindScope = _startOptions.BindLocalOnlyLabel
        }));

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
                    return Results.BadRequest(new { jsonrpc = "2.0", error = new { code = -32600, message = "Invalid JSON-RPC request." }, id = (object?)null });
                }

                var response = await HandleMcpAsync(request, context.RequestAborted);
                return Results.Json(response);
            }
            catch (Exception ex)
            {
                _logger.Error("Api", "MCP request failed", ex);
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
            return MakeResult(new
            {
                protocolVersion = "2025-01-01",
                serverInfo = new { name = "AiyoPerps MCP", version = "1.0.0" },
                capabilities = new { tools = new { } }
            });
        }

        if (string.Equals(method, "ping", StringComparison.OrdinalIgnoreCase))
        {
            return MakeResult(new { ok = true, utcNow = DateTimeOffset.UtcNow });
        }

        if (string.Equals(method, "tools/list", StringComparison.OrdinalIgnoreCase))
        {
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
            return MakeResult(new
            {
                content = new[]
                {
                    new
                    {
                        type = "text",
                        text = JsonSerializer.Serialize(payload)
                    }
                },
                structuredContent = payload
            });
        }
        catch (Exception ex)
        {
            return MakeError(-32000, ex.Message);
        }
    }

    private async Task<object> ExecuteMcpToolAsync(string toolName, JsonElement args, CancellationToken cancellationToken)
    {
        return toolName switch
        {
            "accounts.list" => _trading.ListAccounts(),
            "accounts.get" => _trading.GetAccount(ReadGuid(args, "accountId")),
            "accounts.create" => _trading.CreateAccount(Read<ApiAccountUpsertRequest>(args)),
            "accounts.update" => _trading.UpdateAccount(ReadGuid(args, "accountId"), Read<ApiAccountUpsertRequest>(args)),
            "accounts.delete" => QueueMcpOperation("delete-account", async ct =>
            {
                await _trading.DeleteAccountAsync(ReadGuid(args, "accountId"));
                return new { deleted = true };
            }),
            "symbols.list" => _trading.ListSymbols(ReadGuid(args, "accountId")),
            "connections.list" => _trading.ListConnections(),
            "connections.open" => QueueMcpOperation("open-connection", async ct =>
            {
                var req = Read<ApiConnectionOpenRequest>(args);
                return await _trading.OpenConnectionAsync(req.AccountId, req.Symbol, req.Interval, ct);
            }),
            "connections.close" => QueueMcpOperation("close-connection", async ct =>
            {
                var req = Read<ApiConnectionCloseRequest>(args);
                var closed = await _trading.CloseConnectionAsync(req.AccountId, req.Symbol);
                return new { req.AccountId, req.Symbol, closed };
            }),
            "market.snapshot" => await _trading.GetMarketDataAsync(
                ReadGuid(args, "accountId"),
                ReadString(args, "symbol"),
                ReadString(args, "interval", "5m"),
                ReadNullableLong(args, "cursor"),
                cancellationToken),
            "market_data.get" => await _trading.GetMarketDataAsync(
                ReadGuid(args, "accountId"),
                ReadString(args, "symbol"),
                ReadString(args, "interval", "5m"),
                ReadNullableLong(args, "cursor"),
                cancellationToken),
            "positions.list" => await _trading.ListPositionsAsync(
                ReadGuid(args, "accountId"),
                ReadOptionalString(args, "symbol"),
                cancellationToken),
            "orders.list" => await _trading.ListOpenOrdersAsync(
                ReadGuid(args, "accountId"),
                ReadOptionalString(args, "symbol"),
                cancellationToken),
            "balances.list" => await _trading.ListBalancesAsync(
                ReadGuid(args, "accountId"),
                ReadOptionalString(args, "symbol"),
                cancellationToken),
            "positions.open" => QueueMcpOperation("open-position", async ct => await _trading.OpenPositionAsync(Read<ApiOpenPositionRequest>(args), ct)),
            "positions.close" => QueueMcpOperation("close-position", async ct => await _trading.ClosePositionAsync(Read<ApiClosePositionRequest>(args), ct)),
            "orders.cancel" => QueueMcpOperation("cancel-order", async ct => await _trading.CancelOrderAsync(Read<ApiCancelOrderRequest>(args), ct)),
            "stress.run" => QueueMcpOperation("stress-run", async ct => await _trading.RunStressAsync(Read<ApiStressRunRequest>(args), ct)),
            "app.shutdown" => RequestShutdownFromTool(),
            "operations.get" => _operations.Get(ReadString(args, "operationId")) ?? throw new ApiNotFoundException("Operation not found."),
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

    private static object[] BuildMcpTools()
    {
        return
        [
            Tool("accounts.list", "List all configured accounts."),
            Tool("accounts.get", "Get one account by accountId."),
            Tool("accounts.create", "Create a new account profile with credentials."),
            Tool("accounts.update", "Update account profile and credentials by accountId."),
            Tool("accounts.delete", "Delete account by accountId (async operation)."),
            Tool("symbols.list", "List tradable symbols for one account."),
            Tool("connections.list", "List active market-data connections."),
            Tool("connections.open", "Open connection by accountId + symbol (async operation)."),
            Tool("connections.close", "Close connection by accountId + symbol (async operation)."),
            Tool("market.snapshot", "Get initial snapshot or cursor-based candle delta."),
            Tool("market_data.get", "Get initial or delta candle data by cursor."),
            Tool("positions.list", "List active positions."),
            Tool("orders.list", "List open orders (exchange open orders)."),
            Tool("balances.list", "List balances."),
            Tool("positions.open", "Open position (async order operation)."),
            Tool("positions.close", "Close position by positionId (async order operation)."),
            Tool("orders.cancel", "Cancel order by orderId (async operation)."),
            Tool("stress.run", "Run server-side market snapshot stress test."),
            Tool("app.shutdown", "Request graceful app shutdown and resource release."),
            Tool("operations.get", "Query async operation status by operationId.")
        ];
    }

    private static object Tool(string name, string description)
    {
        return new
        {
            name,
            description,
            inputSchema = new
            {
                type = "object"
            }
        };
    }

    private static T Read<T>(JsonElement element)
    {
        if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw new ApiBadRequestException("arguments is required.");
        }

        var obj = element.Deserialize<T>();
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

    private bool IsAllowedRequestHost(string? host)
    {
        if (!_startOptions.BindLocalOnly)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var h = host.Trim().ToLowerInvariant();
        return h is "localhost" or "127.0.0.1" or "::1" or "[::1]" or "winhost";
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
}

public sealed class LocalApiServerStartOptions
{
    public bool BindLocalOnly { get; init; } = true;
    public bool AllowRemoteOrigins { get; init; }

    public string BindLocalOnlyLabel => BindLocalOnly ? "localhost-only (+winhost)" : "all interfaces";
}
