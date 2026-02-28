# AiyoPerps

[中文版本說明](./Readme_zh.md) <br>
AiyoPerps is a cryptocurrency perpetual futures trading application that supports leveraged trading on both DEXs (decentralized exchanges) and CEXs (centralized exchanges).<br>
Features include multi-tab market K-line charts (candlestick charts), real-time limit and market order placement for multiple trading pairs, and position/order/balance viewing and management.<br>
This software also provides a complete set of [REST API and MCP Server](./API.md) capabilities,<br>
meaning you can use any AI agents you like for automated operations or collaborative workflows, including [OpenClaw](https://molt.bot), [Codex](https://developers.openai.com/codex/cli), [Gemini-CLI](https://github.com/google-gemini/gemini-cli), [Claude Code](https://claude.com/product/claude-code), and more.<br>
The user interface currently supports Traditional Chinese and English, with instant in-app switching; additional languages will be added over time.<br>
<br>
![Software interface](./images/main-en-01.jpg)

### Support us
If you think this project is cool, feel free to [**buy us a coffee**](https://tuunote.com/AiyoPerps/donate)!<br>
**When you sponsor us, you’ll also get a chance to join Taiwan’s receipt lottery.**<br>
That’s because [Chen-Si Studio](https://utunote.com) is a team that pays taxes properly—we issue an official receipt/invoice for every bit of income, and those invoice numbers can be checked for prizes every two months.<br>
Even if you don’t live in Taiwan, if your invoice wins, just let us know. We’ll claim the prize for you and then send you the money (after deducting any necessary handling fees).

## 1. Requirements
- Windows or Linux.
- .NET 10 Runtime (included in the pre-release build).
- Currently supported exchanges: [Hyperliquid](https://app.hyperliquid.xyz/) (DEX) and [BitMEX](https://www.bitmex.com/) (CEX). More will be added in the future.

## 2. Getting Started
### Local Run
- Download the latest precompiled release from [![GitHub Release](https://img.shields.io/github/v/release/phidiassj/AiyoPerps)](https://github.com/phidiassj/AiyoPerps/releases/latest)，or clone the repository and build it yourself using VS2026.
- After extracting the archive, run `AiyoPerps.exe` on Windows, or `./AiyoPerps` on Linux.
- The default UI language is English, and your selection will be remembered after you switch it.
### Connect your AI Agents
- If you want to use MCP or the REST API, enter the local port number (default: 5078) in the Http API field at the top of the application UI, then enable it.
- From your AI agent, connect to `http://127.0.0.1:5078/mcp`.
- After a successful connection, call tools/list first; you should see tools such as connections.open, market.snapshot, positions.open, and orders.cancel.
### REST API Connection
- You also need to enable the Http API toggle.
- Open `http://127.0.0.1:5078/scalar` to view the full OpenAPI specification.

## 3. Top Toolbar
- `+ Add Tab`: create a trading workspace tab.
- `Account Manager`: open account setup window.
- `Language`: instant UI language switch.
- `HTTP API`:
  - Port textbox (editable only when API is OFF).
  - ON/OFF switch starts/stops Kestrel local API.
  - Status text shows runtime state.

## 4. Account Manager Workflow
### Create account
1. Click `Add Account`.
2. Select `Venue` and `Environment`.
3. Fill `Display Name` and `Summary`.
4. Optional credentials fields by venue:
   - `Api Key`, `Api Secret` (for CEX)
   - `Account Address` (for DEX)
   - `API Wallet Address`, `API Wallet Private Key` (for DEX)
5. Click `Add Account`.

### Edit existing account
1. Select an account in left list.
2. Update credentials.
3. Click `Update Credentials`.

### Maintenance
- `Test Connection`: validate current account.
- `Enable/Disable`: toggle account availability.
- `Delete`: remove account.

## 5. Trading Tab Workflow
### Activate tab
1. In unconfigured tab, select account.
2. Click `Enable`.
3. App connects market data and loads symbol metadata/history.

### Header area
- `Symbol` dropdown
- `Interval` dropdown
- `Status`
- `Last Event`
- `Refresh Data` button (reload last 12 hours and overwrite local range)

### Chart panel
- Candlestick chart with right price scale and bottom time scale.
- Mouse wheel zoom adjusts visible candle density/time span.
- Hover shows dynamic axis values and nearest-candle stats.

### Order book panel
- Tick size selector: `1`, `10`, `100`.
- Ask/Bid tables + spread row.
- Optional `Recent Trades` panel appears only when viewport height is large enough.

### Right-side tabs
- `Order`: order type, side, leverage, amount, unit, optional limit price, estimated cost/liquidation.
- `Positions`: active positions + close controls (limit/market).
- `Orders`: pending/open orders + cancel button.
- `Balances`: non-zero assets only.

## 6. Shared Session Behavior (UI + Agent)
When API opens a connection for the same `accountId + symbol`:
- UI automatically opens/attaches a tab.
- UI and API share one backend session.
- Closing the tab closes the API session.
- Closing the API session closes the UI tab.
- In shared mode, `Account`, `Symbol`, and `Interval` are locked in UI; user is notified by toast.

## 7. Toast Notifications
- Toasts appear at bottom-center.
- Display duration is 5 seconds.
- Used for all user-facing operation feedback.

## 8. Local Data
Runtime data is stored under app base directory:
- `db/AiyoPerps.main.db`
- `db/secrets.key`

[API/MCP details](./API.md).
