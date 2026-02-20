# AiyoPerps 本機 API 與 MCP 說明

本文件提供 AiyoPerps 的 REST 與 MCP 連線方式、端點規格、參數說明與程式碼範例，供使用者或 AI Agent 直接接入。

## 1. 服務模型
- 通訊方式：
  - REST：`/api/v1/...`
  - MCP(JSON-RPC)：`POST /mcp`
- 預設 URL：`http://127.0.0.1:5078`
- 常用埠：由 UI 上方 `HTTP API` 設定
- 綁定範圍：僅本機 (`localhost`、`127.0.0.1`、`::1`、`winhost`)

## 2. 在 UI 啟用 API
1. 開啟 AiyoPerps。
2. 上方工具列設定 `HTTP API`：
   - API OFF 時可修改 Port。
   - 切 ON 啟動服務。
3. 狀態文字顯示 `HTTP API: ON (port)` 即可。

## 3. 安全限制
- Host 非本機會回 `403`。
- 帶 `Origin` 時，Origin 也必須是本機網域，否則 `403`。
- 適用於單機與 WSL -> Windows 本機互通。

## 4. 非同步操作模式
多數寫入型操作（開關連線、下單、取消等）皆為非同步：
1. 呼叫端點取得 `operationId`。
2. 透過 `GET /api/v1/operations/{operationId}` 輪詢。
3. 判斷 `status`：`Pending | Running | Succeeded | Failed`。

## 5. REST 端點

## 5.1 健康檢查與關閉
### `GET /api/v1/health`
無參數。

### `POST /api/v1/app/shutdown`
要求軟體優雅關閉。

## 5.2 帳號
### `GET /api/v1/accounts`
取得所有帳號。

### `GET /api/v1/accounts/{accountId}`
Path:
- `accountId` (GUID)

### `POST /api/v1/accounts`
### `PUT /api/v1/accounts/{accountId}`
Body：`ApiAccountUpsertRequest`
- `venueId` (string, 必填)：`BitMEX`、`Hyperliquid`
- `displayName` (string, 必填)
- `environment` (string, 必填)：`testnet` 或 `mainnet`
- `summary` (string, 必填)
- `apiKey` (string, 可選)
- `apiSecret` (string, 可選)
- `accountAddress` (string, 可選，Hyperliquid 資訊查詢重要)
- `walletAddress` (string, 可選)
- `privateKey` (string, 可選)
- `isEnabled` (bool, 可選，更新時可用)

### `DELETE /api/v1/accounts/{accountId}`
非同步刪除。

## 5.3 Symbol
### `GET /api/v1/accounts/{accountId}/symbols`
### `GET /api/v1/symbols?accountId=<GUID>`
皆回傳該帳號可交易商品。

## 5.4 連線
### `GET /api/v1/connections`
查看目前活動連線。

### `POST /api/v1/connections/open`
Body：`ApiConnectionOpenRequest`
- `accountId` (GUID, 必填)
- `symbol` (string, 必填)
- `interval` (string, 必填)
  - `5m|10m|15m|30m|1h|2h|4h|6h|12h|1d|7d|30d`

### `POST /api/v1/connections/close`
Body：`ApiConnectionCloseRequest`
- `accountId` (GUID, 必填)
- `symbol` (string, 必填)

唯一性規則：每個 `accountId + symbol` 只會有一個後端 session。

## 5.5 市場資料
### `GET /api/v1/market-data`
Query:
- `accountId` (GUID, 必填)
- `symbol` (string, 必填)
- `interval` (string, 可選，預設 `5m`)
- `cursor` (long, 可選)

### `GET /api/v1/market/snapshot`
參數與回傳模型同 `market-data`。

### Cursor 用法
1. 首次不帶 `cursor`：拿 `initialCandles + cursor`。
2. 後續帶 `cursor`：拿 `deltaCandles`。

## 5.6 帳戶狀態
### `GET /api/v1/positions`
- `accountId` (GUID, 必填)
- `symbol` (string, 可選)

### `GET /api/v1/orders`
- `accountId` (GUID, 必填)
- `symbol` (string, 可選)

### `GET /api/v1/balances`
- `accountId` (GUID, 必填)
- `symbol` (string, 可選)

## 5.7 交易
### `POST /api/v1/trading/open-position`
Body：`ApiOpenPositionRequest`
- `accountId` (GUID, 必填)
- `symbol` (string, 必填)
- `side` (string, 必填)：`buy|sell|long|short`
- `orderType` (string, 必填)：`market|limit`
- `leverage` (decimal, 必填)
- `amount` (decimal, 必填)
- `amountUnit` (string, 必填)：`USD` 或標的幣數量
- `limitPrice` (decimal, 限價單必填)

### `POST /api/v1/trading/close-position`
Body：`ApiClosePositionRequest`
- `accountId` (GUID, 必填)
- `positionId` (string, 必填)
- `orderType` (string, 必填)：`market|limit`
- `limitPrice` (decimal, 限價單必填)

### `POST /api/v1/trading/cancel-order`
Body：`ApiCancelOrderRequest`
- `accountId` (GUID, 必填)
- `symbol` (string, 必填)
- `orderId` (string, 必填)

平台錯誤訊息會忠實回傳於 operation 的 `error`。

## 5.8 壓測
### `POST /api/v1/stress/run`
Body：`ApiStressRunRequest`
- `accountId` (GUID, 必填)
- `symbol` (string, 必填)
- `interval` (string, 可選，預設 `5m`)
- `concurrency` (int, 可選，`1..64`，預設 `8`)
- `iterations` (int, 可選，`1..20000`，預設 `200`)

## 5.9 Operation 查詢
### `GET /api/v1/operations/{operationId}`
查詢非同步任務狀態與結果。

## 6. MCP 端點
- URL：`POST /mcp`
- RPC method：
  - `initialize`
  - `ping`
  - `tools/list`
  - `tools/call`

### 可用 MCP tools
- `accounts.list`
- `accounts.get`
- `accounts.create`
- `accounts.update`
- `accounts.delete`
- `symbols.list`
- `connections.list`
- `connections.open`
- `connections.close`
- `market.snapshot`
- `market_data.get`
- `positions.list`
- `positions.open`
- `positions.close`
- `orders.list`
- `orders.cancel`
- `balances.list`
- `stress.run`
- `operations.get`
- `app.shutdown`

### MCP tool 參數對照
- `accounts.list`：無參數
- `accounts.get`：`accountId`
- `accounts.create`：同 `ApiAccountUpsertRequest` body
- `accounts.update`：`accountId` + 同 `ApiAccountUpsertRequest` body
- `accounts.delete`：`accountId`
- `symbols.list`：`accountId`
- `connections.list`：無參數
- `connections.open`：`accountId`、`symbol`、`interval`
- `connections.close`：`accountId`、`symbol`
- `market.snapshot`：`accountId`、`symbol`、可選 `interval`、可選 `cursor`
- `market_data.get`：`accountId`、`symbol`、可選 `interval`、可選 `cursor`
- `positions.list`：`accountId`、可選 `symbol`
- `orders.list`：`accountId`、可選 `symbol`
- `balances.list`：`accountId`、可選 `symbol`
- `positions.open`：同 `ApiOpenPositionRequest` body
- `positions.close`：同 `ApiClosePositionRequest` body
- `orders.cancel`：同 `ApiCancelOrderRequest` body
- `stress.run`：同 `ApiStressRunRequest` body
- `operations.get`：`operationId`
- `app.shutdown`：無參數

## 7. 範例

## 7.1 cURL（REST）
```bash
# health
curl http://127.0.0.1:9090/api/v1/health

# 開啟連線
curl -X POST http://127.0.0.1:9090/api/v1/connections/open \
  -H "Content-Type: application/json" \
  -d '{"accountId":"<GUID>","symbol":"XBTUSD","interval":"5m"}'

# 輪詢 operation
curl http://127.0.0.1:9090/api/v1/operations/<operationId>
```

## 7.2 cURL（MCP）
```bash
curl -X POST http://127.0.0.1:9090/mcp \
  -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}'
```

## 7.3 Python（cursor 增量）
```python
import requests

base = "http://127.0.0.1:9090"
params = {"accountId": "<GUID>", "symbol": "XBTUSD", "interval": "5m"}
first = requests.get(f"{base}/api/v1/market/snapshot", params=params).json()
cursor = first["cursor"]

while True:
    delta = requests.get(
        f"{base}/api/v1/market/snapshot",
        params={**params, "cursor": cursor}
    ).json()
    cursor = delta["cursor"]
    for c in delta.get("deltaCandles", []):
        print(c)
```

## 8. 測試資源
- Postman collection：`docs/api/AiyoPerps-LocalAPI.postman_collection.json`
- MCP 呼叫範例：`docs/api/mcp-tool-call-examples.json`
