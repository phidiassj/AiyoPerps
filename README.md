# AiyoPerps

[繁體中文](./Readme_zh.md)

AiyoPerps is a perpetual futures desktop terminal that supports both CEX (centralized) and DEX (decentralized) trading. It provides a full desktop UI, an MCP server for AI agents, and a local REST API.<br>
With AiyoPerps, you can trade crypto perpetual futures **manually**, **with AI collaboration**, or **fully driven by AI**.

![Software interface](./images/main-en-01.jpg)

## 1. Requirements
- Windows, Linux, or macOS (via Docker).
- .NET 10 Runtime (included in release builds).
- A perpetual futures exchange account. Current supported venues are `Hyperliquid` and `BitMEX`, with more to be added later.

## 2. Run the Desktop App
### Use a release build
1. Download the latest package from [GitHub Releases](https://github.com/phidiassj/AiyoPerps/releases/latest).
2. Extract it.
3. Run:
   - Windows: `AiyoPerps.exe`
   - Linux: `./AiyoPerps`

### Build and run from source
1. Clone this repo or download the full source code.<br>
   `git clone https://github.com/phidiassj/AiyoPerps.git`
2. Build it with [Visual Studio 2026](https://visualstudio.microsoft.com/insiders).

## 3. Enable the Local MCP Server and API
1. Start the desktop app.
2. In the top toolbar, set the `HTTP API` port (default `5078`).
3. Turn the API switch `ON`.
4. Open:
   - OpenAPI UI: `http://127.0.0.1:5078/scalar`
   - MCP endpoint: `http://127.0.0.1:5078/mcp`

## 4. Headless Mode
Use this when you only need REST or MCP.

### Local headless
Start the app with `-- headless --port 5078`.<br>
```bash
Windows:
AiyoPerps.exe -- headless --port 5078
```
```bash
Linux:
./AiyoPerps -- headless --port 5078
```

### Docker
macOS is currently supported through Docker only.<br>
```bash
docker run --rm --name aiyoperps -p 5078:5078 phidiassj/aiyoperps:latest
```
The container starts automatically in headless mode and enables the HTTP API automatically.

## 5. Connect an AI Agent
### Recommended: use the installer to auto-register with supported AI agents
```bash
npx -y @phidiassj/aiyoperps-mcp-installer
```
This registers AiyoPerps with supported AI agents such as Codex, Claude Desktop, Claude Code CLI, and OpenClaw.<br>
<br>
![Installer Interface](./images/installer.jpg)

### Manual stdio bridge
```bash
npx -y @phidiassj/aiyoperps-mcp-bridge --quiet --url http://127.0.0.1:5078/mcp
```
If the installer does not support your AI agent, try this manual method.

## 6. Quick UI Workflow
1. Open `Account Manager` and add an account.
2. Create or select a tab.
3. Choose the account, then click `Enable`.
4. Set `Symbol` and `Interval`.
5. Use the right-side tabs for `Order`, `Positions`, `Orders`, and `Balances`.

## 7. More Details
- Full API and MCP guide: [API.md](./API.md)
- 繁體中文 API guide: [API_zh.md](./API_zh.md)
