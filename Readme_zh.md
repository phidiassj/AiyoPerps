# AiyoPerps

[English version](./Readme.md) <br>
AiyoPerps 是一套加密貨幣永續合約操作軟體，同時支援 DEX (去中心化交易平台) 及 CEX (中心化交易平台) 加密貨幣槓桿操作。<br>
功能包括 多分頁行情 K 線圖 (蠟燭圖)、多幣別即時限價及市價下單、持倉/訂單/餘額檢視及操作等。<br>
本軟體同時提供完整的 [REST API 及 MCP Server](./API_zh.md) 功能，<br>
這代表你可以使用任何你喜歡的 AI Agents 進行自動化操作，或跟你一起協作，包括 [龍蝦](https://molt.bot)、[Codex](https://developers.openai.com/codex/cli)、[Gemini-CLI](https://github.com/google-gemini/gemini-cli)、[Claude-Code](https://claude.com/product/claude-code) 等。<br>
目前軟體介面支援繁體中文及英文，可在介面即時切換，未來將陸續增加支援語系。<br>
<br>
![Software interface](./images/main-zh-01.jpg)

## 1. 環境需求
- Windows 或 Linux 。
- .NET 10 Runtime（Pre-Release 版本已自帶）。
- 目前交易所支援 DEX [Hyperliquid](https://app.hyperliquid.xyz/) 及 CEX [BitMEX](https://www.bitmex.com/)，未來將陸續增加。

## 2. 啟動軟體
1. Windows 執行 `AiyoPerps.exe`，Linux 執行 `./AiyoPerps`。
2. 預設語系為英文，切換後會記憶。

## 3. 上方工具列
- `+ Add Tab`：新增交易分頁
- `Account Manager`：開啟帳號管理
- `Language`：即時切換語言
- `HTTP API`：
  - Port 輸入框（僅在 API OFF 時可編輯）
  - ON/OFF 開關啟停本機 API 服務
  - 狀態文字顯示目前執行狀態

## 4. 帳號管理操作
### 新增帳號
1. 按 `新增帳號`。
2. 選擇 `平台`、`環境`。
3. 填 `顯示名稱`、`摘要`。
4. 依平台填入憑證（可選）：
   - `API Key`、`API Secret` (CEX 中心化平台用)
   - `Wallet Address`（DEX 去中心化平台用）
   - `API Wallet Address`、`API Wallet Private Key`（DEX 去中心化平台用）
5. 按 `新增帳號` 完成。

### 編輯帳號
1. 在左側選已存在帳號。
2. 修改憑證欄位。
3. 按 `更新憑證`。

### 其他功能
- `測試連線`
- `啟用/停用`
- `刪除`

## 5. 交易分頁操作
### 啟用分頁
1. 在未啟用分頁中選帳號。
2. 按 `Enable`。
3. 系統會建立行情連線並載入 Symbol 與歷史資料。

### 分頁上方
- `Symbol` 下拉
- `Interval` 下拉
- `Status`
- `Last Event`
- `Refresh Data`（重抓並覆蓋近 12 小時資料）

### K 線圖區
- 右側價格尺規 + 下方時間尺規。
- 滑鼠滾輪可縮放時間範圍（蠟燭根數增減）。
- 滑鼠移動可顯示動態指標與最近蠟燭資訊。

### 委託簿區
- 級距下拉：`1 / 10 / 100`
- 賣買盤 + spread
- 視窗高度足夠時顯示 `最新交易`

### 右側功能分頁
- `下單`：下單類型、方向、槓桿、金額、單位、限價、預估成本/清算價
- `持倉`：持倉清單與限價/市價平倉
- `訂單`：未完成訂單與取消按鈕
- `餘額`：僅顯示非零幣別

## 6. UI 與 Agent 共用 Session 規則
當 API 對同一個 `accountId + symbol` 開啟連線時：
- UI 會自動開啟/接管對應分頁
- UI 與 API 共用同一個後端 session
- 關閉 UI 分頁會同步關閉 API session
- API 關閉 session 會同步關閉 UI 分頁
- 共用模式下 UI 會鎖定 `帳號 / Symbol / Interval`，避免衝突（toast 提示）

## 7. 訊息提示
- 所有提示統一使用底部中央 toast
- 顯示 5 秒

## 8. 本機資料
資料儲存在執行目錄下：
- `db/AiyoPerps.main.db`
- `db/secrets.key`

[API / MCP 詳細規格](./API_zh.md)。
