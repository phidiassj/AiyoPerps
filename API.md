# AiyoPerps Local API and MCP Guide

This document describes how to connect AI agents and custom clients to AiyoPerps local services.

## 1. Server Model
- Protocols:
  - REST (`/api/v1/...`)
  - MCP over JSON-RPC (`POST /mcp`)
- Default base URL: `http://127.0.0.1:5078`
- Typical custom port: configured from app toolbar (`HTTP API`)
- Bind scope: local only (`localhost`, `127.0.0.1`, `::1`, `winhost`)

## 2. Enable API In UI
1. Open app.
2. Top toolbar:
   - Set `HTTP API` port while API is OFF.
   - Toggle ON.
3. Status text should show `HTTP API: ON (<port>)`.

## 3. Security Constraints
- Host validation rejects non-local hosts (`403`).
- If `Origin` header is present, origin must also be local, else `403`.
- Intended for local machine / WSL bridge usage.

## 4. Async Operation Pattern
Many mutating actions return immediately with an operation envelope.

### Response envelope
```json
{
  "operationId": "string",
  "status": "Pending",
  "createdAt": "2026-02-25T10:00:00+00:00",
  "statusUrl": "/api/v1/operations/<operationId>"
}
```

### Poll
- `GET /api/v1/operations/{operationId}`
- Status enum: `Pending | Running | Succeeded | Failed`

## 5. REST Endpoints

## 5.1 Health and App
### `GET /api/v1/health`
No params.

### `POST /api/v1/app/shutdown`
Requests graceful app shutdown.

## 5.2 Accounts
### `GET /api/v1/accounts`
List all configured accounts.

### `GET /api/v1/accounts/{accountId}`
Path params:
- `accountId` (GUID)

### `POST /api/v1/accounts`
### `PUT /api/v1/accounts/{accountId}`
Body: `ApiAccountUpsertRequest`
- `venueId` (string, required): `BitMEX`, `Hyperliquid`
- `displayName` (string, required)
- `environment` (string, required): `testnet` or `mainnet`
- `summary` (string, required)
- `apiKey` (string, optional)
- `apiSecret` (string, optional)
- `accountAddress` (string, optional, important for Hyperliquid info requests)
- `walletAddress` (string, optional)
- `privateKey` (string, optional)
- `isEnabled` (bool, optional for update)

### `DELETE /api/v1/accounts/{accountId}`
Async delete operation.

## 5.3 Symbols
### `GET /api/v1/accounts/{accountId}/symbols`
### `GET /api/v1/symbols?accountId=<GUID>`
Both return tradable symbols for the account venue/environment.

## 5.4 Connections
### `GET /api/v1/connections`
List active sessions.

### `POST /api/v1/connections/open`
Body: `ApiConnectionOpenRequest`
- `accountId` (GUID, required)
- `symbol` (string, required)
- `interval` (string, required): `5m|10m|15m|30m|1h|2h|4h|6h|12h|1d|7d|30d`

### `POST /api/v1/connections/close`
Body: `ApiConnectionCloseRequest`
- `accountId` (GUID, required)
- `symbol` (string, required)

Uniqueness rule: one backend session per `accountId + symbol`.

## 5.5 Market Data
### `GET /api/v1/market-data`
Query params:
- `accountId` (GUID, required)
- `symbol` (string, required)
- `interval` (string, optional, default `5m`)
- `cursor` (long, optional)

### `GET /api/v1/market/snapshot`
Same parameters and payload model as `/market-data`.

### Cursor flow
1. First request without cursor -> `initialCandles` + `cursor`.
2. Next request with cursor -> `deltaCandles` only.

## 5.6 Account State
### `GET /api/v1/positions`
Params:
- `accountId` (GUID, required)
- `symbol` (string, optional)

### `GET /api/v1/orders`
Params:
- `accountId` (GUID, required)
- `symbol` (string, optional)

### `GET /api/v1/balances`
Params:
- `accountId` (GUID, required)
- `symbol` (string, optional)

## 5.7 Trading
### `POST /api/v1/trading/open-position`
Body: `ApiOpenPositionRequest`
- `accountId` (GUID, required)
- `symbol` (string, required)
- `side` (string, required): `buy|sell|long|short`
- `orderType` (string, required): `market|limit`
- `leverage` (decimal, required)
- `amount` (decimal, required)
- `amountUnit` (string, required): `USD` or base asset unit
- `limitPrice` (decimal, required for limit)

### `POST /api/v1/trading/close-position`
Body: `ApiClosePositionRequest`
- `accountId` (GUID, required)
- `positionId` (string, required)
- `orderType` (string, required): `market|limit`
- `limitPrice` (decimal, required for limit)

### `POST /api/v1/trading/cancel-order`
Body: `ApiCancelOrderRequest`
- `accountId` (GUID, required)
- `symbol` (string, required)
- `orderId` (string, required)

Platform error payload is returned faithfully in operation `error`.

## 5.8 Stress
### `POST /api/v1/stress/run`
Body: `ApiStressRunRequest`
- `accountId` (GUID, required)
- `symbol` (string, required)
- `interval` (string, optional, default `5m`)
- `concurrency` (int, optional, `1..64`, default `8`)
- `iterations` (int, optional, `1..20000`, default `200`)

## 5.9 Operations
### `GET /api/v1/operations/{operationId}`
Fetch async execution status/result.

## 6. MCP Endpoint
- URL: `POST /mcp`
- RPC methods:
  - `initialize`
  - `ping`
  - `tools/list`
  - `tools/call`

### MCP tool list
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

### MCP tool arguments
- `accounts.list`: none
- `accounts.get`: `accountId`
- `accounts.create`: same body as `ApiAccountUpsertRequest`
- `accounts.update`: `accountId` + same body as `ApiAccountUpsertRequest`
- `accounts.delete`: `accountId`
- `symbols.list`: `accountId`
- `connections.list`: none
- `connections.open`: `accountId`, `symbol`, `interval`
- `connections.close`: `accountId`, `symbol`
- `market.snapshot`: `accountId`, `symbol`, optional `interval`, optional `cursor`
- `market_data.get`: `accountId`, `symbol`, optional `interval`, optional `cursor`
- `positions.list`: `accountId`, optional `symbol`
- `orders.list`: `accountId`, optional `symbol`
- `balances.list`: `accountId`, optional `symbol`
- `positions.open`: same body as `ApiOpenPositionRequest`
- `positions.close`: same body as `ApiClosePositionRequest`
- `orders.cancel`: same body as `ApiCancelOrderRequest`
- `stress.run`: same body as `ApiStressRunRequest`
- `operations.get`: `operationId`
- `app.shutdown`: none

## 7. Examples

## 7.1 cURL (REST)
```bash
# health
curl http://127.0.0.1:9090/api/v1/health

# open connection
curl -X POST http://127.0.0.1:9090/api/v1/connections/open \
  -H "Content-Type: application/json" \
  -d '{"accountId":"<GUID>","symbol":"XBTUSD","interval":"5m"}'

# poll
curl http://127.0.0.1:9090/api/v1/operations/<operationId>
```

## 7.2 cURL (MCP)
```bash
curl -X POST http://127.0.0.1:9090/mcp \
  -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}'
```

## 7.3 Python (REST cursor loop)
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

## 8. Test Assets
- Postman collection: `docs/api/AiyoPerps-LocalAPI.postman_collection.json`
- MCP tool call examples: `docs/api/mcp-tool-call-examples.json`
