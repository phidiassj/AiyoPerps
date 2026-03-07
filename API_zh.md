# AiyoPerps 本機 API 與 MCP 說明

本文件說明 AiyoPerps 目前的 REST API、MCP 端點、npm bridge，以及 AI Agent 安裝器的實際行為。

## 1. 服務模型
- REST 基底：`http://127.0.0.1:5078/api/v1`
- MCP 端點：`http://127.0.0.1:5078/mcp`
- OpenAPI 介面：`http://127.0.0.1:5078/scalar`
- 桌面版可從上方工具列啟動或關閉本機 HTTP 服務。
- headless 模式會自動啟動 HTTP 服務。

### 本機存取規則
- 允許的本機 host 包含 `127.0.0.1`、`localhost`、`::1`、`winhost`。
- 在 Windows 上，AiyoPerps 也會允許偵測到的 WSL vEthernet 子網段來源。
- 非本機 host 或不合法的本機 `Origin` 會回 `403`。

## 2. Headless 與 HTTP 啟動
### 桌面版
在工具列開啟 `HTTP API`，並在啟用前設定埠號。

### Headless
```bash
dotnet run --project AiyoPerps/AiyoPerps.csproj -- headless --port 5078
```

### Docker
```bash
docker run --rm -p 5078:5078 phidiassj/aiyoperps:latest
```

## 3. REST API

## 3.1 健康檢查與關閉
### `GET /api/v1/health`
回傳目前服務狀態。

常見欄位：
- `status`
- `port`
- `allowedWslSubnets`

### `POST /api/v1/app/shutdown`
要求程式優雅關閉。

## 3.2 帳號
### `GET /api/v1/accounts`
列出所有已設定帳號。

### `GET /api/v1/accounts/{accountId}`
Path 參數：
- `accountId`（`uuid`，必填）

### `POST /api/v1/accounts`
### `PUT /api/v1/accounts/{accountId}`
Body：`ApiAccountUpsertRequest`
- `venueId`（`string`，必填）：`BitMEX`、`Hyperliquid` 或 `Aster`
- `displayName`（`string`，必填）
- `environment`（`string`，必填）：`mainnet` 或 `testnet`
- `summary`（`string`，必填）
- `apiKey`（`string`，可選）
- `apiSecret`（`string`，可選）
- `accountAddress`（`string`，可選）
- `walletAddress`（`string`，可選）
- `privateKey`（`string`，可選）
- `isEnabled`（`boolean`，可選，更新時可用）

### Aster 帳號說明
- Aster 私有端點使用 V3 signer 驗證模型。
- 針對 `Aster` 的已驗證操作，請填入：
  - `accountAddress`：主帳戶錢包地址（`user`）
  - `walletAddress`：API 錢包地址（`signer`）
  - `privateKey`：API 錢包（`signer`）私鑰
- 若未提供上述資料，Aster 仍可使用公開行情，但交易/帳務端點會失敗。
- 本軟體會強制 Aster 使用單向持倉模式。若帳戶為雙向模式且無法切換為單向，則下單請求會被拒絕。

### `DELETE /api/v1/accounts/{accountId}`
啟動非同步刪除作業。

## 3.3 Symbols
### `GET /api/v1/accounts/{accountId}/symbols`
### `GET /api/v1/symbols?accountId=<GUID>`
兩者都會回傳目標帳號可交易的商品清單。

## 3.4 連線
### `GET /api/v1/connections`
列出目前活動中的後端 session。

### `POST /api/v1/connections/open`
Body：`ApiConnectionOpenRequest`
- `accountId`（`uuid`，必填）
- `symbol`（`string`，必填）
- `interval`（`string`，必填）：`5m|10m|15m|30m|1h|2h|4h|6h|12h|1d|7d|30d`

### `POST /api/v1/connections/close`
Body：`ApiConnectionCloseRequest`
- `accountId`（`uuid`，必填）
- `symbol`（`string`，必填）

唯一性規則：每個 `accountId + symbol` 只會有一個後端 session。
若 UI 與 API 同時開啟同一組連線，兩者會共用同一個後端連線。

## 3.5 市場資料
### `GET /api/v1/market-data`
Query 參數：
- `accountId`（`uuid`，必填）
- `symbol`（`string`，必填）
- `interval`（`string`，可選，預設 `5m`）
- `cursor`（`integer`，可選）

### `GET /api/v1/market/snapshot`
參數與 cursor 模型同 `/market-data`。

### Cursor 模式
1. 第一次不帶 `cursor`：回傳 `initialCandles` 與新的 `cursor`。
2. 後續帶入 `cursor`：只回傳增量 `deltaCandles` 與新的 `cursor`。

## 3.6 帳戶狀態
### `GET /api/v1/positions`
Query 參數：
- `accountId`（`uuid`，必填）
- `symbol`（`string`，可選）

### `GET /api/v1/orders`
Query 參數：
- `accountId`（`uuid`，必填）
- `symbol`（`string`，可選）

### `GET /api/v1/balances`
Query 參數：
- `accountId`（`uuid`，必填）
- `symbol`（`string`，可選）

## 3.7 交易
### `POST /api/v1/trading/open-position`
Body：`ApiOpenPositionRequest`
- `accountId`（`uuid`，必填）
- `symbol`（`string`，必填）
- `side`（`string`，必填）：`buy|sell|long|short`
- `orderType`（`string`，必填）：`market|limit`
- `leverage`（`number`，必填）
- `amount`（`number`，必填）
- `amountUnit`（`string`，必填）：`USD` 或標的幣別
- `limitPrice`（`number`，限價單必填）

### `POST /api/v1/trading/close-position`
Body：`ApiClosePositionRequest`
- `accountId`（`uuid`，必填）
- `positionId`（`string`，必填）
- `orderType`（`string`，必填）：`market|limit`
- `limitPrice`（`number`，限價單必填）

### `POST /api/v1/trading/cancel-order`
Body：`ApiCancelOrderRequest`
- `accountId`（`uuid`，必填）
- `symbol`（`string`，必填）
- `orderId`（`string`，必填）

交易所原始錯誤內容會保留在非同步 operation 的 `error` 中。

## 3.8 壓測
### `POST /api/v1/stress/run`
Body：`ApiStressRunRequest`
- `accountId`（`uuid`，必填）
- `symbol`（`string`，必填）
- `interval`（`string`，可選，預設 `5m`）
- `concurrency`（`integer`，可選，`1..64`，預設 `8`）
- `iterations`（`integer`，可選，`1..20000`，預設 `200`）

## 3.9 非同步作業
多數修改型呼叫會先回傳非同步 envelope。

### `GET /api/v1/operations/{operationId}`
Path 參數：
- `operationId`（`string`，必填）

狀態值：
- `Pending`
- `Running`
- `Succeeded`
- `Failed`

常見回應：
```json
{
  "operationId": "string",
  "status": "Pending",
  "createdAt": "2026-02-25T10:00:00+00:00",
  "statusUrl": "/api/v1/operations/<operationId>"
}
```

## 4. MCP 端點
- 端點：`POST /mcp`
- 傳輸：HTTP 上的 JSON-RPC
- 支援 RPC method：
  - `initialize`
  - `ping`
  - `tools/list`
  - `tools/call`

### Initialize 行為
- 若 client 傳入 `protocolVersion`，伺服器會原樣回應該版本。
- 這是目前相容 Claude Desktop 所必需的行為。

## 4.1 Tool 名稱
目前公開的 MCP tool 名稱使用底線，不使用句點。

可用工具：
- `accounts_list`
- `accounts_get`
- `accounts_create`
- `accounts_update`
- `accounts_delete`
- `symbols_list`
- `connections_list`
- `connections_open`
- `connections_close`
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

向後相容：舊的句點名稱會在內部自動轉換，但新 client 應使用底線名稱。

## 4.2 Tool 參數
### 大小寫處理
`tools/call.arguments` 會以不區分大小寫方式反序列化，因此 `camelCase` 與 `PascalCase` 都可用。

### 參數對照
- `accounts_list`：無參數
- `accounts_get`：`accountId`
- `accounts_create`：同 `ApiAccountUpsertRequest`
- `accounts_update`：`accountId` 加上 `ApiAccountUpsertRequest`
- `accounts_delete`：`accountId`
- `symbols_list`：`accountId`
- `connections_list`：無參數
- `connections_open`：`accountId`、`symbol`、`interval`
- `connections_close`：`accountId`、`symbol`
- `market_snapshot`：`accountId`、`symbol`、可選 `interval`、可選 `cursor`
- `market_data_get`：`accountId`、`symbol`、可選 `interval`、可選 `cursor`
- `positions_list`：`accountId`、可選 `symbol`
- `orders_list`：`accountId`、可選 `symbol`
- `balances_list`：`accountId`、可選 `symbol`
- `positions_open`：同 `ApiOpenPositionRequest`
- `positions_close`：同 `ApiClosePositionRequest`
- `orders_cancel`：同 `ApiCancelOrderRequest`
- `stress_run`：同 `ApiStressRunRequest`
- `operations_get`：`operationId`
- `app_shutdown`：無參數

## 4.3 Tool Schema 結構
`tools/list` 目前會回傳完整 object schema，包含：
- `type`
- `properties`
- `required`
- `additionalProperties`
- 適用時的 `enum`
- 適用時的 `format: "uuid"`

## 4.4 Tool 結果格式
AiyoPerps 目前會為所有成功的 `tools/call` 回傳固定 envelope。

範例：
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

規則：
- `structuredContent` 一律是 object。
- 若原始 payload 本來就是 object，直接作為 `result`。
- 若原始 payload 是陣列或單一值，會先包成 `{ "value": ... }` 再放入 `result`。
- 這可避免 Claude Desktop 之類宿主的前端驗證錯誤。

## 5. Stdio MCP Bridge
套件：
- `@phidiassj/aiyoperps-mcp-bridge`

本 repo 路徑：
- `npm/aiyoperps-mcp-bridge`

用途：
- 接收 `stdio` 上的 MCP
- 轉發到 AiyoPerps 的 HTTP MCP

### 支援的 stdio 格式
bridge 同時支援：
- 傳統 `Content-Length` framed MCP
- 某些宿主使用的 newline-delimited JSON（例如 Codex 與 Claude Desktop）

bridge 會依照偵測到的輸入格式，使用相同格式輸出。

### CLI 參數
- `--url <endpoint>`：目標 MCP 端點
- `--quiet`：抑制非錯誤診斷訊息
- `--debug-log <path>`：將 bridge 診斷資訊寫入檔案
- `--health-check`：對上游送 `ping` 後結束
- `--startup-ping`：進入 stdio 模式前先驗證上游
- `--timeout-ms <number>`：上游請求逾時
- `--help`：顯示說明

### 環境變數
- `AIYOPERPS_MCP_URL`：未傳 `--url` 時的 fallback

### 範例
```bash
npx -y @phidiassj/aiyoperps-mcp-bridge --quiet --url http://127.0.0.1:5078/mcp
```

```bash
npx -y @phidiassj/aiyoperps-mcp-bridge --health-check --url http://127.0.0.1:5078/mcp
```

## 6. 互動式 MCP 宿主安裝器
套件：
- `@phidiassj/aiyoperps-mcp-installer`

用途：
- 偵測支援的宿主
- 探測目前執行環境實際可連的 MCP URL
- 安裝、更新、移除 `aiyoperps` 的 MCP 註冊

支援宿主：
- `Codex`
- `Claude Code CLI`
- `Claude Desktop`
- `OpenClaw`

### 互動模式
```bash
npx -y @phidiassj/aiyoperps-mcp-installer
```

### 非互動模式
```bash
npx -y @phidiassj/aiyoperps-mcp-installer --status
npx -y @phidiassj/aiyoperps-mcp-installer --install-all
npx -y @phidiassj/aiyoperps-mcp-installer --install codex,claude-desktop
npx -y @phidiassj/aiyoperps-mcp-installer --uninstall all
```

### 指定 endpoint
```bash
npx -y @phidiassj/aiyoperps-mcp-installer --url http://127.0.0.1:9090/mcp
```

### 安裝行為
- 安裝器會先探測可達的 endpoint，再寫入宿主設定。
- 若找不到任何可達 MCP endpoint，會直接退出，不改設定。
- 在 Windows 的 Claude Desktop 上，安裝器會更新所有偵測到的 `claude_desktop_config.json`。
- 在支援的環境下，Codex 會優先使用穩定的本機 `node + bridge.js` 啟動方式，而不是每次用 `npx`。

### 僅供測試的路徑覆寫
- `CODEX_CONFIG_PATH`
- `CLAUDE_DESKTOP_CONFIG_PATH`
- `OPENCLAW_CONFIG_PATH`

## 6.1 驗證 OpenClaw 安裝
安裝到 OpenClaw 後，請透過 `mcporter` 驗證註冊是否成功。

### 檢查 home config
```bash
cat ~/.mcporter/mcporter.json
```
你應該會在 `mcpServers` 底下看到 `aiyoperps`。

### 列出已註冊的 server
```bash
npx -y mcporter config list --scope home
```

### 列出 AiyoPerps 工具
```bash
npx -y mcporter list aiyoperps --scope home --json
```
若成功，應該會看到 `accounts_list`、`connections_list`、`market_snapshot` 等工具。

### 呼叫一個唯讀工具
```bash
npx -y mcporter call aiyoperps.connections_list --scope home --json
```
若這一步成功，就代表 OpenClaw + mcporter 整合正常，且設定的 MCP endpoint 可連線。

## 7. 宿主設定檔位置

## 7.1 Codex CLI
常見設定檔：
- Linux / WSL：`~/.codex/config.toml`
- macOS：`~/.codex/config.toml`
- Windows：`%USERPROFILE%\.codex\config.toml`

## 7.2 Claude Desktop
常見設定檔：
- Windows（一般安裝）：`%APPDATA%\Claude\claude_desktop_config.json`
- Windows（Microsoft Store）：`%LOCALAPPDATA%\Packages\Claude_*\LocalCache\Roaming\Claude\claude_desktop_config.json`
- macOS：`~/Library/Application Support/Claude/claude_desktop_config.json`
- Linux：`~/.config/Claude/claude_desktop_config.json`

## 7.3 Claude Code CLI
若 `PATH` 中可找到 `claude` 指令，installer 會優先使用官方 CLI。
實際設定檔位置可能會依版本而不同，通常由官方 CLI 自行管理。

## 7.4 OpenClaw
常見位置依安裝方式而異，常見 JSON 設定檔為：
- Windows：`%USERPROFILE%\.openclaw\openclaw.json`
- Linux / macOS：`~/.config/openclaw/config.json`

## 8. 範例

## 8.1 cURL（REST）
```bash
curl http://127.0.0.1:5078/api/v1/health

curl -X POST http://127.0.0.1:5078/api/v1/connections/open \
  -H "Content-Type: application/json" \
  -d '{"accountId":"<GUID>","symbol":"XBTUSD","interval":"5m"}'

curl http://127.0.0.1:5078/api/v1/operations/<operationId>
```

## 8.2 原生 HTTP MCP
```bash
curl -X POST http://127.0.0.1:5078/mcp \
  -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}'
```

## 8.3 MCP 宿主設定範例
### 通用 JSON 設定
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

### Codex TOML 範例
```toml
[mcp_servers.aiyoperps]
startup_timeout_sec = 60
command = "npx"
args = ["-y", "@phidiassj/aiyoperps-mcp-bridge", "--quiet", "--url", "http://127.0.0.1:5078/mcp"]
```
