# AiyoPerps Local API and MCP Guide

This document describes the current REST API, MCP endpoint, npm bridge, and host installer behavior for AiyoPerps.

## 1. Service Model
- REST base: `http://127.0.0.1:5078/api/v1`
- MCP endpoint: `http://127.0.0.1:5078/mcp`
- OpenAPI UI: `http://127.0.0.1:5078/scalar`
- The desktop UI can start or stop the local HTTP server from the top toolbar.
- Headless mode starts the HTTP server automatically.

### Local access rules
- Allowed local hosts include `127.0.0.1`, `localhost`, `::1`, and `winhost`.
- On Windows, AiyoPerps also allows requests from the detected WSL vEthernet subnet.
- Non-local hosts or invalid local origins are rejected with `403`.

## 1.1 Agent Usage Guidance
This section is intended for users who wake AI agents from the app. AiyoPerps MCP provides state inspection and execution pathways. It does not define trading strategy and does not decide whether an agent should only analyze, recommend actions, or execute trades.

### Core principles
- AiyoPerps MCP is the source of truth for live trading state and verified execution results.
- External research, news, reports, and Automation-generated `md` files should be gathered or read by the agent as instructed by the user, then validated against live AiyoPerps MCP state.
- The agent should never invent `accountId`, `symbol`, `positionId`, or `orderId`; it should read them from MCP results.
- If the user wants analysis or recommendations only, the prompt should say so explicitly.
- The agent should use mutating tools only when the user explicitly authorizes execution.

### Common Dashboard MCP tools
- `dashboard_status_get`: read dashboard runtime status and row counts.
- `dashboard_options_get`: read selectable accounts and symbol options.
- `dashboard_config_get`: read the current dashboard configuration.
- `dashboard_config_set`: update the current dashboard configuration.
- `dashboard_snapshot_get`: read the latest dashboard snapshot, including market rows, positions, and orders.
- `dashboard_start`: start the dashboard runtime.
- `dashboard_refresh`: refresh the dashboard runtime immediately.
- `dashboard_stop`: stop the dashboard runtime.
- `dashboard_positions_open`: create or add exposure on a dashboard account row.
- `dashboard_positions_close`: fully close an existing position.
- `dashboard_orders_cancel`: cancel an existing open order.

### Other common read tools
- `positions_list`: read active positions for one account directly.
- `orders_list`: read exchange open orders for one account directly.
- `balances_list`: read balances for one account directly.
- `market_snapshot`: read current or incremental candle snapshots.
- `market_data_get`: read market candle data.
- `operations_get`: read async operation progress and results.

### Recommended operating flow
1. Read external reference files first when the user prompt explicitly points to them.
2. Call `dashboard_status_get` to inspect the current runtime state.
3. Use direct read tools first when the task only needs one account's positions, orders, balances, or market data. These tools do not require `dashboard_start`.
4. If the task needs the integrated Dashboard snapshot or Dashboard-selected account/symbol context, use `dashboard_options_get` and `dashboard_config_set`, then call `dashboard_start` when the runtime is not ready.
5. After every mutating Dashboard tool call, poll `operations_get` until the status is `Succeeded` or `Failed`.
6. Call `dashboard_snapshot_get` to inspect current market rows, positions, and open orders when Dashboard runtime is in use.
7. Use `positions_list`, `orders_list`, `balances_list`, and `market_snapshot` for extra verification when needed.
8. After any actual trading action, call `dashboard_refresh`, wait again with `operations_get`, then read `dashboard_snapshot_get` again to verify the final state when Dashboard runtime is in use.

### Operational limitations
- `dashboard_positions_open` can be used to add exposure or offset part of an existing position, but it is not a dedicated reduce-only partial-close API.
- `dashboard_positions_close` is the full-close operation for an existing position.
- MCP tool calls are handled without opening adapter workspace tabs. Agent-driven reads and writes should not depend on UI tab creation.
- The old dashboard market-news cache has been removed. If the agent needs external market context, it must read files or gather information from the web on its own.

## 2. Headless and HTTP Startup
### Desktop UI
Enable `HTTP API` in the toolbar and choose the port before turning it on.

### Headless
```bash
dotnet run --project AiyoPerps/AiyoPerps.csproj -- headless --port 5078
```

### Docker
```bash
docker run --rm -p 5078:5078 phidiassj/aiyoperps:latest
```

## 3. REST API

## 3.1 Health and App
### `GET /api/v1/health`
Returns current service status.

Typical response fields:
- `status`
- `port`
- `allowedWslSubnets`

### `POST /api/v1/app/shutdown`
Requests graceful application shutdown.

## 3.2 Dashboard
Dashboard endpoints drive the integrated Dashboard runtime.

### `GET /api/v1/dashboard/status`
Returns dashboard runtime state, current configuration, and row counts.

Typical response fields:
- `isRunning`
- `configuration`
- `updatedAt`
- `counts.markets`
- `counts.positions`
- `counts.orders`

### `GET /api/v1/dashboard/options`
Returns the current dashboard configuration together with selectable account and symbol options.

Typical response fields:
- `configuration`
- `accounts`
- `symbols`

### `GET /api/v1/dashboard/config`
Returns the current dashboard configuration only.

### `PUT /api/v1/dashboard/config`
Body: `ApiDashboardConfigurationRequest`
- `selectedAccountIds` (`uuid[]`, required): selected dashboard account ids
- `symbol` (`string`, optional): selected dashboard symbol key
- `interval` (`string`, optional, default `5m`)
- `showTestnet` (`boolean`, required)

Returns the updated dashboard snapshot.

### `GET /api/v1/dashboard/snapshot`
Returns the latest dashboard snapshot used by the UI.

### `POST /api/v1/dashboard/start`
Queues dashboard startup with the current configuration.

### `POST /api/v1/dashboard/stop`
Queues dashboard shutdown and clears runtime data.

### `POST /api/v1/dashboard/refresh`
Queues an immediate dashboard refresh.

### `POST /api/v1/dashboard/open-position`
Body: `ApiOpenPositionRequest`

Queues a dashboard order-entry operation for the selected account row.

### `POST /api/v1/dashboard/close-position`
Body: `ApiClosePositionRequest`

Queues a dashboard close-position operation.

### `POST /api/v1/dashboard/cancel-order`
Body: `ApiCancelOrderRequest`

Queues a dashboard cancel-order operation.

### Dashboard async behavior
The following dashboard REST calls return the standard async operation envelope documented in `3.10`:
- `POST /api/v1/dashboard/start`
- `POST /api/v1/dashboard/stop`
- `POST /api/v1/dashboard/refresh`
- `POST /api/v1/dashboard/open-position`
- `POST /api/v1/dashboard/close-position`
- `POST /api/v1/dashboard/cancel-order`

## 3.3 Accounts
### `GET /api/v1/accounts`
List all configured accounts.

### `GET /api/v1/accounts/{accountId}`
Path params:
- `accountId` (`uuid`, required)

### `POST /api/v1/accounts`
### `PUT /api/v1/accounts/{accountId}`
Body: `ApiAccountUpsertRequest`
- `venueId` (`string`, required): `BitMEX`, `Hyperliquid`, `Aster`, `GRVT`, or `dYdX`
- `displayName` (`string`, required)
- `environment` (`string`, required): `mainnet` or `testnet`
- `summary` (`string`, required)
- `apiKey` (`string`, optional)
- `apiSecret` (`string`, optional)
- `accountAddress` (`string`, optional)
- `walletAddress` (`string`, optional)
- `privateKey` (`string`, optional)
- `isEnabled` (`boolean`, optional, update only)

### Aster account notes
- Aster private endpoints use the V3 signer model.
- For authenticated operations on `Aster`, set:
  - `accountAddress`: main account wallet address (`user`)
  - `walletAddress`: API wallet address (`signer`)
  - `privateKey`: private key of the API wallet (`signer`)
- If these are missing, Aster can still use public market data but trading/account-state endpoints will fail.
- The app enforces one-way position mode for Aster trading. If your account is in hedge mode and cannot switch to one-way mode, order requests are rejected.

### `DELETE /api/v1/accounts/{accountId}`
Starts an async delete operation.

## 3.4 Symbols
### `GET /api/v1/accounts/{accountId}/symbols`
### `GET /api/v1/symbols?accountId=<GUID>`
Both return the tradable symbol catalog for the target account.

## 3.5 Connections
### `GET /api/v1/connections`
List active backend sessions.

### `POST /api/v1/connections/open`
Body: `ApiConnectionOpenRequest`
- `accountId` (`uuid`, required)
- `symbol` (`string`, required)
- `interval` (`string`, required): `5m|10m|15m|30m|1h|2h|4h|6h|12h|1d|7d|30d`

### `POST /api/v1/connections/close`
Body: `ApiConnectionCloseRequest`
- `accountId` (`uuid`, required)
- `symbol` (`string`, required)

Uniqueness rule: one backend session per `accountId + symbol`.
If the same session is opened by UI and API, they share one backend connection.

## 3.6 Market Data
### `GET /api/v1/market-data`
Query params:
- `accountId` (`uuid`, required)
- `symbol` (`string`, required)
- `interval` (`string`, optional, default `5m`)
- `cursor` (`integer`, optional)

### `GET /api/v1/market/snapshot`
Same parameters and same cursor model as `/market-data`.

### Cursor pattern
1. First request without `cursor`: returns `initialCandles` and a new `cursor`.
2. Later request with `cursor`: returns only incremental `deltaCandles` and a new `cursor`.

## 3.7 Account State
### `GET /api/v1/positions`
Query params:
- `accountId` (`uuid`, required)
- `symbol` (`string`, optional)

### `GET /api/v1/orders`
Query params:
- `accountId` (`uuid`, required)
- `symbol` (`string`, optional)

### `GET /api/v1/balances`
Query params:
- `accountId` (`uuid`, required)
- `symbol` (`string`, optional)

## 3.8 Trading
### `POST /api/v1/trading/open-position`
Body: `ApiOpenPositionRequest`
- `accountId` (`uuid`, required)
- `symbol` (`string`, required)
- `side` (`string`, required): `buy|sell|long|short`
- `orderType` (`string`, required): `market|limit`
- `leverage` (`number`, required)
- `amount` (`number`, required)
- `amountUnit` (`string`, required): `USD` or base asset symbol
- `limitPrice` (`number`, required for `limit`)

### `POST /api/v1/trading/close-position`
Body: `ApiClosePositionRequest`
- `accountId` (`uuid`, required)
- `positionId` (`string`, required)
- `orderType` (`string`, required): `market|limit`
- `limitPrice` (`number`, required for `limit`)

### `POST /api/v1/trading/cancel-order`
Body: `ApiCancelOrderRequest`
- `accountId` (`uuid`, required)
- `symbol` (`string`, required)
- `orderId` (`string`, required)

Exchange-side error payloads are preserved in the async operation result.

## 3.9 Stress
### `POST /api/v1/stress/run`
Body: `ApiStressRunRequest`
- `accountId` (`uuid`, required)
- `symbol` (`string`, required)
- `interval` (`string`, optional, default `5m`)
- `concurrency` (`integer`, optional, `1..64`, default `8`)
- `iterations` (`integer`, optional, `1..20000`, default `200`)

## 3.10 Async Operations
Many mutating calls return an async envelope first.

### `GET /api/v1/operations/{operationId}`
Path params:
- `operationId` (`string`, required)

Status values:
- `Pending`
- `Running`
- `Succeeded`
- `Failed`

Typical response:
```json
{
  "operationId": "string",
  "status": "Pending",
  "createdAt": "2026-02-25T10:00:00+00:00",
  "statusUrl": "/api/v1/operations/<operationId>"
}
```

## 4. MCP Endpoint
- Endpoint: `POST /mcp`
- Transport: JSON-RPC over HTTP
- Supported RPC methods:
  - `initialize`
  - `ping`
  - `tools/list`
  - `tools/call`

### Initialize behavior
- The server echoes the client `protocolVersion` when provided.
- This is required for current Claude Desktop compatibility.

## 4.1 Tool Names
Current public MCP tool names use underscores, not dots.

Available tools:
- `accounts_list`
- `accounts_get`
- `accounts_create`
- `accounts_update`
- `accounts_delete`
- `symbols_list`
- `connections_list`
- `connections_open`
- `connections_close`
- `dashboard_status_get`
- `dashboard_options_get`
- `dashboard_config_get`
- `dashboard_config_set`
- `dashboard_snapshot_get`
- `dashboard_start`
- `dashboard_stop`
- `dashboard_refresh`
- `dashboard_positions_open`
- `dashboard_positions_close`
- `dashboard_orders_cancel`
- `market_snapshot`
- `market_data_get`
- `positions_list`
- `positions_open`
- `positions_close`
- `orders_list`
- `orders_cancel`
- `balances_list`
- `stress_run`
- `operations_get`
- `app_shutdown`

Backward compatibility: dotted names are normalized internally, but new clients should use underscore names.

## 4.2 Tool Arguments
### Case handling
`tools/call.arguments` is deserialized case-insensitively. Both `camelCase` and `PascalCase` are accepted.

### Parameter mapping
- `accounts_list`: none
- `accounts_get`: `accountId`
- `accounts_create`: same fields as `ApiAccountUpsertRequest`
- `accounts_update`: `accountId` plus `ApiAccountUpsertRequest`
- `accounts_delete`: `accountId`
- `symbols_list`: `accountId`
- `connections_list`: none
- `connections_open`: `accountId`, `symbol`, `interval`
- `connections_close`: `accountId`, `symbol`
- `dashboard_status_get`: none
- `dashboard_options_get`: none
- `dashboard_config_get`: none
- `dashboard_config_set`: same fields as `ApiDashboardConfigurationRequest`
- `dashboard_snapshot_get`: none
- `dashboard_start`: none
- `dashboard_stop`: none
- `dashboard_refresh`: none
- `dashboard_positions_open`: same fields as `ApiOpenPositionRequest`
- `dashboard_positions_close`: same fields as `ApiClosePositionRequest`
- `dashboard_orders_cancel`: same fields as `ApiCancelOrderRequest`
- `market_snapshot`: `accountId`, `symbol`, optional `interval`, optional `cursor`
- `market_data_get`: `accountId`, `symbol`, optional `interval`, optional `cursor`
- `positions_list`: `accountId`, optional `symbol`
- `orders_list`: `accountId`, optional `symbol`
- `balances_list`: `accountId`, optional `symbol`
- `positions_open`: same fields as `ApiOpenPositionRequest`
- `positions_close`: same fields as `ApiClosePositionRequest`
- `orders_cancel`: same fields as `ApiCancelOrderRequest`
- `stress_run`: same fields as `ApiStressRunRequest`
- `operations_get`: `operationId`
- `app_shutdown`: none

### Dashboard MCP behavior
These dashboard MCP tools return the standard async operation envelope instead of an immediate data payload:
- `dashboard_start`
- `dashboard_stop`
- `dashboard_refresh`
- `dashboard_positions_open`
- `dashboard_positions_close`
- `dashboard_orders_cancel`

These dashboard MCP tools return data directly:
- `dashboard_status_get`
- `dashboard_options_get`
- `dashboard_config_get`
- `dashboard_config_set`
- `dashboard_snapshot_get`

## 4.3 Tool Schema Shape
`tools/list` returns full object schemas with:
- `type`
- `properties`
- `required`
- `additionalProperties`
- `enum` where applicable
- `format: "uuid"` for id fields where relevant

## 4.4 Tool Result Shape
AiyoPerps now returns a consistent MCP result envelope for every successful `tools/call`.

Example:
```json
{
  "content": [
    {
      "type": "text",
      "text": "{\"success\":true,\"result\":{...}}"
    }
  ],
  "structuredContent": {
    "success": true,
    "result": {
      "value": []
    }
  }
}
```

Rules:
- `structuredContent` is always an object.
- If the raw payload is already an object, it is used as `result`.
- If the raw payload is an array or primitive, it is wrapped as `{ "value": ... }` before being placed in `result`.
- This avoids frontend validation failures in hosts such as Claude Desktop.

## 5. Stdio MCP Bridge
Package:
- `@phidiassj/aiyoperps-mcp-bridge`

Local repo path:
- `npm/aiyoperps-mcp-bridge`

Purpose:
- Accept MCP over `stdio`
- Forward requests to AiyoPerps HTTP MCP

### Supported stdio formats
The bridge supports both:
- classic `Content-Length` framed MCP
- newline-delimited JSON used by some hosts (for example Codex and Claude Desktop)

The bridge matches output format to the detected input format.

### CLI options
- `--url <endpoint>`: target MCP endpoint
- `--quiet`: suppress non-error diagnostics on `stderr`
- `--debug-log <path>`: write local bridge diagnostics to a file
- `--health-check`: send upstream `ping` and exit
- `--startup-ping`: verify upstream before entering stdio mode
- `--timeout-ms <number>`: upstream request timeout
- `--help`: print usage

### Environment variables
- `AIYOPERPS_MCP_URL`: fallback if `--url` is not supplied

### Examples
```bash
npx -y @phidiassj/aiyoperps-mcp-bridge --quiet --url http://127.0.0.1:5078/mcp
```

```bash
npx -y @phidiassj/aiyoperps-mcp-bridge --health-check --url http://127.0.0.1:5078/mcp
```

## 6. Interactive MCP Host Installer
Package:
- `@phidiassj/aiyoperps-mcp-installer`

Purpose:
- Detect supported hosts
- Probe which MCP URL is reachable from the current environment
- Install, update, or uninstall the `aiyoperps` MCP registration

Supported hosts:
- `Codex`
- `Claude Code CLI`
- `Claude Desktop`
- `OpenClaw`

### Interactive mode
```bash
npx -y @phidiassj/aiyoperps-mcp-installer
```

### Non-interactive mode
```bash
npx -y @phidiassj/aiyoperps-mcp-installer --status
npx -y @phidiassj/aiyoperps-mcp-installer --install-all
npx -y @phidiassj/aiyoperps-mcp-installer --install codex,claude-desktop
npx -y @phidiassj/aiyoperps-mcp-installer --uninstall all
```

### Endpoint override
```bash
npx -y @phidiassj/aiyoperps-mcp-installer --url http://127.0.0.1:9090/mcp
```

### Install behavior
- The installer probes endpoint candidates before writing config.
- If no MCP endpoint is reachable, it exits without changing host config.
- For Claude Desktop on Windows, the installer updates all detected `claude_desktop_config.json` files.
- For Codex, the installer uses a stable local `node + bridge.js` command instead of per-startup `npx` where supported.

### Test-only path overrides
- `CODEX_CONFIG_PATH`
- `CLAUDE_DESKTOP_CONFIG_PATH`
- `OPENCLAW_CONFIG_PATH`

## 6.1 Verify OpenClaw Installation
After installing AiyoPerps for OpenClaw, verify the registration through `mcporter`.

### Check the home config
```bash
cat ~/.mcporter/mcporter.json
```
You should see an `aiyoperps` entry under `mcpServers`.

### List the registered server
```bash
npx -y mcporter config list --scope home
```

### List tools from AiyoPerps
```bash
npx -y mcporter list aiyoperps --scope home --json
```
A successful result should include tools such as `accounts_list`, `connections_list`, and `market_snapshot`.

### Call a safe read-only tool
```bash
npx -y mcporter call aiyoperps.connections_list --scope home --json
```
If this succeeds, the OpenClaw + mcporter integration is working and the configured MCP endpoint is reachable.

## 7. Host Config Locations

## 7.1 Codex CLI
Typical config file:
- Linux / WSL: `~/.codex/config.toml`
- macOS: `~/.codex/config.toml`
- Windows: `%USERPROFILE%\.codex\config.toml`

## 7.2 Claude Desktop
Typical config files:
- Windows (standard): `%APPDATA%\Claude\claude_desktop_config.json`
- Windows (Microsoft Store): `%LOCALAPPDATA%\Packages\Claude_*\LocalCache\Roaming\Claude\claude_desktop_config.json`
- macOS: `~/Library/Application Support/Claude/claude_desktop_config.json`
- Linux: `~/.config/Claude/claude_desktop_config.json`

## 7.3 Claude Code CLI
The installer prefers the `claude` CLI if it is available in `PATH`.
The exact storage path may vary by release because the official CLI manages its own config.

## 7.4 OpenClaw
Common locations depend on installation style. A common JSON config location is:
- Windows: `%USERPROFILE%\.openclaw\openclaw.json`
- Linux / macOS: `~/.config/openclaw/config.json`

## 8. Examples

## 8.1 cURL (REST)
```bash
curl http://127.0.0.1:5078/api/v1/health

curl -X POST http://127.0.0.1:5078/api/v1/connections/open \
  -H "Content-Type: application/json" \
  -d '{"accountId":"<GUID>","symbol":"XBTUSD","interval":"5m"}'

curl http://127.0.0.1:5078/api/v1/operations/<operationId>
```

## 8.2 Raw HTTP MCP
```bash
curl -X POST http://127.0.0.1:5078/mcp \
  -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}'
```

## 8.3 Example MCP Host Config
### Generic JSON host config
```json
{
  "mcpServers": {
    "aiyoperps": {
      "command": "npx",
      "args": [
        "-y",
        "@phidiassj/aiyoperps-mcp-bridge",
        "--quiet",
        "--url",
        "http://127.0.0.1:5078/mcp"
      ]
    }
  }
}
```

### Codex TOML example
```toml
[mcp_servers.aiyoperps]
startup_timeout_sec = 60
command = "npx"
args = ["-y", "@phidiassj/aiyoperps-mcp-bridge", "--quiet", "--url", "http://127.0.0.1:5078/mcp"]
```
